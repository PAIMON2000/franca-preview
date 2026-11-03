using System.Buffers.Binary;
using System.IO;

namespace Franca.Services.UePak;

// Minimal TrueType reader for font health inspection: cmap coverage, upem,
// glyph count and weight class. Supports sfnt version 1.0 ("00010000"/"true")
// with cmap format 4 and 12 subtables, which covers the UE .ufont files seen.
// All sfnt scalar fields are big-endian.
public sealed class TtfFont
{
    public int UnitsPerEm { get; }
    public int GlyphCount { get; }
    public int WeightClass { get; }
    public IReadOnlySet<int> Codepoints { get; }

    private TtfFont(int unitsPerEm, int glyphCount, int weightClass, IReadOnlySet<int> codepoints)
    {
        UnitsPerEm = unitsPerEm;
        GlyphCount = glyphCount;
        WeightClass = weightClass;
        Codepoints = codepoints;
    }

    public static TtfFont Parse(byte[] data)
    {
        var r = new Reader(data, 0);
        var version = r.U32();
        if (version is not (0x00010000 or 0x74727565)) // 1.0 or 'true'
            throw new InvalidDataException($"Unsupported sfnt version 0x{version:X8}; expected TrueType outlines");
        var numTables = r.U16();
        r.U16(); r.U16(); r.U16(); // searchRange, entrySelector, rangeShift
        uint cmapOffset = 0, cmapLength = 0, headOffset = 0, maxpOffset = 0, os2Offset = 0;
        for (var i = 0; i < numTables; i++)
        {
            var tag = r.U32();
            r.U32(); // checksum
            var offset = r.U32();
            var length = r.U32();
            if ((ulong)offset + length > (uint)data.Length) throw new InvalidDataException("Table outside file bounds");
            switch (tag)
            {
                case 0x636D6170: cmapOffset = offset; cmapLength = length; break; // 'cmap'
                case 0x68656164: headOffset = offset; break;                      // 'head'
                case 0x6D617870: maxpOffset = offset; break;                      // 'maxp'
                case 0x4F532F32: os2Offset = offset; break;                       // 'OS/2'
            }
        }
        if (cmapOffset == 0 || headOffset == 0 || maxpOffset == 0)
            throw new InvalidDataException("Required cmap/head/maxp table missing");
        var upem = new Reader(data, headOffset + 18).U16();
        if (upem == 0) throw new InvalidDataException("unitsPerEm is zero");
        var glyphCount = new Reader(data, maxpOffset + 4).U16();
        var weight = 400;
        if (os2Offset != 0) weight = new Reader(data, os2Offset + 4).U16();
        return new TtfFont(upem, glyphCount, weight, ReadCmap(data, cmapOffset, cmapLength));
    }

    private static IReadOnlySet<int> ReadCmap(byte[] data, uint offset, uint length)
    {
        var r = new Reader(data, offset);
        if (r.U16() != 0) throw new InvalidDataException("cmap version is not zero");
        var count = r.U16();
        if (length < 4 + (ulong)count * 8) throw new InvalidDataException("cmap encoding records truncated");
        // Prefer full-Unicode format 12, then BMP format 4.
        uint fmt12 = 0, fmt4 = 0;
        for (var i = 0; i < count; i++)
        {
            var platform = r.U16();
            r.U16(); // encoding
            var subOffset = r.U32();
            if (subOffset + 2 > length) throw new InvalidDataException("cmap subtable outside table");
            if (new Reader(data, offset + subOffset).U16() == 12 && platform is 0 or 3) fmt12 = subOffset;
            else if (fmt4 == 0 && platform is 0 or 3) fmt4 = subOffset;
        }
        if (fmt12 != 0) return ReadFormat12(data, offset + fmt12);
        if (fmt4 != 0) return ReadFormat4(data, offset + fmt4);
        throw new InvalidDataException("No usable cmap format 4/12 subtable");
    }

    private static IReadOnlySet<int> ReadFormat12(byte[] data, uint position)
    {
        var r = new Reader(data, position);
        if (r.U16() != 12) throw new InvalidDataException("cmap format 12 mismatch");
        r.U16(); // reserved
        var length = r.U32();
        r.U32(); // language
        var groups = r.U32();
        if (length < 16 + (ulong)groups * 12) throw new InvalidDataException("cmap format 12 truncated");
        var set = new HashSet<int>();
        for (var i = 0; i < groups; i++)
        {
            var start = r.U32();
            var end = r.U32();
            r.U32(); // startGlyphId
            if (end < start || end - start > 0x110000) throw new InvalidDataException("cmap format 12 group invalid");
            for (var cp = start; cp <= end; cp++) set.Add((int)cp);
        }
        return set;
    }

    private static IReadOnlySet<int> ReadFormat4(byte[] data, uint position)
    {
        var r = new Reader(data, position);
        if (r.U16() != 4) throw new InvalidDataException("cmap format 4 mismatch");
        var length = r.U16();
        r.U16(); // language
        var segCount = r.U16() / 2;
        r.U16(); r.U16(); r.U16(); // searchRange, entrySelector, rangeShift
        if (length < 16 + (ulong)segCount * 8) throw new InvalidDataException("cmap format 4 truncated");
        var endCodes = new ushort[segCount];
        for (var i = 0; i < segCount; i++) endCodes[i] = r.U16();
        r.U16(); // reservedPad
        var startCodes = new ushort[segCount];
        for (var i = 0; i < segCount; i++) startCodes[i] = r.U16();
        for (var i = 0; i < segCount; i++) r.U16(); // idDelta values not needed for coverage
        var idRangeOffsetPos = r.Pos;
        var idRangeOffsets = new ushort[segCount];
        for (var i = 0; i < segCount; i++) idRangeOffsets[i] = r.U16();
        var set = new HashSet<int>();
        for (var i = 0; i < segCount; i++)
        {
            if (startCodes[i] == 0xFFFF && endCodes[i] == 0xFFFF) continue; // terminator
            if (endCodes[i] < startCodes[i]) throw new InvalidDataException("cmap format 4 segment invalid");
            for (uint cp = startCodes[i]; cp <= endCodes[i]; cp++)
            {
                if (idRangeOffsets[i] == 0)
                {
                    set.Add((int)cp);
                }
                else
                {
                    var glyphPos = idRangeOffsetPos + (ulong)i * 2 + idRangeOffsets[i] + (ulong)(cp - startCodes[i]) * 2;
                    if ((long)glyphPos + 2 <= position + length
                        && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)glyphPos)) != 0)
                        set.Add((int)cp);
                }
            }
        }
        return set;
    }

    private struct Reader
    {
        private readonly byte[] _data;
        public uint Pos { get; private set; }
        public Reader(byte[] data, uint pos) { _data = data; Pos = pos; }
        public ushort U16()
        {
            var v = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)Pos, 2));
            Pos += 2;
            return v;
        }
        public uint U32()
        {
            var v = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan((int)Pos, 4));
            Pos += 4;
            return v;
        }
    }
}
