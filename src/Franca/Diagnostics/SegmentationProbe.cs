using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using Franca.Services;

namespace Franca.Diagnostics;

internal static class SegmentationProbe
{
    public static async Task RunAsync()
    {
        var failures = 0;
        void Check(string name, bool pass)
        {
            Console.WriteLine($"RESULT\tSEGMENTATION\t{name}\t{(pass ? "PASS" : "FAIL")}");
            if (!pass) failures++;
        }
        IReadOnlyList<TranslationTextSegmenter.Part> Split(string text, string label)
        {
            var parts = TranslationTextSegmenter.Split(text);
            Check(label + "_lossless", string.Concat(parts.Select(p => p.Text)) == text);
            Check(label + "_limit", parts.Where(p => p.NeedsTranslation).All(p => p.Text.Length <= 200));
            return parts;
        }

        var shortParts = Split("  Hello, world!  ", "short");
        Check("short_whitespace_retained", shortParts.Single(p => p.NeedsTranslation).Text == "Hello, world!");
        var paragraphs = Split("First paragraph.\r\n\r\nSecond paragraph.\nLast paragraph.", "paragraphs");
        Check("paragraphs_are_separate_requests", paragraphs.Count(p => p.NeedsTranslation) == 3);
        var sentences = Split(new string('a', 175) + ". " + new string('b', 175) + "!", "sentences");
        Check("sentence_boundary_preferred", sentences.First(p => p.NeedsTranslation).Text.EndsWith('.'));
        var clause = Split(new string('a', 140) + ", " + new string('b', 140) + "; " + new string('c', 140), "clauses");
        Check("long_sentence_uses_punctuation", clause.First(p => p.NeedsTranslation).Text.EndsWith(','));
        var unbroken = Split(new string('x', 450), "unbroken");
        Check("unbroken_falls_back_without_stalling", unbroken.Count(p => p.NeedsTranslation) == 3);
        var unicode = Split(new string('a', 199) + "😀" + string.Concat(Enumerable.Repeat("e\u0301", 140)), "unicode");
        Check("unicode_no_broken_surrogates", unicode.All(p => p.Text.Length == 0 ||
            (!char.IsLowSurrogate(p.Text[0]) && !char.IsHighSurrogate(p.Text[^1]))));
        Check("unicode_no_split_combining_sequences", unicode.Where(p => p.NeedsTranslation)
            .All(p => CharUnicodeInfo.GetUnicodeCategory(p.Text, 0) != UnicodeCategory.NonSpacingMark));
        var rich = Split("<color=#fff><b>President</b></color> {player}: " + new string('x', 220) + " %1$s\nNext.", "rich_text");
        Check("markup_and_placeholders_are_literal", rich.Where(p => p.NeedsTranslation)
            .All(p => !p.Text.Contains('<') && !p.Text.Contains('{') && !p.Text.Contains('%')));
        Split(new string('x', 2500), "maximum");
        try { TranslationTextSegmenter.Split(new string('x', 2501)); Check("above_2500_rejected", false); }
        catch (ArgumentOutOfRangeException) { Check("above_2500_rejected", true); }
        Check("whitespace_only_no_requests", TranslationTextSegmenter.Split(" \r\n\t").All(p => !p.NeedsTranslation));
        var quoted = Split(new string('a', 180) + ".\" " + new string('b', 100), "quoted");
        Check("closing_quote_stays_with_sentence", quoted.First(p => p.NeedsTranslation).Text.EndsWith(".\""));

        var root = Path.Combine(Path.GetTempPath(), "franca-segmentation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettings { TargetLanguage = "fr", SourceLanguage = "en", IsTransient = true };
            var packs = new PackStore(Path.Combine(root, "packs"));
            var calls = new Dictionary<string, int>();
            var failMiddle = true;
            Task<(string?, string?)> Translate(string part, string from, string to, AppSettings snapshot, CancellationToken ct)
            {
                calls[part] = calls.GetValueOrDefault(part) + 1;
                return Task.FromResult<(string?, string?)>(part == "Second paragraph." && failMiddle
                    ? (null, null) : ("[" + part + "]", "fixture"));
            }
            const string document = "First paragraph.\nSecond paragraph.\nThird paragraph.";
            var service = new SegmentedTranslationService(packs, Translate);
            var first = await service.TranslateAsync(1, document, "en", "fr", settings, CancellationToken.None);
            Check("failure_never_returns_partial_document", first.Text is null);
            Check("completed_fragment_saved_for_retry", packs.Lookup(1, "fr", "First paragraph.") == "[First paragraph.]");
            Check("failed_document_not_final_cached", packs.Lookup(1, "fr", document) is null);
            failMiddle = false;
            var second = await service.TranslateAsync(1, document, "en", "fr", settings, CancellationToken.None);
            Check("retry_reconstructs_in_original_order", second.Text == "[First paragraph.]\n[Second paragraph.]\n[Third paragraph.]");
            Check("retry_only_unfinished_fragments", calls["First paragraph."] == 1 && calls["Second paragraph."] == 2 && calls["Third paragraph."] == 1);
            var before = calls.Values.Sum();
            await service.TranslateAsync(1, document, "en", "fr", settings, CancellationToken.None);
            Check("successful_fragments_reused", calls.Values.Sum() == before);

            var active = 0;
            var peak = 0;
            var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<(string?, string?)> Queued(string part, string from, string to, AppSettings snapshot, CancellationToken ct)
            {
                var concurrent = Interlocked.Increment(ref active);
                peak = Math.Max(peak, concurrent);
                if (concurrent == 2) bothStarted.TrySetResult();
                try { await release.Task.WaitAsync(ct); return (part, "queue"); }
                finally { Interlocked.Decrement(ref active); }
            }
            var queued = new SegmentedTranslationService(packs, Queued, concurrency: 2);
            var queueWork = Enumerable.Range(10, 4).Select(id => queued.TranslateAsync(id,
                "One.\nTwo.", "en", "fr", settings, CancellationToken.None)).ToArray();
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("queue_limits_concurrent_provider_requests", active == 2 && peak == 2);
            using (var cancel = new CancellationTokenSource())
            {
                var waiting = queued.TranslateAsync(99, "Waiting.", "en", "fr", settings, cancel.Token);
                cancel.Cancel();
                try { await waiting; Check("queue_wait_can_cancel", false); }
                catch (OperationCanceledException) { Check("queue_wait_can_cancel", true); }
            }
            release.SetResult();
            await Task.WhenAll(queueWork);
            Check("queue_finishes_without_lost_slots", active == 0 && peak == 2);

            var received = new List<string>();
            var failHttp = true;
            Task<(string?, string?)> Provider(string part, string from, string to, AppSettings snapshot, CancellationToken ct)
            {
                received.Add(part);
                return Task.FromResult<(string?, string?)>(part == "Second paragraph." && failHttp
                    ? (null, null) : (part.ToUpperInvariant(), "mock"));
            }
            using var relay = new RelayServer(() => settings.Snapshot(), packs, Provider);
            await relay.EnsureStartedAsync();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            string Url(int id, string text) => $"{relay.UrlForGame(id)}?from=en&to=fr&text={Uri.EscapeDataString(text)}";
            var longText = new string('a', 145) + ", " + new string('b', 145) + ".\n\nNext paragraph.";
            var actual = await http.GetStringAsync(Url(501, longText));
            Check("http_reassembles_with_original_paragraphs", actual == longText.ToUpperInvariant());
            Check("http_provider_never_exceeds_200", received.All(t => t.Length <= 200));
            Check("http_full_document_cached_after_success", packs.Lookup(501, "fr", longText) == actual);
            var requestCount = received.Count;
            Check("http_complete_cache_bypasses_provider", await http.GetStringAsync(Url(501, longText)) == actual && received.Count == requestCount);
            using (var failed = await http.GetAsync(Url(502, document)))
                Check("http_failure_is_retryable_not_partial_success", failed.StatusCode == HttpStatusCode.ServiceUnavailable);
            Check("http_partial_document_never_final_cached", packs.Lookup(502, "fr", document) is null);
            failHttp = false;
            actual = await http.GetStringAsync(Url(502, document));
            Check("http_retry_finishes_document", actual == document.ToUpperInvariant());
            Check("http_retry_reuses_completed_fragment", received.Count(t => t == "First paragraph.") == 1);
            var ceiling = new string('z', 2500);
            Check("http_accepts_existing_2500_ceiling", (await http.GetStringAsync(Url(503, ceiling))).Length == 2500);
            requestCount = received.Count;
            var oversized = new string('z', 2501);
            Check("http_above_ceiling_does_not_translate_or_cache", await http.GetStringAsync(Url(503, oversized)) == oversized
                && received.Count == requestCount && packs.Lookup(503, "fr", oversized) is null);
        }
        finally
        {
            var expectedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "franca-segmentation-");
            if (!Path.GetFullPath(root).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected probe cleanup path.");
            Directory.Delete(root, recursive: true);
        }
        Environment.ExitCode = failures == 0 ? 0 : 1;
        Console.WriteLine($"RESULT\tSEGMENTATION\tfailures={failures}");
    }
}
