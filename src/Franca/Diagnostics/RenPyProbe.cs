using System.IO;
using System.IO.Compression;
using System.Text;
using Franca.Services.RenPy;

namespace Franca.Diagnostics;

// Synthetic Ren'Py pipeline probe: hand-built protocol-2 pickles (the exact
// NEWOBJ + (None, {attrs}) BUILD shape Ren'Py 6.99 emits), rpyc v2 slot
// wrapping, AST string extraction, rpy quoting and strings.rpy emission.
internal static class RenPyProbe
{
    private static readonly List<(string Name, bool Pass, string Detail)> Results = [];

    private static void Check(string name, bool pass, string detail = "")
    {
        Results.Add((name, pass, detail));
    }

    public static void Run()
    {
        try
        {
            FixtureRoundTrip();
            RpycV2Wrap();
            ExtractionAndEmission();
        }
        catch (Exception ex)
        {
            Check("unexpected_exception", false, ex.ToString().Replace('\n', ' ')[..Math.Min(300, ex.ToString().Length)]);
        }

        foreach (var (name, pass, detail) in Results)
            Console.WriteLine($"RESULT\tRENPY\t{name}\t{(pass ? "PASS" : "FAIL")}\t{detail}");
        Console.WriteLine($"RESULT\tRENPY\tfailures={Results.Count(r => !r.Pass)}\t{(Results.Any(r => !r.Pass) ? "FAIL" : "PASS")}");
    }

    private sealed class FixtureBuilder
    {
        private readonly MemoryStream _ms = new();

        public byte[] Bytes => _ms.ToArray();
        public void Byte(int b) => _ms.WriteByte((byte)b);
        public void Proto2() { Byte(0x80); Byte(2); }
        public void None() => Byte(0x4e);
        public void Stop() => Byte(0x2e);
        public void Mark() => Byte(0x28);
        public void Tuple2() => Byte(0x86);
        public void Tuple3() => Byte(0x87);
        public void BinUnicode(string s) { Byte(0x58); Int(s.Length); Raw(Encoding.UTF8.GetBytes(s)); }
        public void ShortBinString(string s) { Byte(0x55); Byte(s.Length); Raw(Encoding.UTF8.GetBytes(s)); }
        public void BinInt(int n) { Byte(0x4a); Raw(BitConverter.GetBytes(n)); }
        public void BinInt1(int n) { Byte(0x4b); Byte(n); }
        public void NewTrue() => Byte(0x88);
        public void NewFalse() => Byte(0x89);
        public void EmptyTuple() => Byte(0x29);
        public void EmptyDict() => Byte(0x7d);
        public void EmptyList() => Byte(0x5d);
        public void SetItems() => Byte(0x75);
        public void Append() => Byte(0x61);
        public void Build() => Byte(0x62);
        public void Global(string module, string name) { Byte(0x63); Line(module); Line(name); }
        public void NewObj() => Byte(0x81);
        public void BInput(int idx) { Byte(0x71); Byte(idx); }
        private void Line(string s) { Raw(Encoding.UTF8.GetBytes(s)); Byte(0x0a); }
        private void Int(int n) => Raw(BitConverter.GetBytes(n));
        private void Raw(byte[] b) => _ms.Write(b);
    }

    // A Say node as Ren'Py 6.99 pickles it: NEWOBJ(GLOBAL renpy.ast.Say, ())
    // with BUILD state TUPLE2(NONE, dict-of-attrs). PyExpr 'what' collapses via
    // REDUCE of __getnewargs__, which protocol 2 emits as the NEWOBJ arg tuple.
    private static void WriteSay(FixtureBuilder f, string who, string what, int line, string file, int memo)
    {
        f.Global("renpy.ast", "Say");
        f.EmptyTuple();
        f.NewObj();
        f.BInput(memo);
        f.None();
        f.EmptyDict();
        f.Mark();
        f.ShortBinString("who");
        if (who.Length > 0) f.BinUnicode(who); else f.None();
        f.ShortBinString("what");
        f.BinUnicode(what);
        f.ShortBinString("filename");
        f.BinUnicode(file);
        f.ShortBinString("linenumber");
        f.BinInt1(line);
        f.ShortBinString("translatable");
        f.NewTrue();
        f.SetItems();
        f.Tuple2();
        f.Build();
    }

    private static void FixtureRoundTrip()
    {
        // (data, [Say, Say]) with one None-who narration and one attributed line.
        var f = new FixtureBuilder();
        f.Proto2();
        f.EmptyDict(); // data
        f.BInput(0);
        f.EmptyList();
        f.BInput(1);
        f.Mark();
        WriteSay(f, "", "Hello [player]!", 10, "game/script.rpy", 2);
        f.Append();
        WriteSay(f, "monika", "Just Monika.", 11, "game/script.rpy", 3);
        f.Append();
        f.Tuple2();
        f.Stop();

        var root = PyPickle.Loads(f.Bytes);
        var ok = root is PyTuple { Count: 2 } pair
                 && pair[1] is List<object?> { Count: 2 } stmts
                 && stmts[0] is PyFake say0
                 && say0.Name == "Say"
                 && say0.Get("what") is string w0 && w0 == "Hello [player]!"
                 && say0.Get("who") is null
                 && stmts[1] is PyFake say1
                 && say1.Get("who") is string w1 && w1 == "monika";
        Check("pickle_roundtrip_say_nodes", ok);
    }

    private static void RpycV2Wrap()
    {
        var f = new FixtureBuilder();
        f.Proto2();
        f.None(); // data
        f.EmptyList();
        f.Tuple2();
        f.Stop();
        var blob = ZlibWrap(f.Bytes);

        var rpyc = new List<byte>();
        rpyc.AddRange(Encoding.ASCII.GetBytes("RENPY RPC2"));
        rpyc.AddRange(BitConverter.GetBytes(1));       // slot
        rpyc.AddRange(BitConverter.GetBytes(34));      // start (header + slot triple + terminator)
        rpyc.AddRange(BitConverter.GetBytes(blob.Length));
        rpyc.AddRange(BitConverter.GetBytes(0));       // terminator slot
        rpyc.AddRange(BitConverter.GetBytes(0));       // (start)
        rpyc.AddRange(BitConverter.GetBytes(0));       // (length)
        rpyc.AddRange(blob);

        var stmts = RpycFile.LoadStatements([.. rpyc]);
        Check("rpyc_v2_slot_unwrap", stmts.Count == 0);

        var v1 = RpycFile.LoadStatements(ZlibWrap(f.Bytes));
        Check("rpyc_v1_blob", v1.Count == 0);
    }

    private static byte[] ZlibWrap(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
            zlib.Write(payload);
        return output.ToArray();
    }

    private static void ExtractionAndEmission()
    {
        var f = new FixtureBuilder();
        f.Proto2();
        f.EmptyDict();
        f.BInput(0);
        f.EmptyList();
        f.BInput(1);
        f.Mark();
        WriteSay(f, "", "Line \"one\".\nNext.", 1, "game/a.rpy", 2);
        f.Append();
        WriteSay(f, "", "Line \"one\".\nNext.", 2, "game/a.rpy", 3); // duplicate text
        f.Append();
        WriteSay(f, "", "Second line.", 4, "game/a.rpy", 4);
        f.Append();
        f.Tuple2();
        f.Stop();

        var stmts = RpycFile.LoadStatements(ZlibWrap(f.Bytes));
        var strings = RenPyStringExtractor.FromStatements([("a.rpy", stmts)]);
        Check("extraction_dedup", strings.Count == 2, $"got {strings.Count}");

        var translations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Line \"one\".\nNext."] = "第一行\"引号\"。\n次行。",
        };
        var dir = Path.Combine(Path.GetTempPath(), "franca-renpy-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "strings.rpy");
            RenPyStaticService.WriteStringsRpy(path, "zh_cn", strings.Select(s => s.Text).ToArray(), translations);
            var text = File.ReadAllText(path);
            Check("strings_rpy_emitted", text.Contains("translate zh_cn strings:"));
            Check("strings_rpy_quote_escape", text.Contains("old \"Line \\\"one\\\".\\nNext.\""),
                text.Split('\n').FirstOrDefault(l => l.Contains("old")) ?? "");
            Check("strings_rpy_new_escape", text.Contains("new \"第一行\\\"引号\\\"。\\n次行。\""));
            Check("strings_rpy_untranslated_omitted", !text.Contains("Second line."));

            var reloaded = RenPyExtractService.ExtractStrings(new RenPyScanResult
            {
                GameDir = dir,
                ScriptRoot = dir,
                Scripts = { ("a.rpy", stmts) },
            });
            Check("service_extraction_consistent", reloaded.Count == 2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
