using System.IO;
using System.Text;

namespace Franca.Services.UePak;

// UE locres 读写（v1/v2/v3，重点 v3 = Optimized_CityHash64_UTF16）。
// 魔数为 16 字节 GUID；v1+ 有字符串 LUT（绝对文件偏移，v2+ 带引用计数）；
// v2+ 命名空间/key 带 u32 哈希，key 带 SourceStringHash + LUT 索引。
public sealed class LocResFile
{
    private static readonly byte[] Magic =
        { 0x0e, 0x14, 0x74, 0x75, 0x67, 0x4a, 0x03, 0xfc,
          0x4a, 0x15, 0x90, 0x9d, 0xc3, 0x37, 0x7f, 0x1b };

    public sealed class LocResEntry
    {
        public required string Key { get; init; }
        public string Value { get; set; } = "";
        public uint KeyHash { get; set; }
        public uint SourceStringHash { get; set; }
    }

    public sealed class LocResNamespace
    {
        public required string Name { get; init; }
        public uint Hash { get; set; }
        public List<LocResEntry> Entries { get; } = new();
    }

    public byte Version { get; set; } = 3;
    public List<LocResNamespace> Namespaces { get; } = new();
    public List<string> StringTable { get; private set; } = new();

    public static LocResFile Parse(byte[] data)
    {
        if (data.Length < 17 || !data.AsSpan(0, 16).SequenceEqual(Magic))
            throw new FormatException("locres 魔数不符");
        var version = data[16];
        if (version is < 1 or > 3)
            throw new NotSupportedException($"不支持的 locres 版本：{version}");
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        ms.Position = 17;
        var file = new LocResFile { Version = version };

        var stringsArrOffset = r.ReadInt64();
        var posAfterLutRef = ms.Position;
        if (stringsArrOffset != -1)
        {
            ms.Position = stringsArrOffset;
            var count = r.ReadUInt32();
            for (var i = 0; i < count; i++)
            {
                file.StringTable.Add(FStringCodec.Read(r));
                if (version >= 2) r.ReadInt32(); // 引用计数
            }
        }
        ms.Position = posAfterLutRef;
        if (version >= 2) r.ReadUInt32(); // EntriesCount

        var nsCount = r.ReadUInt32();
        for (var i = 0; i < nsCount; i++)
        {
            var hash = version >= 2 ? r.ReadUInt32() : 0u;
            var ns = new LocResNamespace { Name = FStringCodec.Read(r), Hash = hash };
            var keyCount = r.ReadUInt32();
            for (var j = 0; j < keyCount; j++)
            {
                var keyHash = version >= 2 ? r.ReadUInt32() : 0u;
                var entry = new LocResEntry
                {
                    Key = FStringCodec.Read(r),
                    KeyHash = keyHash,
                    SourceStringHash = r.ReadUInt32(),
                };
                var idx = r.ReadInt32();
                entry.Value = idx >= 0 && idx < file.StringTable.Count ? file.StringTable[idx] : "";
                ns.Entries.Add(entry);
            }
            file.Namespaces.Add(ns);
        }
        return file;
    }

    // 解析回读的哈希逐位保留（包括零）；翻译不改变源文哈希。
    public byte[] Serialize(byte? versionOverride = null)
    {
        var version = versionOverride ?? Version;
        if (version is < 1 or > 3)
            throw new NotSupportedException($"不支持的 locres 版本：{version}");

        var lut = new List<string>();
        var lutIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var refCounts = new List<int>();
        var lutOf = new Func<string, int>(value =>
        {
            if (lutIndex.TryGetValue(value, out var idx))
            {
                refCounts[idx]++;
                return idx;
            }
            idx = lut.Count;
            lut.Add(value);
            refCounts.Add(1);
            lutIndex[value] = idx;
            return idx;
        });
        var indices = new List<List<int>>();
        foreach (var ns in Namespaces)
        {
            var nsIndices = new List<int>();
            foreach (var entry in ns.Entries)
                nsIndices.Add(lutOf(entry.Value));
            indices.Add(nsIndices);
        }

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(version);
            var arrPos = ms.Position;
            w.Write(0L); // 字符串 LUT 绝对偏移占位

            if (version >= 2)
                w.Write((uint)Namespaces.Sum(ns => ns.Entries.Count));
            w.Write(Namespaces.Count);
            for (var i = 0; i < Namespaces.Count; i++)
            {
                var ns = Namespaces[i];
                if (version >= 2) w.Write(version == Version ? ns.Hash : StrHash(version, ns.Name));
                FStringCodec.Write(w, ns.Name);
                w.Write(ns.Entries.Count);
                for (var j = 0; j < ns.Entries.Count; j++)
                {
                    var entry = ns.Entries[j];
                    if (version >= 2) w.Write(version == Version ? entry.KeyHash : StrHash(version, entry.Key));
                    FStringCodec.Write(w, entry.Key);
                    w.Write(entry.SourceStringHash);
                    w.Write(indices[i][j]);
                }
            }

            var lutOffset = ms.Position;
            w.Write(lut.Count);
            for (var i = 0; i < lut.Count; i++)
            {
                FStringCodec.Write(w, lut[i]);
                if (version >= 2) w.Write(refCounts[i]);
            }

            ms.Position = arrPos;
            w.Write(lutOffset);
        }
        return ms.ToArray();
    }

    public Dictionary<string, Dictionary<string, string>> ToDictionary()
    {
        var dict = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var ns in Namespaces)
        {
            var inner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in ns.Entries)
                inner[entry.Key] = entry.Value;
            dict[ns.Name] = inner;
        }
        return dict;
    }

    public static LocResFile FromDictionary(Dictionary<string, Dictionary<string, string>> dict)
    {
        var file = new LocResFile();
        foreach (var (nsName, entries) in dict)
        {
            var ns = new LocResNamespace { Name = nsName, Hash = CityHash64.StringHash32(nsName) };
            foreach (var (key, value) in entries)
                ns.Entries.Add(new LocResEntry { Key = key, Value = value,
                    KeyHash = CityHash64.StringHash32(key), SourceStringHash = Crc32.StringHash(value) });
            file.Namespaces.Add(ns);
        }
        return file;
    }

    private static uint StrHash(byte version, string s) => version switch
    {
        1 => 0,
        2 => Crc32.StringHash(s),
        _ => CityHash64.StringHash32(s),
    };
}
