using System.IO;
using System.Text;

namespace MineLib.Core;

/// <summary>
/// 崩溃兜底：未处理异常写到配置目录的 logs\crash-*.log，
/// 用户遇到问题时能直接把文件发过来，而不是只看到"程序已停止工作"。
/// </summary>
public static class CrashReporter
{
    public static string LogDir
    {
        get
        {
            try { return Path.Combine(AppConfig.Dir, "logs"); }
            catch (Exception) { return Path.GetTempPath(); }
        }
    }

    public static string Report(Exception ex, string where)
    {
        var path = "";
        try
        {
            Directory.CreateDirectory(LogDir);
            path = Path.Combine(LogDir,
                "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            var sb = new StringBuilder();
            sb.AppendLine("时间      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("位置      : " + where);
            try { sb.AppendLine("启动器版本: " + Updates.Current); } catch (Exception) { }
            sb.AppendLine("系统      : " + Environment.OSVersion + "  " +
                          (Environment.Is64BitOperatingSystem ? "x64" : "x86"));
            try { sb.AppendLine("程序目录  : " + AppContext.BaseDirectory); } catch (Exception) { }
            try { sb.AppendLine("配置目录  : " + AppConfig.Dir); } catch (Exception) { }
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception)
        {
            return "";
        }
        return path;
    }
}
