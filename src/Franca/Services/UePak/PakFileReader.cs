using System.IO;
using System.IO.Compression;
using System.Text;

namespace Franca.Services.UePak;

// UE pak v11 读取：页脚（从尾向前找 magic，前 17 字节为 uuid+加密标志）→ 索引头 →
// 编码条目区（bits 位域：31/30/29 控制 offset/uncompressed/compressed 的 u32/u64）→
// 非编码条目 → FDI 目录清单（文件名可 UTF-16）→ PHI 路径哈希。
// 只支持未加密索引；条目数据按块 Zlib/GZip 解压（按块魔数探测）。
public sealed class PakFileReader : IDisposable
{
    public const uint PakMagic = 0x5A6F12E1;
    public const uint SupportedVersion = 11;

    private readonly Func<long, long, byte[]> _readAt;
    private readonly long _fileSize;
    private readonly FileStream? _file;
    private readonly List<PakEntry> _entries = new();
    private readonly Dictionary<ulong, int> _pathHashIndex = new();

    public string MountPoint { get; private set; } = "";
    public ulong PathHashSeed { get; private set; }
    public uint Version { get; private set; }
    public bool EncryptedIndex { get; private set; }
    public bool IndexSha1Valid { get; private set; }
    public string IndexSha1 { get; private set; } = "";
    public IReadOnlyList<string> CompressionMethods { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<PakEntry> Entries => _entries;
    public IReadOnlyDictionary<ulong, int> PathHashIndex => _pathHashIndex;

    public static PakFileReader Open(string path)
    {
        var fs = File.OpenRead(path);
        try
        {
            return new PakFileReader(fs);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public static PakFileReader FromBytes(byte[] data) => new(data);

    private PakFileReader(byte[] data) : this((off, len) =>
    {
        var b = new byte[len];
        Buffer.BlockCopy(data, (int)off, b, 0, (int)len);
        return b;
    }, data.LongLength, null)
    {
    }

    private PakFileReader(FileStream fs) : this((off, len) =>
    {
        var b = new byte[len];
        fs.Position = off;
        fs.ReadExactly(b);
        return b;
    }, fs.Length, fs)
    {
    }

    private PakFileReader(Func<long, long, byte[]> readAtRaw, long fileSize, FileStream? file)
    {
        _file = file;
        _fileSize = fileSize;
        byte[] ReadAt(long off, long len)
        {
            if (off < 0 || len < 0 || off + len > fileSize)
                throw new FormatException($"pak 读取越界：offset={off} len={len} fileSize={fileSize}");
            return readAtRaw(off, len);
        }
        _readAt = ReadAt;

        var tailLen = (int)Math.Min(fileSize, 65536);
        var tail = ReadAt(fileSize - tailLen, tailLen);
        var tb = -1;
        for (var i = tailLen - 204; i >= 17; i--)
        {
            if (BitConverter.ToUInt32(tail, i) != PakMagic) continue;
            var version = BitConverter.ToUInt32(tail, i + 4);
            if (version is >= 10 and <= 11) { tb = i; break; }
        }
        if (tb < 0) throw new FormatException("未找到 pak 页脚 magic");
        Version = BitConverter.ToUInt32(tail, tb + 4);
        if (Version != SupportedVersion)
            throw new NotSupportedException($"仅支持 pak v{SupportedVersion}，实际 v{Version}");
        var indexOffset = BitConverter.ToInt64(tail, tb + 8);
        var indexSize = BitConverter.ToInt64(tail, tb + 16);
        EncryptedIndex = tb >= 1 && tail[tb - 1] != 0;
        if (EncryptedIndex)
            throw new NotSupportedException("pak 索引 AES 加密，需要 exe 提取密钥（二期）");

        var slots = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var p = tb + 44 + i * 32;
            if (p + 32 > tailLen) break;
            var name = Encoding.UTF8.GetString(tail, p, 32);
            var nul = name.IndexOf('\0');
            if (nul >= 0) name = name[..nul];
            slots.Add(name.Trim());
        }
        CompressionMethods = slots;

        var footerIndexSha1 = new byte[20];
        Buffer.BlockCopy(tail, tb + 24, footerIndexSha1, 0, 20);

        var index = ReadAt(indexOffset, indexSize);
        IndexSha1Valid = StructuralEqual(SHA1(index), footerIndexSha1);
        IndexSha1 = Convert.ToHexString(footerIndexSha1);
        if (!IndexSha1Valid) throw new FormatException("pak 主索引 SHA1 校验失败");

        using var ms = new MemoryStream(index, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        MountPoint = FStringCodec.Read(r);
        var recordCount = r.ReadUInt32();
        PathHashSeed = r.ReadUInt64();

        long? phiOff = null, phiSize = null;
        byte[]? phiHash = null, fdiHash = null;
        if (r.ReadUInt32() != 0) { phiOff = (long)r.ReadUInt64(); phiSize = (long)r.ReadUInt64(); phiHash = r.ReadBytes(20); }
        long? fdiOff = null, fdiSize = null;
        if (r.ReadUInt32() != 0) { fdiOff = (long)r.ReadUInt64(); fdiSize = (long)r.ReadUInt64(); fdiHash = r.ReadBytes(20); }
        if (fdiOff is null || fdiSize is null)
            throw new NotSupportedException("pak 无目录索引（FDI），无法列出路径");

        var encSize = r.ReadInt32();
        var blobStartFile = indexOffset + ms.Position;
        var blob = ReadAt(blobStartFile, encSize);
        var nePosFile = blobStartFile + encSize;

        var nonEncoded = new List<PakEntry>();
        var neCount = BitConverter.ToUInt32(ReadAt(nePosFile, 4), 0);
        {
            var p = (int)(nePosFile - indexOffset) + 4;
            for (var i = 0; i < neCount; i++)
                nonEncoded.Add(ReadFullEntry(index, ref p));
        }

        var blobOffsets = new Dictionary<int, int>();
        var fdi = ReadAt(fdiOff.GetValueOrDefault(), fdiSize.GetValueOrDefault());
        if (!SHA1(fdi).AsSpan().SequenceEqual(fdiHash)) throw new FormatException("pak FDI SHA1 校验失败");
        {
            var p = 0;
            var dirCount = BitConverter.ToUInt32(fdi, p);
            p += 4;
            for (var d = 0; d < dirCount; d++)
            {
                var dir = ReadFString(fdi, ref p);
                var fileCount = BitConverter.ToUInt32(fdi, p);
                p += 4;
                for (var f = 0; f < fileCount; f++)
                {
                    var name = ReadFString(fdi, ref p);
                    var encodedOffset = BitConverter.ToInt32(fdi, p);
                    p += 4;
                    PakEntry entry;
                    if (encodedOffset >= 0)
                    {
                        entry = ReadIndexEntry(blob, encodedOffset);
                        blobOffsets[encodedOffset] = _entries.Count;
                    }
                    else
                    {
                        entry = nonEncoded[-encodedOffset - 1];
                    }
                    entry.Path = dir.TrimStart('/') + name;
                    _entries.Add(entry);
                }
            }
        }

        if (phiOff is not null)
        {
            var phi = ReadAt(phiOff.Value, phiSize!.Value);
            if (!SHA1(phi).AsSpan().SequenceEqual(phiHash)) throw new FormatException("pak PHI SHA1 校验失败");
            var p = 0;
            var count = BitConverter.ToUInt32(phi, p);
            p += 4;
            for (var i = 0; i < count; i++)
            {
                var hash = BitConverter.ToUInt64(phi, p);
                p += 8;
                var off = BitConverter.ToInt32(phi, p);
                p += 4;
                if (blobOffsets.TryGetValue(off, out var idx))
                    _pathHashIndex[hash] = idx;
            }
        }

        if (_entries.Count != recordCount)
            throw new FormatException($"索引记录数不符：FDI/编码条目 {_entries.Count}，索引头声明 {recordCount}");
    }

    public PakEntry? Find(string path)
    {
        foreach (var e in _entries)
            if (string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    public PakEntry? FindByPathHash(ulong hash) =>
        _pathHashIndex.TryGetValue(hash, out var idx) ? _entries[idx] : null;

    // 数据区条目头为准重新解析块表后按块解压；块偏移相对条目起始
    public byte[] Extract(PakEntry entry)
    {
        if (entry.Encrypted)
            throw new NotSupportedException($"条目 AES 加密，无法抽取：{entry.Path}");
        var headerLen = 53 + (entry.CompressionMethod != 0 ? 4 + 16 * entry.Blocks.Count : 0);
        var header = _readAt(entry.Offset, headerLen);
        var p = 0;
        var full = ReadFullEntry(header, ref p);
        if (full.CompressedSize != entry.CompressedSize || full.UncompressedSize != entry.UncompressedSize
            || full.CompressionMethod != entry.CompressionMethod || full.Encrypted != entry.Encrypted
            || full.CompressionBlockSize != entry.CompressionBlockSize || !full.Blocks.SequenceEqual(entry.Blocks))
            throw new FormatException($"条目索引与数据头不一致：{entry.Path}");
        var data = _readAt(entry.Offset + p, full.CompressedSize);
        VerifyHash(full, data); // UE 哈希覆盖磁盘中的压缩数据
        if (full.CompressionMethod == 0)
        {
            VerifyHash(full, data);
            return data;
        }
        using var output = new MemoryStream();
        foreach (var (start, end) in full.Blocks)
        {
            var chunk = new byte[(int)(end - start)];
            Buffer.BlockCopy(data, (int)start - p, chunk, 0, chunk.Length);
            if (chunk.Length > 0 && chunk[0] == 0x78)
            {
                using var zs = new ZLibStream(new MemoryStream(chunk), CompressionMode.Decompress);
                zs.CopyTo(output);
            }
            else if (chunk.Length > 1 && chunk[0] == 0x1F && chunk[1] == 0x8B)
            {
                using var gs = new GZipStream(new MemoryStream(chunk), CompressionMode.Decompress);
                gs.CopyTo(output);
            }
            else
                throw new FormatException($"条目 {entry.Path} 含未知压缩块魔数");
        }
        var result = output.ToArray();
        if (result.LongLength != full.UncompressedSize)
            throw new FormatException($"条目 {entry.Path} 解压大小不符：{result.LongLength} != {full.UncompressedSize}");
        return result;
    }

    public void Dispose() => _file?.Dispose();

    private static void VerifyHash(PakEntry entry, byte[] data)
    {
        if (entry.Hash.All(b => b == 0)) return;
        if (!StructuralEqual(SHA1(data), entry.Hash))
            throw new FormatException($"条目 {entry.Path} SHA1 校验失败");
    }

    private static bool StructuralEqual(byte[] a, byte[] b) =>
        a.AsSpan().SequenceEqual(b);

    private static byte[] SHA1(byte[] data) =>
        System.Security.Cryptography.SHA1.HashData(data);

    // 编码条目（索引内，12 字节起）：bits 位域 + 可选 u32 块大小 + 变长整数 + 可选每块 u32 尺寸
    internal static PakEntry ReadIndexEntry(byte[] blob, int pos)
    {
        var bits = BitConverter.ToUInt32(blob, pos);
        pos += 4;
        var compRaw = (bits >> 23) & 0x3f;
        var encrypted = (bits & (1 << 22)) != 0;
        var blockCount = (int)(bits >> 6) & 0xffff;
        var cbsField = bits & 0x3f;
        var cbs = cbsField == 0x3f ? BitConverter.ToUInt32(blob, pos) : cbsField << 11;
        if (cbsField == 0x3f) pos += 4;

        long ReadVarInt(int bit)
        {
            if ((bits & (1u << bit)) != 0)
            {
                var v = (long)BitConverter.ToUInt32(blob, pos);
                pos += 4;
                return v;
            }
            var w = (long)BitConverter.ToUInt64(blob, pos);
            pos += 8;
            return w;
        }

        var offset = ReadVarInt(31);
        var uncompressed = ReadVarInt(30);
        var compressed = compRaw == 0 ? uncompressed : ReadVarInt(29);

        var entry = new PakEntry
        {
            Path = "",
            Offset = offset,
            UncompressedSize = uncompressed,
            CompressedSize = compressed,
            CompressionMethod = compRaw,
            CompressionBlockSize = blockCount == 0 ? 0 : blockCount == 1 ? checked((uint)uncompressed) : cbs,
            Encrypted = encrypted,
        };
        var headerSize = 53L + (compRaw != 0 ? 4 + 16L * blockCount : 0);
        if (blockCount == 1 && !encrypted)
        {
            entry.Blocks.Add((headerSize, headerSize + compressed));
        }
        else if (blockCount > 0)
        {
            var start = headerSize;
            for (var i = 0; i < blockCount; i++)
            {
                var size = BitConverter.ToUInt32(blob, pos);
                pos += 4;
                entry.Blocks.Add((start, start + size));
                start += encrypted ? (size + 15) & ~15 : size;
            }
        }
        return entry;
    }

    // 数据区/非编码条目的完整条目头
    private static PakEntry ReadFullEntry(byte[] buf, ref int p)
    {
        var entry = new PakEntry { Path = "" };
        entry.Offset = (long)BitConverter.ToUInt64(buf, p); p += 8;
        entry.CompressedSize = (long)BitConverter.ToUInt64(buf, p); p += 8;
        entry.UncompressedSize = (long)BitConverter.ToUInt64(buf, p); p += 8;
        entry.CompressionMethod = BitConverter.ToUInt32(buf, p); p += 4;
        Buffer.BlockCopy(buf, p, entry.Hash, 0, 20); p += 20;
        if (entry.CompressionMethod != 0)
        {
            var blockCount = BitConverter.ToInt32(buf, p); p += 4;
            for (var i = 0; i < blockCount; i++)
            {
                var start = (long)BitConverter.ToUInt64(buf, p); p += 8;
                var end = (long)BitConverter.ToUInt64(buf, p); p += 8;
                entry.Blocks.Add((start, end));
            }
        }
        entry.Encrypted = buf[p] != 0; p += 1;
        entry.CompressionBlockSize = BitConverter.ToUInt32(buf, p); p += 4;
        return entry;
    }

    private static string ReadFString(byte[] buf, ref int p)
    {
        using var ms = new MemoryStream(buf, p, buf.Length - p, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var s = FStringCodec.Read(r);
        p += (int)ms.Position;
        return s;
    }
}
