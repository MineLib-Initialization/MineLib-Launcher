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

        JsonNode node;
        try
        {
            node = await Http.GetJsonAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new List<ModFile>();
        }
        if (node is not JsonArray arr) return new List<ModFile>();

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

    public static async Task<bool> DownloadAsync(ModFile file, string modsDir,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(modsDir);
        return await Http.DownloadAsync(file.Url, Path.Combine(modsDir, file.Name), null, progress, ct)
            .ConfigureAwait(false);
    }
}
