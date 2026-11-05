// Locates Ren'Py compiled scripts in a game install (loose .rpyc files plus
// .rpyc entries inside .rpa archives) and extracts translatable strings.

using System;
using System.Collections.Generic;
using System.IO;

namespace Franca.Services.RenPy;

public sealed class RenPyScanResult
{
    public required string GameDir { get; init; }
    public required string ScriptRoot { get; init; }
    public List<(string Source, List<object?> Stmts)> Scripts { get; } = [];
    public List<string> Errors { get; } = [];
}

public static class RenPyExtractService
{
    /// <summary>Finds the directory holding game scripts: <paramref name="gameDir"/>/game
    /// when present, otherwise <paramref name="gameDir"/> itself (depth-limited search).</summary>
    public static RenPyScanResult Scan(string gameDir)
    {
        var root = Path.Combine(gameDir, "game");
        if (!Directory.Exists(root)) root = gameDir;

        var result = new RenPyScanResult { GameDir = gameDir, ScriptRoot = root };

        foreach (var rpyc in Directory.GetFiles(root, "*.rpyc", SearchOption.AllDirectories))
        {
            try
            {
                result.Scripts.Add((Path.GetFileName(rpyc), RpycFile.LoadStatements(File.ReadAllBytes(rpyc))));
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{Path.GetFileName(rpyc)}: {ex.Message}");
            }
        }

        foreach (var rpaPath in Directory.GetFiles(root, "*.rpa", SearchOption.AllDirectories))
        {
            try
            {
                using var rpa = RpaFile.Open(rpaPath);
                foreach (var name in rpa.Names)
                {
                    if (!name.EndsWith(".rpyc", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        result.Scripts.Add((name, RpycFile.LoadStatements(rpa.Extract(name))));
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{Path.GetFileName(rpaPath)}: {ex.Message}");
            }
        }

        return result;
    }

    public static Dictionary<string, int> NodeHistogram(IEnumerable<(string Source, List<object?> Stmts)> scripts)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Walk(object? node)
        {
            switch (node)
            {
                case PyFake f:
                    counts[f.FullName] = counts.TryGetValue(f.FullName, out var n) ? n + 1 : 1;
                    foreach (var v in f.State.Values) WalkValue(v);
                    break;
                case PyTuple t: foreach (var v in t) Walk(v); break;
                case List<object?> l: foreach (var v in l) Walk(v); break;
            }
        }
        void WalkValue(object? v)
        {
            if (v is PyFake or PyTuple or List<object?>) Walk(v);
        }
        foreach (var (_, stmts) in scripts)
            foreach (var s in stmts) Walk(s);
        return counts;
    }

    public static List<RenPyString> ExtractStrings(RenPyScanResult scan) =>
        RenPyStringExtractor.FromStatements(scan.Scripts);
}
