using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineLib.Core;

public sealed class VersionInfo
{
    public required string Id { get; init; }
    public required string Dir { get; init; }
    public required string JsonPath { get; init; }
    public required JsonObject Raw { get; init; }
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "原版";
    public int Mods { get; set; }
    public string ReleaseTime { get; set; } = "";
    public bool HasJar { get; set; }
    public string MissingParent { get; set; } = "";

    public string Title => Id;

    public string LoaderBadge => Loader == "原版" ? "" : Loader;

    public bool HasLoaderBadge => Loader != "原版";

    public string ModsText => Mods > 0 ? Mods + " mods" : "";

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { string.IsNullOrEmpty(McVersion) ? "未知版本" : McVersion };
            if (Loader != "原版") parts.Add(Loader);
            if (Mods > 0) parts.Add(Mods + " 个模组");
            if (!string.IsNullOrEmpty(MissingParent)) parts.Add("缺少父版本 " + MissingParent);
            return string.Join(" · ", parts);
        }
    }
}

public static class Versions
{
    public static string VersionsDir(string gameDir) => Path.Combine(gameDir, "versions");

    public static List<VersionInfo> Scan(string gameDir)
    {
        var list = new List<VersionInfo>();
        var root = VersionsDir(gameDir);
        if (!Directory.Exists(root)) return list;

        // 同一条继承链会被很多版本引用，父版本 json 只读一次，别每个版本都重新解析一遍
        var parsed = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        JsonObject? GetVersion(string id)
        {
            if (parsed.TryGetValue(id, out var hit)) return hit;
            var loaded = LoadVersion(gameDir, id);
            parsed[id] = loaded;
            return loaded;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var id = Path.GetFileName(dir);
            var json = FindJson(dir, id);
            if (json is null) continue;
            JsonObject raw;
            string text;
            try
            {
                text = File.ReadAllText(json);
                raw = (JsonObject)(JsonNode.Parse(text) ?? new JsonObject());
            }
            catch (Exception)
            {
                continue;
            }
            parsed[id] = raw;

            var info = new VersionInfo { Id = id, Dir = dir, JsonPath = json, Raw = raw };
            info.Loader = DetectLoader(text);
            info.ReleaseTime = raw["releaseTime"]?.ToString() ?? raw["time"]?.ToString() ?? "";
            info.Mods = CountMods(gameDir, id);
            info.HasJar = File.Exists(Path.Combine(dir, id + ".jar"))
                          || File.Exists(Path.Combine(dir, (raw["id"]?.ToString() ?? id) + ".jar"));

            var cur = raw;
            var guard = 0;
            while (cur["inheritsFrom"] is JsonValue pv && guard++ < 12)
            {
                var parentId = pv.ToString();
                var parent = GetVersion(parentId);
                if (parent is null)
                {
                    info.MissingParent = parentId;
                    info.McVersion = parentId;
                    break;
                }
                cur = parent;
            }
            if (string.IsNullOrEmpty(info.McVersion))
                info.McVersion = cur["id"]?.ToString() ?? id;

            list.Add(info);
        }

        return list.OrderByDescending(v => v.ReleaseTime, StringComparer.Ordinal).ToList();
    }

    /// <summary>从版本 json 原文识别加载器。以前是反序列化后再 ToJsonString + ToLowerInvariant，
    /// 每扫一个版本都要多拷两份几 MB 的字符串，版本一多扫描就很慢。</summary>
    public static string DetectLoader(string rawJson)
    {
        if (rawJson.Contains("fabricmc", StringComparison.OrdinalIgnoreCase)) return "Fabric";
        if (rawJson.Contains("neoforged", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
        if (rawJson.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase)) return "Forge";
        if (rawJson.Contains("optifine", StringComparison.OrdinalIgnoreCase)) return "OptiFine";
        return "原版";
    }

    public static string DetectLoader(JsonObject raw) => DetectLoader(raw.ToJsonString());

    public static JsonObject? LoadVersion(string gameDir, string id)
    {
        var dir = Path.Combine(VersionsDir(gameDir), id);
        if (!Directory.Exists(dir)) return null;
        var json = FindJson(dir, id);
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(json)) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindJson(string dir, string id)
    {
        var exact = Path.Combine(dir, id + ".json");
        if (File.Exists(exact)) return exact;
        var any = Directory.GetFiles(dir, "*.json").FirstOrDefault();
        return any;
    }

    private static int CountMods(string gameDir, string id)
    {
        foreach (var d in new[]
                 {
                     Path.Combine(VersionsDir(gameDir), id, "mods"),
                     Path.Combine(gameDir, "mods")
                 })
        {
            if (Directory.Exists(d))
                return Directory.EnumerateFiles(d, "*.jar").Count();
        }
        return 0;
    }

    /// <summary>沿继承链合并成一份可启动的版本 json。</summary>
    public static JsonObject Resolve(string gameDir, string id)
    {
        var raw = LoadVersion(gameDir, id) ?? throw new FileNotFoundException("找不到版本 " + id);
        var chain = new List<JsonObject> { raw };
        var cur = raw;
        var guard = 0;
        while (cur["inheritsFrom"] is JsonValue pv && guard++ < 12)
        {
            var parent = LoadVersion(gameDir, pv.ToString());
            if (parent is null) break;
            chain.Add(parent);
            cur = parent;
        }

        var merged = (JsonObject)chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--)
            merged = Merge(merged, chain[i]);
        return merged;
    }

    private static string LibKey(JsonObject lib)
    {
        var name = lib["name"]?.ToString() ?? "";
        var parts = name.Split(':');
        return parts.Length >= 2 ? parts[0] + ":" + parts[1] : name;
    }

    private static JsonObject Merge(JsonObject baseObj, JsonObject layer)
    {
        var result = (JsonObject)baseObj.DeepClone();

        var libs = new JsonArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        if (result["libraries"] is JsonArray bl)
        {
            foreach (var node in bl)
            {
                var clone = node?.DeepClone();
                if (clone is not JsonObject obj) continue;
                index[LibKey(obj)] = libs.Count;
                libs.Add(clone);
            }
        }
        if (layer["libraries"] is JsonArray ll)
        {
            foreach (var node in ll)
            {
                if (node?.DeepClone() is not JsonObject obj) continue;
                var key = LibKey(obj);
                if (index.TryGetValue(key, out var at)) libs[at] = obj;
                else
                {
                    index[key] = libs.Count;
                    libs.Add(obj);
                }
            }
        }
        result["libraries"] = libs;

        if (result["arguments"] is not null || layer["arguments"] is not null)
        {
            var args = new JsonObject();
            foreach (var section in new[] { "jvm", "game" })
            {
                var arr = new JsonArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var src in new[] { result["arguments"] as JsonObject, layer["arguments"] as JsonObject })
                {
                    if (src?[section] is not JsonArray a) continue;
                    foreach (var item in a)
                    {
                        var clone = item?.DeepClone();
                        var sig = clone?.ToJsonString() ?? "";
                        if (seen.Add(sig)) arr.Add(clone);
                    }
                }
                args[section] = arr;
            }
            result["arguments"] = args;
        }

        foreach (var key in new[]
                 {
                     "mainClass", "assetIndex", "assets", "downloads", "javaVersion", "type", "logging",
                     "complianceLevel", "minimumLauncherVersion", "releaseTime", "time",
                     "minecraftArguments", "id"
                 })
        {
            if (layer[key] is not null) result[key] = layer[key]!.DeepClone();
        }

        return result;
    }
}
