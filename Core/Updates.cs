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

    public static string StageDir => Path.Combine(AppContext.BaseDirectory, "MineLib", "update");
    public static string StagedExe => Path.Combine(StageDir, "MineLibLauncher.exe");

    public sealed record UpdateInfo(string Version, string Url, long Size, string Sha256, string Notes,
        string ExeSha256 = "", string PatchFrom = "", string PatchUrl = "", long PatchSize = 0,
        string PatchSha256 = "");

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
        return new UpdateInfo(version, url,
            node["size"]?.GetValue<long>() ?? 0,
            node["sha256"]?.ToString() ?? "",
            node["notes"]?.ToString() ?? "",
            node["exe"]?["sha256"]?.ToString() ?? "",
            node["patch"]?["from"]?.ToString() ?? "",
            node["patch"]?["url"]?.ToString() ?? "",
            node["patch"]?["size"]?.GetValue<long>() ?? 0,
            node["patch"]?["sha256"]?.ToString() ?? "");
    }

    /// <summary>下载并暂存新版本；返回暂存的 exe 路径。</summary>
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

        // 先试增量：本地 exe + 小补丁，通常只有几 MB，比整包快一个数量级
        var localExe = Path.Combine(AppContext.BaseDirectory, "MineLibLauncher.exe");
        // 只有「起点版本与当前版本完全一致」才用增量；跨版本（比如 0.5.0 直接升 0.5.4）
        // 一律走全量包，补丁里的基线大小/哈希也对不上，强行套会重建出坏文件。
        var patchUsable = !string.IsNullOrEmpty(info.PatchUrl)
                          && string.Equals(info.PatchFrom, Current, StringComparison.Ordinal)
                          && File.Exists(localExe)
                          // 补丁比整包还大就别折腾，直接下整包
                          && (info.Size <= 0 || info.PatchSize <= info.Size / 2);
        if (!patchUsable && !string.IsNullOrEmpty(info.PatchFrom) && info.PatchFrom != Current)
            log?.Invoke("当前版本 " + Current + " 与补丁基线 " + info.PatchFrom + " 不同，改下全量包");
        if (patchUsable)
        {
            try
            {
                var patchPath = Path.Combine(StageDir, "update.patch");
                progress?.Report(new InstallProgress(
                    "下载增量补丁 " + (info.PatchSize / 1048576.0).ToString("0.0") + " MB…", 0));
                if (await Http.DownloadAsync(info.PatchUrl, patchPath, null, null, ct).ConfigureAwait(false))
                {
                    if (string.IsNullOrEmpty(info.PatchSha256) ||
                        Http.Sha256Of(patchPath) == info.PatchSha256.ToLowerInvariant())
                    {
                        log?.Invoke("补丁下载完成，正在本地重建新版本");
                        var rebuilt = Path.Combine(StageDir, "update.rebuilt.exe");
                        var sha = await Task.Run(() => Patcher.Apply(localExe, patchPath, rebuilt), ct)
                            .ConfigureAwait(false);
                        if (string.IsNullOrEmpty(info.ExeSha256) ||
                            sha == info.ExeSha256.ToLowerInvariant())
                        {
                            File.Move(rebuilt, StagedExe, true);
                            try { File.Delete(patchPath); } catch (Exception) { }
                            WriteApplyScript();
                            WriteNotes(info);
                            log?.Invoke("增量更新已就绪（校验通过）");
                            progress?.Report(new InstallProgress("增量更新完成", 1));
                            return StagedExe;
                        }
                        log?.Invoke("重建结果校验不符，改用整包");
                        try { File.Delete(rebuilt); } catch (Exception) { }
                    }
                    try { File.Delete(patchPath); } catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("增量更新失败，改用整包：" + ex.Message);
            }
        }

        progress?.Report(new InstallProgress("下载新版本 " + info.Version + "…", 0));
        var dl = progress is null ? null : new Progress<(long done, long total)>(p =>
        {
            var frac = p.total > 0 ? (double)p.done / p.total : 0;
            progress.Report(new InstallProgress(
                "下载新版本… " + (p.done / 1048576.0).ToString("0.0") + " / " +
                (p.total / 1048576.0).ToString("0.0") + " MB", frac));
        });
        if (!await Http.DownloadAsync(info.Url, zipPath, null, dl, ct).ConfigureAwait(false))
            throw new InvalidOperationException("更新包下载失败");

        if (!string.IsNullOrEmpty(info.Sha256))
        {
            await using var fs = File.OpenRead(zipPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(actual, info.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
            {
                File.Delete(zipPath);
                throw new InvalidOperationException("更新包校验失败（sha256 不匹配）");
            }
        }

        log?.Invoke("更新包校验通过，解压中");
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, "MineLibLauncher.exe", StringComparison.OrdinalIgnoreCase));
            if (entry is null) throw new InvalidOperationException("更新包里没有 MineLibLauncher.exe");
            entry.ExtractToFile(StagedExe, true);
        }
        TryDelete(zipPath);
        WriteApplyScript();

        WriteNotes(info);
        return StagedExe;
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