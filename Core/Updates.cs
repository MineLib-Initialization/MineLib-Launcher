using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MineLib.Core;

/// <summary>
/// 更新：官网放一份 version.json，启动器启动时静默比对；有新版就后台下好、
/// 校验 sha256、放进 MineLib\update\，下次启动前由脚本替换 exe 再拉起。
/// </summary>
public static class Updates
{
    public const string ManifestUrl = "https://launcher.endercraft.top/launcher/version.json";

    /// <summary>当前版本，取自程序集版本（csproj 里的 Version）。</summary>
    public static string Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>自检用：把暂存目录指到临时目录，别动真实安装目录。</summary>
    internal static string? StageDirOverride;

    public static string StageDir => StageDirOverride ??
        Path.Combine(AppContext.BaseDirectory, "MineLib", "update");
    public static string StagedExe => Path.Combine(StageDir, "MineLibLauncher.exe");

    public sealed record UpdateInfo(string Version, string Url, long Size, string Sha256, string Notes,
        string ExeSha256 = "", string PatchFrom = "", string PatchUrl = "", long PatchSize = 0,
        string PatchSha256 = "", string Signature = "", long ExeSize = 0, string Canonical = "");

    /// <summary>自检用：替换真实下载动作（参数是 url 与目标路径），避免测试依赖网络。</summary>
    internal static Func<string, string, CancellationToken, Task<bool>>? FetchOverride;

    public static bool HasStagedUpdate => File.Exists(StagedExe);

    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string s) => s.Split('.', '-', '+')
            .Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pa = Parts(a);
        var pb = Parts(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y ? 1 : -1;
        }
        return 0;
    }

    /// <summary>比对线上清单，返回比当前版本新的信息；没有则返回 null。</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        var node = await Http.GetJsonAsync(ManifestUrl, ct).ConfigureAwait(false);
        var version = node["version"]?.ToString();
        var url = node["url"]?.ToString();
        if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(url)) return null;
        if (CompareVersions(version, Current) <= 0) return null;
        var manifest = node as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        return new UpdateInfo(version, url,
            ManifestSign.AsLong(node["size"]),
            node["sha256"]?.ToString() ?? "",
            node["notes"]?.ToString() ?? "",
            node["exe"]?["sha256"]?.ToString() ?? "",
            node["patch"]?["from"]?.ToString() ?? "",
            node["patch"]?["url"]?.ToString() ?? "",
            ManifestSign.AsLong(node["patch"]?["size"]),
            node["patch"]?["sha256"]?.ToString() ?? "",
            node["signature"]?.ToString() ?? "",
            ManifestSign.AsLong(node["exe"]?["size"]),
            ManifestSign.CanonicalFrom(manifest));
    }

    /// <summary>
    /// 下载并暂存新版本；返回暂存的 exe 路径。
    ///
    /// 这里是 fail-closed 的：清单没签名、签名验不过、缺少整包/主程序哈希、哈希对不上、
    /// 体积对不上，一律拒绝更新 —— 宁可这次不更新，也不能把没验过的 exe 换上去。
    /// </summary>
    public static async Task<string> StageAsync(UpdateInfo info, IProgress<InstallProgress>? progress,
        Action<string>? log, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(StageDir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "启动器所在目录不可写，没法自动更新（" + AppContext.BaseDirectory + "）：" + ex.Message);
        }
        var zipPath = Path.Combine(StageDir, "update.zip");

        // 0) 清单本身的签名：没有签名或验不过就别往下走
        if (string.IsNullOrWhiteSpace(info.Signature))
            throw new InvalidOperationException("更新清单没有签名，已拒绝更新");
        var canonical = string.IsNullOrEmpty(info.Canonical)
            ? ManifestSign.Canonical(info.Version, "", info.Size, info.Sha256, info.Url, info.ExeSize,
                info.ExeSha256, info.PatchFrom, info.PatchUrl, info.PatchSize, info.PatchSha256)
            : info.Canonical;
        if (!ManifestSign.Verify(canonical, info.Signature))
            throw new InvalidOperationException("更新清单验签失败，已拒绝更新" +
                                               (ManifestSign.LastError is null ? "" : "：" + ManifestSign.LastError));

        // 1) 哈希字段是必须的：缺了就拒绝（以前的写法是"缺了就跳过校验"，等于没校验）
        if (!IsSha256(info.Sha256)) throw new InvalidOperationException("更新清单缺少整包 sha256，已拒绝更新");
        if (!IsSha256(info.ExeSha256)) throw new InvalidOperationException("更新清单缺少主程序 sha256，已拒绝更新");

        // 2) 先试增量：本地 exe + 小补丁。两个哈希都在、基线版本一致、补丁不太大才用。
        var localExe = Path.Combine(AppContext.BaseDirectory, "MineLibLauncher.exe");
        var patchHasHashes = IsSha256(info.PatchSha256);   // 补丁哈希缺失就不能用补丁
        var patchUsable = !string.IsNullOrEmpty(info.PatchUrl)
                          && string.Equals(info.PatchFrom, Current, StringComparison.Ordinal)
                          && File.Exists(localExe)
                          && patchHasHashes
                          // 补丁比整包还大就别折腾，直接下整包
                          && (info.Size <= 0 || info.PatchSize <= info.Size / 2);
        if (!patchUsable && !string.IsNullOrEmpty(info.PatchFrom) && info.PatchFrom != Current)
            log?.Invoke("当前版本 " + Current + " 与补丁基线 " + info.PatchFrom + " 不同，改下全量包");
        if (!patchUsable && !string.IsNullOrEmpty(info.PatchUrl) && !patchHasHashes)
            log?.Invoke("补丁信息缺少 sha256，改下全量包");
        if (patchUsable)
        {
            try
            {
                var patchPath = Path.Combine(StageDir, "update.patch");
                progress?.Report(new InstallProgress(
                    "下载增量补丁 " + (info.PatchSize / 1048576.0).ToString("0.0") + " MB…", 0));
                if (await FetchAsync(info.PatchUrl, patchPath, null, ct).ConfigureAwait(false))
                {
                    // 两个哈希都必须对上；对不上就整包，绝不把没验过的结果留下来
                    var patchOk = Http.Sha256Of(patchPath) == info.PatchSha256.ToLowerInvariant();
                    if (patchOk)
                    {
                        log?.Invoke("补丁下载完成，正在本地重建新版本");
                        var rebuilt = Path.Combine(StageDir, "update.rebuilt.exe");
                        var sha = await Task.Run(() => Patcher.Apply(localExe, patchPath, rebuilt), ct)
                            .ConfigureAwait(false);
                        if (string.Equals(sha, info.ExeSha256.ToLowerInvariant(), StringComparison.Ordinal))
                        {
                            File.Move(rebuilt, StagedExe, true);
                            TryDelete(patchPath);
                            WriteApplyScript();
                            WriteNotes(info);
                            log?.Invoke("增量更新已就绪（签名 + 双哈希校验通过）");
                            progress?.Report(new InstallProgress("增量更新完成", 1));
                            return StagedExe;
                        }
                        log?.Invoke("重建结果与清单的 exe sha256 不符，改用整包");
                        TryDelete(rebuilt);
                    }
                    else
                    {
                        log?.Invoke("补丁文件 sha256 不符，改用整包");
                    }
                    TryDelete(patchPath);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("增量更新失败，改用整包：" + ex.Message);
            }
        }

        // 3) 全量包
        progress?.Report(new InstallProgress("下载新版本 " + info.Version + "…", 0));
        var dl = progress is null ? null : new Progress<(long done, long total)>(p =>
        {
            var frac = p.total > 0 ? (double)p.done / p.total : 0;
            progress.Report(new InstallProgress(
                "下载新版本… " + (p.done / 1048576.0).ToString("0.0") + " / " +
                (p.total / 1048576.0).ToString("0.0") + " MB", frac));
        });
        if (!await FetchAsync(info.Url, zipPath, dl, ct).ConfigureAwait(false))
            throw new InvalidOperationException("更新包下载失败");

        if (info.Size > 0 && new FileInfo(zipPath).Length != info.Size)
        {
            TryDelete(zipPath);
            throw new InvalidOperationException("更新包体积与清单不符，已删除");
        }
        var zipSha = Http.Sha256Of(zipPath);
        if (!string.Equals(zipSha, info.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            TryDelete(zipPath);
            throw new InvalidOperationException("更新包 sha256 与清单不符，已删除");
        }

        log?.Invoke("更新包校验通过，解压中");
        var exeTmp = Path.Combine(StageDir, "update.extract.exe");
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, "MineLibLauncher.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null) throw new InvalidOperationException("更新包里没有 MineLibLauncher.exe");
                entry.ExtractToFile(exeTmp, true);
            }
            // 解压出来的 exe 也要和清单对得上：防止"包对了但里面的程序被换过"
            if (info.ExeSize > 0 && new FileInfo(exeTmp).Length != info.ExeSize)
                throw new InvalidOperationException("更新包里的 exe 体积与清单不符");
            var exeSha = Http.Sha256Of(exeTmp);
            if (!string.Equals(exeSha, info.ExeSha256.ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidOperationException("更新包里的 exe sha256 与清单不符");
            File.Move(exeTmp, StagedExe, true);
        }
        catch (Exception)
        {
            TryDelete(exeTmp);
            TryDelete(StagedExe);
            throw;
        }
        finally
        {
            TryDelete(zipPath);
        }
        WriteApplyScript();
        WriteNotes(info);
        return StagedExe;
    }

    /// <summary>是不是一个像样的 sha256（64 位十六进制）。</summary>
    internal static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value!.Length == 64 &&
        value.All(c => Uri.IsHexDigit(c));

    /// <summary>下载文件到指定路径：自检可以替换掉真实下载。</summary>
    private static async Task<bool> FetchAsync(string url, string dest,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        if (FetchOverride is not null)
            return await FetchOverride(url, dest, ct).ConfigureAwait(false);
        return await Http.DownloadAsync(url, dest, null, progress, ct).ConfigureAwait(false);
    }



    /// <summary>删文件不抛异常（临时文件被占用不该让整个更新失败）。</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    /// <summary>把这次的更新说明留在暂存目录，新版本启动后取出来告知用户。</summary>
    private static void WriteNotes(UpdateInfo info)
    {
        var notePath = Path.Combine(StageDir, "pending-notes.json");
        File.WriteAllText(notePath, new JsonObject
        {
            ["version"] = info.Version,
            ["notes"] = info.Notes
        }.ToJsonString());
    }

    private static void WriteApplyScript() =>
        WriteApplyScriptFor(StageDir, Path.Combine(AppContext.BaseDirectory, "MineLibLauncher.exe"));

    /// <summary>生成替换脚本。抽出来是为了能被自检直接调用（含成功与失败两种路径）。</summary>
    internal static void WriteApplyScriptFor(string stageDir, string exe, bool relaunch = true)
    {
        var script = Path.Combine(stageDir, "apply-update.cmd");
        // 等的应该是「占用目标 exe 的那个进程」，按目标文件名来，别写死
        var imageName = Path.GetFileNameWithoutExtension(exe);
        // 注意：只有复制成功才删除暂存 exe；否则保留现场并留一份失败说明，
        // 用户下次启动还能重试，不会出现"更新包没了、程序也没换成"的静默失败。
        var lines = new[]
        {
            "@echo off",
            "setlocal enabledelayedexpansion",
            "set \"TRIES=0\"",
            ":wait",
            "tasklist /fi \"imagename eq " + imageName + ".exe\" 2>nul | find /i \"" + imageName + ".exe\" >nul",
            "if errorlevel 1 goto copy",
            "set /a TRIES+=1",
            "if !TRIES! GEQ 120 goto timeout",
            "timeout /t 1 /nobreak >nul",
            "goto wait",
            ":timeout",
            ">\"%~dp0apply-failed.txt\" echo 等待启动器退出超时，未替换。暂存文件已保留，可稍后重启再试。",
            "del /q \"%~f0\"",
            "exit /b 1",
            ":copy",
            "copy /y \"%~dp0MineLibLauncher.exe\" \"" + exe + "\" >nul 2>nul",
            "if errorlevel 1 goto copyfail",
            "del /q \"%~dp0MineLibLauncher.exe\"",
            relaunch ? "start \"\" \"" + exe + "\"" : "rem 自检模式不重启",
            "del /q \"%~f0\"",
            "exit /b 0",
            ":copyfail",
            ">\"%~dp0apply-failed.txt\" echo 复制新版本失败（文件被占用或无权限），暂存文件已保留。",
            "del /q \"%~f0\"",
            "exit /b 2",
        };
        Directory.CreateDirectory(stageDir);
        File.WriteAllText(script, string.Join("\r\n", lines) + "\r\n");
    }

    public sealed record AppliedNotes(string Version, string Notes);

    /// <summary>启动时调用：如果本次启动是更新后的第一次，返回更新说明并清掉标记。</summary>
    public static AppliedNotes? TakeAppliedNotes()
    {
        var notePath = Path.Combine(StageDir, "pending-notes.json");
        if (!File.Exists(notePath)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(notePath));
            var version = node?["version"]?.ToString() ?? "";
            var notes = node?["notes"]?.ToString() ?? "";
            File.Delete(notePath);
            if (string.IsNullOrEmpty(version)) return null;
            // 只有当前版本已经到了这个版本，才算这次更新真的生效了
            if (CompareVersions(Current, version) != 0) return null;
            return new AppliedNotes(version, notes);
        }
        catch (Exception)
        {
            try { File.Delete(notePath); } catch (Exception) { }
            return null;
        }
    }

    /// <summary>启动时调用：如果有暂存好的更新，拉起替换脚本并退出当前进程。</summary>
    public static bool TryApplyStagedAndExit()
    {
        var script = Path.Combine(StageDir, "apply-update.cmd");
        if (!File.Exists(StagedExe)) return false;
        if (!File.Exists(script))
        {
            // 脚本没了（被手动删/被杀软删），暂存的 exe 也就没用了
            TryDelete(StagedExe);
            TryDelete(Path.Combine(StageDir, "pending-notes.json"));
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = StageDir
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}