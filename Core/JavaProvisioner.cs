using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MineLib.Core;

/// <summary>
/// 自动获取 Java 运行时：按需要的版本向 Eclipse Adoptium 取 Windows 的 JRE 包，
/// 解压到程序目录下的 MineLib\java\ 里，之后按普通 Java 使用。
/// 目录布局：<程序目录>\MineLib\java\<release-name>\<解压内容>\bin\javaw.exe
/// </summary>
public static class JavaProvisioner
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "MineLib", "java");

    private static string Arch =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";

    /// <summary>已经下过并且版本对得上的运行时。</summary>
    public static string? FindProvisioned(int needMajor)
    {
        if (!Directory.Exists(Root)) return null;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var exe = Directory.GetFiles(dir, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null) continue;
            if (JavaFinder.MajorOf(exe) == needMajor) return exe;
        }
        return null;
    }

    public static async Task<(string exe, int major)> EnsureAsync(int needMajor,
        IProgress<InstallProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        // FindProvisioned 会递归找 javaw.exe 并逐个启动 -version；不能留在 UI 线程上
        var cached = await Task.Run(() => FindProvisioned(needMajor), ct).ConfigureAwait(false);
        if (cached is not null)
        {
            log?.Invoke("复用已下载的 Java " + needMajor + "：" + cached);
            return (cached, needMajor);
        }

        // Adoptium 只发 LTS（8/11/17/21/25…）。像 1.17 要求 Java 16 这种非 LTS 版本，
        // 直接按 16 查会 404，所以按「需要 → 可用的更高 LTS」顺序挨个试。
        var candidates = new List<int> { needMajor };
        foreach (var fallback in new[] { 17, 21, 11, 8, 25 })
            if (fallback > needMajor || fallback == needMajor)
                candidates.Add(fallback);
        candidates = candidates.Distinct().ToList();

        JsonObject? first = null;
        var chosenMajor = needMajor;
        foreach (var candidate in candidates)
        {
            progress?.Report(new InstallProgress("查询 Java " + candidate + " 下载地址…", 0.01));
            try
            {
                var api = "https://api.adoptium.net/v3/assets/latest/" + candidate +
                          "/hotspot?architecture=" + Arch + "&image_type=jre&os=windows&vendor=eclipse";
                var node = await Http.GetJsonAsync(api, ct).ConfigureAwait(false);
                var hit = (node as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                if (hit is null) continue;
                first = hit;
                chosenMajor = candidate;
                if (candidate != needMajor)
                    log?.Invoke("Adoptium 没有 Java " + needMajor + "，改用 Java " + candidate + "（向下兼容）");
                break;
            }
            catch (Exception ex)
            {
                log?.Invoke("查询 Java " + candidate + " 失败：" + Http.Trim(ex.Message));
            }
        }
        if (first is null)
            throw new InvalidOperationException("Adoptium 没有可用的 Windows JRE（试过 " +
                                                string.Join(" / ", candidates) + "）");

        var package = first["binary"]?["package"]
                      ?? throw new InvalidOperationException("Adoptium 返回的数据缺少 package 字段");
        var link = package["link"]?.ToString()
                   ?? throw new InvalidOperationException("Adoptium 返回的数据缺少下载地址");
        var fileName = package["name"]?.ToString() ?? ("jre" + needMajor + ".zip");
        var sha256 = package["checksum"]?.ToString();
        var size = package["size"]?.GetValue<long>() ?? 0;
        var release = first["release_name"]?.ToString() ?? ("java" + chosenMajor);

        var target = Path.Combine(Root, release);
        var zipPath = Path.Combine(Path.GetTempPath(), "minelib-" + fileName);

        log?.Invoke("开始下载 " + fileName + "（" + (size / 1048576.0).ToString("0.0") + " MB）");
        var downloadProgress = progress is null
            ? null
            : new Progress<(long done, long total)>(p =>
            {
                var frac = p.total > 0 ? (double)p.done / p.total : 0;
                progress.Report(new InstallProgress(
                    "下载 Java " + chosenMajor + " … " + (p.done / 1048576.0).ToString("0.0") + " / " +
                    (p.total / 1048576.0).ToString("0.0") + " MB", 0.02 + frac * 0.86));
            });

        var ok = await Http.DownloadAsync(link, zipPath, null, downloadProgress, ct).ConfigureAwait(false);
        if (!ok) throw new InvalidOperationException("Java 下载失败：" + fileName);

        if (!string.IsNullOrEmpty(sha256))
        {
            await using var fs = File.OpenRead(zipPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(actual, sha256.ToLowerInvariant(), StringComparison.Ordinal))
                throw new InvalidOperationException("Java 包校验失败（sha256 不匹配），已中止");
        }

        progress?.Report(new InstallProgress("解压 Java " + chosenMajor + "…", 0.9));
        if (Directory.Exists(target))
        {
            try { Directory.Delete(target, true); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("旧的 Java 目录删不掉（可能被占用）：" + target + "，" + ex.Message);
            }
        }
        Directory.CreateDirectory(target);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, target, true);
        }
        finally
        {
            try { File.Delete(zipPath); } catch (Exception) { }
        }

        var exe = Directory.GetFiles(target, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe is null) throw new InvalidOperationException("解压后没有找到 javaw.exe，包结构可能变了");
        var major = JavaFinder.MajorOf(exe);
        // 允许「比需要的更新」（比如要 16 给了 17），但不允许更低
        if (major < needMajor)
            throw new InvalidOperationException("下载的 Java 版本是 " + major + "，低于需要的 " + needMajor);

        progress?.Report(new InstallProgress("Java " + major + " 就绪", 1.0));
        log?.Invoke("Java " + major + " 已就绪：" + exe);
        return (exe, major);
    }
}
