using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace MineLib.Core;

public sealed class LaunchException : Exception
{
    public LaunchException(string message) : base(message) { }
}

public static class GameLauncher
{
    public static async Task<Installer.MissingFiles> PrepareAsync(string gameDir, string vid,
        JsonObject merged, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var miss = Installer.ScanMissing(gameDir, vid, merged);
        if (miss.Client)
        {
            progress?.Report(new InstallProgress("补齐客户端 jar…", 0));
            await Installer.EnsureClientAsync(gameDir, vid, merged, null, ct).ConfigureAwait(false);
        }
        if (miss.Libraries.Count > 0)
        {
            progress?.Report(new InstallProgress("补齐依赖库…", 0.1));
            await Installer.EnsureLibrariesAsync(gameDir, merged, null,
                (a, b, name) => progress?.Report(
                    new InstallProgress($"依赖库 {a}/{b}  {name}", 0.1 + 0.4 * a / Math.Max(1, b))), ct)
                .ConfigureAwait(false);
        }
        var indexId = merged["assetIndex"]?["id"]?.ToString();
        if (!string.IsNullOrEmpty(indexId))
        {
            var idx = Path.Combine(gameDir, "assets", "indexes", indexId + ".json");
            if (!File.Exists(idx))
            {
                progress?.Report(new InstallProgress("下载资源索引…", 0.5));
                await Installer.EnsureAssetsAsync(gameDir, merged, null,
                    (a, b) => progress?.Report(new InstallProgress($"资源 {a}/{b}",
                        0.5 + 0.5 * a / Math.Max(1, b))), ct).ConfigureAwait(false);
            }
        }
        return miss;
    }

    public static (List<string> args, string nativesDir) BuildCommand(string gameDir, string vid,
        JsonObject merged, Account account, string javaExe, int memoryMb, string extraJvm,
        string? runDir = null)
    {
        var vdir = Path.Combine(gameDir, "versions", vid);
        var natives = Path.Combine(vdir, "natives-ml");
        Installer.ExtractNatives(gameDir, merged, natives);

        var classpath = new List<string>();
        if (merged["libraries"] is JsonArray libs)
        {
            foreach (var node in libs)
            {
                if (node is not JsonObject lib || !Installer.RuleAllows(lib)) continue;
                var rel = Installer.LibPath(lib);
                if (string.IsNullOrEmpty(rel)) continue;
                var full = Path.Combine(gameDir, "libraries", rel);
                if (File.Exists(full)) classpath.Add(full);
            }
        }
        var jar = Installer.ClientJarPath(gameDir, vid, merged);
        if (File.Exists(jar)) classpath.Add(jar);

        var sep = OperatingSystem.IsWindows() ? ";" : ":";
        var table = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = account.Name,
            ["version_name"] = vid,
            ["game_directory"] = string.IsNullOrEmpty(runDir) ? gameDir : runDir,
            ["assets_root"] = Path.Combine(gameDir, "assets"),
            ["game_assets"] = Path.Combine(gameDir, "assets", "virtual", "legacy"),
            ["assets_index_name"] = merged["assetIndex"]?["id"]?.ToString() ?? "legacy",
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = account.AccessToken,
            ["auth_session"] = "token:" + account.AccessToken + ":" + account.Uuid,
            ["clientid"] = account.ClientId,
            ["auth_xuid"] = account.Xuid,
            ["user_type"] = account.UserType,
            ["version_type"] = merged["type"]?.ToString() ?? "release",
            ["natives_directory"] = natives,
            ["launcher_name"] = "MineLib",
            ["launcher_version"] = "0.1",
            ["classpath"] = string.Join(sep, classpath),
            ["library_directory"] = Path.Combine(gameDir, "libraries"),
            ["classpath_separator"] = sep,
            ["user_properties"] = "{}",
            ["resolution_width"] = "854",
            ["resolution_height"] = "480"
        };

        var args = new List<string>();
        if (!string.IsNullOrEmpty(account.Authlib))
        {
            try
            {
                var injector = YggdrasilAuth.EnsureInjectorAsync().GetAwaiter().GetResult();
                args.Add("-javaagent:" + injector + "=" + account.Authlib);
            }
            catch (Exception)
            {
                // 注入器拿不到就让服务器自己去拒绝，不静默改账号类型
            }
        }
        args.Add("-Xmx" + memoryMb + "m");
        args.Add("-Xms512m");
        args.Add("-Dfile.encoding=UTF-8");
        if (!string.IsNullOrWhiteSpace(extraJvm)) args.AddRange(SplitArgs(extraJvm));

        var jvmSection = merged["arguments"]?["jvm"] as JsonArray;
        if (jvmSection is not null && jvmSection.Count > 0)
        {
            foreach (var node in jvmSection) AppendArg(args, node, table);
        }
        else
        {
            args.Add("-Djava.library.path=" + natives);
            args.Add("-cp");
            args.Add(table["classpath"]);
        }

        var mainClass = merged["mainClass"]?.ToString();
        if (string.IsNullOrEmpty(mainClass)) throw new LaunchException("版本 json 缺少 mainClass");
        args.Add(mainClass);

        if (merged["arguments"]?["game"] is JsonArray gameSection)
        {
            foreach (var node in gameSection) AppendArg(args, node, table);
        }
        else if (merged["minecraftArguments"]?.ToString() is { Length: > 0 } legacy)
        {
            args.AddRange(SplitArgs(Substitute(legacy, table)));
        }

        return (args, natives);
    }

    private static void AppendArg(List<string> args, JsonNode? node, Dictionary<string, string> table)
    {
        switch (node)
        {
            case JsonValue v:
                args.Add(Substitute(v.ToString(), table));
                break;
            case JsonObject obj:
                if (obj["rules"] is JsonArray && !Installer.RuleAllows(obj)) return;
                var value = obj["value"];
                switch (value)
                {
                    case JsonValue sv:
                        args.Add(Substitute(sv.ToString(), table));
                        break;
                    case JsonArray arr:
                        foreach (var item in arr)
                            if (item is JsonValue iv) args.Add(Substitute(iv.ToString(), table));
                        break;
                }
                break;
        }
    }

    private static string Substitute(string text, Dictionary<string, string> table)
    {
        foreach (var kv in table) text = text.Replace(" + kv.Key + ", kv.Value);
        return text;
    }

    public static List<string> SplitArgs(string raw)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quote = false;
        foreach (var ch in raw)
        {
            if (ch == '"') { quote = !quote; continue; }
            if (char.IsWhiteSpace(ch) && !quote)
            {
                if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }
}

/// <summary>游戏进程：按行回调日志，退出回调退出码。</summary>
public sealed class GameProcess
{
    private Process? _proc;
    private StreamWriter? _log;

    public event Action<string>? LineReceived;
    public event Action<int>? Exited;

    public bool Running => _proc is { HasExited: false };

    public void Start(string javaExe, List<string> args, string cwd, string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        _log = new StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };

        var psi = new ProcessStartInfo(javaExe)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => Push(e.Data);
        _proc.ErrorDataReceived += (_, e) => Push(e.Data);
        _proc.Exited += (_, _) =>
        {
            var code = _proc?.ExitCode ?? -1;
            _log?.Dispose();
            _log = null;
            Exited?.Invoke(code);
        };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    private void Push(string? line)
    {
        if (line is null) return;
        try { _log?.WriteLine(line); } catch (Exception) { }
        LineReceived?.Invoke(line);
    }

    public void Stop()
    {
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(true);
        }
        catch (Exception)
        {
        }
    }
}
