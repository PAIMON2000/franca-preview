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
    // One UI font composite (a fallback chain): a character is covered when ANY
    // face in the chain has it, matching engine fallback semantics.
    public sealed record ChainFace(string Name, int GlyphCount, int KanaCount, int CjkCount, int Missing);
    public sealed record ChainReport(string Asset, IReadOnlyList<ChainFace> Faces, int ChainMissing, string ChainMissingSample)
    {
        public bool Healthy => ChainMissing == 0;
    }

    private const int KanaBegin = 0x3040, KanaEnd = 0x30FF;
    private const int CjkBegin = 0x4E00, CjkEnd = 0x9FFF;

    public static IReadOnlySet<int> NeededCodepoints(IEnumerable<string> texts)
        => texts.SelectMany(t => t).Where(c => c >= 0x20 && !char.IsSurrogate(c))
            .Select(c => (int)c).ToHashSet();

    public static ChainReport AnalyzeChain(string asset, IReadOnlyList<(string Name, byte[] Data)> faces,
        IReadOnlySet<int> needed)
    {
        var union = new HashSet<int>();
        var list = new List<ChainFace>(faces.Count);
        foreach (var (name, data) in faces)
        {
            var face = Parse(name, data);
            var cmap = TtfFont.Parse(data).Codepoints;
            union.UnionWith(cmap);
            list.Add(new ChainFace(name, face.GlyphCount, face.KanaCount, face.CjkCount,
                needed.Count(cp => !cmap.Contains(cp))));
        }
        var missing = needed.Where(cp => !union.Contains(cp)).Order().ToArray();
        return new ChainReport(asset, list, missing.Length,
            string.Concat(missing.Take(24).Select(cp => char.ConvertFromUtf32(cp))));
    }

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
        var needed = NeededCodepoints(texts);
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
