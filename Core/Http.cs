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

    public static string Sha1Of(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
