using System.IO;
using System.Text.Json.Nodes;

namespace MineLib.Core;

public sealed class ModHit
{
    public string ProjectId { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string License { get; init; } = "";
    public long Downloads { get; init; }
    public string Categories { get; init; } = "";

    public string DownloadsText => Downloads >= 100_000_000
        ? (Downloads / 100_000_000.0).ToString("0.#") + " 亿"
        : Downloads >= 10000
            ? (Downloads / 10000.0).ToString("0.#") + " 万"
            : Downloads.ToString();

    public string Meta => string.Join(" · ", new[] { License, Categories }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>图标：按 URL 缓存已解码的缩略图（列表翻页时不再重复请求/解码大图）。</summary>
    public System.Windows.Media.ImageSource? Icon => ModIconCache.Get(IconUrl);
}

public sealed class ModFile
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string VersionNumber { get; init; } = "";
    public string VersionType { get; init; } = "";
    public string GameVersions { get; init; } = "";
    public long Size { get; init; }

    public string Display => VersionNumber +
                             (string.IsNullOrEmpty(VersionType) || VersionType == "release" ? "" : " (" + VersionType + ")") +
                             "   " + (Size / 1024 / 1024.0).ToString("0.0") + " MB";
}

/// <summary>模组图标缓存：只解码到 64px，最多存 400 张，避免列表滚动时反复下载解码。</summary>
internal static class ModIconCache
{
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();
    private const int MaxEntries = 400;

    public static System.Windows.Media.ImageSource? Get(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        lock (Lock)
        {
            if (Cache.TryGetValue(url, out var hit)) return hit;
        }

        System.Windows.Media.ImageSource? img = null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.DecodePixelWidth = 64;                       // 只要缩略图，别解码原图
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();                                    // 冻结后可跨线程复用
            img = bmp;
        }
        catch (Exception)
        {
            img = null;
        }

        lock (Lock)
        {
            if (Cache.Count >= MaxEntries) Cache.Clear();     // 简单粗暴的容量控制
            Cache[url] = img;
        }
        return img;
    }
}

/// <summary>模组检索与下载。数据来自 Modrinth 官方 API，文件走它的 CDN，不做镜像。</summary>
public static class Modrinth
{
    private const string Api = "https://api.modrinth.com/v2";

    public static readonly (string Key, string Label)[] Sorts =
    {
        ("relevance", "相关度"),
        ("downloads", "下载量"),
        ("follows", "关注数"),
        ("newest", "最新")
    };

    public static readonly (string Key, string Label)[] LoaderFilters =
    {
        ("", "全部加载器"),
        ("fabric", "Fabric"),
        ("quilt", "Quilt"),
        ("forge", "Forge"),
        ("neoforge", "NeoForge")
    };

    /// <summary>把常见的 HTTP 错误翻成人话（限流、被墙、项目不存在）。</summary>
    public static string FriendlyHttpError(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (msg.Contains("429")) return "请求太频繁，被 Modrinth 限流了，等几秒再试。";
        if (msg.Contains("404")) return "Modrinth 上没有这个项目（可能已下架）。";
        if (msg.Contains("403")) return "Modrinth 拒绝了这次请求（403）。";
        var code = System.Text.RegularExpressions.Regex.Match(msg, @"HTTP (\d{3})");
        if (code.Success) return "Modrinth 返回 " + code.Groups[1].Value + "：" + Http.Trim(msg);
        return Http.Trim(msg);
    }

    public static async Task<(List<ModHit> hits, long total)> SearchAsync(string query, string? mc,
        string? loader, string index, int offset, int limit, CancellationToken ct)
    {
        var facets = new JsonArray { new JsonArray("project_type:mod") };
        if (!string.IsNullOrWhiteSpace(loader)) facets.Add(new JsonArray("categories:" + loader));
        if (!string.IsNullOrWhiteSpace(mc)) facets.Add(new JsonArray("versions:" + mc.Trim()));

        var url = Api + "/search?limit=" + limit + "&offset=" + offset +
                  "&index=" + Uri.EscapeDataString(string.IsNullOrEmpty(index) ? "relevance" : index) +
                  "&query=" + Uri.EscapeDataString(query ?? "") +
                  "&facets=" + Uri.EscapeDataString(facets.ToJsonString());

        var node = await Http.GetJsonAsync(url, ct).ConfigureAwait(false);
        var hits = new List<ModHit>();
        if (node["hits"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject h) continue;
                hits.Add(new ModHit
                {
                    ProjectId = h["project_id"]?.ToString() ?? "",
                    Slug = h["slug"]?.ToString() ?? "",
                    Title = h["title"]?.ToString() ?? "",
                    Description = h["description"]?.ToString() ?? "",
                    IconUrl = h["icon_url"]?.ToString() ?? "",
                    License = h["license"]?.ToString() ?? "",
                    Downloads = h["downloads"]?.GetValue<long>() ?? 0,
                    Categories = string.Join(" ", (h["display_categories"] as JsonArray ?? h["categories"] as JsonArray ?? new JsonArray())
                        .Take(3).Select(c => c?.ToString() ?? ""))
                });
            }
        }
        return (hits, node["total_hits"]?.GetValue<long>() ?? hits.Count);
    }

    /// <summary>取某个项目匹配版本的文件列表，按发布时间倒序。</summary>
    public static async Task<List<ModFile>> FilesAsync(string projectId, string? mc, string? loader,
        CancellationToken ct)
    {
        var url = Api + "/project/" + Uri.EscapeDataString(projectId) + "/version";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(mc)) query.Add("game_versions=" + Uri.EscapeDataString("[\"" + mc.Trim() + "\"]"));
        if (!string.IsNullOrWhiteSpace(loader) && loader != "liteloader")
            query.Add("loaders=" + Uri.EscapeDataString("[\"" + loader + "\"]"));
        if (query.Count > 0) url += "?" + string.Join("&", query);

        // 以前这里吞掉异常，网络/限流都会显示成"没有匹配的文件"，误导性很强
        JsonNode node;
        try
        {
            node = await Http.GetJsonAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("读取文件列表失败：" + FriendlyHttpError(ex), ex);
        }
        if (node is not JsonArray arr) throw new InvalidOperationException("Modrinth 返回的数据格式不对");

        var list = new List<ModFile>();
        foreach (var n in arr)
        {
            if (n is not JsonObject v) continue;
            var file = (v["files"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            if (file is null) continue;
            list.Add(new ModFile
            {
                Name = file["filename"]?.ToString() ?? "",
                Url = file["url"]?.ToString() ?? "",
                Size = file["size"]?.GetValue<long>() ?? 0,
                VersionNumber = v["version_number"]?.ToString() ?? "",
                VersionType = v["version_type"]?.ToString() ?? "",
                GameVersions = string.Join(" ", (v["game_versions"] as JsonArray ?? new JsonArray())
                    .Take(4).Select(g => g?.ToString() ?? ""))
            });
        }
        return list;
    }

    /// <summary>把服务器给的文件名消毒，绝不允许它跳出 mods 目录。</summary>
    public static string SafeFileName(string raw)
    {
        var name = Path.GetFileName(raw ?? "");
        if (string.IsNullOrWhiteSpace(name)) name = "mod.jar";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim('.', ' ');
        if (name.Length == 0) name = "mod.jar";
        if (name.Length > 150) name = name[^150..];
        return name;
    }

    public static async Task<(bool ok, string path, bool skipped)> DownloadAsync(ModFile file, string modsDir,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(modsDir);
        var safe = SafeFileName(file.Name);
        var dest = Path.GetFullPath(Path.Combine(modsDir, safe));
        // 双保险：消毒后仍要确认落点在 mods 目录内
        if (!dest.StartsWith(Path.GetFullPath(modsDir).TrimEnd('\\') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("文件名不合法：" + file.Name);

        if (File.Exists(dest) && file.Size > 0 && new FileInfo(dest).Length == file.Size)
            return (true, dest, true);   // 已经下过同一份，直接跳过

        var ok = await Http.DownloadAsync(file.Url, dest, null, progress, ct).ConfigureAwait(false);
        return (ok, dest, false);
    }
}
