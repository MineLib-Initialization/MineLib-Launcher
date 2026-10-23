using System.IO;
using System.Text.Json.Nodes;

namespace MineLib.Core;

/// <summary>
/// 工作区：负责「只有一个游戏根目录」这件事，以及把旧版「一个客户端一个目录」
/// 的数据合并进来。迁移只做一次，失败不会动原目录。
/// </summary>
public static class Workspace
{
    public sealed record Report(bool Changed, List<string> Lines)
    {
        public string Summary => Changed ? string.Join(Environment.NewLine, Lines) : "已是最新目录结构";
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>需要一个「自己的」客户端目录的子目录集合：这些跟着版本走，合并时要放进版本目录。</summary>
    private static readonly string[] PerVersionDirs =
    {
        "mods", "saves", "resourcepacks", "shaderpacks", "screenshots", "config", "defaultconfigs",
        "server-resource-packs", "logs", "crash-reports"
    };

    private static readonly string[] PerVersionFiles =
    {
        "options.txt", "servers.dat", "servers.dat_old", "usercache.json", "realms_persistence.json",
        "splash.properties", "banned-ips.json", "banned-players.json"
    };

    /// <summary>
    /// 确保是单根目录结构。检测到旧的多目录配置就把它们合并进根目录。
    /// </summary>
    public static Report EnsureSingleRoot(AppConfig cfg, Action<string>? log = null)
    {
        var lines = new List<string>();
        void Say(string s)
        {
            lines.Add(s);
            log?.Invoke(s);
        }

        var root = cfg.GameDir;
        var legacy = (cfg.Instances ?? new List<InstanceRef>())
            .Where(i => !string.IsNullOrWhiteSpace(i.Dir))
            .GroupBy(i => i.Dir.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var extras = legacy.Where(i =>
        {
            try { return !SamePath(i.Dir, root); }
            catch (Exception) { return false; }
        }).ToList();

        if (extras.Count == 0 && cfg.SingleRootMigrated && legacy.Count == 0)
            return new Report(false, lines);

        if (extras.Count == 0)
        {
            // 没有要搬的东西，只把标记补上（首次启动空环境也走这里）
            if (!cfg.SingleRootMigrated)
            {
                cfg.SingleRootMigrated = true;
                cfg.Instances.Clear();
                cfg.CurrentInstance = "";
            }
            return new Report(false, lines);
        }

        Directory.CreateDirectory(root);
        Say("检测到旧版多目录客户端，开始合并到 " + root);

        foreach (var inst in extras)
        {
            var src = inst.Dir;
            if (!Directory.Exists(src))
            {
                Say("  跳过不存在的目录：" + src);
                continue;
            }
            Say("  处理 " + (string.IsNullOrEmpty(inst.Name) ? src : inst.Name + "  (" + src + ")"));

            // 先搬 versions（每个子目录就是一个版本）
            var moved = new List<string>();
            var srcVersions = Path.Combine(src, "versions");
            if (Directory.Exists(srcVersions))
            {
                var dstVersions = Path.Combine(root, "versions");
                Directory.CreateDirectory(dstVersions);
                foreach (var dir in Directory.GetDirectories(srcVersions))
                {
                    var id = Path.GetFileName(dir);
                    var dst = Path.Combine(dstVersions, id);
                    try
                    {
                        if (Directory.Exists(dst))
                        {
                            Say("    版本 " + id + " 已存在于根目录，保留根目录那份");
                            continue;
                        }
                        Directory.Move(dir, dst);
                        moved.Add(id);
                        Say("    版本 " + id + " 已并入根目录");
                    }
                    catch (Exception ex)
                    {
                        Say("    版本 " + id + " 搬运失败：" + ex.Message);
                    }
                }
            }

            // 再把共享文件并进来（libraries / assets 只补缺的，不覆盖）
            foreach (var shared in new[] { "libraries", "assets" })
            {
                var count = MergeTree(Path.Combine(src, shared), Path.Combine(root, shared), Say);
                if (count > 0) Say("    " + shared + " 合并 " + count + " 个文件");
            }

            // 实例根目录的 mods/saves 等：归到它对应的版本目录（开启隔离的模型）
            var targetVersion = moved.LastOrDefault() ?? cfg.FindClient(inst.Dir)?.VersionId ?? "";
            if (string.IsNullOrEmpty(targetVersion))
            {
                // 退而求其次：用旧配置里记住的版本
                if (!string.IsNullOrEmpty(cfg.Version)) targetVersion = cfg.Version;
            }
            if (!string.IsNullOrEmpty(targetVersion))
            {
                var vdir = Path.Combine(root, "versions", targetVersion);
                Directory.CreateDirectory(vdir);
                foreach (var d in PerVersionDirs)
                {
                    var count = MergeTree(Path.Combine(src, d), Path.Combine(vdir, d), Say);
                    if (count > 0) Say("    " + d + " → versions/" + targetVersion + "/" + d + "（" + count + " 个文件）");
                }
                foreach (var f in PerVersionFiles)
                {
                    var from = Path.Combine(src, f);
                    var to = Path.Combine(vdir, f);
                    try
                    {
                        if (File.Exists(from) && !File.Exists(to))
                        {
                            File.Move(from, to);
                            Say("    " + f + " → versions/" + targetVersion + "/");
                        }
                    }
                    catch (Exception ex)
                    {
                        Say("    " + f + " 搬运失败：" + ex.Message);
                    }
                }
            }

            // 清空后删掉旧目录；还有东西就留着并说明
            try
            {
                if (!HasAnyFile(src))
                {
                    Directory.Delete(src, true);
                    Say("    旧目录已清空并删除");
                }
                else
                {
                    Say("    旧目录里还有未归类的内容，已保留：" + src);
                }
            }
            catch (Exception ex)
            {
                Say("    旧目录清理失败：" + ex.Message);
            }
        }

        // 把合并进来的版本登记成客户端（有 inheritsFrom 的是加载器版本，vanilla 只有当它没有子版本时才登记）
        RegisterClientsFromDisk(cfg, Say);

        cfg.Instances.Clear();
        cfg.CurrentInstance = "";
        cfg.SingleRootMigrated = true;
        Say("迁移完成，共 " + cfg.Clients.Count + " 个客户端");
        return new Report(true, lines);
    }

    /// <summary>把根目录里已有的版本登记成客户端列表（幂等，可重复调用）。</summary>
    public static void RegisterClientsFromDisk(AppConfig cfg, Action<string>? log = null)
    {
        var versions = Versions.Scan(cfg.GameDir);
        var ids = new HashSet<string>(versions.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in versions)
        {
            var parent = v.Raw?["inheritsFrom"]?.ToString();
            if (!string.IsNullOrEmpty(parent)) bases.Add(parent);
        }

        foreach (var v in versions)
        {
            if (cfg.FindClient(v.Id) is not null) continue;
            var isBase = bases.Contains(v.Id);
            if (isBase)
            {
                // 基础版本不登记成客户端，它只是加载器的依赖
                log?.Invoke("  隐藏依赖版本：" + v.Id);
                continue;
            }
            var loader = string.IsNullOrEmpty(v.Loader) ? "原版" : v.Loader;
            cfg.RegisterClient(v.Id, v.Id, v.McVersion ?? v.Id, loader);
            log?.Invoke("  登记客户端：" + v.Id);
        }

        // 清掉已经不在磁盘上的客户端
        var removed = cfg.Clients.RemoveAll(c => !ids.Contains(c.VersionId));
        if (removed > 0) log?.Invoke("  清理失效客户端记录 " + removed + " 条");
    }

    /// <summary>递归把 src 合并进 dst：只补 dst 里没有的文件，不覆盖、不删除 src 里的同名文件。</summary>
    private static int MergeTree(string src, string dst, Action<string>? log)
    {
        if (!Directory.Exists(src)) return 0;
        var count = 0;
        try
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.EnumerateFiles(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(file));
                try
                {
                    if (File.Exists(target))
                    {
                        // 目标已有同名文件：大小一致就当作重复，把源文件删掉，别让旧目录残留
                        if (new FileInfo(file).Length == new FileInfo(target).Length) File.Delete(file);
                        continue;
                    }
                    File.Move(file, target);
                    count++;
                }
                catch (Exception) { }
            }
            foreach (var dir in Directory.EnumerateDirectories(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(dir));
                count += MergeTree(dir, target, log);
            }
            // 源目录空了就删掉，保持旧目录整洁
            try
            {
                if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src);
            }
            catch (Exception) { }
        }
        catch (Exception) { }
        return count;
    }

    private static bool HasAnyFile(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(); }
        catch (Exception) { return true; }
    }
}
