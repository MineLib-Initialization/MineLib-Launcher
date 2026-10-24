using System.IO;
using System.Text;
using System.Windows;
using MineLib.Core;

namespace MineLib;

public partial class App : Application
{
    public App()
    {
        InstallGlobalHandlers();
    }

    /// <summary>全局兜底：以前一个没 catch 的 async void 就能让整个启动器无声退出。</summary>
    private void InstallGlobalHandlers()
    {
        DispatcherUnhandledException += (_, ev) =>
        {
            var path = CrashReporter.Report(ev.Exception, "界面线程");
            // 自检模式不弹窗：无人值守，弹了会卡住整个进程
            if (Environment.GetCommandLineArgs()
                .Any(a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                ev.Handled = true;
                return;
            }
            // 主界面在就走界面内提示（不弹系统对话框）；连窗口都没起来才用兜底窗口
            try
            {
                var dispatcher = Current?.Dispatcher;
                if (Current?.MainWindow is MainWindow win && dispatcher is not null &&
                    !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 已经在关窗就别再弹提示了，用户什么都做不了
                        if (!win.IsVisible) return;
                        _ = win.AlertAsync("本次操作出现错误",
                            "再次执行刚才的操作以重试。" + Environment.NewLine + Environment.NewLine +
                            "日志：" + (path ?? ""));
                    }));
                }
            }
            catch (Exception) { }
            ev.Handled = true;   // 界面不因此退出
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            if (ev.ExceptionObject is Exception ex) CrashReporter.Report(ex, "非界面线程");
        };

        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            CrashReporter.Report(ev.Exception, "后台任务");
            ev.SetObserved();
        };
    }

    /// <summary>--selftest：不开界面，跑一遍加载器与模组接口，结果写到临时文件（便于离线核对）。</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        // 任何自检模式都不许写用户真实配置（之前踩过这个坑）
        if (e.Args.Any(a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase)))
            AppConfig.SuppressSave = true;

        // 自检：故意抛异常，验证全局兜底会把崩溃写进日志
        if (e.Args.Contains("--selftest-crash"))
        {
            var sb = new StringBuilder();
            var dir = CrashReporter.LogDir;
            var before = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "crash-*.log").Length
                : 0;
            // 用 BeginInvoke（fire-and-forget）才会走 DispatcherUnhandledException；
            // 用 InvokeAsync 的话异常会回到调用方，测不到兜底
            Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("自检故意抛出的异常")));
            await Task.Delay(800);
            var after = Directory.Exists(dir) ? Directory.GetFiles(dir, "crash-*.log").Length : 0;
            sb.AppendLine("崩溃日志目录 = " + dir);
            sb.AppendLine("抛异常前 " + before + " 个，之后 " + after + " 个");
            var newest = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "crash-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            var body = newest is null ? "" : File.ReadAllText(newest);
            var hasText = body.Contains("自检故意抛出的异常");
            sb.AppendLine("最新日志含异常信息 = " + hasText + "（" + (newest ?? "无") + "）");
            sb.AppendLine(after > before && hasText ? "CRASH SELFTEST PASS" : "CRASH SELFTEST FAIL");
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-crash.txt"), sb.ToString());
            Shutdown();
            return;
        }

        try
        {
        // 发布用：--mkpatch <旧exe> <新exe> <输出补丁> 生成增量补丁，不显示界面
        // 注意：StartupEventArgs.Args 不含 exe 路径，--mkpatch 的下标是 0
        var patchIdx = Array.IndexOf(e.Args, "--mkpatch");
        if (patchIdx >= 0 && e.Args.Length > patchIdx + 3)
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
        // 高并发下载压力自检：64 并发下不能漏文件
        if (e.Args.Contains("--selftest-http64"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-h64-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Directory.CreateDirectory(work);
                // 拿 1.21.1 的资源索引，取前 500 个对象
                var manifest = await Http.GetJsonAsync(Installer.ManifestUrl);
                var entry = (manifest["versions"] as System.Text.Json.Nodes.JsonArray)!
                    .OfType<System.Text.Json.Nodes.JsonObject>()
                    .First(v => v["id"]?.ToString() == "1.21.1");
                var raw = (System.Text.Json.Nodes.JsonObject)await Http.GetJsonAsync(entry["url"]!.ToString());
                var idxPath = Path.Combine(work, "index.json");
                await Http.DownloadAsync(raw["assetIndex"]!["url"]!.ToString(), idxPath, null, null);
                var index = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(idxPath));
                var objects = (index?["objects"] as System.Text.Json.Nodes.JsonObject)!;

                foreach (var round in new[] { 1, 2 })
                {
                    var dir = Path.Combine(work, "round" + round);
                    var tasks = new List<Http.DownloadTask>();
                    foreach (var kv in objects.Take(500))
                    {
                        var hash = kv.Value?["hash"]?.ToString() ?? "";
                        if (hash.Length < 2) continue;
                        tasks.Add(new Http.DownloadTask(
                            DownloadSources.Current.AssetObject(hash),
                            Path.Combine(dir, hash[..2], hash), hash,
                            kv.Value?["size"]?.GetValue<long>() ?? 0));
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var (ok, failed) = await Http.DownloadManyAsync(tasks, 64, null, CancellationToken.None);
                    sw.Stop();

                    var missing = 0;
                    var badHash = 0;
                    foreach (var t in tasks)
                    {
                        if (!File.Exists(t.Dest)) { missing++; continue; }
                        if (!string.IsNullOrEmpty(t.Sha1) && Http.Sha1Of(t.Dest) != t.Sha1) badHash++;
                    }
                    sb.AppendLine($"第 {round} 轮：请求 {tasks.Count}，ok={ok} failed={failed} " +
                                  $"缺失={missing} 哈希不符={badHash} 用时={sw.Elapsed.TotalSeconds:0.0}s");
                    try { Directory.Delete(dir, true); } catch (Exception) { }
                }

                // 第 3 轮：两批任务并发写同一批目标文件（安装和启动校验同时触发的真实场景），
                // 并发 64 x 2，最后要求文件齐全、哈希正确、没有残留 .part
                {
                    var dir = Path.Combine(work, "round3");
                    List<Http.DownloadTask> Make()
                    {
                        var ts = new List<Http.DownloadTask>();
                        foreach (var kv in objects.Take(1000))
                        {
                            var hash = kv.Value?["hash"]?.ToString() ?? "";
                            if (hash.Length < 2) continue;
                            ts.Add(new Http.DownloadTask(
                                DownloadSources.Current.AssetObject(hash),
                                Path.Combine(dir, hash[..2], hash), hash,
                                kv.Value?["size"]?.GetValue<long>() ?? 0));
                        }
                        return ts;
                    }
                    var batchA = Make();
                    var batchB = Make();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var runA = Http.DownloadManyAsync(batchA, 64, null, CancellationToken.None);
                    var runB = Http.DownloadManyAsync(batchB, 64, null, CancellationToken.None);
                    await Task.WhenAll(runA, runB);
                    sw.Stop();
                    var missing = 0;
                    var badHash = 0;
                    foreach (var t in batchA)
                    {
                        if (!File.Exists(t.Dest)) { missing++; continue; }
                        if (!string.IsNullOrEmpty(t.Sha1) && Http.Sha1Of(t.Dest) != t.Sha1) badHash++;
                    }
                    var parts = Directory.Exists(dir)
                        ? Directory.GetFiles(dir, "*.part", SearchOption.AllDirectories).Length
                        : 0;
                    sb.AppendLine($"第 3 轮（双批并发）：请求 {batchA.Count}，ok={runA.Result.ok}+{runB.Result.ok} " +
                                  $"failed={runA.Result.failed}+{runB.Result.failed} 缺失={missing} " +
                                  $"哈希不符={badHash} 残留part={parts} 用时={sw.Elapsed.TotalSeconds:0.0}s");
                    try { Directory.Delete(dir, true); } catch (Exception) { }
                }

                var lines = sb.ToString().Split((char)10, StringSplitOptions.RemoveEmptyEntries);
                // 每轮都要：没有失败、没有缺失、没有哈希不符；带 .part 统计的那轮还要求没有残留
                var allGood = lines.All(l => l.Contains("failed=0") && l.Contains("缺失=0") &&
                                              l.Contains("哈希不符=0")) &&
                              lines.Where(l => l.Contains("残留part")).All(l => l.Contains("残留part=0"));
                sb.AppendLine(allGood ? "HTTP64 SELFTEST PASS" : "HTTP64 SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("HTTP64 SELFTEST FAIL " + ex);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-http64.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 关闭自检：开窗再关掉，确认没有异常落进崩溃日志（标题栏关闭按钮曾经因配色写错而崩）
        if (e.Args.Contains("--selftest-close"))
        {
            var sb = new StringBuilder();
            var crashDir = CrashReporter.LogDir;
            int CountCrashes() => Directory.Exists(crashDir)
                ? Directory.GetFiles(crashDir, "crash-*.log").Length
                : 0;
            var before = CountCrashes();
            try
            {
                var win = new MainWindow();
                win.Show();
                // 让布局与首帧渲染真的跑一遍，异常才会暴露出来
                for (var i = 0; i < 2; i++)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start();
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                win.Close();
            }
            catch (Exception ex)
            {
                sb.AppendLine("关闭自检抛异常：" + ex.Message);
            }
            // 关闭后再等一会儿，让渲染回调与后台清理把异常都吐出来
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            var after = CountCrashes();
            sb.AppendLine($"崩溃日志数：{before} → {after}（应保持不变）");
            var ok = after <= before && !sb.ToString().Contains("关闭自检抛异常");
            sb.AppendLine(ok ? "CLOSE SELFTEST PASS" : "CLOSE SELFTEST FAIL");
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-close.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 模组开关自检：改名规则、重复操作、文件名冲突、大小写、批量性能
        if (e.Args.Contains("--selftest-modmgr"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-mods-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Directory.CreateDirectory(work);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                const int bulk = 500;
                for (var i = 0; i < bulk; i++)
                    File.WriteAllBytes(Path.Combine(work, $"mod-{i:000}.jar"), new byte[64]);
                File.WriteAllBytes(Path.Combine(work, "already-a.jar.disable"), new byte[64]);
                File.WriteAllBytes(Path.Combine(work, "already-b.jar.disabled"), new byte[64]);
                File.WriteAllBytes(Path.Combine(work, "readme.txt"), new byte[8]);
                File.WriteAllBytes(Path.Combine(work, "notes.txt.disable"), new byte[8]);
                sw.Stop();
                sb.AppendLine($"准备 {bulk} 个 jar：{sw.ElapsedMilliseconds} ms");

                sw.Restart();
                var list = ModManager.List(work);
                sw.Stop();
                var enabledCount = list.Count(m => m.Enabled);
                var excluded = list.All(m => !m.Name.StartsWith("readme") && !m.Name.StartsWith("notes"));
                sb.AppendLine($"扫描：{list.Count} 个（启用 {enabledCount} / 禁用 {list.Count - enabledCount}），" +
                              $"耗时 {sw.ElapsedMilliseconds} ms，非模组文件已排除 = {excluded}");
                var okList = list.Count == bulk + 2 && enabledCount == bulk && excluded;

                var one = Path.Combine(work, "mod-000.jar");
                var r1 = ModManager.Disable(one);
                var okDisable = r1.Outcome == ModManager.ToggleOutcome.Disabled
                                && File.Exists(one + ".disable") && !File.Exists(one);
                var r2 = ModManager.Disable(one + ".disable");
                var okDisableTwice = r2.Outcome == ModManager.ToggleOutcome.AlreadyDisabled;
                var r3 = ModManager.Enable(one + ".disable");
                var okEnable = r3.Outcome == ModManager.ToggleOutcome.Enabled && File.Exists(one);
                var r4 = ModManager.Enable(one);
                var okEnableTwice = r4.Outcome == ModManager.ToggleOutcome.AlreadyEnabled;
                sb.AppendLine($"单个切换：禁用={okDisable} 重复禁用被挡={okDisableTwice} " +
                              $"启用={okEnable} 重复启用被挡={okEnableTwice}");

                var legacy = ModManager.Enable(Path.Combine(work, "already-b.jar.disabled"));
                var okLegacy = legacy.Outcome == ModManager.ToggleOutcome.Enabled
                               && File.Exists(Path.Combine(work, "already-b.jar"));
                sb.AppendLine("兼容 .disabled = " + okLegacy);

                File.WriteAllBytes(Path.Combine(work, "dup.jar"), new byte[16]);
                File.WriteAllBytes(Path.Combine(work, "dup.jar.disable"), new byte[16]);
                var dupDisable = ModManager.Disable(Path.Combine(work, "dup.jar"));
                var dupEnable = ModManager.Enable(Path.Combine(work, "dup.jar.disable"));
                var dupIntact = File.Exists(Path.Combine(work, "dup.jar")) &&
                                File.Exists(Path.Combine(work, "dup.jar.disable"));
                var okDup = dupDisable.Outcome == ModManager.ToggleOutcome.NameTaken
                            && dupEnable.Outcome == ModManager.ToggleOutcome.NameTaken && dupIntact;
                sb.AppendLine($"目标名被占用：禁用={dupDisable.Outcome} 启用={dupEnable.Outcome} 原文件都在={dupIntact}");

                File.WriteAllBytes(Path.Combine(work, "Upper.JAR"), new byte[16]);
                var up1 = ModManager.Disable(Path.Combine(work, "Upper.JAR"));
                var up2 = ModManager.Enable(up1.Path);
                var okUpper = up1.Outcome == ModManager.ToggleOutcome.Disabled
                              && up2.Outcome == ModManager.ToggleOutcome.Enabled
                              && File.Exists(Path.Combine(work, "Upper.JAR"));
                sb.AppendLine("大小写后缀 = " + okUpper);

                var missing = ModManager.Disable(Path.Combine(work, "nope.jar"));
                var okMissing = missing.Outcome == ModManager.ToggleOutcome.Failed;
                sb.AppendLine("文件不在时给 Failed = " + okMissing);

                // 冲突用例的两个文件先清掉，否则批量里的 dup.jar 会（正确地）被判 NameTaken
                File.Delete(Path.Combine(work, "dup.jar"));
                File.Delete(Path.Combine(work, "dup.jar.disable"));
                var all = ModManager.List(work).Where(m => m.Enabled).Select(m => m.Path).ToList();
                sw.Restart();
                var bulkDisable = ModManager.DisableMany(all);
                sw.Stop();
                var disableMs = sw.ElapsedMilliseconds;
                var disabledAll = bulkDisable.Count(r => r.Outcome == ModManager.ToggleOutcome.Disabled);
                sw.Restart();
                var bulkEnable = ModManager.EnableMany(
                    ModManager.List(work).Where(m => !m.Enabled).Select(m => m.Path).ToList());
                sw.Stop();
                var enableMs = sw.ElapsedMilliseconds;
                var enabledAll = bulkEnable.Count(r => r.Outcome == ModManager.ToggleOutcome.Enabled);
                sb.AppendLine($"批量：禁用 {disabledAll} 个 / {disableMs} ms，" +
                              $"启用 {enabledAll} 个 / {enableMs} ms");
                // 禁用只该动"原本启用"的那批；启用则要覆盖全部禁用项（含测试前就禁用的那个）
                var okBulk = disabledAll == all.Count && enabledAll == disabledAll + 1;

                var ok = okList && okDisable && okDisableTwice && okEnable && okEnableTwice && okLegacy &&
                         okDup && okUpper && okMissing && okBulk;
                sb.AppendLine(ok ? "MODMGR SELFTEST PASS" : "MODMGR SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("MODMGR SELFTEST FAIL " + ex);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-modmgr.txt"), sb.ToString());
            Shutdown();
            return;
        }

        // 整合包自检：合成一个 .mrpack（含跳过项、越界路径、overrides），真实装到临时目录
        if (e.Args.Contains("--selftest-mrpack"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-mr-" + Guid.NewGuid().ToString("N")[..6]);
            var gameDir = Path.Combine(work, ".minecraft");
            try
            {
                Directory.CreateDirectory(gameDir);
                const string sampleUrl = "https://launcher.endercraft.top/assets/shots/icon-256.png";
                var samplePath = Path.Combine(work, "sample.png");
                if (!await Http.DownloadAsync(sampleUrl, samplePath, null, null))
                    throw new InvalidOperationException("样例文件下载失败");
                var sampleSha1 = Http.Sha1Of(samplePath);
                var sampleSize = new FileInfo(samplePath).Length;

                // 拼一个最小可用的 mrpack
                var packPath = Path.Combine(work, "TestPack.mrpack");
                var index = new System.Text.Json.Nodes.JsonObject
                {
                    ["formatVersion"] = 1,
                    ["game"] = "minecraft",
                    ["versionId"] = "1.0.0",
                    ["name"] = "测试整合包",
                    ["summary"] = "自检用",
                    ["dependencies"] = new System.Text.Json.Nodes.JsonObject { ["minecraft"] = "1.7.10" },
                    ["files"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["path"] = "mods/test-mod.jar",
                            ["hashes"] = new System.Text.Json.Nodes.JsonObject { ["sha1"] = sampleSha1 },
                            ["env"] = new System.Text.Json.Nodes.JsonObject { ["client"] = "required" },
                            ["downloads"] = new System.Text.Json.Nodes.JsonArray { sampleUrl },
                            ["fileSize"] = sampleSize
                        },
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["path"] = "mods/server-only.jar",
                            ["hashes"] = new System.Text.Json.Nodes.JsonObject { ["sha1"] = sampleSha1 },
                            ["env"] = new System.Text.Json.Nodes.JsonObject { ["client"] = "unsupported" },
                            ["downloads"] = new System.Text.Json.Nodes.JsonArray { sampleUrl },
                            ["fileSize"] = sampleSize
                        },
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["path"] = "../evil.txt",
                            ["downloads"] = new System.Text.Json.Nodes.JsonArray { sampleUrl }
                        }
                    }
                };
                using (var zip = System.IO.Compression.ZipFile.Open(packPath,
                           System.IO.Compression.ZipArchiveMode.Create))
                {
                    void Add(string name, string text)
                    {
                        var entry = zip.CreateEntry(name);
                        using var w = new StreamWriter(entry.Open());
                        w.Write(text);
                    }
                    Add(Modpack.IndexName, index.ToJsonString());
                    Add("overrides/config/pack.cfg", "pack=1");
                    Add("client-overrides/config/extra.txt", "extra=1");
                    Add("server-overrides/server.properties", "motd=should-not-copy");
                }

                // 1) 只解析
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var info = Modpack.Read(packPath);
                sw.Stop();
                var parseOk = info.McVersion == "1.7.10" && info.Files.Count == 1
                              && info.Files[0].Path == "mods\\test-mod.jar"
                              && info.Name == "测试整合包";
                sb.AppendLine($"解析：{info.Detail}，耗时 {sw.ElapsedMilliseconds} ms");
                sb.AppendLine($"解析结果正确（跳过 server-only、丢弃 ../evil.txt）= {parseOk}");

                // 2) 越界路径消毒
                var safeCases = new[]
                {
                    Modpack.SafeRelativePath("../evil.txt") is null,
                    Modpack.SafeRelativePath("C:\\Windows\\x.txt") is null,
                    Modpack.SafeRelativePath("/etc/passwd") is null,
                    Modpack.SafeRelativePath("mods/ok.jar") == "mods\\ok.jar",
                    Modpack.SafeRelativePath("config/a/b.cfg") == "config\\a\\b.cfg"
                };
                sb.AppendLine("路径消毒 = " + safeCases.All(x => x));

                // 3) 真装一遍（会下 1.7.10 的文件）
                var cfg = new AppConfig { GameDir = gameDir };
                var progress = new Progress<InstallProgress>(p => sb.AppendLine("  " + p.Stage));
                sw.Restart();
                var result = await Modpack.InstallAsync(cfg, packPath, 16, "", progress,
                    line => sb.AppendLine("  log: " + line), CancellationToken.None);
                sw.Stop();
                sb.AppendLine($"安装：客户端「{result.ClientName}」版本 {result.VersionId}，用时 {sw.Elapsed.TotalSeconds:0.0}s");

                var clientDir = Path.Combine(gameDir, "versions", result.VersionId);
                var modPath = Path.Combine(clientDir, "mods", "test-mod.jar");
                var modOk = File.Exists(modPath) && Http.Sha1Of(modPath) == sampleSha1;
                var skippedOk = !File.Exists(Path.Combine(clientDir, "mods", "server-only.jar"));
                var overrideOk = File.Exists(Path.Combine(clientDir, "config", "pack.cfg"))
                                 && File.Exists(Path.Combine(clientDir, "config", "extra.txt"));
                var serverOverrideOk = !File.Exists(Path.Combine(clientDir, "server.properties"));
                var evilOk = !File.Exists(Path.Combine(work, "evil.txt")) &&
                             !File.Exists(Path.Combine(gameDir, "evil.txt"));
                var registered = cfg.FindClient(result.VersionId) is not null;
                sb.AppendLine($"模组落盘+sha1={modOk} 跳过只给服务端的={skippedOk} " +
                              $"overrides 解压={overrideOk} 不解压 server-overrides={serverOverrideOk} " +
                              $"没有越界文件={evilOk} 已登记客户端={registered}");

                var safeOk = safeCases.All(x => x);

                // 拖入时挑文件：一堆文件里要能挑出那个 .mrpack
                var pick = Modpack.FirstMrpack(new[] { @"C:\x\a.zip", packPath, @"C:\x\b.txt" });
                var pickNone = Modpack.FirstMrpack(new[] { @"C:\x\a.zip", @"C:\x\b.txt" });
                var pickOk = string.Equals(pick, packPath, StringComparison.OrdinalIgnoreCase) && pickNone is null;
                sb.AppendLine("拖入时挑文件 = " + pickOk);

                // 可选：再解析一个真实整合包（传路径进来）
                var extraPack = e.Args.FirstOrDefault(a => Modpack.LooksLikeModpack(a) && File.Exists(a));
                if (extraPack is not null)
                {
                    try
                    {
                        var real = Modpack.Read(extraPack);
                        sb.AppendLine($"真实整合包：{real.Name} → {real.Detail}");
                        // 包锁定加载器版本时，确认那个版本在该游戏版本上真的存在（安装时会优先用它）
                        if (!string.IsNullOrEmpty(real.LoaderVersion) &&
                            ClientCreator.ParseLoader(real.LoaderLabel) is { } kind)
                        {
                            var vers = await Loaders.VersionsAsync(kind, real.McVersion, CancellationToken.None);
                            var hit = vers.Any(v => string.Equals(v.Version, real.LoaderVersion,
                                StringComparison.OrdinalIgnoreCase));
                            sb.AppendLine($"包锁定的 {real.LoaderLabel} {real.LoaderVersion} 在 {real.McVersion} 上存在 = " +
                                          hit + "（该游戏版本共 " + vers.Count + " 个构建）");
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("真实整合包解析失败：" + ex.Message);
                    }
                }

                var ok = parseOk && safeOk && modOk && skippedOk && overrideOk &&
                         serverOverrideOk && evilOk && registered && pickOk;
                sb.AppendLine(ok ? "MRPACK SELFTEST PASS" : "MRPACK SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("MRPACK SELFTEST FAIL " + ex);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-mrpack.txt"), sb.ToString());
            Shutdown();
            return;
        }

        // 界面接线自检：真的开窗 → 选中假客户端 → 切模组页 → 点「禁用选中」/「启用选中」→ 校验磁盘
        // 只在便携目录里跑：非便携就跳过，绝不动用户的真实游戏数据。
        // 界面接线自检：开窗 → 选中假客户端 → 切模组页 → 点「禁用选中」/「启用选中」→ 校验磁盘
        // 先把启动器目录指到临时目录，配置与游戏目录都在里面，绝不动用户的真实数据。
        if (e.Args.Contains("--selftest-ui-mods"))
        {
            var sb = new StringBuilder();
            var vid = "ui-modtest";
            var tempRoot = Path.Combine(Path.GetTempPath(), "minelib-ui-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Paths.OverrideLauncherDir(tempRoot);
                var gameDir = Path.Combine(tempRoot, ".minecraft");
                var vdir = Path.Combine(gameDir, "versions", vid);
                var modsDir = Path.Combine(vdir, "mods");
                Directory.CreateDirectory(modsDir);
                File.WriteAllText(Path.Combine(vdir, vid + ".json"),
                    "{\"id\":\"" + vid + "\",\"libraries\":[],\"mainClass\":\"net.minecraft.client.main.Main\"}");
                File.WriteAllBytes(Path.Combine(vdir, vid + ".jar"), new byte[16]);
                for (var i = 0; i < 3; i++)
                    File.WriteAllBytes(Path.Combine(modsDir, $"ui-{i}.jar"), new byte[32]);

                // 预先写一份只指向临时目录的配置，MainWindow 载入的就是它
                var cfgDir = Path.Combine(tempRoot, "MineLibLauncher");
                Directory.CreateDirectory(cfgDir);
                var cfgJson = new System.Text.Json.Nodes.JsonObject
                {
                    ["GameDir"] = gameDir,
                    ["Roots"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["Name"] = "测试",
                            ["Path"] = gameDir,
                            ["Clients"] = new System.Text.Json.Nodes.JsonArray
                            {
                                new System.Text.Json.Nodes.JsonObject
                                {
                                    ["VersionId"] = vid,
                                    ["Name"] = "UI 测试客户端",
                                    ["McVersion"] = "1.21.1",
                                    ["Loader"] = "Fabric"
                                }
                            }
                        }
                    },
                    ["Version"] = vid,
                    ["DownloadThreads"] = 8,
                    ["Isolation"] = true
                };
                File.WriteAllText(Path.Combine(cfgDir, "config.json"), cfgJson.ToJsonString());

                var win = new MainWindow();
                win.Show();
                void Settle(double seconds)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var timer = new System.Windows.Threading.DispatcherTimer
                        { Interval = TimeSpan.FromSeconds(seconds) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start();
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }

                Settle(2.0);
                var picked = -1;
                for (var i = 0; i < win.VersionList.Items.Count; i++)
                {
                    if (win.VersionList.Items[i] is VersionInfo vi &&
                        string.Equals(vi.Id, vid, StringComparison.OrdinalIgnoreCase))
                    {
                        picked = i;
                        break;
                    }
                }
                sb.AppendLine("列表里找到测试客户端 = " + (picked >= 0) + "（列表共 " + win.VersionList.Items.Count + " 项）");
                if (picked < 0) throw new InvalidOperationException("测试客户端没进列表");

                win.VersionList.SelectedIndex = picked;
                Settle(1.0);
                win.Tabs.SelectedIndex = 2;
                Settle(0.6);

                var listed = win.ClientModList.Items.Cast<ModManager.Entry>().ToList();
                sb.AppendLine("模组页列出 " + listed.Count + " 个（应为 3）");
                sb.AppendLine("状态文字：" + win.LblClientModsStat.Text);
                sb.AppendLine("页面上的模组：" + string.Join(", ", listed.Select(m => m.Name)));
                var allMine = listed.All(m => m.Path.StartsWith(modsDir, StringComparison.OrdinalIgnoreCase));
                sb.AppendLine("列出的都是测试客户端的 = " + allMine);
                if (!allMine || listed.Count != 3) throw new InvalidOperationException("模组页内容不对");

                win.ClientModList.SelectAll();
                Settle(0.3);
                sb.AppendLine("选中项数量 = " + win.ClientModList.SelectedItems.Count);
                if (win.ClientModList.SelectedItems.Count == 0) throw new InvalidOperationException("没选中任何模组");
                win.BtnModDisable.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Settle(1.5);
                var disabledOnDisk = Directory.GetFiles(modsDir, "*.jar.disable").Length;
                var enabledOnDisk = Directory.GetFiles(modsDir, "*.jar").Length;
                sb.AppendLine("点「禁用选中」后：磁盘 .jar.disable=" + disabledOnDisk + "，剩余 .jar=" +
                              enabledOnDisk + "，提示：「" + win.LblClientModsHint.Text + "」");

                win.ClientModList.SelectAll();
                Settle(0.3);
                win.BtnModEnable.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Settle(1.5);
                var enabledBack = Directory.GetFiles(modsDir, "*.jar").Length;
                var disabledLeft = Directory.GetFiles(modsDir, "*.jar.disable").Length;
                sb.AppendLine("点「启用选中」后：磁盘 .jar=" + enabledBack + "，剩余 .jar.disable=" +
                              disabledLeft + "，提示：「" + win.LblClientModsHint.Text + "」");

                var ok = listed.Count == 3 && disabledOnDisk == 3 && enabledOnDisk == 0 &&
                         enabledBack == 3 && disabledLeft == 0;
                sb.AppendLine(ok ? "UI MODS SELFTEST PASS" : "UI MODS SELFTEST FAIL");
                win.Close();
            }
            catch (Exception ex)
            {
                sb.AppendLine("UI MODS SELFTEST FAIL " + ex.Message);
            }
            finally
            {
                try { Paths.OverrideLauncherDir(null); } catch (Exception) { }
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-uimods.txt"), sb.ToString());
            Shutdown();
            return;
        }

        // 日志写入器自检：批量刷盘后内容不能丢、行数要对得上
        if (e.Args.Contains("--selftest-log"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-log-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Directory.CreateDirectory(work);
                var path = Path.Combine(work, "game.log");
                const int lines = 20000;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var w = new GameLogWriter(path))
                {
                    for (var i = 0; i < lines; i++)
                        w.WriteLine("[12:00:00] [main/INFO]: 第 " + i + " 行日志 with some padding text");
                }
                sw.Stop();
                var written = File.ReadAllLines(path).Length;
                var lastOk = File.ReadAllText(path).Contains("第 " + (lines - 1) + " 行日志");
                sb.AppendLine("写入 " + lines + " 行，用时 " + sw.ElapsedMilliseconds + "ms，落盘 " + written +
                              " 行，末行完整=" + lastOk);
                sb.AppendLine(written == lines && lastOk ? "LOG SELFTEST PASS" : "LOG SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("LOG SELFTEST FAIL " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-log.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 客户端管理自检：改名要连带目录/json/jar/其它版本的 inheritsFrom；删除前能查出依赖
        if (e.Args.Contains("--selftest-client"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-cli-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                var versions = Path.Combine(work, "versions");
                void MakeVersion(string id, string? inherits = null, bool withJar = false)
                {
                    var dir = Path.Combine(versions, id);
                    Directory.CreateDirectory(dir);
                    var node = new System.Text.Json.Nodes.JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "release",
                        ["mainClass"] = "net.minecraft.client.main.Main"
                    };
                    if (inherits is not null) node["inheritsFrom"] = inherits;
                    File.WriteAllText(Path.Combine(dir, id + ".json"), node.ToJsonString());
                    if (withJar) File.WriteAllText(Path.Combine(dir, id + ".jar"), "jar");
                }

                MakeVersion("1.20.1", null, true);
                MakeVersion("fabric-loader-0.15-1.20.1", "1.20.1", true);
                MakeVersion("子客户端", "fabric-loader-0.15-1.20.1");

                var renamed = ClientCreator.RenameVersionDir(work, "fabric-loader-0.15-1.20.1", "我的客户端",
                    null, null, line => sb.AppendLine("  " + line));
                var newDir = Path.Combine(versions, "我的客户端");
                var jsonOk = File.Exists(Path.Combine(newDir, "我的客户端.json"));
                var idOk = false;
                if (jsonOk)
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(newDir, "我的客户端.json")));
                    idOk = node?["id"]?.ToString() == "我的客户端";
                }
                var jarOk = File.Exists(Path.Combine(newDir, "我的客户端.jar"));
                var childJson = Path.Combine(versions, "子客户端", "子客户端.json");
                var childInherits = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(childJson))?["inheritsFrom"]?.ToString();
                sb.AppendLine("改名=" + renamed + " 目录=" + Directory.Exists(newDir) + " json=" + jsonOk +
                              " id=" + idOk + " jar=" + jarOk + " 子版本 inheritsFrom=" + childInherits);

                // 目标已存在时必须拒绝，且不改动任何东西
                var reject = ClientCreator.RenameVersionDir(work, "我的客户端", "1.20.1");
                sb.AppendLine("重名目标拒绝 = " + (reject is null));

                var deps = ClientCreator.Dependents(work, "我的客户端");
                sb.AppendLine("依赖检测 = " + string.Join(",", deps) + "（应含 子客户端）");

                var ok = renamed == "我的客户端" && jsonOk && idOk && jarOk
                         && childInherits == "我的客户端" && reject is null && deps.Contains("子客户端");
                sb.AppendLine(ok ? "CLIENT SELFTEST PASS" : "CLIENT SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("CLIENT SELFTEST FAIL " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-client.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // Modrinth 自检：检索、文件列表、下载落盘与文件名消毒
        if (e.Args.Contains("--selftest-modrinth"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-mr-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Directory.CreateDirectory(work);
                var (hits, total) = await Modrinth.SearchAsync("sodium", "1.21.1", "fabric", "relevance", 0, 20,
                    CancellationToken.None);
                sb.AppendLine("检索：命中=" + hits.Count + " 总数=" + total + "（应 >0）");
                // 图标必须是能解码的位图（Modrinth 检索接口默认给 WebP，本机解不了）
                var iconOk = 0;
                var iconWebp = 0;
                foreach (var h in hits)
                {
                    if (h.IconUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) iconWebp++;
                    if (await ModIconCache.LoadAsync(h.IconUrl) is not null) iconOk++;
                }
                sb.AppendLine($"图标：可显示={iconOk} 仍是 WebP={iconWebp}（共 {hits.Count}，应全部可显示且 WebP=0）");
                if (iconOk == 0) sb.AppendLine("  图标加载错误：" + (ModIconCache.LastError ?? "（无）"));

                var target = hits.FirstOrDefault(h => h.Title.Contains("Sodium", StringComparison.OrdinalIgnoreCase))
                             ?? hits.FirstOrDefault();
                var files = target is null
                    ? new List<ModFile>()
                    : await Modrinth.FilesAsync(target.ProjectId, "1.21.1", "fabric", CancellationToken.None);
                sb.AppendLine("文件列表：" + files.Count + " 个（应 >0）");

                var sanitizeOk = Modrinth.SafeFileName("..\\..\\evil.jar") == "evil.jar"
                                 && Modrinth.SafeFileName("a/b\\c?.jar").Length > 0;
                sb.AppendLine("文件名消毒 = " + sanitizeOk +
                              "（..\\..\\evil.jar → " + Modrinth.SafeFileName("..\\..\\evil.jar") + "）");

                var downloadOk = false;
                var skipped = false;
                if (files.Count > 0)
                {
                    var smallest = files.Where(f => f.Size > 0).OrderBy(f => f.Size).FirstOrDefault();
                    if (smallest is not null)
                    {
                        var (ok1, path1, skip1) = await Modrinth.DownloadAsync(smallest, work, null,
                            CancellationToken.None);
                        var inside = Path.GetFullPath(path1)
                            .StartsWith(Path.GetFullPath(work).TrimEnd('\\') + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase);
                        var (ok2, _, skip2) = await Modrinth.DownloadAsync(smallest, work, null,
                            CancellationToken.None);
                        downloadOk = ok1 && File.Exists(path1) && inside;
                        skipped = ok2 && skip2;
                        sb.AppendLine("下载：" + Path.GetFileName(path1) + " 落盘=" + downloadOk +
                                      " 二次下载跳过=" + skipped);
                    }
                }

                var ok = hits.Count > 0 && files.Count > 0 && sanitizeOk && downloadOk && skipped;
                sb.AppendLine(ok ? "MODRINTH SELFTEST PASS" : "MODRINTH SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("MODRINTH SELFTEST FAIL " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-modrinth.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // Java 自检：本机探测（带缓存）、Adoptium 版本映射（含非 LTS 回退）
        if (e.Args.Contains("--selftest-javafind"))
        {
            var sb = new StringBuilder();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var found = JavaFinder.Find("", Path.GetTempPath(), 21);
                sw.Stop();
                sb.AppendLine("首次探测：exe=" + found.exe + " major=" + found.major +
                              " 用时=" + sw.ElapsedMilliseconds + "ms  说明=" + found.note);

                var again = System.Diagnostics.Stopwatch.StartNew();
                var cachedMajor = JavaFinder.MajorOf(found.exe);
                again.Stop();
                var cacheFile = Path.Combine(AppConfig.Dir, "java-cache.json");
                sb.AppendLine("命中缓存：major=" + cachedMajor + " 用时=" + again.ElapsedMilliseconds +
                              "ms 缓存文件=" + File.Exists(cacheFile));

                // Adoptium：16 这种非 LTS 应当回退到 17/21 而不是直接失败
                var json = await Http.GetJsonAsync(
                    "https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse",
                    CancellationToken.None);
                var has17 = (json as System.Text.Json.Nodes.JsonArray)?.Count > 0;
                sb.AppendLine("Adoptium 17 可用=" + has17);

                var ok = found.major > 0 && again.ElapsedMilliseconds <= sw.ElapsedMilliseconds && has17;
                sb.AppendLine(ok ? "JAVAFIND SELFTEST PASS" : "JAVAFIND SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("JAVAFIND SELFTEST FAIL " + ex.Message);
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-javafind.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 账号自检：离线 UUID 是否与 Java 一致、注入器能否就位、外置登录的错误提示
        if (e.Args.Contains("--selftest-auth"))
        {
            var sb = new StringBuilder();
            try
            {
                var uuid = OfflineAuth.UuidFor("Ender_drate");
                var expect = "b9e4a538-b312-34c3-8c99-88056a1dd440";
                sb.AppendLine("离线 UUID = " + uuid + "  与 Java 一致=" + (uuid == expect));
                var a1 = OfflineAuth.Create("测试玩家");
                var a2 = OfflineAuth.Create("测试玩家");
                sb.AppendLine("Create 幂等=" + (a1.Uuid == a2.Uuid) + " uuid=" + a1.Uuid + " tokenLen=" + a1.AccessToken.Length);

                var inj = await YggdrasilAuth.EnsureInjectorAsync(CancellationToken.None);
                var injOk = File.Exists(inj) && new FileInfo(inj).Length > 100_000;
                var cached = YggdrasilAuth.FindCachedInjector();
                sb.AppendLine("注入器 = " + Path.GetFileName(inj) + " 有效=" + injOk + " 缓存查找命中=" + (cached == inj));

                var friendly = "";
                try
                {
                    await YggdrasilAuth.LoginAsync("https://launcher.endercraft.top/nope", "u", "p",
                        CancellationToken.None);
                }
                catch (AuthException ex) { friendly = ex.Message; }
                sb.AppendLine("错误提示 = " + friendly);
                var friendlyOk = friendly.Contains("API 地址不对") || friendly.Contains("外置登录失败");

                var ok = uuid == expect && a1.Uuid == a2.Uuid && injOk && cached == inj && friendlyOk;
                sb.AppendLine(ok ? "AUTH SELFTEST PASS" : "AUTH SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("AUTH SELFTEST FAIL " + ex.Message);
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-auth.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 启动参数自检：占位符替换、主类、超长 classpath 走 @argfile
        if (e.Args.Contains("--selftest-args"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-args-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Directory.CreateDirectory(Path.Combine(work, "versions", "t"));
                var merged = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = "t",
                    ["mainClass"] = "net.minecraft.client.main.Main",
                    ["type"] = "release",
                    ["assetIndex"] = new System.Text.Json.Nodes.JsonObject { ["id"] = "17" },
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["jvm"] = new System.Text.Json.Nodes.JsonArray
                        {
                            "-Djava.library.path=${natives_directory}",
                            "-cp",
                            "${classpath}"
                        },
                        ["game"] = new System.Text.Json.Nodes.JsonArray
                        {
                            "--username",
                            "${auth_player_name}",
                            "--version",
                            "${version_name}",
                            "--gameDir",
                            "${game_directory}"
                        }
                    }
                };
                var acct = new Account { Type = "offline", Name = "测试玩家", Uuid = "0123456789abcdef0123456789abcdef", AccessToken = "0" };
                var (args, natives) = GameLauncher.BuildCommand(work, "t", merged, acct, "java.exe", 2048, "", work);

                var joined = string.Join(" ", args);
                var nameOk = joined.Contains("测试玩家");
                var mainOk = args.Contains("net.minecraft.client.main.Main");
                var noToken = !joined.Contains("${") && !joined.Contains("#{" + "");
                var memOk = args.Contains("-Xmx2048m");
                sb.AppendLine("占位符替换=" + nameOk + " 主类=" + mainOk + " 无残留占位符=" + noToken + " 内存=" + memOk);

                // 超长 classpath：造 400 个真实存在的库文件，让 classpath 超过 30000 字符
                var libs = new System.Text.Json.Nodes.JsonArray();
                long cpLen = 0;
                for (var i = 0; i < 400; i++)
                {
                    var rel = Path.Combine("very" + i.ToString("000"), "longlibraryname-" + i.ToString("000") + ".jar");
                    var full = Path.Combine(work, "libraries", rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, "x");
                    cpLen += full.Length + 1;
                    libs.Add(new System.Text.Json.Nodes.JsonObject
                    {
                        ["name"] = "group:artifact" + i + ":1.0",
                        ["downloads"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["artifact"] = new System.Text.Json.Nodes.JsonObject { ["path"] = rel }
                        }
                    });
                }
                ((System.Text.Json.Nodes.JsonObject)merged["arguments"]!)["jvm"] =
                    new System.Text.Json.Nodes.JsonArray("-cp", "${classpath}");
                merged["libraries"] = libs;
                var (args2, _) = GameLauncher.BuildCommand(work, "t", merged, acct, "java.exe", 2048, "", work);
                var atArg = args2.FirstOrDefault(a => a.StartsWith("@", StringComparison.Ordinal));
                var argFileOk = false;
                if (atArg is not null)
                {
                    var path = atArg[1..];
                    if (File.Exists(path))
                    {
                        var text = File.ReadAllText(path);
                        argFileOk = text.Contains("-cp") && text.Length > 30000;
                    }
                }
                sb.AppendLine("超长 classpath（约 " + cpLen + " 字符）→ argfile=" + (atArg ?? "无") +
                              " 文件有效=" + argFileOk);

                var ok = nameOk && mainOk && memOk && argFileOk;
                sb.AppendLine(ok ? "ARGS SELFTEST PASS" : "ARGS SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("ARGS SELFTEST FAIL " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-args.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 加载器自检：Fabric 真装一次 + 安装器结果识别逻辑
        if (e.Args.Contains("--selftest-loader"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-loader-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Directory.CreateDirectory(Path.Combine(work, "versions", "1.20.1"));

                // 1) 结果识别：只认新出现且带关键字的目录
                var before = Loaders.SnapshotVersions(work);
                Directory.CreateDirectory(Path.Combine(work, "versions", "1.19.2"));
                Directory.CreateDirectory(Path.Combine(work, "versions", "1.20.1-forge-47.2.0"));
                var found = Loaders.FindNewVersion(work, before, new[] { "forge", "47.2.0" });
                var wrong = Loaders.FindNewVersion(work, before, new[] { "neoforge" });
                sb.AppendLine("识别安装结果 = " + (found ?? "null") + "（应为 1.20.1-forge-47.2.0）不匹配项=" + (wrong ?? "null"));

                // 2) Fabric 真装一次（纯文本 profile，不依赖 Java）
                var versions = await Loaders.VersionsAsync(LoaderKind.Fabric, "1.20.1", CancellationToken.None);
                var pick = versions.FirstOrDefault(v => v.Channel == "稳定") ?? versions.FirstOrDefault();
                var madeId = await Loaders.InstallAsync(LoaderKind.Fabric, "1.20.1", pick!.Version, work, "",
                    null, line => sb.AppendLine("  " + line), CancellationToken.None);
                var jsonPath = Path.Combine(work, "versions", madeId, madeId + ".json");
                var jsonOk = File.Exists(jsonPath);
                var inheritOk = false;
                if (jsonOk)
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(jsonPath));
                    inheritOk = node?["inheritsFrom"]?.ToString() == "1.20.1";
                }
                sb.AppendLine("Fabric 安装 = " + madeId + "  json=" + jsonOk + " inheritsFrom(1.20.1)=" + inheritOk);

                var ok = found == "1.20.1-forge-47.2.0" && wrong is null && jsonOk && inheritOk;
                sb.AppendLine(ok ? "LOADER SELFTEST PASS" : "LOADER SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("LOADER SELFTEST FAIL " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-loader.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 资源检测自检：索引在但对象被删，必须能被 ScanMissing 发现
        if (e.Args.Contains("--selftest-assets"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-assets-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                var idxDir = Path.Combine(work, "assets", "indexes");
                Directory.CreateDirectory(idxDir);
                var hash = "b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356";
                var index = new System.Text.Json.Nodes.JsonObject
                {
                    ["objects"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["minecraft/lang/zh_cn.json"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["hash"] = hash,
                            ["size"] = 9101
                        }
                    }
                };
                File.WriteAllText(Path.Combine(idxDir, "test.json"), index.ToJsonString());
                var merged = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = "test",
                    ["assetIndex"] = new System.Text.Json.Nodes.JsonObject { ["id"] = "test" }
                };

                var miss1 = Installer.ScanMissing(work, "test", merged);
                sb.AppendLine("对象缺失时：AssetIndex=" + miss1.AssetIndex +
                              " 缺失对象=" + miss1.AssetObjectsMissing + " Any=" + miss1.Any +
                              "（应为 0 / 1 / True）");

                var objDir = Path.Combine(work, "assets", "objects", hash[..2]);
                Directory.CreateDirectory(objDir);
                File.WriteAllText(Path.Combine(objDir, hash), "x");
                var miss2 = Installer.ScanMissing(work, "test", merged);
                sb.AppendLine("对象齐全时：缺失对象=" + miss2.AssetObjectsMissing + " AssetAny=" +
                              (miss2.AssetIndex || miss2.AssetObjectsMissing > 0) + "（应为 0 / False）");

                // virtual 资源铺开（1.6 及更早）
                var vIdx = new System.Text.Json.Nodes.JsonObject
                {
                    ["virtual"] = true,
                    ["objects"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["minecraft/sounds/click.ogg"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["hash"] = hash,
                            ["size"] = 9101
                        }
                    }
                };
                File.WriteAllText(Path.Combine(idxDir, "legacy.json"), vIdx.ToJsonString());
                var mergedVirtual = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = "legacy",
                    ["assetIndex"] = new System.Text.Json.Nodes.JsonObject { ["id"] = "legacy" }
                };
                await Installer.EnsureVirtualAssetsAsync(work, mergedVirtual, null, CancellationToken.None);
                var virtualFile = Path.Combine(work, "assets", "virtual", "legacy", "minecraft", "sounds", "click.ogg");
                var virtualOk = File.Exists(virtualFile);
                sb.AppendLine("virtual 资源铺开 = " + virtualOk + "（" + virtualFile + "）");

                var ok = miss1.AssetObjectsMissing == 1 && miss1.Any && miss2.AssetObjectsMissing == 0 && virtualOk;
                sb.AppendLine(ok ? "ASSETS SELFTEST PASS" : "ASSETS SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("ASSETS SELFTEST FAIL " + ex);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-assets.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 下载层自检：重复任务去重、取消后不留 .part、小文件按 sha1 校验
        if (e.Args.Contains("--selftest-http"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-http-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                Directory.CreateDirectory(work);
                const string assetUrl = "https://resources.download.minecraft.net/b6/b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356";
                const string assetSha = "b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356";
                var dest = Path.Combine(work, "asset.bin");
                var dup = new List<Http.DownloadTask>
                {
                    new(assetUrl, dest, assetSha, 9101),
                    new(assetUrl, dest, assetSha, 9101)
                };
                var (ok, failed) = await Http.DownloadManyAsync(dup, 8, null, CancellationToken.None);
                var shaOk = File.Exists(dest) && Http.Sha1Of(dest) == assetSha;
                sb.AppendLine($"重复任务：ok={ok} failed={failed} 文件有效={shaOk}（应为 ok=1 failed=0）");

                // 取消：挑个大文件，下 300ms 立刻取消，检查有没有留下 .part
                var big = Path.Combine(work, "big.bin");
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                var canceled = false;
                try
                {
                    await Http.DownloadAsync("https://launcher.endercraft.top/launcher/MineLibLauncher-win-x64.zip",
                        big, null, null, cts.Token);
                }
                catch (OperationCanceledException) { canceled = true; }
                var parts = Directory.GetFiles(work, "*.part", SearchOption.AllDirectories);
                sb.AppendLine($"取消：已取消={canceled} 残留 .part={parts.Length}（应为 0）");
                sb.AppendLine(ok == 1 && failed == 0 && shaOk && canceled && parts.Length == 0
                    ? "HTTP SELFTEST PASS" : "HTTP SELFTEST FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("HTTP SELFTEST FAIL " + ex);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-http.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 更新替换脚本自检：验证「成功替换」与「复制失败时保留暂存」两条路径
        if (e.Args.Contains("--selftest-update"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-upd-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                var stage = Path.Combine(work, "stage");
                var target = Path.Combine(work, "target");
                Directory.CreateDirectory(stage);
                Directory.CreateDirectory(target);
                // 目标故意换个名字：自检进程本身就占用 MineLibLauncher.exe，
                // 用同名会让脚本一直等自己退出（这也是脚本按目标文件名等待的意义）
                var exe = Path.Combine(target, "Target.exe");
                File.WriteAllText(exe, "OLD");
                File.WriteAllText(Path.Combine(stage, "MineLibLauncher.exe"), "NEW");
                Updates.WriteApplyScriptFor(stage, exe, relaunch: false);

                void RunScript()
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        "/c \"" + Path.Combine(stage, "apply-update.cmd") + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = stage
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc!.WaitForExit(60000);
                }

                RunScript();
                Thread.Sleep(600);
                var content = File.ReadAllText(exe);
                var c1 = content == "NEW";
                var c2 = !File.Exists(Path.Combine(stage, "apply-update.cmd"));
                var c3 = !File.Exists(Path.Combine(stage, "MineLibLauncher.exe"));
                var c4 = !File.Exists(Path.Combine(stage, "apply-failed.txt"));
                var ok1 = c1 && c2 && c3 && c4;
                sb.AppendLine("成功替换 = " + ok1 + "（内容新=" + c1 + " 脚本已删=" + c2 +
                              " 暂存已删=" + c3 + " 无失败标记=" + c4 + "）");

                // 目标被占用：复制应失败，暂存 exe 与失败标记保留
                File.WriteAllText(Path.Combine(stage, "MineLibLauncher.exe"), "NEW2");
                Updates.WriteApplyScriptFor(stage, exe, relaunch: false);
                using (var hold = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    RunScript();
                }
                Thread.Sleep(600);
                var ok2 = File.Exists(Path.Combine(stage, "MineLibLauncher.exe"))
                          && File.Exists(Path.Combine(stage, "apply-failed.txt"));
                sb.AppendLine("占用时保留暂存 = " + ok2 +
                              "（暂存=" + File.Exists(Path.Combine(stage, "MineLibLauncher.exe")) +
                              " 失败标记=" + File.Exists(Path.Combine(stage, "apply-failed.txt")) + "）");
                sb.AppendLine(ok1 && ok2 ? "UPDATE SCRIPT PASS" : "UPDATE SCRIPT FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("UPDATE SCRIPT FAIL " + ex);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-update.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 迁移自检：造一份旧版（一个客户端一个目录）的假数据，验证合并成单根目录
        if (e.Args.Contains("--selftest-migrate"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-mig-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                var rootDir = Path.Combine(work, "root");
                var a = Path.Combine(work, "client-a");
                var b = Path.Combine(work, "client-b");
                void FakeVersion(string dir, string id, string? inherits)
                {
                    var vdir = Path.Combine(dir, "versions", id);
                    Directory.CreateDirectory(vdir);
                    var json = new System.Text.Json.Nodes.JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "release",
                        ["releaseTime"] = "2026-01-01T00:00:00+00:00",
                        ["mainClass"] = "net.minecraft.client.main.Main"
                    };
                    if (inherits is not null) json["inheritsFrom"] = inherits;
                    File.WriteAllText(Path.Combine(vdir, id + ".json"), json.ToJsonString());
                }
                void FakeFile(string path, string content)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, content);
                }

                FakeVersion(a, "1.21.1", null);
                FakeVersion(a, "fabric-loader-0.19.3-1.21.1", "1.21.1");
                FakeFile(Path.Combine(a, "libraries", "org", "x", "x.jar"), "lib-a");
                FakeFile(Path.Combine(a, "assets", "objects", "ab", "abcdef"), "asset-a");
                FakeFile(Path.Combine(a, "mods", "sodium.jar"), "mod-a");
                FakeFile(Path.Combine(a, "options.txt"), "fov:90");

                FakeVersion(b, "1.20.6", null);
                FakeFile(Path.Combine(b, "libraries", "org", "y", "y.jar"), "lib-b");
                FakeFile(Path.Combine(b, "libraries", "org", "x", "x.jar"), "lib-a");
                FakeFile(Path.Combine(b, "saves", "world1", "level.dat"), "save");

                var cfg = new AppConfig { GameDir = rootDir, SingleRootMigrated = false };
                cfg.Instances.Add(new InstanceRef { Name = "旧客户端A", Dir = a });
                cfg.Instances.Add(new InstanceRef { Name = "旧客户端B", Dir = b });

                var report = Workspace.EnsureSingleRoot(cfg, line => sb.AppendLine("  " + line));
                sb.AppendLine("changed = " + report.Changed);
                var versions = Directory.GetDirectories(Path.Combine(rootDir, "versions")).Select(Path.GetFileName).OrderBy(x => x).ToList();
                sb.AppendLine("versions/ = " + string.Join(" | ", versions));
                sb.AppendLine("客户端 = " + string.Join(" | ", cfg.Clients.Select(c => c.VersionId + "(" + c.Name + ")")));
                sb.AppendLine("libraries 数 = " + Directory.GetFileSystemEntries(Path.Combine(rootDir, "libraries"), "*", SearchOption.AllDirectories).Count(x => File.Exists(x)));
                sb.AppendLine("assets 数 = " + Directory.GetFileSystemEntries(Path.Combine(rootDir, "assets"), "*", SearchOption.AllDirectories).Count(x => File.Exists(x)));
                sb.AppendLine("旧目录 A 还在 = " + Directory.Exists(a) + "  B 还在 = " + Directory.Exists(b));
                if (Directory.Exists(b))
                    sb.AppendLine("B 残留 = " + string.Join(" | ",
                        Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories).Select(x => x.Replace(b, ""))));

                // 断言
                var ok = versions.Count == 3
                         && cfg.Clients.Any(c => c.VersionId == "fabric-loader-0.19.3-1.21.1")
                         && cfg.Clients.Any(c => c.VersionId == "1.20.6")
                         && cfg.Clients.All(c => c.VersionId != "1.21.1")
                         && !Directory.Exists(a) && !Directory.Exists(b)
                         && File.Exists(Path.Combine(rootDir, "versions", "fabric-loader-0.19.3-1.21.1", "mods", "sodium.jar"))
                         && File.Exists(Path.Combine(rootDir, "versions", "1.20.6", "saves", "world1", "level.dat"));
                // 改名逻辑：加载器版本目录改成客户端名字，json 里的 id 也要跟着变
                var renamed = ClientCreator.RenameVersionDir(rootDir, "fabric-loader-0.19.3-1.21.1", "我的客户端",
                    "Fabric", "1.21.1");
                var renamedJson = Path.Combine(rootDir, "versions", "我的客户端", "我的客户端.json");
                var renamedId = "";
                if (File.Exists(renamedJson))
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(renamedJson));
                    renamedId = node?["id"]?.ToString() ?? "";
                }
                sb.AppendLine("改名结果 = " + (renamed ?? "null") + "  json.id = " + renamedId +
                              "  目录存在 = " + Directory.Exists(Path.Combine(rootDir, "versions", "我的客户端")));
                ok = ok && renamed == "我的客户端" && renamedId == "我的客户端";

                sb.AppendLine(ok ? "MIGRATE PASS" : "MIGRATE FAIL");
            }
            catch (Exception ex)
            {
                sb.AppendLine("MIGRATE FAIL " + ex);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-migrate.txt"), sb.ToString());
            Shutdown();
            return;
        }
        // 下载源自检：分别用官方与镜像走一遍 清单→版本 json→资源→库，结果写临时文件
        if (e.Args.Contains("--selftest-source"))
        {
            var sb = new StringBuilder();
            var work = Path.Combine(Path.GetTempPath(), "minelib-src");
            try
            {
                Directory.CreateDirectory(work);
                foreach (var s in DownloadSource.All)
                {
                    DownloadSources.Apply(s.Id);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var manifest = await Http.GetJsonAsync(Installer.ManifestUrl);
                    var versions = manifest["versions"] as System.Text.Json.Nodes.JsonArray;
                    var entry = versions?.OfType<System.Text.Json.Nodes.JsonObject>()
                        .FirstOrDefault(v => v["id"]?.ToString() == "1.21.1")
                        ?? throw new InvalidDataException("清单里没有 1.21.1");
                    var vjsonUrl = s.VersionJson?.Invoke("1.21.1") ?? entry["url"]!.ToString();
                    var vj = await Http.GetJsonAsync(vjsonUrl);
                    var indexUrl = vj["assetIndex"]?["url"]?.ToString() ?? "";
                    var idxPath = Path.Combine(work, s.Id + "-index.json");
                    await Http.DownloadAsync(indexUrl, idxPath, null, null);
                    var idx = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(idxPath));
                    var obj = idx?["objects"]?.AsObject().FirstOrDefault();
                    var hash = obj?.Value?["hash"]?.ToString() ?? "";
                    var assetDest = Path.Combine(work, s.Id + "-asset.bin");
                    var assetOk = hash.Length >= 2 &&
                                  await Http.DownloadAsync(s.AssetObject(hash), assetDest, hash);
                    // 拿一个有 artifact 的官方库验证 maven 地址
                    var libUrl = "";
                    var libDest = Path.Combine(work, s.Id + "-lib.jar");
                    var libOk = false;
                    if (vj["libraries"] is System.Text.Json.Nodes.JsonArray libs)
                    {
                        foreach (var l in libs)
                        {
                            if (l is not System.Text.Json.Nodes.JsonObject lo) continue;
                            if (Installer.LibPath(lo).Length == 0) continue;
                            var u = Installer.LibUrl(lo);
                            if (string.IsNullOrEmpty(u)) continue;
                            libUrl = u;
                            libOk = await Http.DownloadAsync(u, libDest, null, null);
                            break;
                        }
                    }
                    sw.Stop();
                    sb.AppendLine("[" + s.Name + "] 用时 " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");
                    sb.AppendLine("  清单      " + Installer.ManifestUrl);
                    sb.AppendLine("  版本 json " + vjsonUrl);
                    sb.AppendLine("  资源索引  " + (indexUrl.Length > 80 ? indexUrl[..80] + "…" : indexUrl));
                    sb.AppendLine("  一个资源  " + (assetOk ? "OK  " : "FAIL ") + s.AssetObject(hash));
                    sb.AppendLine("  一个库    " + (libOk ? "OK  " : "FAIL ") + libUrl);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("SOURCE SELFTEST FAIL " + ex.Message);
            }
            DownloadSources.Apply("official");
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "minelib-source.txt"), sb.ToString());
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
            // 自检用独立根目录，别碰用户真实的 .minecraft
            cfg.GameDir = Environment.GetEnvironmentVariable("MINELIB_NEW_DIR")
                          ?? Path.Combine(Path.GetTempPath(), "minelib-root-" + Guid.NewGuid().ToString("N")[..6]);
            cfg.Clients.Clear();
            var name = Environment.GetEnvironmentVariable("MINELIB_NEW_NAME") ?? "测试新客户端";
            var mc = Environment.GetEnvironmentVariable("MINELIB_NEW_MC") ?? "1.7.10";
            var loader = Environment.GetEnvironmentVariable("MINELIB_NEW_LOADER") ?? "不装（纯原版）";
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var progress = new Progress<InstallProgress>(p => { if (p.Fraction is > 0 and < 1) sb.AppendLine("  " + p.Stage); });
                var result = await ClientCreator.CreateAsync(cfg, name, mc, loader, 16, "", progress,
                    line => sb.AppendLine("  log: " + line), CancellationToken.None);
                sw.Stop();
                sb.AppendLine($"创建成功 root={cfg.GameDir} version={result.VersionId} note={result.Note} 用时={sw.Elapsed.TotalSeconds:0}s");
                sb.AppendLine("客户端列表 = " + string.Join(" | ", cfg.Clients.Select(c => c.Display)));
                sb.AppendLine("versions/ = " + string.Join(" | ", Directory.GetDirectories(Path.Combine(cfg.GameDir, "versions")).Select(Path.GetFileName)));
                var diskMb = new DirectoryInfo(cfg.GameDir)
                    .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1048576.0;
                sb.AppendLine("磁盘占用 = " + Math.Round(diskMb, 1) + " MB");

                var scanned = Versions.Scan(cfg.GameDir);
                sb.AppendLine("版本扫描 = " + string.Join(" | ", scanned.Select(v => v.Id + "(" + v.Subtitle + ")")));

                var merged = Versions.Resolve(cfg.GameDir, result.VersionId);
                var javaInfo = JavaFinder.Find("", cfg.GameDir, merged["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 21);
                sb.AppendLine("java = " + javaInfo.exe + "  " + javaInfo.note);
                var acct = OfflineAuth.Create("Tester");
                await GameLauncher.PrepareAsync(cfg.GameDir, result.VersionId, merged, 16, null, CancellationToken.None);
                var (args, _) = GameLauncher.BuildCommand(cfg.GameDir, result.VersionId, merged, acct,
                    javaInfo.exe, 2048, "", cfg.GameDir);

                var proc = new GameProcess();
                var lines = new List<string>();
                var exited = new TaskCompletionSource<int>();
                proc.LineReceived += line => { lock (lines) { if (lines.Count < 200) lines.Add(line); } };
                proc.Exited += code => exited.TrySetResult(code);
                proc.Start(javaInfo.exe, args, cfg.GameDir, Path.Combine(cfg.GameDir, "logs", "create-test.log"));
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
        catch (Exception ex)
        {
            // 启动阶段崩了也要留下证据，而不是静默退出
            var logPath = CrashReporter.Report(ex, "启动");
            if (!Environment.GetCommandLineArgs()
                .Any(a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    NoticeWindow.ShowError("在打开启动器时出现错误",
                        "重新打开启动器以重试。如果一直打不开，把日志发给作者。", logPath);
                }
                catch (Exception) { }
            }
            Shutdown(1);
        }
    }
}