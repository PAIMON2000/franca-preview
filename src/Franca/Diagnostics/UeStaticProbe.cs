using System.Collections.Concurrent;
using System.IO;
using Franca.Services;
using Franca.Services.UePak;

namespace Franca.Diagnostics;

// Temporary synthetic archives and injected providers only; never accesses a real game or network.
internal static class UeStaticProbe
{
    public static async Task RunAsync()
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"RESULT\tUESTATIC\t{name}\t{(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }
        var root = Path.Combine(Path.GetTempPath(), "franca-ue-static-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            var pakPath = Path.Combine(game, "source.pak");
            const string sourcePath = "Game/Content/Localization/UI/en/UI.locres";
            const string targetPath = "Game/Content/Localization/UI/zh-Hans/UI.locres";
            var source = LocResFile.FromDictionary(new() { ["UI"] = new()
                { ["hello"] = "  Hello {player}!\r\nNext line.  ", ["failed"] = "Retry this.", ["dup"] = "Retry this." } });
            var target = LocResFile.Parse(source.Serialize());
            foreach (var e in target.Namespaces.SelectMany(n => n.Entries)) e.Value = "官方对照";
            target.Namespaces[0].Entries.Add(new LocResFile.LocResEntry { Key = "native-only", Value = "原生独有",
                KeyHash = CityHash64.StringHash32("native-only"), SourceStringHash = 123 });
            source.Namespaces[0].Entries[0].SourceStringHash = 0;
            target.Namespaces[0].Entries[0].SourceStringHash = 0;
            var original = PakFileWriter.Write("../../../", 42, new List<PakFileWriter.Item>
            {
                new() { Path = sourcePath, Data = source.Serialize(), Compress = true },
                new() { Path = targetPath, Data = target.Serialize(), Compress = true },
            });
            await File.WriteAllBytesAsync(pakPath, original);
            Check("inspect_slots", UeStaticService.Inspect(pakPath).Single().Entries == 3);
            var settings = new AppSettings { SourceLanguage = "en", TargetLanguage = "zh-CN", Endpoint = "BingTranslate", FallbackEndpoint = "" };
            foreach (var e in EndpointCatalog.All.Where(e => e.Id != "BingTranslate")) settings.DisabledEndpoints.Add(e.Id);
            var calls = new ConcurrentDictionary<string, int>();
            var fail = true;
            Task<string?> Provider(string id, string text, string from, string to, AppSettings snapshot, CancellationToken ct)
            {
                calls.AddOrUpdate(text, 1, (_, n) => n + 1);
                Check("request_200_source_and_free", text.Length <= 200 && from == "en" && to == "zh-CN" && id == "BingTranslate");
                return Task.FromResult<string?>(fail && text == "Retry this." ? null : "译:" + text);
            }
            var cacheRoot = Path.Combine(root, "packs");
            var service = new UeStaticService(cacheRoot, Provider);
            var first = await service.PrepareAsync(game, pakPath, 999991, Path.Combine(root, "output"),
                new[] { sourcePath }, settings, null, CancellationToken.None);
            Check("partial_marked_incomplete", first.Incomplete && first.Missing == 1 && first.UniqueTexts == 2);
            var packs = new PackStore(cacheRoot);
            Check("failed_not_cached_as_translation", packs.Lookup(999991, "zh-CN", "Retry this.") is null);
            using (var prepared = PakFileReader.Open(Path.Combine(first.OutputDirectory, "pakchunk99-WindowsNoEditor_P.pak")))
            {
                Check("overlay_uncompressed_for_ue426", prepared.Entries.All(e => e.CompressionMethod == 0 && e.Blocks.Count == 0));
                var loc = LocResFile.Parse(prepared.Extract(prepared.Find(targetPath)!));
                Check("partial_keeps_english", loc.ToDictionary()["UI"]["failed"] == "Retry this.");
                Check("whitespace_placeholders_retained", loc.ToDictionary()["UI"]["hello"] == "  译:Hello {player}!\r\n译:Next line.  ");
                Check("zero_source_hash_retained", loc.Namespaces[0].Entries[0].SourceStringHash == 0);
                Check("target_only_preserved_and_reported", loc.ToDictionary()["UI"]["native-only"] == "原生独有"
                    && first.PreservedTargetOnlyEntries == 1);
            }
            var priorCalls = calls.GetValueOrDefault("Hello");
            fail = false;
            var second = await service.PrepareAsync(game, pakPath, 999991, Path.Combine(root, "output"),
                new[] { sourcePath }, settings, null, CancellationToken.None);
            Check("resume_cached_and_complete", !second.Incomplete && second.Cached == 1 && calls.GetValueOrDefault("Hello") == priorCalls);
            Check("per_game_cache_isolation", packs.Lookup(999992, "zh-CN", "Retry this.") is null);
            var japaneseSettings = settings.Snapshot();
            japaneseSettings.TargetLanguage = "ja";
            var japaneseCalls = 0;
            var japanese = new UeStaticService(cacheRoot, (_, text, from, to, _, _) =>
            {
                Interlocked.Increment(ref japaneseCalls);
                if (from != "en" || to != "ja") throw new InvalidOperationException("Wrong Japanese language pair");
                return Task.FromResult<string?>("日本語:" + text);
            });
            var japaneseReport = await japanese.PrepareAsync(game, pakPath, 999991, Path.Combine(root, "japanese"),
                new[] { sourcePath }, japaneseSettings, null, CancellationToken.None);
            Check("japanese_uses_separate_language_cache", japaneseReport.Cached == 0 && japaneseCalls > 0
                && packs.Lookup(999991, "ja", "Retry this.") == "日本語:Retry this."
                && new PackStore(cacheRoot).Lookup(999991, "zh-CN", "Retry this.") == "译:Retry this.");
            using (var jpPak = PakFileReader.Open(Path.Combine(japaneseReport.OutputDirectory, "pakchunk99-WindowsNoEditor_P.pak")))
            {
                var jpLocres = LocResFile.Parse(jpPak.Extract(jpPak.Find(targetPath)!));
                Check("japanese_values_in_explicit_chinese_slot", jpLocres.ToDictionary()["UI"]["failed"] == "日本語:Retry this."
                    && jpLocres.ToDictionary()["UI"]["native-only"] == "原生独有");
            }
            Check("source_pak_unchanged", (await File.ReadAllBytesAsync(pakPath)).SequenceEqual(original));
            Check("game_no_artifacts", Directory.GetFiles(game, "*", SearchOption.AllDirectories).Length == 1);
            try
            {
                await service.PrepareAsync(game, pakPath, 999991, Path.Combine(game, "output"), new[] { sourcePath }, settings, null, CancellationToken.None);
                Check("game_output_rejected", false);
            }
            catch (ArgumentException) { Check("game_output_rejected", true); }
            try
            {
                await new UeStaticService(Path.Combine(game, "packs"), Provider).PrepareAsync(game, pakPath, 999991,
                    Path.Combine(root, "out2"), new[] { sourcePath }, settings, null, CancellationToken.None);
                Check("game_cache_rejected", false);
            }
            catch (ArgumentException) { Check("game_cache_rejected", true); }
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try
                {
                    await service.PrepareAsync(game, pakPath, 999991, Path.Combine(root, "cancelled"),
                        new[] { sourcePath }, settings, null, cancel.Token);
                    Check("cancel_no_publication", false);
                }
                catch (OperationCanceledException) { Check("cancel_no_publication", !Directory.Exists(Path.Combine(root, "cancelled"))); }
            }
            var circuitCalls = 0;
            var breaker = new StaticTextTranslationService(new PackStore(Path.Combine(root, "breaker")),
                (_, _, _, _, _, _) => { Interlocked.Increment(ref circuitCalls); return Task.FromResult<string?>(null); });
            var broken = await breaker.TranslateAsync(999991, Enumerable.Range(0, 100).Select(i => "Uncached " + i),
                settings, true, null, CancellationToken.None);
            Check("breaker_stops_without_caching_failure", broken.Missing == 100 && circuitCalls <= 10);
            var llmSettings = settings.Snapshot();
            llmSettings.Endpoint = "LlmDeepSeek";
            llmSettings.DisabledEndpoints.Remove("LlmDeepSeek");
            var batchCalls = 0;
            var batch = new StaticTextTranslationService(new PackStore(Path.Combine(root, "batch")),
                (_, text, _, _, _, _) =>
                {
                    batchCalls++;
                    Check("batch_size_limit", text.Length <= 200 && text.Split('\n').Length <= 20);
                    return Task.FromResult<string?>(string.Join('\n', text.Split('\n').Select(t => "译:" + t)));
                });
            var batched = await batch.TranslateAsync(999991, Enumerable.Range(0, 25).Select(i => "Item " + i), llmSettings, false, null, CancellationToken.None);
            Check("llm_batch_20", batched.Missing == 0 && batchCalls == 2);
            var mismatchCalls = 0;
            var mismatch = new StaticTextTranslationService(new PackStore(Path.Combine(root, "mismatch")),
                (_, text, _, _, _, _) => { mismatchCalls++; return Task.FromResult<string?>(text.Contains('\n') ? "bad" : "译:" + text); });
            var recovered = await mismatch.TranslateAsync(999991, new[] { "First", "Second" }, llmSettings, false, null, CancellationToken.None);
            Check("bad_batch_falls_back", recovered.Missing == 0 && mismatchCalls == 3);
            var segmentPacks = new PackStore(Path.Combine(root, "segments"));
            var segmentCalls = new ConcurrentDictionary<string, int>();
            var middleFails = true;
            var segments = new StaticTextTranslationService(segmentPacks, (_, text, _, _, _, _) =>
            {
                segmentCalls.AddOrUpdate(text, 1, (_, n) => n + 1);
                return Task.FromResult<string?>(middleFails && text == "Middle" ? null : "译:" + text);
            });
            const string longDocument = "First\n\nMiddle\r\nLast {name}";
            var partial = await segments.TranslateAsync(999993, new[] { longDocument }, settings, true, null, CancellationToken.None);
            Check("document_failure_not_cached", partial.Missing == 1 && segmentPacks.Lookup(999993, "zh-CN", longDocument) is null);
            middleFails = false;
            var resumed = await segments.TranslateAsync(999993, new[] { longDocument }, settings, true, null, CancellationToken.None);
            Check("fragment_resume_and_paragraphs", resumed.Translations[longDocument] == "译:First\n\n译:Middle\r\n译:Last {name}"
                && segmentCalls["First"] == 1 && segmentCalls["Last"] == 1);
            var oversized = await segments.TranslateAsync(999993, new[] { new string('x', 2501) }, settings, true, null, CancellationToken.None);
            Check("oversized_marked_missing_no_truncation", oversized.Missing == 1);
            var literals = await segments.TranslateAsync(999993, new[] { " ...\n{name}" }, settings, true, null, CancellationToken.None);
            Check("literal_only_not_translation_cache", literals.Missing == 0 && segmentPacks.Lookup(999993, "zh-CN", " ...\n{name}") is null);
            using var inFlight = new CancellationTokenSource();
            var cancelledPacks = new PackStore(Path.Combine(root, "cancelled-packs"));
            var cancelService = new StaticTextTranslationService(cancelledPacks, async (_, text, _, _, _, ct) =>
            {
                if (text == "First") return "译:First";
                inFlight.Cancel();
                await Task.Delay(1, ct);
                return null;
            });
            try
            {
                await cancelService.TranslateAsync(999994, new[] { "First\nSecond" }, settings, true, null, inFlight.Token);
                Check("inflight_cancel_propagated", false);
            }
            catch (OperationCanceledException)
            {
                Check("inflight_cancel_propagated", cancelledPacks.Lookup(999994, "zh-CN", "First") == "译:First"
                    && cancelledPacks.Lookup(999994, "zh-CN", "First\nSecond") is null);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RESULT\tUESTATIC\texception\t{ex}");
            failures++;
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"RESULT\tUESTATIC\tfailures={failures}");
        if (failures > 0) Environment.ExitCode = 1;
        await UeDataTableProbe.RunAsync();
    }
}
