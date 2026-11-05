// Walks a Ren'Py AST object graph (PyFake nodes) and collects translatable
// strings: Say statement text and Menu choice captions. Extraction only —
// translation is applied later via a generated `translate <lang> strings:`
// block, which Ren'Py applies to dialogue that has no translate block.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Franca.Services.RenPy;

public sealed record RenPyString(string Text, string Kind, string Source);

public static partial class RenPyStringExtractor
{
    [GeneratedRegex(@"_\(\s*u?(['""])((?:\\.|(?!\1).)*)\1\s*\)")]
    private static partial Regex TranslatedCallPattern();
    public static List<RenPyString> FromStatements(IEnumerable<(string Source, List<object?> Stmts)> scripts)
    {
        var seen = new Dictionary<string, RenPyString>(StringComparer.Ordinal);
        foreach (var (source, stmts) in scripts)
        {
            foreach (var node in stmts)
                Walk(node, source, seen);
        }
        return [.. seen.Values];
    }

    private static void Walk(object? node, string source, Dictionary<string, RenPyString> seen)
    {
        switch (node)
        {
            case PyFake f:
                switch (f.Name)
                {
                    case "Say":
                        {
                            if (f.Get("what") is string what && what.Length > 0)
                                Add(seen, what, "dialogue", source);
                            break;
                        }
                    case "Menu":
                        {
                            if (f.Get("items") is List<object?> items)
                            {
                                foreach (var item in items)
                                {
                                    if (item is PyTuple { Count: >= 1 } t && t[0] is { } caption && caption is string s && s.Length > 0)
                                        Add(seen, s, "menu", source);
                                }
                            }
                            // Menu blocks are still walked generically below.
                            break;
                        }
                    case "PyCode":
                        {
                            // Interface strings: screens/_() calls compile into PyCode
                            // sources; extract the literal wrapped in _(...). PyCode
                            // pickles its state as the tuple (1, source, location, mode),
                            // and source itself is a PyExpr object.
                            var code = PyStr(f.Get("source"))
                                ?? (f.State.TryGetValue("__state__", out var st) && st is PyTuple { Count: >= 2 } t
                                    ? PyStr(t[1]) : null);
                            if (code is not null)
                            {
                                foreach (Match m in TranslatedCallPattern().Matches(code))
                                    Add(seen, UnescapePy(m.Groups[2].Value), "code", source);
                            }
                            break;
                        }
                }
                foreach (var kv in f.State)
                    WalkValue(kv.Value, source, seen);
                break;
            case PyTuple tuple:
                foreach (var item in tuple) Walk(item, source, seen);
                break;
            case List<object?> list:
                foreach (var item in list) Walk(item, source, seen);
                break;
        }
    }

    // PyExpr pickles as NEWOBJ(renpy.ast.PyExpr, (text, filename, line)); collapse to text.
    internal static string? PyStr(object? value) => value switch
    {
        string s => s,
        PyFake { Name: "PyExpr", Args: [string text, ..] } => text,
        _ => null,
    };

    private static void WalkValue(object? value, string source, Dictionary<string, RenPyString> seen)
    {
        switch (value)
        {
            case PyFake:
            case PyTuple:
            case List<object?>:
                Walk(value, source, seen);
                break;
        }
    }

    private static void Add(Dictionary<string, RenPyString> seen, string text, string kind, string source)
    {
        if (!seen.TryGetValue(text, out var existing))
            seen[text] = new RenPyString(text, kind, source);
        else if (existing.Kind != kind)
            seen[text] = existing with { Kind = "mixed" };
    }

    private static string UnescapePy(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var next = s[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    _ => next,
                });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
