using System.Buffers.Binary;
using System.IO;
using Franca.Services;

namespace Franca.Diagnostics;

// Synthetic minimal TTF fixtures only; never touches a real game, font or network.
internal static class UeFontProbe
{
    public static void Run()
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"RESULT\tUEFONT\t{name}\t{(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }

        // text-capable face: ASCII + a few CJK + one kana, upem 1024 like the Octopath faces
        var text = BuildFont(upem: 1024, weight: 770, glyphs: 100,
            [(0x0020, 0x007E), (0x4E00, 0x4E03), (0x30A2, 0x30A2)]);
        var face = UeFontHealthService.Parse("text", text);
        Check("parse_units_per_em", face.UnitsPerEm == 1024);
        Check("parse_weight_class", face.WeightClass == 770);
        Check("parse_glyph_count", face.GlyphCount == 100);
        Check("parse_kana_and_cjk_counts", face.KanaCount == 1 && face.CjkCount == 4);
        Check("text_capable_detected", face.IsTextCapable);

        // symbol-only face: ASCII + circled digits, no kana/CJK
        var symbol = BuildFont(upem: 1000, weight: 400, glyphs: 80, [(0x0020, 0x007E), (0x2460, 0x2473)]);
        var sym = UeFontHealthService.Parse("symbol", symbol);
        Check("symbol_only_classified", !sym.IsTextCapable && sym.CodepointCount == 0x7E - 0x20 + 1 + 0x14);

        // format 12 subtable is preferred when present
        var fmt12 = BuildFont12(glyphs: 50, [(0x0020, 0x007E), (0x4E10, 0x4E12)]);
        var f12 = UeFontHealthService.Parse("fmt12", fmt12);
        Check("format12_preferred", f12.CjkCount == 3 && f12.CodepointCount == 0x7E - 0x20 + 1 + 3);

        // health analysis over both faces
        var report = UeFontHealthService.Analyze(
            [("text", text), ("symbol", symbol)],
            ["設定を続ける 123", "冒険の旅", "♥①⑩"]);
        Check("symbol_font_not_flagged", report.SymbolOnlyFaces == 1 && report.Issues.All(i => i.Name != "symbol"));
        Check("missing_reported_with_sample", report.Issues.Single(i => i.Name == "text") is { } issue
            && issue.Missing == 13 // 設定を続ける + 冒険の旅 + ♥①⑩ (space/digits covered)
            && issue.Sample.Contains('設') && issue.Sample.Contains('旅'));
        Check("needed_chars_counted", report.NeededChars == "設定を続ける 123冒険の旅♥①⑩".Distinct().Count());
        Check("uncovered_sample_heart", report.UncoveredSample.Contains('♥'));
        Check("not_healthy", !report.Healthy);

        var covered = UeFontHealthService.Analyze([("text", text)], ["abc ア"]);
        Check("healthy_when_covered", covered.Healthy && covered.Issues.Count == 0 && covered.UncoveredSample == "");

        try { UeFontHealthService.Parse("bad", new byte[64]); Check("short_file_rejected", false); }
        catch (InvalidDataException) { Check("short_file_rejected", true); }
        try { UeFontHealthService.Parse("bad", BuildFont(upem: 0, weight: 400, glyphs: 1, [(0x20, 0x21)])); Check("zero_upem_rejected", false); }
        catch (InvalidDataException) { Check("zero_upem_rejected", true); }

        Console.WriteLine($"RESULT\tUEFONT\tfailures={failures}");
        if (failures > 0) Environment.ExitCode = 1;
    }

    // Minimal sfnt: head (54B), maxp (6B), OS/2 (96B), cmap with one format 4 subtable.
    private static byte[] BuildFont(int upem, int weight, int glyphs, (ushort Start, ushort End)[] ranges)
    {
        var cmap = new BeWriter();
        var segCount = ranges.Length + 1; // + terminator
        ushort subLength = (ushort)(16 + segCount * 8);
        cmap.U16(0).U16(1);                // version, numTables
        cmap.U16(3).U16(1).U32(12);        // platform 3, encoding 1, subtable offset
        cmap.U16(4).U16(subLength).U16(0);
        cmap.U16((ushort)(segCount * 2)).U16(4);                     // searchRange (2^1*2)
        cmap.U16(1).U16((ushort)(segCount * 2 - 2));                 // entrySelector, rangeShift
        foreach (var (_, e) in ranges) cmap.U16(e);
        cmap.U16(0xFFFF);
        cmap.U16(0); // reservedPad
        foreach (var (s, _) in ranges) cmap.U16(s);
        cmap.U16(0xFFFF);
        for (var i = 0; i < segCount; i++) cmap.U16(0); // idDelta 0
        for (var i = 0; i < segCount; i++) cmap.U16(0); // idRangeOffset 0
        return Assemble(upem, weight, glyphs, ("cmap", cmap.ToArray()));
    }

    // cmap with a single format 12 subtable (platform 3, encoding 10).
    private static byte[] BuildFont12(int glyphs, (uint Start, uint End)[] groups)
    {
        var cmap = new BeWriter();
        cmap.U16(0).U16(1);
        cmap.U16(3).U16(10).U32(12);
        cmap.U16(12).U16(0);
        cmap.U32((uint)(16 + groups.Length * 12)).U32(0);
        cmap.U32((uint)groups.Length);
        foreach (var (s, e) in groups) cmap.U32(s).U32(e).U32(1);
        return Assemble(upem: 1000, weight: 400, glyphs: glyphs, ("cmap", cmap.ToArray()));
    }

    private static byte[] Assemble(int upem, int weight, int glyphs, params (string Tag, byte[] Data)[] tables)
    {
        var head = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), (ushort)upem);
        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxp.AsSpan(0), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphs);
        var os2 = new byte[96];
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4), (ushort)weight);
        var all = new List<(string Tag, byte[] Data)> { ("head", head), ("maxp", maxp), ("OS/2", os2) };
        all.AddRange(tables);
        var w = new BeWriter();
        w.U32(0x00010000).U16((ushort)all.Count);
        var maxPow = 1; while (maxPow * 2 <= all.Count) maxPow *= 2;
        w.U16((ushort)(maxPow * 16)).U16((ushort)Log2(maxPow)).U16((ushort)(all.Count * 16 - maxPow * 16));
        var offset = 12u + (uint)all.Count * 16;
        var records = new List<byte[]>();
        foreach (var (tag, data) in all)
        {
            var rec = new byte[16];
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(rec, 0);
            BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(8), offset);
            BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(12), (uint)data.Length);
            records.Add(rec);
            offset += (uint)((data.Length + 3) & ~3);
        }
        foreach (var rec in records) w.Raw(rec);
        foreach (var (_, data) in all)
        {
            w.Raw(data);
            for (var i = 0; i < ((data.Length + 3) & ~3) - data.Length; i++) w.U8(0);
        }
        return w.ToArray();

        static int Log2(int n) { var log = 0; while (n > 1) { n >>= 1; log++; } return log; }
    }

    private sealed class BeWriter
    {
        private readonly List<byte> _bytes = [];
        public BeWriter U8(byte v) { _bytes.Add(v); return this; }
        public BeWriter U16(ushort v) { _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)v); return this; }
        public BeWriter U32(uint v) { _bytes.Add((byte)(v >> 24)); _bytes.Add((byte)(v >> 16)); _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)v); return this; }
        public BeWriter Raw(byte[] b) { _bytes.AddRange(b); return this; }
        public byte[] ToArray() => [.. _bytes];
    }
}
