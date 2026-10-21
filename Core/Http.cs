using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MineLib.Core;

/// <summary>官方源访问。所有请求都带 UA，不伪装成官方启动器。</summary>
public static class Http
{
    public const string UserAgent = "MineLibLauncher/0.1 (+https://github.com/MineLib-Initialization)";

    private static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        c.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return c;
    }

    public static async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct = default)
    {
        using var resp = await Client.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(text) ?? throw new InvalidDataException("空 JSON: " + url);
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var resp = await Client.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

    public static async Task<bool> DownloadAsync(string url, string dest, string? sha1 = null,
        IProgress<(long done, long total)>? progress = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest) && (sha1 is null || Sha1Of(dest) == sha1))
                return true;

            var tmp = dest + ".part";
            using (var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                long done = 0;
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    var buf = new byte[1 << 16];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        progress?.Report((done, total));
                    }
                }
            }

            if (sha1 is not null && Sha1Of(tmp) != sha1)
            {
                File.Delete(tmp);
                return false;
            }
            File.Move(tmp, dest, true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public sealed record DownloadTask(string Url, string Dest, string? Sha1, long Size);

    public sealed record BulkProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal,
        double BytesPerSecond)
    {
        public string SpeedText => BytesPerSecond >= 1048576
            ? (BytesPerSecond / 1048576).ToString("0.0") + " MB/s"
            : (BytesPerSecond / 1024).ToString("0.0") + " KB/s";
    }

    /// <summary>
    /// 并发下载一批文件。资源文件动辄几千个小文件，串行下载是纯浪费 —— 这里开 N 个并发连接，
    /// 汇总进度（已完成文件数 + 总字节 + 实时速度）。已存在的文件直接跳过。
    /// </summary>
    public static async Task<(int ok, int failed)> DownloadManyAsync(IReadOnlyList<DownloadTask> tasks,
        int concurrency, IProgress<BulkProgress>? progress, CancellationToken ct)
    {
        if (tasks.Count == 0) return (0, 0);
        concurrency = Math.Clamp(concurrency, 1, 32);

        var total = tasks.Count;
        var totalBytes = tasks.Sum(t => Math.Max(0, t.Size));
        int done = 0, ok = 0, failed = 0;
        long bytes = 0;
        var started = DateTime.UtcNow;
        var gate = new object();

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

        foreach (var t in tasks)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(t.Dest) && (t.Sha1 is null || Sha1Of(t.Dest) == t.Sha1))
            {
                Interlocked.Increment(ref done);
                Interlocked.Increment(ref ok);
                Interlocked.Add(ref bytes, Math.Max(0, t.Size));
                continue;
            }

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
            }, ct));
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            timerCts.Cancel();
            try { await timer.ConfigureAwait(false); } catch (Exception) { }
            semaphore.Dispose();
        }
        Report();
        return (ok, failed);
    }

    public static string Sha1Of(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
