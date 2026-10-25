using System.IO;

namespace MineLib.Core;

/// <summary>
/// 便携化路径：配置和默认游戏目录都放启动器所在目录，别散到 C 盘的 AppData。
///
///   &lt;启动器目录&gt;\MineLibLauncher\config.json   配置、installers、libs
///   &lt;启动器目录&gt;\.minecraft                    默认的游戏根目录（客户端都在它的 versions 下）
///
/// 如果启动器放在没有写权限的地方（比如 Program Files），自动退回 %APPDATA%。
/// </summary>
public static class Paths
{
    private static string? _baseOverride;
    private static bool? _portable;

    /// <summary>启动器所在目录。</summary>
    public static string LauncherDir
    {
        get
        {
            if (!string.IsNullOrEmpty(_baseOverride)) return _baseOverride!;
            var dir = AppContext.BaseDirectory;
            return string.IsNullOrEmpty(dir) ? Environment.CurrentDirectory : dir.TrimEnd('\\');
        }
    }

    /// <summary>能不能在启动器目录下写东西（决定用便携路径还是回退 AppData）。</summary>
    public static bool Portable
    {
        get
        {
            if (_portable.HasValue) return _portable.Value;
            try
            {
                var probe = Path.Combine(LauncherDir, ".minelib-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                _portable = true;
            }
            catch (Exception)
            {
                _portable = false;
            }
            return _portable!.Value;
        }
    }

    /// <summary>配置目录。</summary>
    public static string ConfigDir => Portable
        ? Path.Combine(LauncherDir, "MineLibLauncher")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MineLib");

    /// <summary>默认游戏根目录。</summary>
    public static string DefaultGameRoot => Portable
        ? Path.Combine(LauncherDir, ".minecraft")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    /// <summary>旧版本（AppData）的配置路径，用于首次便携化时导入。</summary>
    public static string LegacyConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MineLib", "config.json");

    /// <summary>
    /// 供自检使用：把启动器目录指到别处。
    /// 必须先把目录建出来：`Portable` 是靠"往启动器目录写探针文件"判断的，
    /// 目录不存在会判成不可写 → 回退 %APPDATA%，自检就会写到用户真实配置上（踩过一次）。
    /// </summary>
    public static void OverrideLauncherDir(string? dir)
    {
        _baseOverride = dir;
        _portable = null;
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir!); } catch (Exception) { }
        }
    }
}
