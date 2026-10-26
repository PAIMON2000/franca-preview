using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Franca.Services.UePak;

// pak v11 写出：数据区（完整条目头 + 分块 Zlib）→ 自造索引（PHI fnv64 路径哈希 + FDI 目录
// 清单 + 编码条目区）→ 页脚。产物语义对齐 tools/ue-pak-recon Node 原型验证过的真实 pak。
public static class PakFileWriter
{
    public sealed class Item
    {
        public required string Path { get; init; }
        public required byte[] Data { get; init; }
        public bool Compress { get; init; }
        public uint BlockSize { get; init; } = 0x8000;
    }

    private static readonly byte[] ZlibSlot = Encoding.UTF8.GetBytes("Zlib");

    public static byte[] Write(string mountPoint, ulong pathHashSeed, IReadOnlyList<Item> items)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);

        var entries = new List<PakEntry>();
        foreach (var item in items)
        {
            var entry = new PakEntry { Path = item.Path };
            WriteDataArea(w, item, entry);
            entries.Add(entry);
        }

        var indexOffset = ms.Position;
        FStringCodec.Write(w, mountPoint);
        w.Write(entries.Count);
        w.Write(pathHashSeed);

        var phiRefPos = WriteRegionRef(w);
        var fdiRefPos = WriteRegionRef(w);
        var encSizePos = ms.Position;
        w.Write(0u);
        var blobStart = ms.Position;

        var blobOffsets = new List<int>();
        foreach (var entry in entries)
        {
            blobOffsets.Add((int)(ms.Position - blobStart));
            WriteIndexEntry(w, entry);
        }
        PatchU32(ms, encSizePos, (int)(ms.Position - blobStart));

        w.Write(0u); // 非编码条目数
        var indexSize = ms.Position - indexOffset;

        var phiBytes = BuildPathHashIndex(mountPoint, pathHashSeed, entries, blobOffsets);
        PatchRegionRef(ms, phiRefPos, ms.Position, phiBytes.Length, phiBytes);
        w.Write(phiBytes);

        var fdiBytes = BuildDirectoryIndex(entries, blobOffsets);
        PatchRegionRef(ms, fdiRefPos, ms.Position, fdiBytes.Length, fdiBytes);
        w.Write(fdiBytes);

        var indexSha1 = System.Security.Cryptography.SHA1.HashData(
            ms.GetBuffer().AsSpan((int)indexOffset, (int)indexSize));

        w.Write(new byte[16]); // uuid
        w.Write((byte)0);      // 索引加密标志
        w.Write(PakFileReader.PakMagic);
        w.Write(PakFileReader.SupportedVersion);
        w.Write(indexOffset);
        w.Write(indexSize);
        w.Write(indexSha1);
        for (var i = 0; i < 5; i++)
        {
            var slot = i == 0 ? ZlibSlot : Array.Empty<byte>();
            w.Write(slot);
            w.Write(new byte[32 - slot.Length]);
        }
        w.Flush();
        return ms.ToArray();
    }

    // 编码条目：bits 位域（压缩槽/加密位/块数/块大小 + 31/30/29 变长整数选择位）
    internal static void WriteIndexEntry(BinaryWriter w, PakEntry e)
    {
        var blockCount = e.Blocks.Count;
        uint bits = 0;
        if (e.CompressionMethod != 0) bits |= (e.CompressionMethod & 0x3f) << 23;
        if (e.Encrypted) bits |= 1u << 22;
        bits |= (uint)(blockCount & 0xffff) << 6;
        var cbsField = blockCount > 1 ? e.CompressionBlockSize >> 11 : 0;
        var cbsInline = cbsField >= 0x3f;
        bits |= cbsInline ? 0x3fu : cbsField;
        if (e.Offset <= uint.MaxValue) bits |= 1u << 31;
        if (e.UncompressedSize <= uint.MaxValue) bits |= 1u << 30;
        if (e.CompressionMethod != 0 && e.CompressedSize <= uint.MaxValue) bits |= 1u << 29;

        w.Write(bits);
        if (cbsInline) w.Write(e.CompressionBlockSize);
        WriteVarInt(w, e.Offset, (bits & (1u << 31)) != 0);
        WriteVarInt(w, e.UncompressedSize, (bits & (1u << 30)) != 0);
        if (e.CompressionMethod != 0)
            WriteVarInt(w, e.CompressedSize, (bits & (1u << 29)) != 0);
        if (e.CompressionMethod != 0 && blockCount > 1)
            foreach (var (start, end) in e.Blocks)
                w.Write((uint)(end - start));
    }

    private static void WriteVarInt(BinaryWriter w, long value, bool fitsU32)
    {
        if (fitsU32) w.Write((uint)value);
        else w.Write((ulong)value);
    }

    private static void WriteDataArea(BinaryWriter w, Item item, PakEntry entry)
    {
        entry.Offset = w.BaseStream.Position;
        entry.Hash = System.Security.Cryptography.SHA1.HashData(item.Data);
        entry.UncompressedSize = item.Data.LongLength;
        if (!item.Compress || item.Data.Length == 0)
        {
            entry.CompressedSize = entry.UncompressedSize;
            WriteFullHeader(w, entry);
            w.Write(item.Data);
            return;
        }

        if (item.BlockSize == 0 || item.BlockSize % 0x800 != 0)
            throw new ArgumentException($"块大小必须是 0x800 的倍数：{item.BlockSize}");
        entry.CompressionMethod = 1;
        entry.CompressionBlockSize = item.BlockSize;
        var chunks = new List<byte[]>();
        for (var off = 0; off < item.Data.Length; off += (int)item.BlockSize)
        {
            var len = (int)Math.Min(item.BlockSize, item.Data.Length - off);
            using var buf = new MemoryStream();
            using (var zs = new ZLibStream(buf, CompressionLevel.Fastest, leaveOpen: true))
                zs.Write(item.Data, off, len);
            chunks.Add(buf.ToArray());
        }
        var headerSize = 53 + 4 + 16L * chunks.Count;
        var cursor = headerSize;
        foreach (var chunk in chunks)
        {
            entry.Blocks.Add((cursor, cursor + chunk.LongLength));
            cursor += chunk.LongLength;
        }
        entry.CompressedSize = cursor - headerSize;
        if (chunks.Count == 1) entry.CompressionBlockSize = checked((uint)entry.UncompressedSize);
        using (var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1))
        {
            foreach (var chunk in chunks) hash.AppendData(chunk);
            entry.Hash = hash.GetHashAndReset();
        }
        WriteFullHeader(w, entry);
        foreach (var chunk in chunks) w.Write(chunk);
    }

    private static void WriteFullHeader(BinaryWriter w, PakEntry e)
    {
        w.Write(0UL); // 数据区头偏移为零；真实偏移只保存在索引
        w.Write((ulong)e.CompressedSize);
        w.Write((ulong)e.UncompressedSize);
        w.Write(e.CompressionMethod);
        w.Write(e.Hash);
        if (e.CompressionMethod != 0)
        {
            w.Write(e.Blocks.Count);
            foreach (var (start, end) in e.Blocks)
            {
                w.Write((ulong)start);
                w.Write((ulong)end);
            }
        }
        w.Write(e.Encrypted ? (byte)1 : (byte)0);
        w.Write(e.CompressionBlockSize);
    }

    private static byte[] BuildPathHashIndex(string mountPoint, ulong seed,
        List<PakEntry> entries, List<int> blobOffsets)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            w.Write(Fnv64.HashPath(entries[i].Path, seed));
            w.Write(blobOffsets[i]);
        }
        w.Write(0u); // PHI 中的空目录索引
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildDirectoryIndex(List<PakEntry> entries, List<int> blobOffsets)
    {
        var groups = new List<(string Dir, List<int> Files)>();
        var files = new List<(string Dir, string File, int Index)>();
        for (var i = 0; i < entries.Count; i++)
        {
            var path = entries[i].Path;
            var slash = path.LastIndexOf('/');
            var dir = slash >= 0 ? path[..(slash + 1)] : "/";
            var file = slash >= 0 ? path[(slash + 1)..] : path;
            var group = groups.Find(g => g.Dir == dir);
            if (group.Files is null)
            {
                group = (dir, new List<int>());
                groups.Add(group);
            }
            group.Files.Add(i);
            files.Add((dir, file, i));
            var parent = dir;
            while (parent != "/")
            {
                var trimmed = parent.TrimEnd('/');
                var split = trimmed.LastIndexOf('/');
                parent = split < 0 ? "/" : trimmed[..(split + 1)];
                if (!groups.Any(g => g.Dir == parent)) groups.Add((parent, new List<int>()));
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(groups.Count);
        foreach (var (dir, indices) in groups)
        {
            FStringCodec.Write(w, dir);
            w.Write(indices.Count);
            foreach (var idx in indices)
            {
                FStringCodec.Write(w, files[idx].File);
                w.Write(blobOffsets[idx]);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    private static long WriteRegionRef(BinaryWriter w)
    {
        var pos = w.BaseStream.Position;
        w.Write(1u);
        w.Write(0ul); // 偏移占位
        w.Write(0ul); // 大小占位
        w.Write(new byte[20]);
        return pos;
    }

    private static void PatchRegionRef(MemoryStream ms, long refPos, long dataPos, long size, byte[] data)
    {
        var sha1 = System.Security.Cryptography.SHA1.HashData(data);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, (ulong)dataPos);
        ms.Position = refPos + 4;
        ms.Write(buf);
        BinaryPrimitives.WriteUInt64LittleEndian(buf, (ulong)size);
        ms.Write(buf);
        ms.Write(sha1);
        ms.Position = dataPos;
    }

    private static void PatchU32(MemoryStream ms, long pos, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        var resume = ms.Position;
        ms.Position = pos;
        ms.Write(buf);
        ms.Position = resume;
    }
}
