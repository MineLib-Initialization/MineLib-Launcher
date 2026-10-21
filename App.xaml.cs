using System.IO;
using System.Text;
using System.Windows;
using MineLib.Core;

namespace MineLib;

public partial class App : Application
{
    /// <summary>--selftest：不开界面，跑一遍加载器与模组接口，结果写到临时文件（便于离线核对）。</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest-launch"))
        {
            var sb = new StringBuilder();
            var gameDir = Environment.GetEnvironmentVariable("MINELIB_E2E_DIR")
                          ?? Path.Combine(Path.GetTempPath(), "minelib-e2e");
            var vid = "fabric-loader-0.19.5-1.21.1";
            try
            {
                var merged = Versions.Resolve(gameDir, vid);
                var javaInfo = JavaFinder.Find("", gameDir, 21);
                var acct = OfflineAuth.Create("Tester");
                await GameLauncher.PrepareAsync(gameDir, vid, merged, 16, null, CancellationToken.None);
                var (args, _) = GameLauncher.BuildCommand(gameDir, vid, merged, acct, javaInfo.exe, 2048, "", gameDir);

                var proc = new GameProcess();
                var lines = new List<string>();
                var exited = new TaskCompletionSource<int>();
                proc.LineReceived += line => { lock (lines) { if (lines.Count < 300) lines.Add(line); } };
                proc.Exited += code => exited.TrySetResult(code);
                proc.Start(javaInfo.exe, args, gameDir, Path.Combine(gameDir, "logs", "verify.log"));
                var done = await Task.WhenAny(exited.Task, Task.Delay(35000));
                var finishedEarly = done == exited.Task;
                if (!finishedEarly) proc.Stop();
                await Task.Delay(2000);
                lock (lines)
                {
                    sb.AppendLine("提前退出=" + finishedEarly);
                    sb.AppendLine("主类错误=" + lines.Any(l => l.Contains("Could not find") || l.Contains("ClassNotFoundException")));
                    sb.AppendLine("进到游戏初始化=" + lines.Any(l => l.Contains("LWJGL") || l.Contains("Setting user") || l.Contains("Backend library") || l.Contains("OpenAL")));
                    sb.AppendLine("---- 输出 ----");
                    foreach (var line in lines.Take(45)) sb.AppendLine(line);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("LAUNCH FAIL " + ex);
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-launch.txt"), sb.ToString());
            Shutdown();
            return;
        }
        if (e.Args.Contains("--selftest-cmd"))
        {
            var sb = new StringBuilder();
            var gameDir = Environment.GetEnvironmentVariable("MINELIB_E2E_DIR")
                          ?? Path.Combine(Path.GetTempPath(), "minelib-e2e");
            try
            {
                var vid = "fabric-loader-0.19.5-1.21.1";
                var merged = Versions.Resolve(gameDir, vid);
                var libs = (merged["libraries"] as System.Text.Json.Nodes.JsonArray) ?? new();
                sb.AppendLine("merged libraries = " + libs.Count);
                var withUrl = 0;
                var existing = 0;
                foreach (var node in libs)
                {
                    if (node is not System.Text.Json.Nodes.JsonObject lib) continue;
                    if (Installer.LibUrl(lib) is not null) withUrl++;
                    var rel = Installer.LibPath(lib);
                    if (rel.Length > 0 && File.Exists(Path.Combine(gameDir, "libraries", rel))) existing++;
                }
                sb.AppendLine("有下载地址的 = " + withUrl + "，磁盘上存在的 = " + existing);
                var miss = Installer.ScanMissing(gameDir, vid, merged);
                sb.AppendLine("缺失: client=" + miss.Client + " libs=" + miss.Libraries.Count + " assetIndex=" + miss.AssetIndex);

                var javaInfo = JavaFinder.Find("", gameDir, 21);
                var acct = OfflineAuth.Create("Tester");
                var (args, _) = GameLauncher.BuildCommand(gameDir, vid, merged, acct, javaInfo.exe, 2048, "", gameDir);
                var cpIndex = args.IndexOf("-cp");
                if (cpIndex >= 0 && cpIndex + 1 < args.Count)
                {
                    var entries = args[cpIndex + 1].Split(';');
                    sb.AppendLine("classpath 条目 = " + entries.Length);
                    foreach (var entry in entries)
                        sb.AppendLine("   " + Path.GetFileName(entry) + (File.Exists(entry) ? "" : "   [不存在]"));
                }
                else
                {
                    sb.AppendLine("参数里没有独立的 -cp，前 12 个参数：");
                    foreach (var a in args.Take(12)) sb.AppendLine("   " + a);
                }
                sb.AppendLine("client jar = " + (Installer.FindClientJar(gameDir, vid) ?? "未找到"));
                if (merged["arguments"]?["jvm"] is System.Text.Json.Nodes.JsonArray jvm)
                {
                    sb.AppendLine("原始 jvm 参数 (" + jvm.Count + "):");
                    foreach (var n in jvm.Take(6)) sb.AppendLine("   [" + (n?.GetType().Name ?? "null") + "] " + n?.ToJsonString());
                }
                var cpAt = args.IndexOf("-cp");
                sb.AppendLine("-cp 之后那一项 = " + (cpAt >= 0 && cpAt + 1 < args.Count ? args[cpAt + 1].Substring(0, Math.Min(120, args[cpAt + 1].Length)) : "(无)"));
                sb.AppendLine("含占位符的参数：");
                foreach (var a in args.Where(x => x.Contains("$" + "{") || x.Contains("classpath")).Take(5))
                    sb.AppendLine("   " + (a.Length > 120 ? a.Substring(0, 120) + "..." : a));
            }
            catch (Exception ex)
            {
                sb.AppendLine("CMD FAIL " + ex);
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-cmd.txt"), sb.ToString());
            Shutdown();
            return;
        }
        if (e.Args.Contains("--selftest-e2e"))
        {
            var sb = new StringBuilder();
            var gameDir = Environment.GetEnvironmentVariable("MINELIB_E2E_DIR")
                          ?? Path.Combine(Path.GetTempPath(), "minelib-e2e");
            Directory.CreateDirectory(gameDir);
            try
            {
                sb.AppendLine("gameDir = " + gameDir);
                var dl = new Progress<InstallProgress>(p =>
                {
                    if (p.Fraction is > 0 and < 1) sb.AppendLine("  " + p.Stage);
                });
                await Installer.InstallVersionAsync(gameDir, "1.21.1", 16, dl, CancellationToken.None);
                sb.AppendLine("原版安装完成");

                var fabric = await Loaders.VersionsAsync(LoaderKind.Fabric, "1.21.1", CancellationToken.None);
                var pick = fabric.First(v => v.Channel == "稳定");
                var fabId = await Loaders.InstallAsync(LoaderKind.Fabric, "1.21.1", pick.Version, gameDir, "",
                    null, s => sb.AppendLine("  " + s), CancellationToken.None);
                sb.AppendLine("Fabric 安装完成: " + fabId);

                var merged = Versions.Resolve(gameDir, fabId);
                var javaInfo = JavaFinder.Find("", gameDir, merged["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 21);
                sb.AppendLine("java = " + javaInfo.exe + "  " + javaInfo.note);

                var acct = OfflineAuth.Create("Tester");
                await GameLauncher.PrepareAsync(gameDir, fabId, merged, 16, null, CancellationToken.None);
                var (args, natives) = GameLauncher.BuildCommand(gameDir, fabId, merged, acct, javaInfo.exe, 2048, "",
                    gameDir);
                var cpEntry = args.FirstOrDefault(a => a.Contains("fabric-loader"));
                sb.AppendLine("classpath 里有 fabric-loader: " + (cpEntry is not null));
                sb.AppendLine("classes 目录数量: " +
                              Directory.GetFiles(Path.Combine(gameDir, "libraries", "net", "fabricmc"),
                                  "*.jar", SearchOption.AllDirectories).Length);

                var proc = new GameProcess();
                var lines = new List<string>();
                var exited = new TaskCompletionSource<int>();
                proc.LineReceived += line =>
                {
                    lock (lines)
                    {
                        if (lines.Count < 400) lines.Add(line);
                    }
                };
                proc.Exited += code => exited.TrySetResult(code);
                proc.Start(javaInfo.exe, args, gameDir, Path.Combine(gameDir, "logs", "e2e.log"));
                var done = await Task.WhenAny(exited.Task, Task.Delay(40000));
                if (done != exited.Task) proc.Stop();
                await Task.Delay(1500);
                lock (lines)
                {
                    sb.AppendLine("---- 启动输出前 40 行 ----");
                    foreach (var line in lines.Take(40)) sb.AppendLine(line);
                    sb.AppendLine("---- 是否出现 main class 错误: " +
                                  lines.Any(l => l.Contains("Could not find or load main class") ||
                                                 l.Contains("ClassNotFoundException"))) ;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("E2E FAIL " + ex);
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-e2e.txt"), sb.ToString());
            Shutdown();
            return;
        }
        if (e.Args.Contains("--selftest-download"))
        {
            var sb = new StringBuilder();
            try
            {
                var manifest = await Http.GetJsonAsync(Installer.ManifestUrl);
                var entry = (manifest["versions"] as System.Text.Json.Nodes.JsonArray)!
                    .OfType<System.Text.Json.Nodes.JsonObject>()
                    .First(v => v["id"]?.ToString() == "1.21.1");
                var raw = (System.Text.Json.Nodes.JsonObject)await Http.GetJsonAsync(entry["url"]!.ToString());
                var indexId = raw["assetIndex"]?["id"]?.ToString();
                var indexUrl = raw["assetIndex"]?["url"]?.ToString();
                var idxPath = Path.Combine(Path.GetTempPath(), "minelib-assetindex-" + indexId + ".json");
                await Http.DownloadAsync(indexUrl!, idxPath, null, null);
                var index = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(idxPath));
                var objects = (index?["objects"] as System.Text.Json.Nodes.JsonObject)!;
                sb.AppendLine("asset index " + indexId + " objects=" + objects.Count);

                foreach (var threads in new[] { 1, 8, 16 })
                {
                    var dir = Path.Combine(Path.GetTempPath(), "minelib-dltest-" + threads);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    var tasks = new List<Http.DownloadTask>();
                    foreach (var kv in objects.Take(200))
                    {
                        var hash = kv.Value?["hash"]?.ToString() ?? "";
                        if (hash.Length < 2) continue;
                        tasks.Add(new Http.DownloadTask($"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                            Path.Combine(dir, hash[..2], hash), hash,
                            kv.Value?["size"]?.GetValue<long>() ?? 0));
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var (ok, failed) = await Http.DownloadManyAsync(tasks, threads, null, CancellationToken.None);
                    sw.Stop();
                    var bytes = tasks.Sum(t => t.Size);
                    sb.AppendLine($"线程 {threads,2}: {tasks.Count} 个文件 ok={ok} failed={failed} " +
                                  $"{bytes / 1024.0 / 1024.0:0.0} MB 用时 {sw.Elapsed.TotalSeconds:0.0}s " +
                                  $"= {bytes / 1024.0 / sw.Elapsed.TotalSeconds:0.0} KB/s");
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("download selftest FAIL " + ex.Message);
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-dlspeed.txt"), sb.ToString());
            Shutdown();
            return;
        }
        if (e.Args.Contains("--selftest"))
        {
            var sb = new StringBuilder();
            foreach (var kind in new[]
                     {
                         LoaderKind.Fabric, LoaderKind.Quilt, LoaderKind.Forge, LoaderKind.NeoForge
                     })
            {
                try
                {
                    var list = await Loaders.VersionsAsync(kind, "1.21.1", CancellationToken.None);
                    sb.AppendLine($"{Loaders.Label(kind),-10} {list.Count,4} 个构建   首个={list.FirstOrDefault()?.Display}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{Loaders.Label(kind),-10} FAIL {ex.Message}");
                }
            }
            try
            {
                var lite = await Loaders.LiteLoaderMinecraftVersionsAsync(CancellationToken.None);
                sb.AppendLine($"{"LiteLoader",-10} 支持 MC: {string.Join(' ', lite)}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("LiteLoader FAIL " + ex.Message);
            }
            try
            {
                var (hits, total) = await Modrinth.SearchAsync("sodium", "1.21.1", "fabric", "downloads", 0, 5,
                    CancellationToken.None);
                sb.AppendLine($"Modrinth   total={total}  " + string.Join(" | ",
                    hits.Select(h => $"{h.Title}[{h.License}] {h.DownloadsText}")));
                if (hits.Count > 0)
                {
                    var files = await Modrinth.FilesAsync(hits[0].ProjectId, "1.21.1", "fabric",
                        CancellationToken.None);
                    sb.AppendLine($"文件列表   {files.Count} 个，首个={files.FirstOrDefault()?.Display} {files.FirstOrDefault()?.Name}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Modrinth FAIL " + ex.Message);
            }

            // 真跑一次 Fabric 安装，确认 profile json 落盘后能被版本扫描认出来
            try
            {
                var cfg = AppConfig.Load();
                var fabric = await Loaders.VersionsAsync(LoaderKind.Fabric, "1.21.1", CancellationToken.None);
                var pick = fabric.FirstOrDefault(v => v.Channel == "稳定") ?? fabric.First();
                var newId = await Loaders.InstallAsync(LoaderKind.Fabric, "1.21.1", pick.Version, cfg.GameDir,
                    "", null, s => sb.AppendLine("  install: " + s), CancellationToken.None);
                var scanned = Versions.Scan(cfg.GameDir).FirstOrDefault(v => v.Id == newId);
                sb.AppendLine($"Fabric安装  {newId}  扫描结果={(scanned is null ? "未识别" : scanned.Subtitle)}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("Fabric安装 FAIL " + ex.Message);
            }

            var outFile = Path.Combine(Path.GetTempPath(), "minelib-selftest.txt");
            await File.WriteAllTextAsync(outFile, sb.ToString());
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
