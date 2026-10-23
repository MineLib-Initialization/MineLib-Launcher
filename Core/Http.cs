using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MineLib.Core;

/// <summary>官方源访问。所有请求都带 UA，不伪装成官方启动器。</summary>
public static class Http
{
    public const string UserAgent = "MineLibLauncher/0.5 (+https://github.com/MineLib-Initialization)";

    /// <summary>超过这个大小的文件走分段并发（多个 Range 请求同时拉）。</summary>
    private const long RangeThreshold = 2L << 20;

    /// <summary>单文件最多切成几段同时下。</summary>
    private const int MaxParts = 16;

    private static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        // 默认的 HttpClientHandler 连接池偏保守：几千个小文件 + 几个大文件同时拉的时候，
        // 连接复用和并发上限就是瓶颈。这里直接给 SocketsHttpHandler 指定连接数。
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 64,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8
        };
        var c = new HttpClient(handler)
        {
            // 单个大文件在慢网络下可能超过 60 秒，超时交给每次调用的 CancellationToken
            Timeout = TimeSpan.FromMinutes(10),
            // 支持 HTTP/2 的源（Mojang 的资源 CDN 就是）用一条连接多路复用，省掉几千次握手
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        c.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return c;
    }

    public static async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct = default)
    {
        using var resp = await Client.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // 有的编辑器/脚本会写带 BOM 的 JSON，直接 Parse 会报错
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        return JsonNode.Parse(text) ?? throw new InvalidDataException("空 JSON: " + url);
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var resp = await Client.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return s.Length > 0 && s[0] == '\uFEFF' ? s[1..] : s;
    }

    public static async Task<JsonNode> PostJsonAsync(string url, JsonObject body,
        string? bearer = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(bearer))
            req.Headers.Add("Authorization", "Bearer " + bearer);
        using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Trim(text)}");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    public static async Task<JsonNode> PostFormAsync(string url, Dictionary<string, string> form,
        CancellationToken ct = default)
    {
        using var resp = await Client.PostAsync(url, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode}|{Trim(text)}");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    public static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    /// <summary>单文件下载。大文件自动分段并发，失败重试两次。</summary>
    public static async Task<bool> DownloadAsync(string url, string dest, string? sha1 = null,
        IProgress<(long done, long total)>? progress = null, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var ok = await DownloadOnceAsync(url, dest, sha1, progress, ct).ConfigureAwait(false);
            if (ok) return true;
            ct.ThrowIfCancellationRequested();
            if (attempt >= 2) return false;
            try { await Task.Delay(300 * (attempt + 1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
    }

    private static async Task<bool> DownloadOnceAsync(string url, string dest, string? sha1,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest) && (sha1 is null || Sha1Of(dest) == sha1))
                return true;

            var tmp = dest + ".part";
            TryDelete(tmp);

            var (size, ranges) = await ProbeAsync(url, ct).ConfigureAwait(false);

            var ok = size >= RangeThreshold && ranges
                ? await DownloadRangesAsync(url, tmp, size, progress, ct).ConfigureAwait(false)
                : await DownloadStreamAsync(url, tmp, progress, ct).ConfigureAwait(false);
            if (!ok)
            {
                TryDelete(tmp);
                return false;
            }

            if (sha1 is not null && Sha1Of(tmp) != sha1)
            {
                TryDelete(tmp);
                return false;
            }
            File.Move(tmp, dest, true);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 取消也把半成品删掉，不然反复取消会在 assets 里堆一堆 .part
            TryDelete(dest + ".part");
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>问一下有多大、是否支持 Range。拿不到就退回单流下载。</summary>
    private static async Task<(long size, bool ranges)> ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (0, false);
            var size = resp.Content.Headers.ContentLength ?? 0;
            var ranges = resp.Headers.AcceptRanges.Any(v => v.Contains("bytes", StringComparison.OrdinalIgnoreCase));
            return (size, ranges);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (0, false);
        }
    }

    /// <summary>强制走单连接下载（对比用，也是 Range 不可用时的退路）。</summary>
    public static async Task<bool> DownloadSingleStreamAsync(string url, string dest, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".part";
            TryDelete(tmp);
            var ok = await DownloadStreamAsync(url, tmp, null, ct).ConfigureAwait(false);
            if (!ok) { TryDelete(tmp); return false; }
            File.Move(tmp, dest, true);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
    }

    private static async Task<bool> DownloadStreamAsync(string url, string tmp,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        long done = 0;
        var last = 0L;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                         1 << 18, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buf = new byte[1 << 18];
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (progress is not null && Throttle(ref last))
                    progress.Report((done, total));
            }
        }
        progress?.Report((done, done));
        return true;
    }

    /// <summary>把一个文件切成几段，同时发 Range 请求写进同一个文件的不同偏移。</summary>
    private static async Task<bool> DownloadRangesAsync(string url, string tmp, long total,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        var parts = (int)Math.Clamp(total / (1L << 20), 2, MaxParts);
        var chunk = total / parts;
        await using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite,
                         1, FileOptions.Asynchronous))
            f.SetLength(total);

        long done = 0;
        var last = 0L;
        var jobs = new List<Task>(parts);

        for (var i = 0; i < parts; i++)
        {
            var start = i * chunk;
            var end = i == parts - 1 ? total - 1 : start + chunk - 1;
            jobs.Add(Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(start, end);
                using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                using var handle = File.OpenHandle(tmp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                    FileOptions.Asynchronous);
                var buf = new byte[1 << 16];
                long pos = start;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await RandomAccess.WriteAsync(handle, buf.AsMemory(0, n), pos, ct).ConfigureAwait(false);
                    pos += n;
                    var d = Interlocked.Add(ref done, n);
                    if (progress is not null && Throttle(ref last))
                        progress.Report((d, total));
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(jobs).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }

        progress?.Report((total, total));
        return new FileInfo(tmp).Length == total;
    }

    /// <summary>进度回调按 100ms 节流，不然多段并发会把 UI 线程刷爆。</summary>
    private static bool Throttle(ref long lastLocal)
    {
        var now = Environment.TickCount64;
        if (now - lastLocal < 100) return false;
        lastLocal = now;
        return true;
    }

    public sealed record DownloadTask(string Url, string Dest, string? Sha1, long Size);

    public sealed record BulkProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal,
        double BytesPerSecond)
    {
        public string SpeedText => BytesPerSecond >= 1048576
            ? (BytesPerSecond / 1048576).ToString("0.0") + " MB/s"
            : (BytesPerSecond / 1024).ToString("0.0") + " KB/s";
    }

    /// <summary>便宜判断：文件在且大小对得上。哈希校验交给 worker，避免在调用线程上跑几千次 SHA1。</summary>
    private static bool LooksPresent(string dest, long size)
    {
        try
        {
            var fi = new FileInfo(dest);
            return fi.Exists && size > 0 && fi.Length == size;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>文件已经就位（大小对得上、校验值对得上）就不用再下。</summary>
    public static bool UpToDate(string dest, string? sha1, long size)
    {
        var fi = new FileInfo(dest);
        if (!fi.Exists) return false;
        if (size > 0 && fi.Length != size) return false;
        if (sha1 is null) return size > 0;
        if (size <= 0 && fi.Length == 0) return false;
        return Sha1Of(dest) == sha1;
    }

    /// <summary>
    /// 并发下载一批文件。资源文件动辄几千个小文件，串行下载是纯浪费 —— 这里开 N 个并发连接，
    /// 汇总进度（已完成文件数 + 总字节 + 实时速度）。已存在的文件直接跳过，失败会重试。
    /// </summary>
    public static async Task<(int ok, int failed)> DownloadManyAsync(IReadOnlyList<DownloadTask> tasks,
        int concurrency, IProgress<BulkProgress>? progress, CancellationToken ct)
    {
        if (tasks.Count == 0) return (0, 0);
        concurrency = Math.Clamp(concurrency, 1, 64);

        // 同一个目标只留一个任务：否则两个 worker 会同时写同一个 .part，互相踩成坏文件
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DownloadTask>(tasks.Count);
        foreach (var t in tasks)
        {
            if (t is null || string.IsNullOrEmpty(t.Dest)) continue;
            string key;
            try { key = Path.GetFullPath(t.Dest); }
            catch (Exception) { key = t.Dest; }
            if (!seen.Add(key)) continue;
            list.Add(t);
        }

        var total = list.Count;
        var totalBytes = list.Sum(t => Math.Max(0, t.Size));
        int done = 0, ok = 0, failed = 0;
        long bytes = 0;
        var started = DateTime.UtcNow;

        void Report()
        {
            if (progress is null) return;
            var secs = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
            var snapshot = new BulkProgress(Volatile.Read(ref done), total, Interlocked.Read(ref bytes),
                totalBytes, Interlocked.Read(ref bytes) / secs);
            progress.Report(snapshot);
        }

        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Run(async () =>
        {
            while (!timerCts.IsCancellationRequested)
            {
                try { await Task.Delay(400, timerCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                Report();
            }
        }, CancellationToken.None);

        var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var workers = new List<Task>(total);

        try
        {
            foreach (var t in list)
            {
                ct.ThrowIfCancellationRequested();
                // 有校验值的任务一律交给 worker（它在下载前会先校验 SHA1，顺便把"大小对但内容坏"
                // 的文件挑出来重下）；只有"没校验值"的任务才在这里按大小跳过。
                // 关键点：这段在调用线程上跑，不能做哈希。
                if (t.Sha1 is null && LooksPresent(t.Dest, t.Size))
                {
                    Interlocked.Increment(ref done);
                    Interlocked.Increment(ref ok);
                    Interlocked.Add(ref bytes, Math.Max(0, t.Size));
                    continue;
                }

                // ConfigureAwait(false)：否则每次 await 都回到 UI 线程，接下来整批
                // UpToDate/SHA1 校验会挂在界面上，窗口就卡住了
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                workers.Add(Task.Run(async () =>
                {
                    try
                    {
                        var fine = await DownloadAsync(t.Url, t.Dest, t.Sha1, null, ct).ConfigureAwait(false);
                        if (fine)
                        {
                            Interlocked.Increment(ref ok);
                            Interlocked.Add(ref bytes, Math.Max(0, t.Size));
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        Interlocked.Increment(ref done);
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            // 取消或异常退出时也必须收掉计时器和已启动的 worker：以前调度循环一抛异常，
            // 这个每 400ms 向界面汇报一次的循环就永远活着，取消/失败几次后越积越多，
            // 界面被一堆进度回调拖到卡顿。
            timerCts.Cancel();
            try { await timer.ConfigureAwait(false); } catch (Exception) { }
            try { await Task.WhenAll(workers).ConfigureAwait(false); } catch (Exception) { }
            semaphore.Dispose();
        }
        Report();
        return (ok, failed);
    }

    /// <summary>边下边丢，只用来量吞吐（测速）。</summary>
    public static async Task<long> ProbeDownloadAsync(string url, CancellationToken ct)
    {
        using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0) total += n;
        return total;
    }

    public static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static string Sha1Of(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}