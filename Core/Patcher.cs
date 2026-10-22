using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MineLib.Core;

/// <summary>
/// 增量更新：把新 exe 与旧 exe 做内容定界分块（CDC）比对，只把变化的部分打进补丁。
/// 单文件发布改一点代码就会让后面所有字节整体位移，按固定偏移比对会全废；
/// 内容定界分块按内容切，位移不影响匹配。
/// </summary>
public static class Patcher
{
    private const uint Magic = 0x314C504D; // "MLP1"
    private const int MinChunk = 2 * 1024;
    private const int MaxChunk = 64 * 1024;
    private const ulong Mask = 0x1FFF;     // 平均 8 KB 一块

    private static readonly ulong[] Gear = BuildGear();

    private static ulong[] BuildGear()
    {
        var t = new ulong[256];
        var x = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < 256; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            t[i] = x;
        }
        return t;
    }

    private static ulong Hash(ReadOnlySpan<byte> data)
    {
        ulong h = 0xCBF29CE484222325UL;
        for (var i = 0; i < data.Length; i++)
            h = (h ^ data[i]) * 0x100000001B3UL;
        return h;
    }

    /// <summary>按内容切块：内容相同的区域在任何位置都会切出同样的块。</summary>
    private static List<(int Offset, int Length)> Chunk(byte[] data)
    {
        var list = new List<(int, int)>();
        var start = 0;
        var n = data.Length;
        while (start < n)
        {
            var max = Math.Min(MaxChunk, n - start);
            var len = max;
            if (max > MinChunk)
            {
                ulong h = 0;
                var i = start;
                var limit = start + max;
                for (; i < start + MinChunk; i++) h = (h << 1) + Gear[data[i]];
                for (; i < limit; i++)
                {
                    h = (h << 1) + Gear[data[i]];
                    if ((h & Mask) == 0) break;
                }
                len = Math.Min(i - start + 1, max);
            }
            list.Add((start, len));
            start += len;
        }
        return list;
    }

    public sealed record PatchInfo(string Path, long PatchBytes, string TargetSha256, long TargetSize,
        long BaseSize, long LiteralBytes);

    /// <summary>生成补丁：新文件 = 旧文件里的块 + 新增的字面量块。</summary>
    public static PatchInfo Create(string oldPath, string newPath, string patchPath)
    {
        var oldBytes = File.ReadAllBytes(oldPath);
        var newBytes = File.ReadAllBytes(newPath);

        var index = new Dictionary<ulong, int>(oldBytes.Length / 4096 + 16);
        foreach (var (off, len) in Chunk(oldBytes))
            index.TryAdd(Hash(oldBytes.AsSpan(off, len)), off);

        using var ops = new MemoryStream();
        long literalBytes = 0;
        var chunks = Chunk(newBytes);
        using (var w = new BinaryWriter(ops, System.Text.Encoding.UTF8, true))
        {
            w.Write(chunks.Count);
            foreach (var (off, len) in chunks)
            {
                var h = Hash(newBytes.AsSpan(off, len));
                if (index.TryGetValue(h, out var src))
                {
                    w.Write((byte)0);
                    w.Write(src);
                    w.Write(len);
                }
                else
                {
                    w.Write((byte)1);
                    w.Write(len);
                    w.Write(newBytes, off, len);
                    literalBytes += len;
                }
            }
        }

        var targetSha = Convert.ToHexString(SHA256.HashData(newBytes)).ToLowerInvariant();
        using (var fs = File.Create(patchPath))
        {
            using (var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, true))
            {
                bw.Write(Magic);
                bw.Write((long)oldBytes.Length);
                bw.Write((long)newBytes.Length);
                bw.Write(targetSha);
                bw.Flush();
            }
            ops.Position = 0;
            using (var br = new BrotliStream(fs, CompressionLevel.SmallestSize, true))
                ops.CopyTo(br);
        }

        return new PatchInfo(patchPath, new FileInfo(patchPath).Length, targetSha, newBytes.Length,
            oldBytes.Length, literalBytes);
    }

    /// <summary>应用补丁：旧文件 + 补丁 → 输出文件，并核对结果 sha256。</summary>
    public static string Apply(string oldPath, string patchPath, string outPath)
    {
        string targetSha;
        long newSize;
        using (var fs = File.OpenRead(patchPath))
        {
            using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, true);
            if (br.ReadUInt32() != Magic) throw new InvalidDataException("不是 MineLib 补丁文件");
            var oldSize = br.ReadInt64();
            newSize = br.ReadInt64();
            targetSha = br.ReadString();
            using (var oldFs = File.OpenRead(oldPath))
            {
                if (oldFs.Length != oldSize)
                    throw new InvalidDataException("本地文件与补丁的基线大小不符（" + oldFs.Length + " ≠ " + oldSize + "）");
                using var brl = new BrotliStream(fs, CompressionMode.Decompress, true);
                using var ops = new BinaryReader(brl);
                using var outFs = File.Create(outPath);
                var count = ops.ReadInt32();
                var buf = new byte[MaxChunk];
                for (var i = 0; i < count; i++)
                {
                    var type = ops.ReadByte();
                    if (type == 0)
                    {
                        var src = ops.ReadInt64();
                        var len = ops.ReadInt32();
                        oldFs.Position = src;
                        oldFs.ReadExactly(buf, 0, len);
                        outFs.Write(buf, 0, len);
                    }
                    else
                    {
                        var len = ops.ReadInt32();
                        ops.BaseStream.ReadExactly(buf, 0, len);
                        outFs.Write(buf, 0, len);
                    }
                }
            }
        }

        if (new FileInfo(outPath).Length != newSize)
            throw new InvalidDataException("重建后大小不对：期望 " + newSize);
        using var check = File.OpenRead(outPath);
        var actual = Convert.ToHexString(SHA256.HashData(check)).ToLowerInvariant();
        if (!string.Equals(actual, targetSha, StringComparison.Ordinal))
            throw new InvalidDataException("重建后 sha256 校验失败");
        return actual;
    }
}
