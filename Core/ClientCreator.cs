using System.IO;

namespace MineLib.Core;

/// <summary>
/// 新建客户端：往游戏根目录装一个版本（可选加载器），装完登记成客户端。
/// 所有客户端共用一个根目录，libraries/assets 只下一份。
/// </summary>
public static class ClientCreator
{
    public sealed record Result(string Name, string VersionId, string McVersion, string Loader, string Note);

    public static string? Validate(AppConfig cfg, string name, out string id)
    {
        id = "";
        if (string.IsNullOrWhiteSpace(name)) return "给客户端起个名字。";
        var safe = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars())).Trim();
        if (safe.Length == 0) return "这个名字不能用作文件夹名。";
        id = safe;
        if (cfg.FindClient(safe) is not null) return "已经有叫「" + safe + "」的客户端了，换个名字。";
        return null;
    }

    public static LoaderKind? ParseLoader(string label) => label switch
    {
        "Fabric" => LoaderKind.Fabric,
        "Quilt" => LoaderKind.Quilt,
        "Forge" => LoaderKind.Forge,
        "NeoForge" => LoaderKind.NeoForge,
        _ => null
    };

    /// <summary>
    /// 装进 cfg.GameDir。加载器版本会写成 versions/&lt;名字&gt;，原版基础版本单独放一个目录且不登记成客户端。
    /// </summary>
    public static async Task<Result> CreateAsync(AppConfig cfg, string name, string mcver,
        string loaderLabel, int threads, string javaExe, IProgress<InstallProgress>? progress,
        Action<string>? log, CancellationToken ct, string loaderVersion = "")
    {
        var error = Validate(cfg, name, out var safeName);
        if (error is not null) throw new InvalidOperationException(error);

        var kind = ParseLoader(loaderLabel ?? "");
        var root = cfg.GameDir;
        Directory.CreateDirectory(root);

        // 原版基础版本：已有就跳过，避免重复下载；下载依然落在共用根目录
        progress?.Report(new InstallProgress("安装 Minecraft " + mcver + "…", 0));
        await Installer.InstallVersionAsync(root, mcver, threads, progress, ct).ConfigureAwait(false);

        var versionId = mcver;
        var loaderLabelText = "原版";
        if (kind is not null)
        {
            if (Loaders.NeedsJava(kind.Value) && string.IsNullOrEmpty(javaExe))
                throw new InvalidOperationException(Loaders.Label(kind.Value) + " 要用 Java 跑官方安装器，先装 Java 21。");

            progress?.Report(new InstallProgress("读取 " + Loaders.Label(kind.Value) + " 版本…", 0.99));
            var versions = await Loaders.VersionsAsync(kind.Value, mcver, ct).ConfigureAwait(false);
            // 整合包一般锁死加载器版本，能对上就用它，对不上才退回最新稳定版
            var lv = string.IsNullOrWhiteSpace(loaderVersion)
                ? null
                : versions.FirstOrDefault(x => string.Equals(x.Version, loaderVersion, StringComparison.OrdinalIgnoreCase));
            if (lv is null && !string.IsNullOrWhiteSpace(loaderVersion))
                log?.Invoke("整合包指定的 " + Loaders.Label(kind.Value) + " " + loaderVersion +
                            " 在 " + mcver + " 上没有构建，改用最新稳定版");
            lv ??= versions.FirstOrDefault(x => x.Channel == "稳定") ?? versions.FirstOrDefault();
            if (lv is null)
                throw new InvalidOperationException("这个游戏版本没有可用的 " + Loaders.Label(kind.Value) + " 构建。");

            progress?.Report(new InstallProgress("安装 " + Loaders.Label(kind.Value) + " " + lv.Version + "…", 0.995));
            var madeId = await Loaders.InstallAsync(kind.Value, mcver, lv.Version, root, javaExe, progress, log, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(madeId)) versionId = madeId;
            loaderLabelText = Loaders.Label(kind.Value);

            // 把加载器版本改成用户起的名字，客户端列表里就是「我的客户端」而不是一长串 loader id
            versionId = RenameVersionDir(root, versionId, safeName, loaderLabelText, mcver) ?? versionId;
        }

        progress?.Report(new InstallProgress("完成", 1.0));

        // LiteLoader 这类只有图形安装器的：拿不到版本 id 就别登记一个空客户端
        if (string.IsNullOrEmpty(versionId))
        {
            log?.Invoke("安装器已在窗口里启动，完成后点「刷新」即可看到新版本。");
            return new Result(safeName, "", mcver, loaderLabelText,
                "安装器已启动，完成后再刷新");
        }

        var client = cfg.RegisterClient(versionId, safeName, mcver, loaderLabelText);
        var note = mcver + (kind is null ? "" : " · " + loaderLabelText);
        return new Result(client.Name, versionId, mcver, loaderLabelText, note);
    }

    /// <summary>
    /// 把 versions/&lt;旧 id&gt; 改成用户起的名字：目录、json 文件名、json 里的 id、
    /// 该版本的 jar 文件名，以及**其它版本里指向它的 inheritsFrom** 都要跟着改。
    /// </summary>
    internal static string? RenameVersionDir(string root, string oldId, string newId, string? loaderLabel = null,
        string? mcver = null, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(newId)) return null;
        if (string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase)) return oldId;

        var versionsDir = Path.Combine(root, "versions");
        var from = Path.Combine(versionsDir, oldId);
        var to = Path.Combine(versionsDir, newId);
        if (!Directory.Exists(from))
        {
            log?.Invoke("改名失败：找不到版本目录 " + oldId);
            return null;
        }
        if (Directory.Exists(to))
        {
            log?.Invoke("改名失败：已经有 " + newId + " 这个版本目录了");
            return null;
        }

        try
        {
            Directory.Move(from, to);

            // json 文件名 + 内容
            var oldJson = Path.Combine(to, oldId + ".json");
            var newJson = Path.Combine(to, newId + ".json");
            if (File.Exists(oldJson)) File.Move(oldJson, newJson, true);
            if (File.Exists(newJson))
            {
                var text = File.ReadAllText(newJson);
                var node = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject;
                if (node is not null)
                {
                    node["id"] = newId;
                    File.WriteAllText(newJson, node.ToJsonString());
                }
            }

            // 版本自己的 jar（有的话）
            var oldJar = Path.Combine(to, oldId + ".jar");
            if (File.Exists(oldJar))
            {
                try { File.Move(oldJar, Path.Combine(to, newId + ".jar"), true); }
                catch (Exception ex) { log?.Invoke("jar 改名失败（不影响启动）：" + ex.Message); }
            }

            // 其它版本可能 inheritsFrom 指向旧 id，一起改掉，否则它们会变成"缺少父版本"
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                if (string.Equals(dir, to, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var json in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var text = File.ReadAllText(json);
                        if (!text.Contains("\"" + oldId + "\"", StringComparison.OrdinalIgnoreCase)) continue;
                        var node = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject;
                        if (node is null) continue;
                        if (!string.Equals(node["inheritsFrom"]?.ToString(), oldId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        node["inheritsFrom"] = newId;
                        File.WriteAllText(json, node.ToJsonString());
                        log?.Invoke("已把 " + Path.GetFileNameWithoutExtension(json) + " 的 inheritsFrom 改为 " + newId);
                    }
                    catch (Exception) { }
                }
            }
            return newId;
        }
        catch (Exception ex)
        {
            log?.Invoke("改名失败：" + ex.Message);
            // 尽力回滚，避免留下半个版本
            try { if (Directory.Exists(to) && !Directory.Exists(from)) Directory.Move(to, from); }
            catch (Exception) { }
            return null;
        }
    }

    /// <summary>哪些版本继承了某个版本（删掉它就会让这些版本起不来）。</summary>
    public static List<string> Dependents(string root, string versionId)
    {
        var list = new List<string>();
        try
        {
            var versionsDir = Path.Combine(root, "versions");
            if (!Directory.Exists(versionsDir)) return list;
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var id = Path.GetFileName(dir);
                if (string.Equals(id, versionId, StringComparison.OrdinalIgnoreCase)) continue;
                var json = Path.Combine(dir, id + ".json");
                if (!File.Exists(json)) continue;
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(json));
                    if (string.Equals(node?["inheritsFrom"]?.ToString(), versionId,
                            StringComparison.OrdinalIgnoreCase))
                        list.Add(id);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return list;
    }
}
