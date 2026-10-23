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
        Action<string>? log, CancellationToken ct)
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
            var lv = versions.FirstOrDefault(x => x.Channel == "稳定") ?? versions.FirstOrDefault();
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
        var client = cfg.RegisterClient(versionId, safeName, mcver, loaderLabelText);
        var note = mcver + (kind is null ? "" : " · " + loaderLabelText);
        return new Result(client.Name, versionId, mcver, loaderLabelText, note);
    }

    /// <summary>把 versions/&lt;旧 id&gt; 改成用户起的名字，并同步 json 文件名与继承链里对它的引用。</summary>
    private static string? RenameVersionDir(string root, string oldId, string newId, string loaderLabel, string mcver)
    {
        if (string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase)) return oldId;
        var versionsDir = Path.Combine(root, "versions");
        var from = Path.Combine(versionsDir, oldId);
        var to = Path.Combine(versionsDir, newId);
        if (!Directory.Exists(from) || Directory.Exists(to)) return null;
        try
        {
            Directory.Move(from, to);
            var oldJson = Path.Combine(to, oldId + ".json");
            var newJson = Path.Combine(to, newId + ".json");
            if (File.Exists(oldJson)) File.Move(oldJson, newJson, true);

            // json 里的 id 字段改成新名字；inheritsFrom 仍指向原版版本
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
            return newId;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
