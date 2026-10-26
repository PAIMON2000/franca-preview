using System.IO;
using System.Text;

namespace Franca.Services;

// Local Steam appinfo v39-v41 / binary KeyValues format. Reads metadata only;
// it is never evidence that an application is installed.
internal static class SteamAppInfoReader
{
    internal sealed record SteamAppMetadata(string Name, string Type, string InstallDir);

    internal static Dictionary<int, SteamAppMetadata> Read(string? steamRoot)
    {
        if (steamRoot is null) return new();
        try
        {
            using var file = new FileStream(Path.Combine(steamRoot, "appcache", "appinfo.vdf"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 128 * 1024 * 1024) return new();
            var data = new byte[checked((int)file.Length)];
            file.ReadExactly(data);
            return Parse(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return new(); // Missing, locked or changing cache must not break discovery.
        }
    }

    internal static Dictionary<int, SteamAppMetadata> Parse(byte[] data)
    {
        var entries = new Dictionary<int, SteamAppMetadata>();
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            var magic = reader.ReadUInt32();
            var version = magic & 255;
            if ((magic >> 8) != 0x075644 || version is < 39 or > 41) return entries;
            reader.ReadUInt32(); // universe
            string[]? keys = null;
            long recordsEnd = data.Length;
            if (version == 41)
            {
                recordsEnd = reader.ReadInt64();
                var recordsStart = stream.Position;
                if (recordsEnd < recordsStart || recordsEnd > data.Length - 4) return entries;
                stream.Position = recordsEnd;
                var count = reader.ReadInt32();
                if (count < 0 || count > 1_000_000 || count > data.Length - stream.Position) return entries;
                keys = new string[count];
                for (var i = 0; i < count; i++) keys[i] = ReadString(reader, data.Length);
                stream.Position = recordsStart;
            }

            while (stream.Position <= recordsEnd - 4)
            {
                var id = reader.ReadInt32();
                if (id == 0) break;
                var size = reader.ReadUInt32();
                var end = stream.Position + size;
                var headerSize = version >= 40 ? 60 : 40;
                if (size < headerSize || end > recordsEnd) break;
                stream.Position += headerSize;
                try
                {
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ReadObject(reader, end, keys, "", fields, 0);
                    if (fields.TryGetValue("appinfo/common/type", out var type) && !string.IsNullOrWhiteSpace(type))
                        entries[id] = new SteamAppMetadata(fields.GetValueOrDefault("appinfo/common/name", ""),
                            type, fields.GetValueOrDefault("appinfo/config/installdir", ""));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    // A malformed record must not classify this entry or affect later records.
                }
                stream.Position = end;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return new(); // Invalid header/string table: none of its classifications are trusted.
        }
        return entries;
    }

    private static void ReadObject(BinaryReader reader, long end, string[]? keys, string parent,
        Dictionary<string, string> fields, int depth)
    {
        if (depth > 64) throw new InvalidDataException("KeyValues nesting too deep.");
        while (reader.BaseStream.Position < end)
        {
            var kind = reader.ReadByte();
            if (kind == 8) return;
            string key;
            if (keys is null) key = ReadString(reader, end);
            else
            {
                Require(reader, end, 4);
                var index = reader.ReadInt32();
                if ((uint)index >= keys.Length) throw new InvalidDataException("Invalid string table index.");
                key = keys[index];
            }
            var path = parent.Length == 0 ? key : parent + "/" + key;
            switch (kind)
            {
                case 0: ReadObject(reader, end, keys, path, fields, depth + 1); break;
                case 1:
                    var value = ReadString(reader, end);
                    if (path is "appinfo/common/name" or "appinfo/common/type" or "appinfo/config/installdir")
                        fields[path] = value;
                    break;
                case 2: case 3: case 4: case 6: Skip(reader, end, 4); break;
                case 7: case 10: Skip(reader, end, 8); break;
                default: throw new InvalidDataException("Unsupported KeyValues value type.");
            }
        }
        throw new InvalidDataException("Unterminated KeyValues object.");
    }

    private static string ReadString(BinaryReader reader, long end)
    {
        var bytes = new List<byte>();
        while (reader.BaseStream.Position < end && bytes.Count < 65536)
        {
            var next = reader.ReadByte();
            if (next == 0) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(next);
        }
        throw new InvalidDataException("Unterminated KeyValues string.");
    }

    private static void Require(BinaryReader reader, long end, int size)
    {
        if (reader.BaseStream.Position + size > end) throw new InvalidDataException("Truncated KeyValues value.");
    }

    private static void Skip(BinaryReader reader, long end, int size)
    {
        Require(reader, end, size);
        reader.BaseStream.Position += size;
    }
}
