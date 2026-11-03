using Franca.Services.UePak;

namespace Franca.Services;

// Read-only static font health inspection for UE games (roadmap stage 3):
// parse .ufont (bare TTF) faces, compare cmap coverage against the characters
// translations actually need, and report the missing set per text-capable font.
// Symbol-only faces (no kana/CJK, e.g. icon/number fonts) are classified, not
// flagged: they cannot receive CJK text in any official language either.
public static class UeFontHealthService
{
    public sealed record FontFace(string Name, int GlyphCount, int CodepointCount, int UnitsPerEm,
        int WeightClass, int KanaCount, int CjkCount)
    {
        public bool IsTextCapable => KanaCount > 0 || CjkCount > 0;
    }
    public sealed record FontIssue(string Name, int Missing, string Sample);
    public sealed record Report(int Faces, int TextCapableFaces, int SymbolOnlyFaces,
        int NeededChars, IReadOnlyList<FontIssue> Issues, string UncoveredSample)
    {
        public bool Healthy => Issues.Count == 0;
    }

    private const int KanaBegin = 0x3040, KanaEnd = 0x30FF;
    private const int CjkBegin = 0x4E00, CjkEnd = 0x9FFF;

    public static FontFace Parse(string name, byte[] data)
    {
        var font = TtfFont.Parse(data);
        var kana = 0; var cjk = 0;
        foreach (var cp in font.Codepoints)
        {
            if (cp is >= KanaBegin and <= KanaEnd) kana++;
            else if (cp is >= CjkBegin and <= CjkEnd) cjk++;
        }
        return new FontFace(name, font.GlyphCount, font.Codepoints.Count, font.UnitsPerEm,
            font.WeightClass, kana, cjk);
    }

    public static Report Analyze(IReadOnlyList<(string Name, byte[] Data)> faces, IEnumerable<string> texts)
    {
        var needed = texts.SelectMany(t => t).Where(c => c >= 0x20 && !char.IsSurrogate(c))
            .Select(c => (int)c).ToHashSet();
        var issues = new List<FontIssue>();
        var textCapable = 0;
        var symbolOnly = 0;
        var union = new HashSet<int>();
        foreach (var (name, data) in faces)
        {
            var face = Parse(name, data);
            if (!face.IsTextCapable) { symbolOnly++; continue; }
            textCapable++;
            var cmap = TtfFont.Parse(data).Codepoints;
            var missing = needed.Where(cp => !cmap.Contains(cp)).ToArray();
            union.UnionWith(cmap);
            if (missing.Length > 0)
                issues.Add(new FontIssue(name, missing.Length,
                    string.Concat(missing.Order().Take(24).Select(cp => char.ConvertFromUtf32(cp)))));
        }
        var uncovered = needed.Where(cp => !union.Contains(cp)).Order().ToArray();
        return new Report(faces.Count, textCapable, symbolOnly, needed.Count, issues,
            string.Concat(uncovered.Take(24).Select(cp => char.ConvertFromUtf32(cp))));
    }
}
