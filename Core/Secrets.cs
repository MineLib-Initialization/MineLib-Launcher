using System.Runtime.InteropServices;
using System.Text;

namespace MineLib.Core;

/// <summary>
/// 配置里的令牌不能明文躺着：用 Windows DPAPI（CurrentUser 作用域）加密，
/// 只有同一台机器上的同一个用户能解开。换机器/换用户解不开时当作没有令牌，不让程序崩。
///
/// 直接 P/Invoke crypt32 的 CryptProtectData / CryptUnprotectData：不引 NuGet 包，
/// 单文件发布也不会凭空多出依赖。
/// </summary>
public static class Secrets
{
    /// <summary>密文前缀。带前缀的按密文解，没有前缀的当作老版本明文（下次保存时自动迁移）。</summary>
    public const string Prefix = "dpapi:v1:";

    /// <summary>附加熵：和本程序绑一下，别的程序拿同一个用户身份也不容易解。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MineLib.Launcher.secrets.v1");

    /// <summary>最近一次失败的原因（日志/自检用）。</summary>
    public static string? LastError { get; private set; }

    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密；空字符串原样返回（离线账号的 token 本来就是空的）。</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (IsProtected(plain)) return plain!;                 // 已经是密文，别二次加密
        try
        {
            var blob = Crypt(true, Encoding.UTF8.GetBytes(plain!));
            if (blob is null) return plain!;
            return Prefix + Convert.ToBase64String(blob);
        }
        catch (Exception ex)
        {
            // 理论上只有非 Windows 才会走到这里：不要因此丢掉令牌，退回明文并记下来
            LastError = "加密失败，退回明文：" + ex.GetType().Name;
            return plain!;
        }
    }

    /// <summary>解密；明文原样返回（迁移期）；解不开返回空串。</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!IsProtected(stored)) return stored!;              // 老版本的明文
        try
        {
            var payload = Convert.FromBase64String(stored![Prefix.Length..]);
            var bytes = Crypt(false, payload);
            if (bytes is null) return "";
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            LastError = "解密失败（换了机器或用户？）：" + ex.GetType().Name;
            return "";
        }
    }

    /// <summary>日志用：只留头尾，别把令牌写进日志。</summary>
    public static string Mask(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "（空）";
        if (token!.Length <= 8) return "***";
        return token[..4] + "…" + token[^4..];
    }

    // ---------------------------------------------------------------- 序列化

    /// <summary>
    /// 给账号令牌用的 JSON 转换器：写出去是密文，读进来自动解密；
    /// 读到老版本的明文也照常收下，下次保存就变密文（自动迁移）。
    /// </summary>
    public sealed class ProtectedStringConverter : System.Text.Json.Serialization.JsonConverter<string>
    {
        public override string Read(ref System.Text.Json.Utf8JsonReader reader,
            Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
            Unprotect(reader.GetString());

        public override void Write(System.Text.Json.Utf8JsonWriter writer, string value,
            System.Text.Json.JsonSerializerOptions options) =>
            writer.WriteStringValue(Protect(value));
    }

    // ---------------------------------------------------------------- DPAPI
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pIn, string? description,
        ref DataBlob pEntropy, IntPtr pReserved, IntPtr pPrompt, int flags, ref DataBlob pOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pIn, IntPtr ppszDescription,
        ref DataBlob pEntropy, IntPtr pReserved, IntPtr pPrompt, int flags, ref DataBlob pOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>protect=true 加密、false 解密；失败返回 null。</summary>
    private static byte[]? Crypt(bool protect, byte[] input)
    {
        var inBlob = new DataBlob();
        var outBlob = new DataBlob();
        var entropyBlob = new DataBlob();
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            inBlob.Size = input.Length;
            inBlob.Data = inHandle.AddrOfPinnedObject();
            entropyBlob.Size = Entropy.Length;
            entropyBlob.Data = entropyHandle.AddrOfPinnedObject();

            var ok = protect
                ? CryptProtectData(ref inBlob, "MineLib", ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref outBlob);
            if (!ok || outBlob.Data == IntPtr.Zero)
            {
                LastError = "DPAPI 调用失败：" + Marshal.GetLastWin32Error();
                return null;
            }
            var result = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
            return result;
        }
        finally
        {
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
            inHandle.Free();
            entropyHandle.Free();
        }
    }
}
