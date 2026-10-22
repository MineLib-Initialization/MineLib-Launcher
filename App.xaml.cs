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
        // 发布用：--mkpatch <旧exe> <新exe> <输出补丁> 生成增量补丁，不显示界面
        var patchIdx = Array.IndexOf(e.Args, "--mkpatch");
        if (patchIdx > 0 && e.Args.Length > patchIdx + 3)
        {
            var sb = new StringBuilder();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var info = Patcher.Create(e.Args[patchIdx + 1], e.Args[patchIdx + 2], e.Args[patchIdx + 3]);
                sw.Stop();
                // 当场回放一遍补丁，确认重建结果与目标文件逐字节一致
                var checkPath = e.Args[patchIdx + 3] + ".check";
                var rebuilt = Patcher.Apply(e.Args[patchIdx + 1], e.Args[patchIdx + 3], checkPath);
                var target = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(e.Args[patchIdx + 2]))).ToLowerInvariant();
                sb.AppendLine("回放校验 = " + (rebuilt == target ? "通过" : "失败"));
                try { File.Delete(checkPath); } catch (Exception) { }
                sb.AppendLine("patch = " + info.Path);
                sb.AppendLine("补丁大小 = " + (info.PatchBytes / 1048576.0).ToString("0.00") + " MB");
                sb.AppendLine("字面量 = " + (info.LiteralBytes / 1048576.0).ToString("0.00") + " MB");
                sb.AppendLine("目标 sha256 = " + info.TargetSha256);
                sb.AppendLine("目标大小 = " + info.TargetSize + "  基线大小 = " + info.BaseSize);
                sb.AppendLine("用时 = " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");
            }
            catch (Exception ex)
            {
                sb.AppendLine("MKPATCH FAIL " + ex.Message);
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-patch.txt"), sb.ToString());
            Shutdown();
            return;
        }
        if (e.Args.Contains("--selftest-java"))
        {
            var sb = new StringBuilder();
            var idx = Array.IndexOf(e.Args, "--selftest-java");
            var need = idx + 1 < e.Args.Length ? int.Parse(e.Args[idx + 1]) : 17;
            try
            {
                sb.AppendLine("程序目录: " + AppContext.BaseDirectory);
                sb.AppendLine("Java 根目录: " + JavaProvisioner.Root);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var got = await JavaProvisioner.EnsureAsync(need, new Progress<InstallProgress>(p =>
                {
                    if (p.Fraction is > 0 and < 1) sb.AppendLine("  " + p.Stage);
                }), line => sb.AppendLine("  " + line), CancellationToken.None);
                sw.Stop();
                sb.AppendLine("结果: exe=" + got.exe);
                sb.AppendLine("      major=" + got.major + "  用时=" + sw.Elapsed.TotalSeconds.ToString("0") + "s");
                var second = JavaFinder.Find("", Path.GetTempPath(), need);
                sb.AppendLine("JavaFinder 是否能找到它: " + second.note + "  -> " + second.exe);
            }
            catch (Exception ex)
            {
                sb.AppendLine("JAVA SELFTEST FAIL " + ex.Message);
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-java.txt"), sb.ToString());
            Shutdown();
            return;
        }
        if (e.Args.Contains("--selftest-create"))
        {
            var sb = new StringBuilder();
            var cfg = AppConfig.Load();
            var parent = Environment.GetEnvironmentVariable("MINELIB_NEW_DIR")
                         ?? Path.Combine(Path.GetTempPath(), "minelib-clients");
            var name = Environment.GetEnvironmentVariable("MINELIB_NEW_NAME") ?? "测试新客户端";
            var mc = Environment.GetEnvironmentVariable("MINELIB_NEW_MC") ?? "1.7.10";
            var loader = Environment.GetEnvironmentVariable("MINELIB_NEW_LOADER") ?? "不装（纯原版）";
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var progress = new Progress<InstallProgress>(p => { if (p.Fraction is > 0 and < 1) sb.AppendLine("  " + p.Stage); });
                var result = await ClientCreator.CreateAsync(cfg, parent, name, mc, loader, 16, "", progress,
                    line => sb.AppendLine("  log: " + line), CancellationToken.None);
                sw.Stop();
                sb.AppendLine($"创建成功 dir={result.Dir} version={result.VersionId} note={result.Note} 用时={sw.Elapsed.TotalSeconds:0}s");
                sb.AppendLine("磁盘占用 = " + Math.Round(new DirectoryInfo(result.Dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1048576.0, 1) + " MB");

                var scanned = Versions.Scan(result.Dir);
                sb.AppendLine("版本扫描 = " + string.Join(" | ", scanned.Select(v => v.Id + "(" + v.Subtitle + ")")));

                var merged = Versions.Resolve(result.Dir, result.VersionId);
                var javaInfo = JavaFinder.Find("", result.Dir, merged["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 21);
                sb.AppendLine("java = " + javaInfo.exe + "  " + javaInfo.note);
                var acct = OfflineAuth.Create("Tester");
                await GameLauncher.PrepareAsync(result.Dir, result.VersionId, merged, 16, null, CancellationToken.None);
                var (args, _) = GameLauncher.BuildCommand(result.Dir, result.VersionId, merged, acct,
                    javaInfo.exe, 2048, "", result.Dir);

                var proc = new GameProcess();
                var lines = new List<string>();
                var exited = new TaskCompletionSource<int>();
                proc.LineReceived += line => { lock (lines) { if (lines.Count < 200) lines.Add(line); } };
                proc.Exited += code => exited.TrySetResult(code);
                proc.Start(javaInfo.exe, args, result.Dir, Path.Combine(result.Dir, "logs", "create-test.log"));
                var done = await Task.WhenAny(exited.Task, Task.Delay(35000));
                var early = done == exited.Task;
                if (!early) proc.Stop();
                await Task.Delay(1500);
                lock (lines)
                {
                    sb.AppendLine("提前退出=" + early);
                    sb.AppendLine("主类错误=" + lines.Any(l => l.Contains("Could not find") || l.Contains("ClassNotFoundException") || l.Contains("NoClassDefFound")));
                    sb.AppendLine("---- 输出 ----");
                    foreach (var line in lines.Take(25)) sb.AppendLine(line);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("CREATE FAIL " + ex.Message);
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-create.txt"), sb.ToString());
            Shutdown();
            return;
        }
        if (e.Args.Contains("--selftest-launch"))
        {
            var sb = new StringBuilder();
            var gameDir = Environment.GetEnvironmentVariable("MINELIB_E2E_DIR")
                          ?? Path.Combine(Path.GetTempPath(), "minelib-e2e");
            var vid = Environment.GetEnvironmentVariable("MINELIB_E2E_VER") ?? "fabric-loader-0.19.5-1.21.1";
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
            var work = Path.Combine(Path.GetTempPath(), "minelib-dlspeed");
            try
            {
                Directory.CreateDirectory(work);
                var manifest = await Http.GetJsonAsync(Installer.ManifestUrl);
                var entry = (manifest["versions"] as System.Text.Json.Nodes.JsonArray)!
                    .OfType<System.Text.Json.Nodes.JsonObject>()
                    .First(v => v["id"]?.ToString() == "1.21.1");
                var raw = (System.Text.Json.Nodes.JsonObject)await Http.GetJsonAsync(entry["url"]!.ToString());

                // ---- 单个大文件：客户端 jar。单流 vs 分段并发，都从零开始 ----
                var jarUrl = raw["downloads"]?["client"]?["url"]?.ToString();
                var jarSize = raw["downloads"]?["client"]?["size"]?.GetValue<long>() ?? 0;
                sb.AppendLine("client jar " + (jarSize / 1048576.0).ToString("0.0") + " MB");
                if (!string.IsNullOrEmpty(jarUrl) && jarSize > 0)
                {
                    var single = Path.Combine(work, "client-single.jar");
                    if (File.Exists(single)) File.Delete(single);
                    var sw1 = System.Diagnostics.Stopwatch.StartNew();
                    var ok1 = await Http.DownloadSingleStreamAsync(jarUrl!, single);
                    sw1.Stop();
                    sb.AppendLine("单连接    ok=" + ok1 + "  用时 " + sw1.Elapsed.TotalSeconds.ToString("0.0") +
                                  "s  = " + (jarSize / 1024.0 / sw1.Elapsed.TotalSeconds).ToString("0.0") + " KB/s");

                    var multi = Path.Combine(work, "client-multi.jar");
                    if (File.Exists(multi)) File.Delete(multi);
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    var ok2 = await Http.DownloadAsync(jarUrl!, multi, raw["downloads"]?["client"]?["sha1"]?.ToString());
                    sw2.Stop();
                    sb.AppendLine("分段并发  ok=" + ok2 + "  用时 " + sw2.Elapsed.TotalSeconds.ToString("0.0") +
                                  "s  = " + (jarSize / 1024.0 / sw2.Elapsed.TotalSeconds).ToString("0.0") + " KB/s" +
                                  "  加速 " + (sw1.Elapsed.TotalSeconds / Math.Max(0.01, sw2.Elapsed.TotalSeconds)).ToString("0.0") + "x");
                }

                // ---- 小文件海量并发 ----
                var indexId = raw["assetIndex"]?["id"]?.ToString();
                var indexUrl = raw["assetIndex"]?["url"]?.ToString();
                var idxPath = Path.Combine(work, "assets-" + indexId + ".json");
                await Http.DownloadAsync(indexUrl!, idxPath, null, null);
                var index = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(idxPath));
                var objects = (index?["objects"] as System.Text.Json.Nodes.JsonObject)!;
                sb.AppendLine("asset index " + indexId + " objects=" + objects.Count);

                foreach (var threads in new[] { 1, 8, 16, 32, 64 })
                {
                    var dir = Path.Combine(work, "t" + threads);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    var tasks = new List<Http.DownloadTask>();
                    foreach (var kv in objects.Take(300))
                    {
                        var hash = kv.Value?["hash"]?.ToString() ?? "";
                        if (hash.Length < 2) continue;
                        tasks.Add(new Http.DownloadTask("https://resources.download.minecraft.net/" + hash[..2] + "/" + hash,
                            Path.Combine(dir, hash[..2], hash), hash,
                            kv.Value?["size"]?.GetValue<long>() ?? 0));
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var (ok, failed) = await Http.DownloadManyAsync(tasks, threads, null, CancellationToken.None);
                    sw.Stop();
                    var bytes = tasks.Sum(t => t.Size);
                    sb.AppendLine("线程 " + threads.ToString().PadLeft(2) + ": " + tasks.Count + " 个文件 ok=" + ok +
                                  " failed=" + failed + "  " + (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB  用时 " +
                                  sw.Elapsed.TotalSeconds.ToString("0.0") + "s  = " +
                                  (bytes / 1024.0 / sw.Elapsed.TotalSeconds).ToString("0.0") + " KB/s");
                    Directory.Delete(dir, true);
                }
                Directory.Delete(work, true);
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
        // 有暂存好的新版本：拉起替换脚本并退出，让脚本覆盖 exe 后重新启动
        if (Updates.TryApplyStagedAndExit())
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}