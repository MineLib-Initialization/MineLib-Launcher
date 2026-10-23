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
        var hint = needMajor is > 0 and <= 8 && first.Major > 8
            ? $"这个版本需要 Java {needMajor}，当前只有 Java {first.Major}（老版本在 Java 21 上会崩）"
            : $"Java {first.Major}（建议 {needMajor}）";
        return (first.Exe, first.Major, hint);
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

        // 程序目录下自带的运行时（由 JavaProvisioner 下载）
        var provisioned = Path.Combine(AppContext.BaseDirectory, "MineLib", "java");
        if (Directory.Exists(provisioned))
            list.AddRange(Directory.GetFiles(provisioned, ExeName, SearchOption.AllDirectories)
                .OrderByDescending(p => p, StringComparer.Ordinal));

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

    /// <summary>探测 Java 主版本。-version 的输出可能一直不结束（坏 JDK），这里给硬超时并杀进程。</summary>
    public static int MajorOf(string exe)
    {
        var cached = ReadCache(exe);
        if (cached >= 0) return cached;

        var major = ProbeMajor(exe);
        WriteCache(exe, major);
        return major;
    }

    private static int ProbeMajor(string exe)
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
            // 两个流同时读，避免其中一个写满缓冲区把进程卡住
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var all = Task.WhenAll(errTask, outTask);
            if (!all.Wait(TimeSpan.FromSeconds(4)))
            {
                try { p.Kill(true); } catch (Exception) { }
                return 0;
            }
            try { p.WaitForExit(1500); } catch (Exception) { }
            var text = errTask.Result + outTask.Result;
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

    // ---------------------------------------------------------------- 主版本探测缓存
    // 每启动一次都要探一遍所有候选 JDK 太慢（每个都 spawn 一次 java）；按「路径 + 修改时间」缓存。
    private static readonly Dictionary<string, (long ticks, int major)> MemCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();
    private static bool _diskLoaded;

    private static string CachePath => Path.Combine(AppConfig.Dir, "java-cache.json");

    private static void LoadDiskCacheOnce()
    {
        lock (CacheLock)
        {
            if (_diskLoaded) return;
            _diskLoaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(CachePath))
                    as System.Text.Json.Nodes.JsonObject;
                if (node is null) return;
                foreach (var kv in node)
                {
                    var ticks = kv.Value?["t"]?.GetValue<long>() ?? 0;
                    var major = kv.Value?["m"]?.GetValue<int>() ?? 0;
                    MemCache[kv.Key] = (ticks, major);
                }
            }
            catch (Exception) { }
        }
    }

    private static int ReadCache(string exe)
    {
        LoadDiskCacheOnce();
        try
        {
            var info = new FileInfo(exe);
            if (!info.Exists) return -1;
            lock (CacheLock)
            {
                if (MemCache.TryGetValue(exe, out var hit) && hit.ticks == info.LastWriteTimeUtc.Ticks)
                    return hit.major;
            }
        }
        catch (Exception) { }
        return -1;
    }

    private static void WriteCache(string exe, int major)
    {
        try
        {
            var info = new FileInfo(exe);
            if (!info.Exists) return;
            lock (CacheLock)
            {
                MemCache[exe] = (info.LastWriteTimeUtc.Ticks, major);
                var node = new System.Text.Json.Nodes.JsonObject();
                foreach (var kv in MemCache)
                    node[kv.Key] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["t"] = kv.Value.ticks,
                        ["m"] = kv.Value.major
                    };
                Directory.CreateDirectory(AppConfig.Dir);
                File.WriteAllText(CachePath, node.ToJsonString());
            }
        }
        catch (Exception) { }
    }
}
