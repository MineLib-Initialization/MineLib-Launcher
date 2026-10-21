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
