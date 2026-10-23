using System.Diagnostics;
using System.IO;

namespace MineLib.Core;

/// <summary>
/// 下载源。游戏文件来自 Mojang 的几台机器，国内直连经常只有几十 KB/s；
/// 这里把「从哪台机器下」做成可选项，并在设置页里给出实测速度。
/// </summary>
public sealed class DownloadSource
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Note { get; init; }

    /// <summary>版本清单地址。</summary>
    public required string ManifestUrl { get; init; }

    /// <summary>拿某个版本的 json 的地址（null 表示按清单里给的地址取）。</summary>
    public Func<string, string>? VersionJson { get; init; }

    /// <summary>客户端 jar 的地址（null 表示用版本 json 里的 downloads.client.url）。</summary>
    public Func<string, string>? ClientJar { get; init; }

    /// <summary>资源对象（assets/objects）的地址。</summary>
    public required Func<string, string> AssetObject { get; init; }

    /// <summary>maven 库的地址：给 json 里写的原始地址与相对路径，返回实际要下的地址。</summary>
    public required Func<string?, string, string?> Library { get; init; }

    /// <summary>测速用的样本文件（一个几百 KB 的真实库文件）。</summary>
    public required string SampleUrl { get; init; }

    public static readonly DownloadSource Official = new()
    {
        Id = "official",
        Name = "Mojang 官方",
        Note = "原始来源，海外线路通常更快",
        ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json",
        VersionJson = null,
        ClientJar = null,
        AssetObject = hash => "https://resources.download.minecraft.net/" + hash[..2] + "/" + hash,
        Library = (url, path) => string.IsNullOrEmpty(url)
            ? "https://libraries.minecraft.net/" + path.Replace('\\', '/')
            : url,
        SampleUrl = "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"
    };

    public static readonly DownloadSource Bmclapi = new()
    {
        Id = "bmclapi",
        Name = "BMCLAPI 镜像",
        Note = "社区维护的国内镜像，覆盖清单、版本 json、客户端、资源和库",
        ManifestUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
        VersionJson = id => "https://bmclapi2.bangbang93.com/version/" + id + "/json",
        ClientJar = id => "https://bmclapi2.bangbang93.com/version/" + id + "/client",
        AssetObject = hash => "https://bmclapi2.bangbang93.com/assets/" + hash[..2] + "/" + hash,
        Library = (url, path) =>
        {
            var rel = path.Replace('\\', '/');
            // 镜像只覆盖 Minecraft 自己的库；Fabric / Forge 的私库保持原样
            if (!string.IsNullOrEmpty(url) &&
                !url.StartsWith("https://libraries.minecraft.net/", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("http://libraries.minecraft.net/", StringComparison.OrdinalIgnoreCase))
                return url;
            return "https://bmclapi2.bangbang93.com/maven/" + rel;
        },
        SampleUrl = "https://bmclapi2.bangbang93.com/maven/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"
    };

    public static IReadOnlyList<DownloadSource> All { get; } = new[] { Official, Bmclapi };

    public static DownloadSource Get(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Official;

    public sealed record ProbeResult(bool Ok, int LatencyMs, double Kbps, long Bytes, string Detail);

    /// <summary>测一次速：先量清单的往返延迟，再下一个样本文件量吞吐。</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            await Http.GetJsonAsync(ManifestUrl, ct).ConfigureAwait(false);
            var latency = (int)sw.ElapsedMilliseconds;

            sw.Restart();
            var bytes = await Http.ProbeDownloadAsync(SampleUrl, ct).ConfigureAwait(false);
            var secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            var kbps = bytes / 1024.0 / secs;
            var detail = latency + " ms · " + (kbps >= 1024
                ? (kbps / 1024).ToString("0.0") + " MB/s"
                : kbps.ToString("0") + " KB/s") + "（样本 " + (bytes / 1024) + " KB）";
            return new ProbeResult(true, latency, kbps, bytes, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, 0, 0, 0, "失败：" + Http.Trim(ex.Message));
        }
    }
}

/// <summary>当前生效的下载源。配置在启动时套用，切换后立刻影响后续下载。</summary>
public static class DownloadSources
{
    public static DownloadSource Current { get; private set; } = DownloadSource.Official;

    public static void Apply(string? id) => Current = DownloadSource.Get(id);

    public static string Id => Current.Id;
}
