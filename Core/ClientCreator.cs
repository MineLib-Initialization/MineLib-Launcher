using System.IO;

namespace MineLib.Core;

/// <summary>
/// 创建一个全新的客户端（独立游戏目录）：装原版 →（可选）装加载器 → 注册到配置。
/// 不会写入任何已有客户端，目标目录非空直接拒绝。
/// </summary>
public static class ClientCreator
{
    public sealed record Result(string Name, string Dir, string VersionId, string Note);

    public static string? Validate(string parent, string name, out string dir)
    {
        dir = "";
        if (string.IsNullOrWhiteSpace(name)) return "给客户端起个名字。";
        if (string.IsNullOrWhiteSpace(parent)) return "选一个安装位置。";

        var safe = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
        if (safe.Length == 0) return "这个名字不能用作文件夹名。";
        dir = Path.Combine(parent.Trim(), safe);
        if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0)
            return "目录 " + dir + " 里已经有东西了。换个名字或位置——不会往已有客户端里塞东西。";
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

    public static async Task<Result> CreateAsync(AppConfig cfg, string parent, string name, string mcver,
        string loaderLabel, int threads, string javaExe, IProgress<InstallProgress>? progress,
        Action<string>? log, CancellationToken ct)
    {
        var error = Validate(parent, name, out var dir);
        if (error is not null) throw new InvalidOperationException(error);

        var kind = ParseLoader(loaderLabel ?? "");
        Directory.CreateDirectory(dir);

        progress?.Report(new InstallProgress("安装 Minecraft " + mcver + "…", 0));
        await Installer.InstallVersionAsync(dir, mcver, threads, progress, ct).ConfigureAwait(false);

        var note = mcver;
        var installedId = mcver;
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
            var madeId = await Loaders.InstallAsync(kind.Value, mcver, lv.Version, dir, javaExe, progress, log, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(madeId)) installedId = madeId;
            note = mcver + " · " + Loaders.Label(kind.Value);
        }

        progress?.Report(new InstallProgress("完成", 1.0));
        cfg.RegisterInstance(name.Trim(), dir, note);
        return new Result(name.Trim(), dir, installedId, note);
    }
}
