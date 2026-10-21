using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace MineLib.Core;

public sealed record InstallProgress(string Stage, double Fraction);

/// <summary>官方源装配：客户端 / libraries / assets 的检查与下载。全部走 Mojang 官方 CDN。</summary>
public static class Installer
{
    public const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    private const string ResourceBase = "https://resources.download.minecraft.net";

    private static string CurrentOs =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static bool RuleAllows(JsonObject lib)
    {
        if (lib["rules"] is not JsonArray rules) return true;
        var allowed = false;
        foreach (var node in rules)
        {
            if (node is not JsonObject rule) continue;
            var action = rule["action"]?.ToString() ?? "allow";
            var ok = true;
            if (rule["os"] is JsonObject os)
            {
                var name = os["name"]?.ToString();
                if (name is not null && name != CurrentOs) ok = false;
                var arch = os["arch"]?.ToString();
                if (ok && arch is not null && arch != "x86")
                {
                    var machine = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                        .ToString().ToLowerInvariant();
                    ok = arch == machine || (arch == "x86_64" && machine == "x64");
                }
            }
            if (ok && rule["features"] is JsonObject feats)
            {
                foreach (var kv in feats)
                    if ((kv.Value?.GetValue<bool>() ?? false) != false) ok = false;
            }
            if (ok) allowed = action == "allow";
        }
        return allowed;
    }

    public static string MavenPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return "";
        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        var file = $"{artifact}-{version}{classifier}.jar";
        return Path.Combine(group.Replace('.', Path.DirectorySeparatorChar), artifact, version, file);
    }

    public static string LibPath(JsonObject lib)
    {
        var path = lib["downloads"]?["artifact"]?["path"]?.ToString();
        return string.IsNullOrEmpty(path) ? MavenPath(lib["name"]?.ToString() ?? "") : path;
    }

    public static string ClientJarPath(string gameDir, string vid, JsonObject merged)
    {
        var dir = Path.Combine(gameDir, "versions", vid);
        var own = Path.Combine(dir, vid + ".jar");
        if (File.Exists(own)) return own;
        var alt = Path.Combine(dir, (merged["id"]?.ToString() ?? vid) + ".jar");
        return File.Exists(alt) ? alt : own;
    }

    public sealed class MissingFiles
    {
        public bool Client;
        public List<JsonObject> Libraries = new();
        public bool AssetIndex;
        public bool AssetObjects;

        public bool Any => Client || Libraries.Count > 0 || AssetIndex;
    }

    public static MissingFiles ScanMissing(string gameDir, string vid, JsonObject merged)
    {
        var miss = new MissingFiles();
        if (!File.Exists(ClientJarPath(gameDir, vid, merged))) miss.Client = true;

        if (merged["libraries"] is JsonArray libs)
        {
            foreach (var node in libs)
            {
                if (node is not JsonObject lib || !RuleAllows(lib)) continue;
                var art = lib["downloads"]?["artifact"];
                if (art is null) continue;
                var path = art["path"]?.ToString() ?? MavenPath(lib["name"]?.ToString() ?? "");
                if (path.Length == 0) continue;
                if (!File.Exists(Path.Combine(gameDir, "libraries", path))) miss.Libraries.Add(lib);
            }
        }

        var indexId = merged["assetIndex"]?["id"]?.ToString();
        if (!string.IsNullOrEmpty(indexId))
        {
            var idx = Path.Combine(gameDir, "assets", "indexes", indexId + ".json");
            if (!File.Exists(idx)) miss.AssetIndex = true;
        }
        return miss;
    }

    public static async Task EnsureClientAsync(string gameDir, string vid, JsonObject merged,
        IProgress<(long, long)>? progress = null, CancellationToken ct = default)
    {
        var url = merged["downloads"]?["client"]?["url"]?.ToString();
        var sha1 = merged["downloads"]?["client"]?["sha1"]?.ToString();
        var dest = ClientJarPath(gameDir, vid, merged);
        if (string.IsNullOrEmpty(url))
            return;
        await Http.DownloadAsync(url, dest, sha1, progress, ct).ConfigureAwait(false);
    }

    public static async Task EnsureLibrariesAsync(string gameDir, JsonObject merged,
        IProgress<(long, long)>? progress = null, Action<int, int, string>? onFile = null,
        CancellationToken ct = default)
    {
        if (merged["libraries"] is not JsonArray libs) return;
        var pick = libs.OfType<JsonObject>().Where(RuleAllows).ToList();
        for (var i = 0; i < pick.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var lib = pick[i];
            var art = lib["downloads"]?["artifact"];
            var url = art?["url"]?.ToString();
            if (string.IsNullOrEmpty(url)) continue;
            var path = art!["path"]?.ToString() ?? MavenPath(lib["name"]?.ToString() ?? "");
            var dest = Path.Combine(gameDir, "libraries", path);
            onFile?.Invoke(i + 1, pick.Count, Path.GetFileName(dest));
            await Http.DownloadAsync(url, dest, art["sha1"]?.ToString(), progress, ct).ConfigureAwait(false);
        }
    }

    public static async Task EnsureAssetsAsync(string gameDir, JsonObject merged,
        IProgress<(long, long)>? progress = null, Action<int, int>? onFile = null,
        CancellationToken ct = default)
    {
        var indexId = merged["assetIndex"]?["id"]?.ToString();
        var indexUrl = merged["assetIndex"]?["url"]?.ToString();
        if (string.IsNullOrEmpty(indexId) || string.IsNullOrEmpty(indexUrl)) return;

        var idxPath = Path.Combine(gameDir, "assets", "indexes", indexId + ".json");
        await Http.DownloadAsync(indexUrl, idxPath, merged["assetIndex"]?["sha1"]?.ToString(), null, ct)
            .ConfigureAwait(false);
        if (!File.Exists(idxPath)) return;

        var index = JsonNode.Parse(await File.ReadAllTextAsync(idxPath, ct).ConfigureAwait(false));
        if (index?["objects"] is not JsonObject objects) return;

        var entries = objects.ToList();
        var done = 0;
        foreach (var kv in entries)
        {
            ct.ThrowIfCancellationRequested();
            var hash = kv.Value?["hash"]?.ToString();
            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
            if (done % 40 == 0) onFile?.Invoke(done, entries.Count);
            var dest = Path.Combine(gameDir, "assets", "objects", hash[..2], hash);
            if (File.Exists(dest))
            {
                done++;
                continue;
            }
            var ok = await Http.DownloadAsync($"{ResourceBase}/{hash[..2]}/{hash}", dest, hash, progress, ct)
                .ConfigureAwait(false);
            if (ok) done++;
        }
    }

    /// <summary>老版本（1.19 之前）的 natives 需要解压到独立目录。</summary>
    public static void ExtractNatives(string gameDir, JsonObject merged, string nativesDir)
    {
        Directory.CreateDirectory(nativesDir);
        var os = CurrentOs;
        if (merged["libraries"] is not JsonArray libs) return;
        foreach (var node in libs)
        {
            if (node is not JsonObject lib || !RuleAllows(lib)) continue;
            var key = lib["natives"]?[os]?.ToString();
            if (string.IsNullOrEmpty(key)) continue;
            var rel = lib["downloads"]?["classifiers"]?[key]?["path"]?.ToString();
            if (string.IsNullOrEmpty(rel)) continue;
            var src = Path.Combine(gameDir, "libraries", rel);
            if (!File.Exists(src)) continue;
            try
            {
                using var zip = ZipFile.OpenRead(src);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileName(entry.FullName);
                    if (name.Length == 0) continue;
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext is not (".dll" or ".so" or ".dylib" or ".jnilib")) continue;
                    entry.ExtractToFile(Path.Combine(nativesDir, name), true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>从头安装一个官方版本到 versions/ 下。</summary>
    public static async Task<string> InstallVersionAsync(string gameDir, string mcver,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new InstallProgress("读取官方版本清单…", 0));
        var manifest = await Http.GetJsonAsync(ManifestUrl, ct).ConfigureAwait(false);
        var entry = (manifest["versions"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(v => v["id"]?.ToString() == mcver);
        if (entry is null) throw new InvalidOperationException("官方清单里没有版本 " + mcver);

        var raw = (JsonObject)await Http.GetJsonAsync(entry["url"]!.ToString(), ct).ConfigureAwait(false);
        var dir = Path.Combine(gameDir, "versions", mcver);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, mcver + ".json"), raw.ToJsonString(), ct)
            .ConfigureAwait(false);

        progress?.Report(new InstallProgress("下载客户端 jar…", 0.05));
        await EnsureClientAsync(gameDir, mcver, raw, null, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("下载依赖库…", 0.2));
        await EnsureLibrariesAsync(gameDir, raw, null,
            (a, b, name) => progress?.Report(new InstallProgress($"依赖库 {a}/{b}  {name}",
                0.2 + 0.3 * a / Math.Max(1, b))), ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("下载资源文件（首次较慢，约 4000 个文件）…", 0.5));
        await EnsureAssetsAsync(gameDir, raw, null,
            (a, b) => progress?.Report(new InstallProgress($"资源 {a}/{b}",
                0.5 + 0.5 * a / Math.Max(1, b))), ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("完成", 1.0));
        return mcver;
    }
}
