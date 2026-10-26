using System.IO;
using System.Text;
using Franca.Services.UePak;

namespace Franca.Diagnostics;

// UE pak/locres 格式库合成数据探针（--probe-ue-pak）。
// 全部夹具在 Path.GetTempPath 临时目录：合成 pak（UTF-8/UTF-16 中文文件名、多 Zlib 块、
// 未压缩小条目、locres v3 打桩）经 PakFileWriter 写出 → PakFileReader 读回逐项断言；
// locres v1/v2/v3 序列化回读；编码条目 varint 64 位边界；页脚 17 字节前缀（uuid+加密
// 标志内含伪 magic）从尾扫描定位。不联网、不触碰任何真实游戏目录。
internal static class UePakProbe
{
    public static void Run(string? artifactDirectory = null)
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tUEPAK\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition) failures++;
        }

        var root = Path.Combine(Path.GetTempPath(), "franca-uepak-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check("locres_roundtrip", TestLocResRoundtrip());
            Check("locres_lut_dedup", TestLocResLutDedup());
            Check("locres_hash_version_conversion_and_zero", TestHashVersions());
            Check("pak_roundtrip", TestPakRoundtrip(root, out var seed, out var mountPoint));
            Check("index_varint_64bit", TestVarintBoundary());
            Check("footer_17byte_prefix", TestFooterPrefix());
            Check("cityhash_known_vector", CityHash64.Hash([]) == 0x9AE16A3B2F90404FUL);
            Check("fnv_empty_offset_basis", Fnv64.HashPath("", 7) == 0xcbf29ce48422232cUL);
            Check("fnv_path_case", Fnv64.HashPath("Game/Boss战.uasset", 42) == Fnv64.HashPath("game/boss战.uasset", 42));
            Check("pak_wire_contract", TestWireContract(root));
            if (artifactDirectory is not null)
            {
                Directory.CreateDirectory(artifactDirectory);
                File.Copy(Path.Combine(root, "pakchunk99-WindowsNoEditor.pak"), Path.Combine(artifactDirectory, "synthetic.pak"), overwrite: true);
                File.Copy(Path.Combine(root, "synthetic-expected.json"), Path.Combine(artifactDirectory, "synthetic-expected.json"), overwrite: true);
            }
            _ = seed;
            _ = mountPoint;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine($"RESULT\tUEPAK\tfailures={failures}");
        if (failures > 0) Environment.ExitCode = 1;
    }

    private static bool TestLocResRoundtrip()
    {
        var dict = new Dictionary<string, Dictionary<string, string>>
        {
            ["Npc"] = new()
            {
                ["greeting"] = "你好，旅行者。",
                ["long"] = string.Concat(Enumerable.Repeat("长风万里送秋雁，", 600)), // 长文本，跨 CityHash64 全部分支
                ["shared"] = "重复值",
            },
            ["UI"] = new()
            {
                ["title"] = "逸剑风云决",
                ["shared"] = "重复值", // 触发 LUT 共享
            },
            ["中文命名空间"] = new() { ["按键·标签"] = "值" }, // UTF-16 FString 路径
        };
        foreach (var version in new byte[] { 1, 2, 3 })
        {
            var bytes = LocResFile.FromDictionary(dict).Serialize(version);
            var parsed = LocResFile.Parse(bytes);
            if (parsed.Version != version || !DictEqual(dict, parsed.ToDictionary()))
                return false;
        }
        return true;
    }

    private static bool TestLocResLutDedup()
    {
        var dict = new Dictionary<string, Dictionary<string, string>>
        {
            ["A"] = new() { ["k1"] = "共享", ["k2"] = "独有甲" },
            ["B"] = new() { ["k3"] = "共享", ["k4"] = "独有乙" },
        };
        var bytes = LocResFile.FromDictionary(dict).Serialize(3);
        var parsed = LocResFile.Parse(bytes);
        return parsed.StringTable.Count == 3 && DictEqual(dict, parsed.ToDictionary());
    }

    private static bool TestHashVersions()
    {
        var v3 = LocResFile.FromDictionary(new() { ["Namespace"] = new() { ["Key"] = "Value" } });
        var v2 = LocResFile.Parse(v3.Serialize(2));
        if (v2.Namespaces[0].Hash != Crc32.StringHash("Namespace")
            || v2.Namespaces[0].Entries[0].KeyHash != Crc32.StringHash("Key")) return false;
        var converted = LocResFile.Parse(v2.Serialize(3));
        if (converted.Namespaces[0].Hash != v3.Namespaces[0].Hash
            || converted.Namespaces[0].Entries[0].KeyHash != v3.Namespaces[0].Entries[0].KeyHash) return false;
        v3.Namespaces[0].Hash = 0;
        v3.Namespaces[0].Entries[0].KeyHash = 0;
        v3.Namespaces[0].Entries[0].SourceStringHash = 0;
        var preserved = LocResFile.Parse(v3.Serialize());
        return preserved.Namespaces[0].Hash == 0 && preserved.Namespaces[0].Entries[0].KeyHash == 0
            && preserved.Namespaces[0].Entries[0].SourceStringHash == 0
            && Crc32.Hash(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926;
    }

    private static bool TestWireContract(string root)
    {
        // Inspect bytes independently of our reader: primary index excludes PHI/FDI,
        // first encoded offset is blob-relative zero, and first footer slot is Zlib.
        var bytes = File.ReadAllBytes(Path.Combine(root, "pakchunk99-WindowsNoEditor.pak"));
        var magic = bytes.Length - 204;
        if (BitConverter.ToUInt32(bytes, magic) != PakFileReader.PakMagic) return false;
        var indexOffset = (int)BitConverter.ToInt64(bytes, magic + 8);
        var indexSize = (int)BitConverter.ToInt64(bytes, magic + 16);
        if (Encoding.ASCII.GetString(bytes, magic + 44, 4) != "Zlib") return false;
        var p = indexOffset;
        p += 4 + BitConverter.ToInt32(bytes, p); // ASCII mount FString
        p += 4 + 8; // count and seed
        var phiOffset = (int)BitConverter.ToInt64(bytes, p + 4);
        if (indexOffset + indexSize != phiOffset) return false;
        p += 40;
        var fdiOffset = (int)BitConverter.ToInt64(bytes, p + 4);
        if (fdiOffset <= phiOffset) return false;
        // First PHI tuple points to offset zero in encoded entry blob.
        if (BitConverter.ToInt32(bytes, phiOffset + 12) != 0) return false;
        if (BitConverter.ToUInt64(bytes, 0) != 0) return false;
        var firstCompressedSize = (int)BitConverter.ToInt64(bytes, 8);
        var blocks = BitConverter.ToInt32(bytes, 48);
        var headerSize = 57 + blocks * 16;
        return System.Security.Cryptography.SHA1.HashData(bytes.AsSpan(headerSize, firstCompressedSize))
            .AsSpan().SequenceEqual(bytes.AsSpan(28, 20));
    }

    private static bool TestPakRoundtrip(string root, out ulong seed, out string mountPoint)
    {
        seed = 0x0123456789ABCDEFUL;
        mountPoint = "../../../";
        var rng = new Random(42);
        var big = new byte[300_000]; // 随机数据 + 0x800 小块 → 必然多块 Zlib
        rng.NextBytes(big);
        var locBytes = LocResFile.FromDictionary(new Dictionary<string, Dictionary<string, string>>
        {
            ["Game"] = new() { ["hello"] = "你好" },
        }).Serialize(3);

        var items = new List<PakFileWriter.Item>
        {
            new() { Path = "YiJian/Content/Localization/Game.locres", Data = locBytes, Compress = true },
            new() { Path = "YiJian/Content/Textures/Boss战.uasset", Data = Encoding.UTF8.GetBytes("UTF-16 中文文件名条目内容") },
            new() { Path = "YiJian/Content/Data/config.bin", Data = new byte[] { 1, 2, 3, 4, 5 }, Compress = true },
            new() { Path = "YiJian/Content/Big/blob.bin", Data = big, Compress = true, BlockSize = 0x800 },
        };
        var pakPath = Path.Combine(root, "pakchunk99-WindowsNoEditor.pak");
        File.WriteAllBytes(pakPath, PakFileWriter.Write(mountPoint, seed, items));
        File.WriteAllText(Path.Combine(root, "synthetic-expected.json"), System.Text.Json.JsonSerializer.Serialize(
            items.ToDictionary(i => i.Path, i => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(i.Data)))));

        using var reader = PakFileReader.Open(pakPath);
        if (!reader.IndexSha1Valid || reader.Version != 11
            || reader.MountPoint != mountPoint || reader.PathHashSeed != seed)
            return false;
        if (reader.Entries.Count != items.Count)
            return false;
        foreach (var item in items)
        {
            var entry = reader.Find(item.Path);
            if (entry is null || !reader.Extract(entry).SequenceEqual(item.Data))
                return false;
        }
        var bigEntry = reader.Find("YiJian/Content/Big/blob.bin")!;
        if (bigEntry.Blocks.Count <= 1)
            return false; // 0x800 小块 × 300KB 必须产生多压缩块
        // PHI：fnv64（lower(挂载点相对路径)，种子）应能解析回同一批条目
        foreach (var item in items)
        {
            var hash = Fnv64.HashPath(item.Path, seed);
            var found = reader.FindByPathHash(hash);
            if (found is null || found.Path != item.Path)
                return false;
        }
        // 打桩 locres 条目应能按 locres 解析回原字典
        var locEntry = reader.Find("YiJian/Content/Localization/Game.locres")!;
        var locParsed = LocResFile.Parse(reader.Extract(locEntry));
        return locParsed.ToDictionary()["Game"]["hello"] == "你好";
    }

    private static bool TestVarintBoundary()
    {
        var entry = new PakEntry
        {
            Path = "",
            Offset = 0x1_0000_0001L,           // 超 u32，强制 64 位编码
            UncompressedSize = 0x1_0000_0002L,
            CompressedSize = 0x2_0000_0003L,
            CompressionMethod = 1,
            CompressionBlockSize = 0x10000,
        };
        var headerSize = 53 + 4 + 16 * 2;
        entry.Blocks.Add((headerSize, headerSize + 7));
        entry.Blocks.Add((headerSize + 7, headerSize + 9));

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            PakFileWriter.WriteIndexEntry(w, entry);
        var decoded = PakFileReader.ReadIndexEntry(ms.ToArray(), 0);
        return decoded.Offset == entry.Offset
            && decoded.UncompressedSize == entry.UncompressedSize
            && decoded.CompressedSize == entry.CompressedSize
            && decoded.CompressionMethod == 1
            && decoded.CompressionBlockSize == 0x10000
            && decoded.Blocks.SequenceEqual(entry.Blocks)
            && decoded.Offset > uint.MaxValue;
    }

    // 页脚 17 字节前缀（16 字节 uuid + 1 字节加密标志）中植入伪 magic；
    // 从尾向前扫描 + 版本号核验必须定位真 magic 并正常解析
    private static bool TestFooterPrefix()
    {
        var pak = PakFileWriter.Write("../../../", 0, new List<PakFileWriter.Item>());
        var footerLen = 16 + 1 + 4 + 4 + 8 + 8 + 20 + 5 * 32;
        BitConverter.GetBytes(PakFileReader.PakMagic).CopyTo(pak, pak.Length - footerLen); // uuid 头部放伪 magic
        using var reader = PakFileReader.FromBytes(pak);
        return reader.Entries.Count == 0 && reader.MountPoint == "../../../" && reader.Version == 11;
    }

    private static bool DictEqual(
        Dictionary<string, Dictionary<string, string>> a,
        Dictionary<string, Dictionary<string, string>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (ns, inner) in a)
        {
            if (!b.TryGetValue(ns, out var other) || other.Count != inner.Count) return false;
            foreach (var (key, value) in inner)
                if (!other.TryGetValue(key, out var v) || v != value) return false;
        }
        return true;
    }
}
