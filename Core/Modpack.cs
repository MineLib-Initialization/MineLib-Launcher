using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace MineLib.Core;

/// <summary>
/// Modrinth 整合包（.mrpack）：拖进启动器就按包里的内容装一个客户端。
/// 包里是 `modrinth.index.json`（依赖 + 要下载的文件）+ `overrides/`（要解压的散文件）。
/// </summary>
public static class Modpack
{
    /// <summary>整合包索引在 zip 里的固定名字。</summary>
    public const string IndexName = "modrinth.index.json";

    public const string Extension = ".mrpack";

    /// <summary>一个要下载的文件。</summary>
    public sealed record PackFile(string Path, string Url, string? AltUrl, string? Sha1, long Size)
    {
        public string Display => Path.Replace('\\', '/');
    }

    /// <summary>解析出来的整合包信息。</summary>
    public sealed record PackInfo(string Name, string Summary, string McVersion, string LoaderLabel,
        string LoaderVersion, List<PackFile> Files, long TotalBytes)
    {
        public int FileCount => Files.Count;

        public string SizeText => TotalBytes >= 1048576
            ? (TotalBytes / 1048576.0).ToString("0.0") + " MB"
            : Math.Max(1, TotalBytes / 1024) + " KB";

        public string Detail => "Minecraft " + McVersion +
                                (string.IsNullOrEmpty(LoaderLabel) ? " · 原版" : " · " + LoaderLabel +
                                    (string.IsNullOrEmpty(LoaderVersion) ? "" : " " + LoaderVersion)) +
                                " · " + FileCount + " 个文件 · " + SizeText;
    }

    public sealed record InstallResult(string ClientName, string VersionId, PackInfo Info);

    public static bool LooksLikeModpack(string path)
    {
        try { return string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>从候选路径里挑出第一个 .mrpack（拖进来可能是一堆文件）。</summary>
    public static string? FirstMrpack(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (LooksLikeModpack(p) && File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>只读解析：拿依赖、文件清单、名字。不落盘、不联网。</summary>
    public static PackInfo Read(string mrpackPath)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, IndexName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidDataException("这个文件里没有 " + IndexName + "，不是 Modrinth 整合包");

        JsonObject? index;
        using (var stream = entry.Open())
        using (var reader = new StreamReader(stream))
        {
            index = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        }
        if (index is null) throw new InvalidDataException("整合包索引解析失败");

        var game = index["game"]?.ToString();
        if (!string.IsNullOrEmpty(game) && !string.Equals(game, "minecraft", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("这个整合包不是给 Minecraft 用的：" + game);

        var deps = index["dependencies"] as JsonObject ?? new JsonObject();
        var mc = deps["minecraft"]?.ToString() ?? "";
        var (loaderLabel, loaderVersion) = PickLoader(deps);
        if (string.IsNullOrEmpty(mc)) throw new InvalidDataException("整合包里没写 Minecraft 版本");

        var files = new List<PackFile>();
        if (index["files"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject f) continue;
                var rel = SafeRelativePath(f["path"]?.ToString());
                if (rel is null) continue;                       // 越界或空的路径：直接不要

                // env.client == "unsupported" 表示这个文件只给服务端用
                var env = f["env"] as JsonObject;
                var side = env?["client"]?.ToString();
                if (string.Equals(side, "unsupported", StringComparison.OrdinalIgnoreCase)) continue;

                var downloads = f["downloads"] as JsonArray;
                var url = downloads?.FirstOrDefault()?.ToString();
                if (string.IsNullOrEmpty(url)) continue;
                var alt = downloads?.Skip(1).FirstOrDefault()?.ToString();
                var sha1 = (f["hashes"] as JsonObject)?["sha1"]?.ToString();
                var size = f["fileSize"]?.GetValue<long>() ?? 0;
                files.Add(new PackFile(rel, url!, alt, string.IsNullOrEmpty(sha1) ? null : sha1, size));
            }
        }

        var name = index["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(mrpackPath);
        var summary = index["summary"]?.ToString() ?? "";
        var total = files.Sum(f => Math.Max(0, f.Size));
        return new PackInfo(name!.Trim(), summary, mc, loaderLabel, loaderVersion, files, total);
    }

    private static (string label, string version) PickLoader(JsonObject deps)
    {
        if (deps["fabric-loader"]?.ToString() is { Length: > 0 } fabric) return ("Fabric", fabric);
        if (deps["quilt-loader"]?.ToString() is { Length: > 0 } quilt) return ("Quilt", quilt);
        if (deps["neoforge"]?.ToString() is { Length: > 0 } neo) return ("NeoForge", neo);
        if (deps["forge"]?.ToString() is { Length: > 0 } forge) return ("Forge", forge);
        return ("", "");
    }

    /// <summary>
    /// 校验并归一化整合包里的相对路径：不允许绝对路径、盘符、`..`，
    /// 也统一成 Windows 分隔符。
    /// </summary>
    internal static string? SafeRelativePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var path = raw!.Replace('/', '\\').Trim();
        if (path.Length == 0) return null;
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) return null;   // UNC
        if (Path.IsPathRooted(path)) return null;
        if (path.Length >= 2 && path[1] == ':') return null;                       // C:xxx
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        foreach (var part in parts)
        {
            if (part == "." || part == "..") return null;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        }
        return string.Join('\\', parts);
    }

    /// <summary>
    /// 完整安装：建客户端（游戏版本 + 加载器随包）→ 下 `files` → 解压 overrides → 登记客户端。
    /// </summary>
    public static async Task<InstallResult> InstallAsync(AppConfig cfg, string mrpackPath, int threads,
        string javaExe, IProgress<InstallProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var info = Read(mrpackPath);

        // 客户端名字用包名；重名就加序号，避免覆盖已有客户端
        var baseName = SafeClientName(info.Name);
        var name = baseName;
        for (var i = 2; cfg.FindClient(name) is not null; i++)
        {
            if (i > 99) throw new InvalidOperationException("同名客户端太多，先改个名再装");
            name = baseName + " " + i;
        }

        progress?.Report(new InstallProgress("安装 " + info.Name + "：Minecraft " + info.McVersion + "…", 0));
        var made = await ClientCreator.CreateAsync(cfg, name, info.McVersion, info.LoaderLabel, threads,
            javaExe, progress, log, ct, info.LoaderVersion).ConfigureAwait(false);

        var clientDir = Path.Combine(cfg.GameDir, "versions", made.VersionId);
        if (!Directory.Exists(clientDir))
            throw new InvalidOperationException("客户端目录没建出来：" + clientDir);

        // 1) 下载 files[]
        var tasks = new List<Http.DownloadTask>(info.Files.Count);
        foreach (var f in info.Files)
        {
            var dest = Path.GetFullPath(Path.Combine(clientDir, f.Path));
            if (!InsideDir(dest, clientDir)) continue;          // 双保险：消毒后仍要确认落点在客户端目录里
            tasks.Add(new Http.DownloadTask(f.Url, dest, f.Sha1, f.Size, f.AltUrl));
        }
        if (tasks.Count > 0)
        {
            progress?.Report(new InstallProgress("下载整合包文件 0/" + tasks.Count + "…", 0.9));
            var done = 0;
            var wrap = new Progress<Http.BulkProgress>(p =>
            {
                done = p.FilesDone;
                progress?.Report(new InstallProgress(
                    "下载整合包文件 " + done + "/" + tasks.Count + " · " + p.SpeedText,
                    0.9 + 0.09 * (tasks.Count == 0 ? 1 : (double)done / tasks.Count)));
            });
            var (ok, failed) = await Http.DownloadManyAsync(tasks, threads, wrap, ct).ConfigureAwait(false);
            if (failed > 0)
                throw new InvalidOperationException("整合包里有 " + failed + " 个文件没下下来（成功 " + ok +
                                                    "/" + tasks.Count + "）");
        }

        // 2) 解压 overrides / client-overrides
        progress?.Report(new InstallProgress("解压 overrides…", 0.99));
        var extracted = ExtractOverrides(mrpackPath, clientDir, log);

        progress?.Report(new InstallProgress("完成", 1.0));
        log?.Invoke("整合包安装完成：" + name + "（文件 " + tasks.Count + " 个，overrides " + extracted + " 个）");
        return new InstallResult(name, made.VersionId, info);
    }

    /// <summary>overrides 与 client-overrides 都解压到客户端目录；server-overrides 不要。</summary>
    internal static int ExtractOverrides(string mrpackPath, string clientDir, Action<string>? log)
    {
        var count = 0;
        using var zip = ZipFile.OpenRead(mrpackPath);
        foreach (var prefix in new[] { "overrides/", "client-overrides/" })
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = SafeRelativePath(entry.FullName[prefix.Length..]);
                if (rel is null) continue;
                var dest = Path.GetFullPath(Path.Combine(clientDir, rel));
                if (!InsideDir(dest, clientDir)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, true);
                    count++;
                }
                catch (Exception ex)
                {
                    log?.Invoke("解压 " + rel + " 失败：" + ex.Message);
                }
            }
        }
        return count;
    }

    private static string SafeClientName(string raw)
    {
        var name = new string((raw ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (name.Length == 0) name = "整合包客户端";
        if (name.Length > 60) name = name[..60].Trim();
        return name;
    }

    private static bool InsideDir(string full, string dir)
    {
        var root = Path.GetFullPath(dir).TrimEnd('\\') + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
