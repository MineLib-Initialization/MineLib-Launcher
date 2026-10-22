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

    public sealed record UpdateInfo(string Version, string Url, long Size, string Sha256, string Notes);

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
            node["notes"]?.ToString() ?? "");
    }

    /// <summary>下载并暂存新版本；返回暂存的 exe 路径。</summary>
    public static async Task<string> StageAsync(UpdateInfo info, IProgress<InstallProgress>? progress,
        Action<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(StageDir);
        var zipPath = Path.Combine(StageDir, "update.zip");

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
        File.Delete(zipPath);
        WriteApplyScript();

        // 把这次的更新说明留在暂存目录，新版本启动后取出来告知用户
        var notePath = Path.Combine(StageDir, "pending-notes.json");
        File.WriteAllText(notePath, new JsonObject
        {
            ["version"] = info.Version,
            ["notes"] = info.Notes
        }.ToJsonString());
        return StagedExe;
    }

    private static void WriteApplyScript()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "MineLibLauncher.exe");
        var script = Path.Combine(StageDir, "apply-update.cmd");
        var lines = new[]
        {
            "@echo off",
            "setlocal",
            ":wait",
            "tasklist /fi \"imagename eq MineLibLauncher.exe\" 2>nul | find /i \"MineLibLauncher.exe\" >nul",
            "if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)",
            "copy /y \"%~dp0MineLibLauncher.exe\" \"" + exe + "\" >nul",
            "del /q \"%~dp0MineLibLauncher.exe\"",
            "start \"\" \"" + exe + "\"",
            "del /q \"%~f0\"",
        };
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
        if (!File.Exists(StagedExe) || !File.Exists(script)) return false;
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
