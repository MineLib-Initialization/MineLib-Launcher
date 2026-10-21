using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace MineLib.Core;

public sealed record JavaCandidate(string Exe, int Major);

public static class JavaFinder
{
    public const string ExeName = "javaw.exe";

    public static (string exe, int major, string note) Find(string configPath, string gameDir, int needMajor)
    {
        var candidates = Candidates(configPath, gameDir);
        if (candidates.Count == 0) return ("", 0, "未找到 Java，请安装 Java 21");

        var scanned = new List<JavaCandidate>();
        foreach (var path in candidates)
        {
            var major = MajorOf(path);
            scanned.Add(new JavaCandidate(path, major));
            if (needMajor > 0 && major == needMajor)
                return (path, major, $"Java {major}");
            if (needMajor == 0 && major > 0)
                return (path, major, $"Java {major}");
        }

        var closest = scanned.Where(c => c.Major >= needMajor && c.Major > 0)
            .OrderBy(c => c.Major).FirstOrDefault();
        if (closest is not null)
            return (closest.Exe, closest.Major, $"Java {closest.Major}（建议 {needMajor}）");

        var first = scanned[0];
        return (first.Exe, first.Major, $"Java {first.Major}（建议 {needMajor}）");
    }

    private static List<string> Candidates(string configPath, string gameDir)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(configPath)) list.Add(configPath);

        // 官方启动器自带的运行时：runtime/<component>/<platform>/<component>/bin/javaw.exe
        var runtime = Path.Combine(gameDir, "runtime");
        if (Directory.Exists(runtime))
        {
            list.AddRange(Directory.GetFiles(runtime, ExeName, SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal));
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = Path.Combine(dir.Trim(), ExeName);
            if (File.Exists(exe)) list.Add(exe);
        }

        foreach (var baseDir in new[]
                 {
                     @"C:\Program Files\Java", @"C:\Program Files\Eclipse Adoptium",
                     @"C:\Program Files\Microsoft", @"C:\Program Files\Zulu",
                     @"C:\Program Files (x86)\Java", @"D:\AndroidTools"
                 })
        {
            if (!Directory.Exists(baseDir)) continue;
            try
            {
                foreach (var exe in Directory.GetFiles(baseDir, ExeName, SearchOption.AllDirectories))
                    list.Add(exe);
            }
            catch (Exception)
            {
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
    }

    public static int MajorOf(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return 0;
            var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            var m = Regex.Match(text, "version \"(\\d+)(?:\\.(\\d+))?");
            if (!m.Success) return 0;
            var major = int.Parse(m.Groups[1].Value);
            return major == 1 && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : major;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
