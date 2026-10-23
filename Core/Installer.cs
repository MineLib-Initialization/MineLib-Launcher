using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace MineLib.Core;

public sealed record InstallProgress(string Stage, double Fraction);

/// <summary>官方源装配：客户端 / libraries / assets 的检查与下载。全部走 Mojang 官方 CDN。</summary>
public static class Installer
{
    /// <summary>清单地址跟随设置里选的下载源。</summary>
    public static string ManifestUrl => DownloadSources.Current.ManifestUrl;

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

    /// <summary>
    /// 库的真实下载地址。官方 vanilla 库带 downloads.artifact.url；
    /// Fabric / Forge / NeoForge 的库只有 url（maven 根）+ name，必须自己拼。
    /// </summary>
    public static string? LibUrl(JsonObject lib)
    {
        var path = LibPath(lib);
        if (string.IsNullOrEmpty(path)) return null;

        var artUrl = lib["downloads"]?["artifact"]?["url"]?.ToString();
        var mavenRoot = lib["url"]?.ToString();
        var resolved = !string.IsNullOrWhiteSpace(artUrl)
            ? artUrl!
            : string.IsNullOrWhiteSpace(mavenRoot)
                ? null
                : mavenRoot!.TrimEnd('/') + "/" + path.Replace('\\', '/');

        // 交给当前下载源决定最终地址（镜像只覆盖 Minecraft 自己的库，其它私库原样返回）
        return DownloadSources.Current.Library(resolved, path);
    }

    /// <summary>natives 的 classifier 键（natives-windows-${arch} 这类占位符要替换）。</summary>
    public static string? NativeKey(JsonObject lib)
    {
        if (lib["natives"] is not JsonObject natives) return null;
        var key = natives[CurrentOs]?.ToString();
        if (string.IsNullOrEmpty(key)) return null;
        return key.Replace("$" + "{arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
    }

    /// <summary>一个库需要下载的全部文件：主 jar + 当前系统的 natives jar。</summary>
    public static List<Http.DownloadTask> LibArtifacts(JsonObject lib, string gameDir)
    {
        var list = new List<Http.DownloadTask>();

        var url = LibUrl(lib);
        var path = LibPath(lib);
        if (url is not null && path.Length > 0)
        {
            var art = lib["downloads"]?["artifact"];
            list.Add(new Http.DownloadTask(url, Path.Combine(gameDir, "libraries", path),
                art?["sha1"]?.ToString(), art?["size"]?.GetValue<long>() ?? 0));
        }

        var key = NativeKey(lib);
        if (key is not null && lib["downloads"]?["classifiers"]?[key] is JsonObject cls)
        {
            var clsPath = cls["path"]?.ToString();
            var clsUrl = cls["url"]?.ToString();
            if (!string.IsNullOrEmpty(clsPath) && !string.IsNullOrEmpty(clsUrl))
                list.Add(new Http.DownloadTask(clsUrl, Path.Combine(gameDir, "libraries", clsPath),
                    cls["sha1"]?.ToString(), cls["size"]?.GetValue<long>() ?? 0));
        }
        return list;
    }

    /// <summary>沿继承链找客户端 jar：继承版本的 jar 在父版本目录里。</summary>
    public static string? FindClientJar(string gameDir, string vid)
    {
        var cur = vid;
        var guard = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrEmpty(cur) && guard++ < 12 && seen.Add(cur))
        {
            var dir = Path.Combine(gameDir, "versions", cur);
            foreach (var cand in new[] { Path.Combine(dir, cur + ".jar") })
                if (File.Exists(cand)) return cand;
            cur = Versions.LoadVersion(gameDir, cur)?["inheritsFrom"]?.ToString() ?? "";
        }
        return null;
    }

    /// <summary>下载客户端 jar 该落到哪：优先沿用已有的，否则落到最底层原版版本的目录。</summary>
    public static string ClientJarDest(string gameDir, string vid)
    {
        var existing = FindClientJar(gameDir, vid);
        if (existing is not null) return existing;
        var cur = vid;
        var guard = 0;
        var rootId = vid;
        while (!string.IsNullOrEmpty(cur) && guard++ < 12)
        {
            rootId = cur;
            var parent = Versions.LoadVersion(gameDir, cur)?["inheritsFrom"]?.ToString();
            if (string.IsNullOrEmpty(parent)) break;
            cur = parent;
        }
        return Path.Combine(gameDir, "versions", rootId, rootId + ".jar");
    }

    public static string ClientJarPath(string gameDir, string vid, JsonObject merged)
    {
        return FindClientJar(gameDir, vid) ?? ClientJarDest(gameDir, vid);
    }

    public sealed class MissingFiles
    {
        public bool Client;
        public List<JsonObject> Libraries = new();
        public bool AssetIndex;
        public int AssetObjectsMissing;

        public bool Any => Client || Libraries.Count > 0 || AssetIndex || AssetObjectsMissing > 0;
    }

    /// <summary>资源对象有没有缺的（索引存在但文件被删/下坏的情况）。</summary>
    public static int CountMissingAssets(string gameDir, JsonObject merged, int stopAfter = 50)
    {
        try
        {
            var indexId = merged["assetIndex"]?["id"]?.ToString();
            if (string.IsNullOrEmpty(indexId)) return 0;
            var idxPath = Path.Combine(gameDir, "assets", "indexes", indexId + ".json");
            if (!File.Exists(idxPath)) return 0;   // 索引都没有：交给 AssetIndex 那条分支去下
            var index = JsonNode.Parse(File.ReadAllText(idxPath));
            if (index?["objects"] is not JsonObject objects) return 0;
            var missing = 0;
            foreach (var kv in objects)
            {
                var hash = kv.Value?["hash"]?.ToString();
                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                var dest = Path.Combine(gameDir, "assets", "objects", hash[..2], hash);
                if (!File.Exists(dest) && ++missing >= stopAfter) break;
            }
            return missing;
        }
        catch (Exception)
        {
            return 0;
        }
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
                var artifacts = LibArtifacts(lib, gameDir);
                if (artifacts.Count == 0) continue;
                if (!artifacts.All(a => File.Exists(a.Dest))) miss.Libraries.Add(lib);
            }
        }

        var indexId = merged["assetIndex"]?["id"]?.ToString();
        if (!string.IsNullOrEmpty(indexId))
        {
            var idx = Path.Combine(gameDir, "assets", "indexes", indexId + ".json");
            if (!File.Exists(idx)) miss.AssetIndex = true;
            else miss.AssetObjectsMissing = CountMissingAssets(gameDir, merged);
        }
        return miss;
    }

    public static async Task EnsureClientAsync(string gameDir, string vid, JsonObject merged,
        IProgress<(long, long)>? progress = null, CancellationToken ct = default)
    {
        var url = merged["downloads"]?["client"]?["url"]?.ToString();
        var sha1 = merged["downloads"]?["client"]?["sha1"]?.ToString();
        if (string.IsNullOrEmpty(url)) return;
        var dest = ClientJarDest(gameDir, vid);
        // 镜像按「版本 id」给客户端 jar，版本 id 要用继承链最底下那个原版版本
        var mirror = DownloadSources.Current.ClientJar;
        if (mirror is not null)
        {
            var rootId = RootVersionId(gameDir, vid);
            var alt = mirror(rootId);
            if (await Http.DownloadAsync(alt, dest, sha1, progress, ct).ConfigureAwait(false)) return;
        }
        if (!await Http.DownloadAsync(url, dest, sha1, progress, ct).ConfigureAwait(false))
            throw new InvalidOperationException("客户端 jar 下载失败：" + url);
    }

    /// <summary>沿着 inheritsFrom 走到底，拿到原版版本 id。</summary>
    public static string RootVersionId(string gameDir, string vid)
    {
        var cur = vid;
        var guard = 0;
        while (!string.IsNullOrEmpty(cur) && guard++ < 12)
        {
            var parent = Versions.LoadVersion(gameDir, cur)?["inheritsFrom"]?.ToString();
            if (string.IsNullOrEmpty(parent)) return cur;
            cur = parent;
        }
        return cur;
    }

    public static async Task EnsureLibrariesAsync(string gameDir, JsonObject merged, int threads,
        Action<Http.BulkProgress>? onProgress, CancellationToken ct)
    {
        var tasks = new List<Http.DownloadTask>();
        if (merged["libraries"] is JsonArray libs)
        {
            foreach (var node in libs)
            {
                if (node is not JsonObject lib || !RuleAllows(lib)) continue;
                tasks.AddRange(LibArtifacts(lib, gameDir));
            }
        }
        var (ok, failed) = await Http.DownloadManyAsync(tasks, threads, Wrap(onProgress), ct)
            .ConfigureAwait(false);
        if (failed > 0)
            throw new InvalidOperationException("依赖库有 " + failed + " 个文件下载失败（成功 " + ok +
                                                " / 共 " + tasks.Count + "），请重试或换下载源");
    }

    public static async Task EnsureAssetsAsync(string gameDir, JsonObject merged, int threads,
        Action<Http.BulkProgress>? onProgress, CancellationToken ct)
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

        var tasks = new List<Http.DownloadTask>(objects.Count);
        foreach (var kv in objects)
        {
            var hash = kv.Value?["hash"]?.ToString();
            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
            tasks.Add(new Http.DownloadTask(DownloadSources.Current.AssetObject(hash),
                Path.Combine(gameDir, "assets", "objects", hash[..2], hash), hash,
                kv.Value?["size"]?.GetValue<long>() ?? 0));
        }
        var (ok, failed) = await Http.DownloadManyAsync(tasks, threads, Wrap(onProgress), ct)
            .ConfigureAwait(false);
        if (failed > 0)
            throw new InvalidOperationException("资源文件有 " + failed + " 个下载失败（成功 " + ok +
                                                " / 共 " + tasks.Count + "），请重试或换下载源");
    }

    /// <summary>
    /// 1.6 及更早的版本直接用 assets/virtual/legacy 下的散文件，而不是按 hash 取。
    /// 之前这里从来没被填充过，所以老版本会没有声音和语言文件。
    /// </summary>
    public static async Task EnsureVirtualAssetsAsync(string gameDir, JsonObject merged,
        Action<string>? log, CancellationToken ct)
    {
        try
        {
            var indexId = merged["assetIndex"]?["id"]?.ToString();
            if (string.IsNullOrEmpty(indexId)) return;
            var idxPath = Path.Combine(gameDir, "assets", "indexes", indexId + ".json");
            if (!File.Exists(idxPath)) return;

            var index = JsonNode.Parse(await File.ReadAllTextAsync(idxPath, ct).ConfigureAwait(false));
            var isVirtual = index?["virtual"]?.GetValue<bool>() == true;
            if (!isVirtual) return;
            if (index?["objects"] is not JsonObject objects) return;

            var virtualRoot = Path.Combine(gameDir, "assets", "virtual", "legacy");
            Directory.CreateDirectory(virtualRoot);
            var copied = 0;
            foreach (var kv in objects)
            {
                ct.ThrowIfCancellationRequested();
                var hash = kv.Value?["hash"]?.ToString();
                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                var src = Path.Combine(gameDir, "assets", "objects", hash[..2], hash);
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(virtualRoot, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dst)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, true);
                    copied++;
                }
                catch (Exception) { }
            }
            if (copied > 0) log?.Invoke("已为老版本铺开 " + copied + " 个资源文件到 assets/virtual/legacy");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { }
    }

    private static IProgress<Http.BulkProgress>? Wrap(Action<Http.BulkProgress>? onProgress) =>
        onProgress is null ? null : new Progress<Http.BulkProgress>(onProgress);

    /// <summary>老版本（1.19 之前）的 natives 需要解压到独立目录。</summary>
    public static void ExtractNatives(string gameDir, JsonObject merged, string nativesDir)
    {
        Directory.CreateDirectory(nativesDir);
        var os = CurrentOs;
        if (merged["libraries"] is not JsonArray libs) return;
        foreach (var node in libs)
        {
            if (node is not JsonObject lib || !RuleAllows(lib)) continue;
            var key = NativeKey(lib);
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
    public static async Task<string> InstallVersionAsync(string gameDir, string mcver, int threads = 8,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new InstallProgress("读取官方版本清单…", 0));
        var manifest = await Http.GetJsonAsync(ManifestUrl, ct).ConfigureAwait(false);
        var entry = (manifest["versions"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(v => v["id"]?.ToString() == mcver);
        if (entry is null) throw new InvalidOperationException("官方清单里没有版本 " + mcver);

        var vjson = DownloadSources.Current.VersionJson;
        var raw = (JsonObject)await Http.GetJsonAsync(
                vjson is null ? entry["url"]!.ToString() : vjson(mcver), ct).ConfigureAwait(false);
        var dir = Path.Combine(gameDir, "versions", mcver);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, mcver + ".json"), raw.ToJsonString(), ct)
            .ConfigureAwait(false);

        // 客户端 jar 和依赖库同时开跑：jar 是单个大文件（分段并发），库是一堆小文件，
        // 串行会有一头一直在等
        progress?.Report(new InstallProgress("下载客户端与依赖库…", 0.02));
        await Task.WhenAll(
            EnsureClientAsync(gameDir, mcver, raw, null, ct),
            EnsureLibrariesAsync(gameDir, raw, threads, p =>
                progress?.Report(new InstallProgress(
                    $"依赖库 {p.FilesDone}/{p.FilesTotal} · {p.SpeedText}",
                    0.05 + 0.35 * p.FilesDone / Math.Max(1, p.FilesTotal))), ct)).ConfigureAwait(false);

        await EnsureAssetsAsync(gameDir, raw, threads, p =>
            progress?.Report(new InstallProgress(
                $"资源 {p.FilesDone}/{p.FilesTotal} · {p.SpeedText}",
                0.4 + 0.6 * p.FilesDone / Math.Max(1, p.FilesTotal))), ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("完成", 1.0));
        return mcver;
    }
}