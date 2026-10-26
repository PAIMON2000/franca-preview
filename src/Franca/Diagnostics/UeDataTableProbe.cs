using System.Buffers.Binary;
using System.IO;
using System.Text;
using Franca.Services;
using Franca.Services.UePak;

namespace Franca.Diagnostics;

internal static class UeDataTableProbe
{
    public static async Task RunAsync()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine($"RESULT\tUETABLE\t{name}\t{(ok ? "PASS" : "FAIL")}"); if (!ok) failures++; }
        var root = Path.Combine(Path.GetTempPath(), "franca-table-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rich = TranslationTextSegmenter.Split("<span style=\"italic\">Hello</>");
            Check("ue_anonymous_closing_tag_is_literal", rich.Count(p => p.NeedsTranslation) == 1 && rich.Single(p=>p.NeedsTranslation).Text == "Hello"
                && string.Concat(rich.Where(p=>!p.NeedsTranslation).Select(p=>p.Text)) == "<span style=\"italic\"></>");
            Check("protected_tokens_detect_corruption", !TranslationTextSegmenter.HasSameProtectedTokens("<R>Hello</> {0}\n", "<R>訳</> 0\n")
                && TranslationTextSegmenter.HasSameProtectedTokens("<R>Hello</> {0}\n", "<R>訳</> {0}\n"));
            var (sh, sb) = Fixture(false);
            var (th, tb) = Fixture(true);
            var source = UeTextDataTable.Parse(sh, sb); var target = UeTextDataTable.Parse(th, tb);
            var noop = source.Rewrite(new Dictionary<string, string>());
            Check("no_op_exact_bytes", noop.Header.SequenceEqual(sh) && noop.Body.SequenceEqual(sb));
            var rewritten = target.Rewrite(new Dictionary<string, string> { ["hello"] = "日本語 {name}\n次の行" },
                new Dictionary<string, (string, string)> { ["hello"] = ("UI", "hello_Text") });
            var parsed = UeTextDataTable.Parse(rewritten.Header, rewritten.Body);
            Check("empty_history_created_with_explicit_identity", parsed.Rows[0].Text == "日本語 {name}\n次の行" && parsed.Rows[0].Namespace == "UI" && parsed.Rows[0].TextKey == "hello_Text");
            Check("unselected_row_unchanged", parsed.Rows[1].Text == "Native only" && tb.AsSpan(target.Rows[1].Start, target.Rows[1].End - target.Rows[1].Start)
                .SequenceEqual(rewritten.Body.AsSpan(parsed.Rows[1].Start, parsed.Rows[1].End - parsed.Rows[1].Start)));
            try { target.Rewrite(new Dictionary<string, string> { ["hello"] = "bad" }); Check("empty_identity_required", false); }
            catch (InvalidDataException) { Check("empty_identity_required", true); }
            try { UeTextDataTable.Parse(sh, sb[..^1]); Check("truncated_rejected", false); }
            catch (InvalidDataException) { Check("truncated_rejected", true); }
            var malformed = (byte[])sb.Clone(); malformed[source.Rows[0].PropertyStart + 29] = 11;
            try { UeTextDataTable.Parse(sh, malformed); Check("unknown_history_rejected", false); }
            catch (InvalidDataException) { Check("unknown_history_rejected", true); }
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            const string prefix = "Game/Content/GameText/Database/GameText";
            var original = PakFileWriter.Write("../../../", 12, new[] {
                new PakFileWriter.Item { Path=prefix+"EN.uasset",Data=sh }, new PakFileWriter.Item { Path=prefix+"EN.uexp",Data=sb },
                new PakFileWriter.Item { Path=prefix+"ZH_CN.uasset",Data=th }, new PakFileWriter.Item { Path=prefix+"ZH_CN.uexp",Data=tb } });
            var pak = Path.Combine(game, "original.pak"); await File.WriteAllBytesAsync(pak, original);
            Check("inspect_matches_identity", UeStaticService.InspectTables(pak).Single().Entries == 2);
            var calls = 0; var settings = new AppSettings { SourceLanguage="en",TargetLanguage="ja",Endpoint="BingTranslate",FallbackEndpoint="" };
            foreach (var e in EndpointCatalog.All.Where(e => e.Id != "BingTranslate")) settings.DisabledEndpoints.Add(e.Id);
            var service = new UeStaticService(Path.Combine(root, "packs"), (_, text, _, _, _, _) => {
                Interlocked.Increment(ref calls); return Task.FromResult<string?>("日本語:"+text); });
            new PackStore(Path.Combine(root,"packs")).Store(999995,"ja","Hello {name}\nLine2","壊れた {wrong}","fixture",false);
            var first = await service.PrepareTablesAsync(game, pak, 999995, Path.Combine(root, "out"), new[] {prefix+"EN.uasset"}, settings, null, CancellationToken.None);
            var priorCalls = calls;
            var second = await service.PrepareTablesAsync(game, pak, 999995, Path.Combine(root, "out"), new[] {prefix+"EN.uasset"}, settings, null, CancellationToken.None);
            Check("corrupt_cache_repaired_and_resumed", !first.Incomplete && first.Cached == 0 && !second.Incomplete && second.Cached == 1 && calls == priorCalls && priorCalls > 0);
            using var output = PakFileReader.Open(Path.Combine(second.OutputDirectory, "pakchunk99-WindowsNoEditor_P.pak"));
            var result = UeTextDataTable.Parse(output.Extract(output.Find(prefix+"ZH_CN.uasset")!), output.Extract(output.Find(prefix+"ZH_CN.uexp")!));
            Check("format_and_native_preserved", result.Rows[0].Text == "日本語:Hello {name}\n日本語:Line2" && result.Rows[1].Text == "Native only");
            Check("source_unchanged_uncompressed", original.SequenceEqual(await File.ReadAllBytesAsync(pak)) && output.Entries.All(e=>e.CompressionMethod==0));
            try { await service.PrepareTablesAsync(game, pak, 999995, game, new[]{prefix+"EN.uasset"},settings,null,CancellationToken.None); Check("game_output_rejected",false); }
            catch(ArgumentException) { Check("game_output_rejected",true); }
        }
        catch (Exception ex) { Console.WriteLine($"RESULT\tUETABLE\texception\t{ex}"); failures++; }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"RESULT\tUETABLE\tfailures={failures}");
        if (failures > 0) Environment.ExitCode = 1;
    }

    private static (byte[] Header, byte[] Body) Fixture(bool target)
    {
        var names = new[] { "None", "RowStruct", "ObjectProperty", "Id", "IntProperty", "Text", "TextProperty", "hello", "empty" };
        using var data = new MemoryStream(); using var w = new BinaryWriter(data, Encoding.UTF8, true);
        void Name(int n) { w.Write(n); w.Write(0); }
        void Str(string s) => FStringCodec.Write(w, s);
        Name(1); Name(2); w.Write(4); w.Write(0); w.Write((byte)0); w.Write(-1); Name(0); w.Write(0); w.Write(2);
        for (var i = 0; i < 2; i++)
        {
            Name(7+i); Name(3); Name(4); w.Write(4); w.Write(0); w.Write((byte)0); w.Write(i);
            Name(5); Name(6); var sizeAt=data.Position; w.Write(0); w.Write(0); w.Write((byte)0); var begin=data.Position;
            w.Write(0u);
            if ((i==0 && target) || (i==1 && !target)) { w.Write((byte)255); w.Write(0); }
            else { w.Write((byte)0); Str("UI"); Str(i==0?"hello_Text":"empty_Text"); Str(i==0?"Hello {name}\nLine2":"Native only"); }
            var end=data.Position; data.Position=sizeAt; w.Write((int)(end-begin)); data.Position=end; Name(0);
        }
        w.Write(0x9e2a83c1u); var body=data.ToArray();
        using var nameStream=new MemoryStream(); using(var nw=new BinaryWriter(nameStream,Encoding.UTF8,true))
            foreach(var name in names) { FStringCodec.Write(nw,name); nw.Write(0u); }
        var export=193+(int)nameStream.Length; var header=new byte[export+104];
        void Put(int at,int value)=>BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(at),value);
        Put(0,unchecked((int)0x9e2a83c1)); Put(4,-7); Put(24,header.Length); Put(28,5); "None\0"u8.CopyTo(header.AsSpan(32));
        Put(41,names.Length); Put(45,193); Put(57,1); Put(61,export); Put(69,export);
        nameStream.ToArray().CopyTo(header,193);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(export+28),body.Length-4);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(export+36),header.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(169),header.Length+body.Length-4);
        return(header,body);
    }
}
