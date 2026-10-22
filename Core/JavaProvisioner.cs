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
        var cached = FindProvisioned(needMajor);
        if (cached is not null)
        {
            log?.Invoke("复用已下载的 Java " + needMajor + "：" + cached);
            return (cached, needMajor);
        }

        var api = "https://api.adoptium.net/v3/assets/latest/" + needMajor +
                  "/hotspot?architecture=" + Arch + "&image_type=jre&os=windows&vendor=eclipse";
        progress?.Report(new InstallProgress("查询 Java " + needMajor + " 下载地址…", 0.01));
        var node = await Http.GetJsonAsync(api, ct).ConfigureAwait(false);
        var first = (node as JsonArray)?.OfType<JsonObject>().FirstOrDefault()
                    ?? throw new InvalidOperationException("Adoptium 没有提供 Java " + needMajor + " 的 Windows 包");

        var package = first["binary"]?["package"]
                      ?? throw new InvalidOperationException("Adoptium 返回的数据缺少 package 字段");
        var link = package["link"]?.ToString()
                   ?? throw new InvalidOperationException("Adoptium 返回的数据缺少下载地址");
        var fileName = package["name"]?.ToString() ?? ("jre" + needMajor + ".zip");
        var sha256 = package["checksum"]?.ToString();
        var size = package["size"]?.GetValue<long>() ?? 0;
        var release = first["release_name"]?.ToString() ?? ("java" + needMajor);

        var target = Path.Combine(Root, release);
        var zipPath = Path.Combine(Path.GetTempPath(), "minelib-" + fileName);

        log?.Invoke("开始下载 " + fileName + "（" + (size / 1048576.0).ToString("0.0") + " MB）");
        var downloadProgress = progress is null
            ? null
            : new Progress<(long done, long total)>(p =>
            {
                var frac = p.total > 0 ? (double)p.done / p.total : 0;
                progress.Report(new InstallProgress(
                    "下载 Java " + needMajor + " … " + (p.done / 1048576.0).ToString("0.0") + " / " +
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

        progress?.Report(new InstallProgress("解压 Java " + needMajor + "…", 0.9));
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        ZipFile.ExtractToDirectory(zipPath, target, true);
        try { File.Delete(zipPath); } catch (Exception) { }

        var exe = Directory.GetFiles(target, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe is null) throw new InvalidOperationException("解压后没有找到 javaw.exe，包结构可能变了");
        var major = JavaFinder.MajorOf(exe);
        if (major != needMajor)
            throw new InvalidOperationException("下载的 Java 版本是 " + major + "，与需要的 " + needMajor + " 不符");

        progress?.Report(new InstallProgress("Java " + major + " 就绪", 1.0));
        log?.Invoke("Java " + major + " 已就绪：" + exe);
        return (exe, major);
    }
}
