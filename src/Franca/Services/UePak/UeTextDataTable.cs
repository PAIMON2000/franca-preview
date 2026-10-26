using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Franca.Services.UePak;

// Restricted cooked UE4 DataTable codec, verified against Octopath Traveler II.
// Supports Id/IntProperty + Text/TextProperty with Base or empty FText histories.
// Unknown package layouts and property histories are rejected, never guessed.
public sealed class UeTextDataTable
{
    public sealed record Row(string Key, int Id, string Text, string? Namespace, string? TextKey,
        int Start, int End, int PropertyStart, int TextStart, int TextEnd);
    private readonly byte[] _header;
    private readonly byte[] _body;
    private readonly int _exportOffset;
    public IReadOnlyList<Row> Rows { get; }

    private UeTextDataTable(byte[] header, byte[] body, int exportOffset, List<Row> rows)
    { _header = header; _body = body; _exportOffset = exportOffset; Rows = rows; }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("Unsupported text DataTable: " + message); }

    public static UeTextDataTable Parse(byte[] header, byte[] body)
    {
        int I32(byte[] data, int p)
        { Require(p >= 0 && p <= data.Length - 4, "integer bounds"); return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p)); }
        long I64(int p)
        { Require(p >= 0 && p <= header.Length - 8, "export bounds"); return BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(p)); }
        string ReadString(byte[] data, ref int p)
        {
            var n = I32(data, p); p += 4;
            if (n == 0) return "";
            Require(n != int.MinValue, "string size");
            var bytes = Math.Abs((long)n) * (n < 0 ? 2 : 1);
            Require(bytes <= data.Length - p && bytes <= 2_000_000, "string bounds");
            var terminator = n < 0 ? 2 : 1;
            Require(data[p + (int)bytes - 1] == 0 && (n > 0 || data[p + (int)bytes - 2] == 0), "string terminator");
            var value = (n < 0 ? Encoding.Unicode : new UTF8Encoding(false, true)).GetString(data, p, (int)bytes - terminator);
            p += (int)bytes;
            return value;
        }
        Require(header.Length >= 193 && body.Length >= 49, "package too short");
        Require(I32(header, 0) == unchecked((int)0x9e2a83c1) && I32(header, 4) == -7
            && I32(header, 20) == 0 && I32(header, 24) == header.Length
            && I32(header, 28) == 5 && header.AsSpan(32, 5).SequenceEqual("None\0"u8), "package summary");
        var exportOffset = I32(header, 61);
        Require(I32(header, 57) == 1 && I64(exportOffset + 36) == header.Length
            && I64(exportOffset + 28) == body.Length - 4
            && I64(169) == (long)header.Length + body.Length - 4, "single export/bulk layout");
        var nameCount = I32(header, 41);
        var p = I32(header, 45);
        Require(nameCount > 0 && nameCount <= 1_000_000 && p >= 193, "name table");
        var names = new string[nameCount];
        for (var i = 0; i < names.Length; i++) { names[i] = ReadString(header, ref p); p = checked(p + 4); }
        Require(p == I32(header, 69), "name table extent");
        string Name(int at)
        {
            var index = I32(body, at); var number = I32(body, at + 4);
            Require(index >= 0 && index < names.Length && number >= 0, "FName");
            return names[index] + (number == 0 ? "" : "_" + (number - 1));
        }
        Require(Name(0) == "RowStruct" && Name(8) == "ObjectProperty" && I32(body, 16) == 4
            && I32(body, 20) == 0 && body[24] == 0 && Name(29) == "None" && I32(body, 37) == 0, "DataTable header");
        var count = I32(body, 41);
        Require(count >= 0 && count <= body.Length / 16, "row count");
        p = 45;
        var rows = new List<Row>(count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var start = p; var key = Name(p); p += 8;
            Require(keys.Add(key), "duplicate row");
            int? id = null; string? text = null, ns = null, textKey = null;
            var propertyStart = -1; var textStart = -1; var textEnd = -1;
            while (true)
            {
                var tag = p; var field = Name(p); p += 8;
                if (field == "None") break;
                var type = Name(p); p += 8;
                var size = I32(body, p); p += 4;
                Require(I32(body, p) == 0, "property array index"); p += 4;
                Require(p < body.Length && body[p++] == 0 && size >= 0 && size <= body.Length - p, "property extent/guid");
                var end = p + size;
                if (field == "Id" && type == "IntProperty" && size == 4 && id is null) id = I32(body, p);
                else if (field == "Text" && type == "TextProperty" && text is null && size >= 5)
                {
                    if (body[p + 4] == 255 && size == 9 && I32(body, p + 5) == 0) { text = ""; propertyStart = tag; }
                    else
                    {
                        Require(body[p + 4] == 0, "FText history");
                        var q = p + 5; ns = ReadString(body, ref q); textKey = ReadString(body, ref q);
                        textStart = q; text = ReadString(body, ref q); textEnd = q; propertyStart = tag;
                        Require(q == end, "FText extent");
                    }
                }
                else Require(false, "property schema");
                p = end;
            }
            Require(id.HasValue && text is not null, "row schema");
            rows.Add(new Row(key, id!.Value, text!, ns, textKey, start, p, propertyStart, textStart, textEnd));
        }
        Require(p == body.Length - 4 && I32(body, p) == unchecked((int)0x9e2a83c1), "package trailer");
        return new UeTextDataTable((byte[])header.Clone(), (byte[])body.Clone(), exportOffset, rows);
    }

    public (byte[] Header, byte[] Body) Rewrite(IReadOnlyDictionary<string, string> replacements,
        IReadOnlyDictionary<string, (string Namespace, string TextKey)>? emptyIdentities = null)
    {
        var known = Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        Require(replacements.Keys.All(k => known.TryGetValue(k, out var r) && (r.TextStart >= 0 || emptyIdentities?.ContainsKey(k) == true)), "replacement keys/empty identities");
        using var stream = new MemoryStream();
        var cursor = 0;
        foreach (var row in Rows)
        {
            if (!replacements.TryGetValue(row.Key, out var value) || value == row.Text) continue;
            stream.Write(_body.AsSpan(cursor, row.PropertyStart - cursor));
            using var text = new MemoryStream();
            using (var writer = new BinaryWriter(text, Encoding.UTF8, leaveOpen: true))
            {
                if (row.TextStart < 0)
                {
                    var identity = emptyIdentities![row.Key];
                    writer.Write(0u); writer.Write((byte)0);
                    FStringCodec.Write(writer, identity.Namespace); FStringCodec.Write(writer, identity.TextKey);
                }
                FStringCodec.Write(writer, value);
            }
            var tag = _body.AsSpan(row.PropertyStart, row.TextStart < 0 ? 25 : row.TextStart - row.PropertyStart).ToArray();
            var oldSize = BinaryPrimitives.ReadInt32LittleEndian(tag.AsSpan(16));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), row.TextStart < 0 ? (int)text.Length
                : checked(oldSize + (int)text.Length - (row.TextEnd - row.TextStart)));
            stream.Write(tag); stream.Write(text.ToArray()); cursor = row.TextStart < 0 ? row.PropertyStart + 25 + oldSize : row.TextEnd;
        }
        stream.Write(_body.AsSpan(cursor));
        var body = stream.ToArray(); var header = (byte[])_header.Clone(); var delta = (long)body.Length - _body.Length;
        foreach (var at in new[] { _exportOffset + 28, 169 })
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(at), checked(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(at)) + delta));
        var verified = Parse(header, body);
        for (var i = 0; i < Rows.Count; i++)
        {
            var before = Rows[i]; var after = verified.Rows[i];
            Require(before.Key == after.Key && before.Id == after.Id, "rewritten row identity");
            if (before.TextStart < 0 && before.Text != after.Text)
                Require(emptyIdentities is not null && after.Namespace == emptyIdentities[before.Key].Namespace && after.TextKey == emptyIdentities[before.Key].TextKey, "new Base FText identity");
            else Require(before.Namespace == after.Namespace && before.TextKey == after.TextKey, "rewritten text identity");
            if (replacements.TryGetValue(before.Key, out var expected)) Require(after.Text == expected, "rewritten value");
            else Require(_body.AsSpan(before.Start, before.End - before.Start).SequenceEqual(body.AsSpan(after.Start, after.End - after.Start)), "untouched row changed");
        }
        return (header, body);
    }
}
