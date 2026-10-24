using System.IO;

namespace MineLib.Core;

/// <summary>
/// 客户端的模组开关：只改文件名后缀，不移动、不删除。
/// 启用 = `xxx.jar`；禁用 = `xxx.jar.disable`（也认别的启动器留下的 `.disabled`）。
/// 游戏只加载 `.jar`，所以加后缀就等于"关掉"，改回来就等于"打开"。
/// </summary>
public static class ModManager
{
    /// <summary>禁用时加的后缀（按用户要求用 .disable）。</summary>
    public const string DisableSuffix = ".disable";

    /// <summary>识别"已被禁用"的后缀：自家的 .disable，加上从别的启动器搬来的 .disabled。</summary>
    private static readonly string[] DisabledSuffixes = { ".disable", ".disabled" };

    private const string JarSuffix = ".jar";

    /// <summary>一个模组文件（含被禁用的）。</summary>
    public sealed record Entry(string Path, string Name, bool Enabled, long Size)
    {
        public string State => Enabled ? "已启用" : "已禁用";

        public string SizeText => Size >= 1048576
            ? (Size / 1048576.0).ToString("0.0") + " MB"
            : Math.Max(1, Size / 1024) + " KB";
    }

    public enum ToggleOutcome
    {
        /// <summary>成功禁用。</summary>
        Disabled,
        /// <summary>成功启用。</summary>
        Enabled,
        /// <summary>本来就是启用的，禁用操作跳过。</summary>
        AlreadyEnabled,
        /// <summary>本来就是禁用的，启用操作跳过。</summary>
        AlreadyDisabled,
        /// <summary>目标文件名已被占用，没动它。</summary>
        NameTaken,
        /// <summary>文件不在/改名失败。</summary>
        Failed
    }

    public sealed record ToggleResult(ToggleOutcome Outcome, string Path, string Message)
    {
        public bool Changed => Outcome is ToggleOutcome.Disabled or ToggleOutcome.Enabled;
    }

    /// <summary>文件名是否处于启用状态（.jar 结尾，且没有 .disable/.disabled）。</summary>
    public static bool IsEnabledName(string fileName) =>
        fileName.EndsWith(JarSuffix, StringComparison.OrdinalIgnoreCase) && DisabledSuffixOf(fileName) is null;

    /// <summary>文件名是否处于禁用状态（.jar 后面还跟着 .disable / .disabled）。</summary>
    public static bool IsDisabledName(string fileName) => DisabledSuffixOf(fileName) is not null;

    private static string? DisabledSuffixOf(string fileName)
    {
        foreach (var suffix in DisabledSuffixes)
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return suffix;
        return null;
    }

    /// <summary>由禁用名推出启用名（去掉 .disable/.disabled）。</summary>
    public static string? EnabledNameOf(string fileName)
    {
        var suffix = DisabledSuffixOf(fileName);
        if (suffix is null) return null;
        var stripped = fileName[..^suffix.Length];
        return stripped.EndsWith(JarSuffix, StringComparison.OrdinalIgnoreCase) ? stripped : null;
    }

    /// <summary>列出目录里的模组（启用的排前面，然后按名字）。目录不存在就当没有。</summary>
    public static List<Entry> List(string modsDir)
    {
        var list = new List<Entry>();
        if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return list;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(modsDir); }
        catch (Exception) { return list; }

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var enabled = IsEnabledName(name);
            if (!enabled && !IsDisabledName(name)) continue;      // 既不是 jar 也不是禁用的 jar
            if (!enabled && EnabledNameOf(name) is null) continue; // .txt.disable 之类，不算模组
            long size = 0;
            try { size = new FileInfo(path).Length; } catch (Exception) { }
            list.Add(new Entry(path, name, enabled, size));
        }

        return list
            .OrderByDescending(e => e.Enabled)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>禁用：只对"当前是启用状态"的 jar 生效，改成 xxx.jar.disable。</summary>
    public static ToggleResult Disable(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || !File.Exists(path))
            return new ToggleResult(ToggleOutcome.Failed, path, "文件不在");
        if (!IsEnabledName(name))
            return new ToggleResult(ToggleOutcome.AlreadyDisabled, path, "这个模组已经是禁用状态");

        var target = path + DisableSuffix;
        if (File.Exists(target))
            return new ToggleResult(ToggleOutcome.NameTaken, path, "已经有 " + name + DisableSuffix + " 了，先处理它");
        try
        {
            File.Move(path, target);
            return new ToggleResult(ToggleOutcome.Disabled, target, "已禁用 " + name);
        }
        catch (Exception ex)
        {
            return new ToggleResult(ToggleOutcome.Failed, path, "改名失败：" + ex.Message);
        }
    }

    /// <summary>启用：只对"当前是禁用状态"的模组生效，把 .disable / .disabled 去掉。</summary>
    public static ToggleResult Enable(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || !File.Exists(path))
            return new ToggleResult(ToggleOutcome.Failed, path, "文件不在");
        if (!IsDisabledName(name))
            return new ToggleResult(ToggleOutcome.AlreadyEnabled, path, "这个模组本来就是启用的");

        var stripped = EnabledNameOf(name);
        if (stripped is null)
            return new ToggleResult(ToggleOutcome.Failed, path, "这不是模组文件");
        var dir = Path.GetDirectoryName(path)!;
        var target = Path.Combine(dir, stripped);
        if (File.Exists(target))
            return new ToggleResult(ToggleOutcome.NameTaken, path, "已经有 " + stripped + " 了，先处理它");
        try
        {
            File.Move(path, target);
            return new ToggleResult(ToggleOutcome.Enabled, target, "已启用 " + stripped);
        }
        catch (Exception ex)
        {
            return new ToggleResult(ToggleOutcome.Failed, path, "改名失败：" + ex.Message);
        }
    }

    /// <summary>批量禁用：逐个判断状态，已经在禁用状态的直接跳过。</summary>
    public static List<ToggleResult> DisableMany(IEnumerable<string> paths) =>
        paths.Select(Disable).ToList();

    /// <summary>批量启用：逐个判断状态，本来就启用的直接跳过。</summary>
    public static List<ToggleResult> EnableMany(IEnumerable<string> paths) =>
        paths.Select(Enable).ToList();

    /// <summary>统计（共 / 启用 / 禁用），给页签顶部显示用。</summary>
    public static (int total, int enabled, int disabled) Count(string modsDir)
    {
        var list = List(modsDir);
        var enabled = list.Count(e => e.Enabled);
        return (list.Count, enabled, list.Count - enabled);
    }
}
