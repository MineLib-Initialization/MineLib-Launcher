using System.Security.Cryptography;
using System.Text;

namespace MineLib.Core;

/// <summary>
/// 更新清单签名。光校验「下载到的文件 == 清单里写的哈希」还不够：
/// 清单和文件都来自同一台服务器，服务器一被拿下（或 DNS/证书被做掉）就能一起换掉。
/// 所以清单用离线私钥签名、启动器内置公钥验签，验不过就不更新。
///
/// 签名的是**规范串**（字段顺序固定、用 \n 拼），不做 JSON 规范化，
/// PowerShell 发布脚本和 C# 两边都能一字节不差地拼出来。
/// </summary>
public static class ManifestSign
{
    /// <summary>发布用的 ECDSA P-256 公钥（SubjectPublicKeyInfo，base64）。私钥不进仓库。</summary>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEN0e7RhbKgQJ/I2zBzV/eqL3+/6zjxdPs2T9B/a+pzfge9myp6n5pZkOLIwZNeSgIkMYI97BPvvcgH65WHsEcUw==";

    /// <summary>
    /// 参与签名的规范串。任何字段变了签名就不对，所以顺序和分隔符必须和发布脚本完全一致。
    /// 顺序：version, date, size, sha256, url, exe.size, exe.sha256,
    ///       patch.from, patch.url, patch.size, patch.sha256（没有补丁就留空）。
    /// </summary>
    public static string Canonical(string version, string date, long size, string zipSha256, string url,
        long exeSize, string exeSha256, string patchFrom, string patchUrl, long patchSize, string patchSha256)
    {
        var sb = new StringBuilder();
        void Add(string value) { sb.Append(value ?? "").Append('\n'); }
        Add(version);
        Add(date);
        Add(size.ToString());
        Add((zipSha256 ?? "").ToLowerInvariant());
        Add(url);
        Add(exeSize.ToString());
        Add((exeSha256 ?? "").ToLowerInvariant());
        Add(patchFrom);
        Add(patchUrl);
        Add(patchSize.ToString());
        Add((patchSha256 ?? "").ToLowerInvariant());
        return sb.ToString();
    }

    /// <summary>
    /// 直接从清单 JSON 取字段拼规范串。签名（发布脚本）和验签（启动器）都走这一个函数，
    /// 不会出现两边字段顺序/取值方式不一致的问题。
    /// </summary>
    public static string CanonicalFrom(System.Text.Json.Nodes.JsonObject manifest) => Canonical(
        manifest["version"]?.ToString() ?? "",
        manifest["date"]?.ToString() ?? "",
        AsLong(manifest["size"]),
        manifest["sha256"]?.ToString() ?? "",
        manifest["url"]?.ToString() ?? "",
        AsLong(manifest["exe"]?["size"]),
        manifest["exe"]?["sha256"]?.ToString() ?? "",
        manifest["patch"]?["from"]?.ToString() ?? "",
        manifest["patch"]?["url"]?.ToString() ?? "",
        AsLong(manifest["patch"]?["size"]),
        manifest["patch"]?["sha256"]?.ToString() ?? "");

    /// <summary>
    /// 容错读数字：JSON 里同样一个数可能被序列化成 Int32 / Int64 / double / 字符串，
    /// 直接 GetValue&lt;long&gt;() 会在 Int32 上抛异常（会让整次更新检查失败）。
    /// </summary>
    internal static long AsLong(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<long>(); } catch (Exception) { }
        try { return (long)node.GetValue<double>(); } catch (Exception) { }
        return long.TryParse(node.ToString(), out var v) ? v : 0;
    }

    /// <summary>用给定公钥验签（自检里会传测试公钥；线上用内置公钥）。</summary>
    internal static bool Verify(string canonical, string signatureBase64, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(publicKeyBase64)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(canonical),
                Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch (Exception ex)
        {
            LastError = "验签异常：" + ex.GetType().Name;
            return false;
        }
    }

    /// <summary>自检用：替换验签用的公钥（null = 用内置的）。</summary>
    internal static string? PublicKeyOverride;

    /// <summary>用内置公钥验签。</summary>
    public static bool Verify(string canonical, string signatureBase64) =>
        Verify(canonical, signatureBase64, PublicKeyOverride ?? PublicKeyBase64);

    /// <summary>自检/发布工具用：生成一对密钥，返回 (公钥 base64, 私钥 base64)。</summary>
    internal static (string publicKey, string privateKey) NewKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
    }

    /// <summary>自检/发布工具用：签名。</summary>
    internal static string Sign(string canonical, string privateKeyBase64)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        return Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
    }

    public static string? LastError { get; private set; }
}
