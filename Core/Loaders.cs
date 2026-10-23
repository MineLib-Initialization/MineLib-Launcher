using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace MineLib.Core;

public enum LoaderKind
{
    Fabric,
    Quilt,
    Forge,
    NeoForge,
    LiteLoader
}

public sealed record LoaderVersion(string Version, string Channel)
{
    public string Display => string.IsNullOrEmpty(Channel) ? Version : Version + "  (" + Channel + ")";
}

/// <summary>
/// 模组加载器：列版本 + 装到 versions/ 下。
/// Fabric / Quilt 直接取官方 meta 的 profile json（纯文本，秒装）；
/// Forge / NeoForge 走它们自己的官方 installer（java -jar ... --installClient）；
/// LiteLoader 只有 GUI installer，下载后交给它自己跑。
/// </summary>
public static class Loaders
{
    public static readonly LoaderKind[] All =
    {
        LoaderKind.Fabric, LoaderKind.Quilt, LoaderKind.Forge, LoaderKind.NeoForge, LoaderKind.LiteLoader
    };

    public static string Label(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "LiteLoader"
    };

    public static bool NeedsJava(LoaderKind kind) =>
        kind is LoaderKind.Forge or LoaderKind.NeoForge or LoaderKind.LiteLoader;

    // ---------------------------------------------------------------- 版本列表
    public static async Task<List<LoaderVersion>> VersionsAsync(LoaderKind kind, string mc, CancellationToken ct)
    {
        var list = new List<LoaderVersion>();
        switch (kind)
        {
            case LoaderKind.Fabric:
            {
                var arr = await Http.GetJsonAsync("https://meta.fabricmc.net/v2/versions/loader/" + mc, ct)
                    .ConfigureAwait(false) as JsonArray;
                foreach (var node in arr ?? new JsonArray())
                {
                    var loader = node?["loader"];
                    var ver = loader?["version"]?.ToString();
                    if (string.IsNullOrEmpty(ver)) continue;
                    var stable = loader?["stable"]?.GetValue<bool>() ?? false;
                    list.Add(new LoaderVersion(ver, stable ? "稳定" : "测试"));
                }
                break;
            }
            case LoaderKind.Quilt:
            {
                var arr = await Http.GetJsonAsync("https://meta.quiltmc.org/v3/versions/loader/" + mc, ct)
                    .ConfigureAwait(false) as JsonArray;
                foreach (var node in arr ?? new JsonArray())
                {
                    var loader = node?["loader"];
                    var ver = loader?["version"]?.ToString();
                    if (string.IsNullOrEmpty(ver)) continue;
                    var stable = loader?["stable"]?.GetValue<bool>() ?? false;
                    list.Add(new LoaderVersion(ver, stable ? "稳定" : "测试"));
                }
                break;
            }
            case LoaderKind.Forge:
            {
                var xml = await Http.GetStringAsync(
                    "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct)
                    .ConfigureAwait(false);
                var doc = XDocument.Parse(xml);
                var tags = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    var promos = await Http.GetJsonAsync(
                        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct)
                        .ConfigureAwait(false);
                    if (promos["promos"] is JsonObject p)
                    {
                        if (p[mc + "-recommended"] is JsonValue r) tags[r.ToString()] = "推荐";
                        if (p[mc + "-latest"] is JsonValue l) tags[l.ToString()] = "最新";
                    }
                }
                catch (Exception)
                {
                }
                var prefix = mc + "-";
                foreach (var v in doc.Descendants("version").Select(e => e.Value))
                {
                    if (!v.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var forge = v[prefix.Length..];
                    list.Add(new LoaderVersion(forge, tags.TryGetValue(forge, out var tag) ? tag : ""));
                }
                list.Reverse();
                break;
            }
            case LoaderKind.NeoForge:
            {
                var node = await Http.GetJsonAsync(
                    "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", ct)
                    .ConfigureAwait(false);
                var prefix = NeoPrefix(mc) + ".";
                if (node["versions"] is JsonArray arr)
                {
                    foreach (var v in arr)
                    {
                        var ver = v?.ToString() ?? "";
                        if (!ver.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        list.Add(new LoaderVersion(ver, ""));
                    }
                }
                list.Reverse();
                break;
            }
            case LoaderKind.LiteLoader:
            {
                var node = await Http.GetJsonAsync("http://dl.liteloader.com/versions/versions.json", ct)
                    .ConfigureAwait(false);
                if (node["versions"] is JsonObject vs && vs[mc] is not null)
                    list.Add(new LoaderVersion(mc, "legacy"));
                break;
            }
        }
        return list;
    }

    public static async Task<List<string>> LiteLoaderMinecraftVersionsAsync(CancellationToken ct)
    {
        var node = await Http.GetJsonAsync("http://dl.liteloader.com/versions/versions.json", ct)
            .ConfigureAwait(false);
        return (node["versions"] as JsonObject)?.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal)
            .ToList() ?? new List<string>();
    }

    /// <summary>1.21.1 → 21.1，用于把 Minecraft 版本映射到 NeoForge 版本号前缀。</summary>
    private static string NeoPrefix(string mc)
    {
        var parts = mc.Split('.');
        return parts.Length >= 3 ? parts[1] + "." + parts[2] : mc;
    }

    // ---------------------------------------------------------------- 安装
    public static async Task<string> InstallAsync(LoaderKind kind, string mc, string loaderVersion,
        string gameDir, string javaExe, IProgress<InstallProgress>? progress, Action<string>? log,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(gameDir, "versions"));
        switch (kind)
        {
            case LoaderKind.Fabric:
            case LoaderKind.Quilt:
                return await InstallFromProfileAsync(kind, mc, loaderVersion, gameDir, log, ct).ConfigureAwait(false);
            case LoaderKind.Forge:
                return await InstallForgeAsync(mc, loaderVersion, gameDir, javaExe, progress, log, ct)
                    .ConfigureAwait(false);
            case LoaderKind.NeoForge:
                return await InstallNeoForgeAsync(loaderVersion, gameDir, javaExe, progress, log, ct)
                    .ConfigureAwait(false);
            default:
                return await RunLiteLoaderInstallerAsync(mc, gameDir, javaExe, progress, log, ct)
                    .ConfigureAwait(false);
        }
    }

    private static async Task<string> InstallFromProfileAsync(LoaderKind kind, string mc, string loaderVersion,
        string gameDir, Action<string>? log, CancellationToken ct)
    {
        var (url, id) = kind == LoaderKind.Fabric
            ? ($"https://meta.fabricmc.net/v2/versions/loader/{mc}/{loaderVersion}/profile/json",
               $"fabric-loader-{loaderVersion}-{mc}")
            : ($"https://meta.quiltmc.org/v3/versions/loader/{mc}/{loaderVersion}/profile/json",
               $"quilt-loader-{loaderVersion}-{mc}");

        var profile = (JsonObject)await Http.GetJsonAsync(url, ct).ConfigureAwait(false);
        profile["id"] = id;
        profile["inheritsFrom"] = mc;
        var dir = Path.Combine(gameDir, "versions", id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"), profile.ToJsonString(), ct)
            .ConfigureAwait(false);
        log?.Invoke("已写入版本 " + id);
        return id;
    }

    private static async Task<string> InstallForgeAsync(string mc, string forgeVersion, string gameDir,
        string javaExe, IProgress<InstallProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var before = SnapshotVersions(gameDir);
        var full = mc + "-" + forgeVersion;
        var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{full}/forge-{full}-installer.jar";
        var jar = Path.Combine(AppConfig.Dir, "installers", $"forge-{full}-installer.jar");
        progress?.Report(new InstallProgress("下载 Forge 安装器…", 0.02));
        if (!await Http.DownloadAsync(url, jar, null, null, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Forge 安装器下载失败：" + url);

        await RunInstallerAsync(javaExe, jar, new[] { "--installClient", gameDir }, gameDir,
            progress, log, ct).ConfigureAwait(false);

        // 安装器实际生成的目录名才是准的（版本号/命名规则可能变），别硬猜
        var guess = mc + "-forge-" + forgeVersion;
        var found = FindNewVersion(gameDir, before, new[] { "forge", forgeVersion });
        if (found is not null)
        {
            log?.Invoke("安装器生成版本：" + found);
            return found;
        }
        if (Directory.Exists(Path.Combine(gameDir, "versions", guess)))
        {
            log?.Invoke("按约定版本名找到：" + guess);
            return guess;
        }
        throw new InvalidOperationException(
            "安装器跑完了但没找到它生成的版本目录（期望 " + guess + "），请检查日志。");
    }

    private static async Task<string> InstallNeoForgeAsync(string neoforgeVersion, string gameDir,
        string javaExe, IProgress<InstallProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var before = SnapshotVersions(gameDir);
        var url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/" + neoforgeVersion +
                  "/neoforge-" + neoforgeVersion + "-installer.jar";
        var jar = Path.Combine(AppConfig.Dir, "installers", $"neoforge-{neoforgeVersion}-installer.jar");
        progress?.Report(new InstallProgress("下载 NeoForge 安装器…", 0.02));
        if (!await Http.DownloadAsync(url, jar, null, null, ct).ConfigureAwait(false))
            throw new InvalidOperationException("NeoForge 安装器下载失败：" + url);

        await RunInstallerAsync(javaExe, jar, new[] { "--installClient", gameDir }, gameDir,
            progress, log, ct).ConfigureAwait(false);

        var guess = "neoforge-" + neoforgeVersion;
        var found = FindNewVersion(gameDir, before, new[] { "neoforge", neoforgeVersion });
        if (found is not null)
        {
            log?.Invoke("安装器生成版本：" + found);
            return found;
        }
        if (Directory.Exists(Path.Combine(gameDir, "versions", guess)))
        {
            log?.Invoke("按约定版本名找到：" + guess);
            return guess;
        }
        throw new InvalidOperationException(
            "安装器跑完了但没找到它生成的版本目录（期望 " + guess + "），请检查日志。");
    }

    private static async Task<string> RunLiteLoaderInstallerAsync(string mc, string gameDir, string javaExe,
        IProgress<InstallProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        // LiteLoader 官方安装器只有图形界面，没有 headless 参数，所以这里下载后交给它自己跑
        var url = $"http://dl.liteloader.com/versions/com/mumfrey/liteloader-installer/{mc}/liteloader-installer-{mc}.jar";
        var jar = Path.Combine(AppConfig.Dir, "installers", $"liteloader-installer-{mc}.jar");
        progress?.Report(new InstallProgress("下载 LiteLoader 安装器…", 0.1));
        if (!await Http.DownloadAsync(url, jar, null, null, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "LiteLoader 安装器下载失败（它只提供到 " + mc + " 这类老版本）。地址：" + url);

        log?.Invoke("LiteLoader 安装器只有图形界面，已为你启动，请在窗口里点 OK 完成安装。");
        Process.Start(new ProcessStartInfo(javaExe.Replace("javaw", "java"))
        {
            ArgumentList = { "-jar", jar },
            UseShellExecute = false,
            CreateNoWindow = false
        });
        return "";
    }

    /// <summary>记录 versions/ 下已有哪些目录，用来判断安装器新造了什么。</summary>
    internal static HashSet<string> SnapshotVersions(string gameDir)
    {
        try
        {
            var dir = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(dir)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return Directory.GetDirectories(dir).Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>安装器跑完后，找它新生成的那个版本目录（按关键字过滤）。</summary>
    internal static string? FindNewVersion(string gameDir, HashSet<string> before, string[] keywords)
    {
        try
        {
            var dir = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(dir)) return null;
            var candidates = Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n) && !before.Contains(n!))
                .Select(n => n!)
                .Where(n => keywords.All(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(n => Directory.GetLastWriteTime(Path.Combine(dir, n)))
                .ToList();
            return candidates.FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task RunInstallerAsync(string javaExe, string jar, string[] args, string gameDir,
        IProgress<InstallProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        // Forge / NeoForge 安装器要求 gameDir 里有 launcher_profiles.json
        var profiles = Path.Combine(gameDir, "launcher_profiles.json");
        if (!File.Exists(profiles))
            await File.WriteAllTextAsync(profiles,
                "{\"profiles\":{},\"settings\":{},\"version\":3}", ct).ConfigureAwait(false);

        var exe = javaExe.Replace("javaw", "java");
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = gameDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(jar);
        foreach (var a in args) psi.ArgumentList.Add(a);

        progress?.Report(new InstallProgress("运行安装器（要下载几百 MB 依赖）…", 0.08));
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var spin = 0.08;
        var started = DateTime.UtcNow;
        while (!proc.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                try { proc.Kill(true); } catch (Exception) { }
                ct.ThrowIfCancellationRequested();
            }
            // 安装器卡死时别让用户干等：30 分钟还没退就杀掉并说明
            if ((DateTime.UtcNow - started).TotalMinutes > 30)
            {
                try { proc.Kill(true); } catch (Exception) { }
                throw new InvalidOperationException("安装器运行超过 30 分钟仍未结束，已中止。请检查网络或改用官方安装器。");
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
            spin = Math.Min(0.95, spin + 0.004);
            progress?.Report(new InstallProgress("安装器运行中…", spin));
        }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("安装器退出码 " + proc.ExitCode + "，详情见日志。");
    }
}
