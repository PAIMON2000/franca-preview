using System.Collections.Concurrent;
using System.Text;

namespace Franca.Services;

// Instance-scoped archive translation: existing providers, shared game packs, bounded requests.
// Successful fragments survive retries; a document is cached only after every fragment succeeds.
public sealed class StaticTextTranslationService
{
    public sealed record StaticTranslationProgress(int Completed, int Total, int Cached, int Failed, int FragmentsDone, int FragmentsTotal);
    public sealed record StaticTranslationResult(IReadOnlyDictionary<string, string> Translations, int Missing, int Cached);
    private readonly PackStore _packs;
    private readonly Func<string, string, string, string, AppSettings, CancellationToken, Task<string?>>? _provider;

    public StaticTextTranslationService(PackStore packs,
        Func<string, string, string, string, AppSettings, CancellationToken, Task<string?>>? provider = null)
    {
        _packs = packs;
        _provider = provider;
    }

    public async Task<StaticTranslationResult> TranslateAsync(int appId, IEnumerable<string> texts, AppSettings settings,
        bool freeOnly, IProgress<StaticTranslationProgress>? progress, CancellationToken ct)
    {
        if (appId <= 0 || !PackStore.IsValidLanguage(settings.TargetLanguage))
            throw new ArgumentException("Invalid translation identity");
        var snapshot = settings.Snapshot();
        var from = snapshot.SourceLanguage;
        var to = snapshot.TargetLanguage;
        var order = new List<string>();
        void Add(string id)
        {
            var def = EndpointCatalog.Find(id);
            if (def is null || snapshot.DisabledEndpoints.Contains(id) || order.Contains(id)) return;
            if (def.Category == EndpointCategory.Free || (!freeOnly && def.Category is EndpointCategory.Byok or EndpointCategory.Llm))
                order.Add(id);
        }
        Add(snapshot.Endpoint);
        Add(snapshot.FallbackEndpoint);
        // Unselected paid endpoints never enter the fallback pool.
        foreach (var def in EndpointCatalog.All.Where(e => e.Category == EndpointCategory.Free)) Add(def.Id);
        var failures = new ConcurrentDictionary<string, int>();
        bool Open(string id) => failures.GetValueOrDefault(id) >= 3;
        var translated = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var plans = new Dictionary<string, IReadOnlyList<TranslationTextSegmenter.Part>>(StringComparer.Ordinal);
        var fragments = new HashSet<string>(StringComparer.Ordinal);
        var unique = texts.Distinct(StringComparer.Ordinal).ToArray();
        var cached = 0;
        foreach (var text in unique)
        {
            ct.ThrowIfCancellationRequested();
            if (text.Length > TranslationTextSegmenter.MaxTextLength) continue; // report missing, never truncate
            if (_packs.Lookup(appId, to, text) is { } hit && TranslationTextSegmenter.HasSameProtectedTokens(text, hit))
            {
                translated[text] = hit;
                cached++;
                continue;
            }
            var parts = TranslationTextSegmenter.Split(text);
            plans[text] = parts;
            foreach (var part in parts.Where(p => p.NeedsTranslation)) fragments.Add(part.Text);
        }
        var values = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (var part in fragments)
        {
            if (_packs.Lookup(appId, to, part) is { } hit && TranslationTextSegmenter.HasSameProtectedTokens(part, hit)) values[part] = hit;
            else pending.Add(part);
        }
        var done = values.Count;
        void Report() => progress?.Report(new StaticTranslationProgress(translated.Count, unique.Length, cached,
            0, Volatile.Read(ref done), fragments.Count));
        Report();
        async Task<string?> Request(string id, string text)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(EndpointCatalog.Find(id)?.Category == EndpointCategory.Llm ? 60 : 10));
            try
            {
                var result = _provider is not null
                    ? await _provider(id, text, from, to, snapshot, timeout.Token)
                    : await AppTranslator.TranslateAsync(id, text, to, snapshot.EndpointKeys.GetValueOrDefault(id), timeout.Token, from);
                return string.IsNullOrWhiteSpace(result) || !TranslationTextSegmenter.HasSameProtectedTokens(text, result.Trim()) ? null : result.Trim();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }
        void Save(string source, string value, string id)
        {
            values[source] = value;
            _packs.Store(appId, to, source, value, "mt:ue-static:" + id, retry: false);
            Interlocked.Increment(ref done);
            Report();
        }
        // LLM batches contain at most 20 lines AND at most 200 characters per request.
        if (!freeOnly && order.FirstOrDefault() is { } primary && EndpointCatalog.Find(primary)?.Category == EndpointCategory.Llm)
        {
            for (var start = 0; start < pending.Count && !Open(primary);)
            {
                var chunk = new List<string>();
                var length = 0;
                while (start < pending.Count && chunk.Count < 20)
                {
                    var next = pending[start];
                    if (chunk.Count > 0 && length + 1 + next.Length > TranslationTextSegmenter.MaxSegmentLength) break;
                    chunk.Add(next);
                    length += next.Length + (chunk.Count > 1 ? 1 : 0);
                    start++;
                }
                var response = await Request(primary, string.Join('\n', chunk));
                var lines = response?.Replace("\r\n", "\n").Split('\n');
                if (lines is not null && lines.Length == chunk.Count && lines.All(l => !string.IsNullOrWhiteSpace(l)))
                {
                    failures[primary] = 0;
                    for (var i = 0; i < chunk.Count; i++) Save(chunk[i], lines[i].Trim(), primary);
                }
                else failures.AddOrUpdate(primary, 1, (_, n) => n + 1); // malformed response also opens breaker
            }
        }
        var remaining = pending.Where(p => !values.ContainsKey(p)).ToArray();
        var index = -1;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (order.All(Open)) return;
                var i = Interlocked.Increment(ref index);
                if (i >= remaining.Length) return;
                var text = remaining[i];
                foreach (var id in order)
                {
                    if (Open(id)) continue;
                    var value = await Request(id, text);
                    if (value is null) { failures.AddOrUpdate(id, 1, (_, n) => n + 1); continue; }
                    failures[id] = 0;
                    Save(text, value, id);
                    break;
                }
            }
        }));
        foreach (var (text, parts) in plans)
        {
            ct.ThrowIfCancellationRequested();
            if (parts.Any(p => p.NeedsTranslation && !values.ContainsKey(p.Text))) continue;
            var output = new StringBuilder();
            foreach (var part in parts) output.Append(part.NeedsTranslation ? values[part.Text] : part.Text);
            translated[text] = output.ToString();
            if (parts.Any(p => p.NeedsTranslation))
                _packs.Store(appId, to, text, output.ToString(), "mt:ue-static:complete", retry: false);
        }
        var missing = unique.Length - translated.Count;
        progress?.Report(new StaticTranslationProgress(translated.Count, unique.Length, cached, missing, done, fragments.Count));
        return new StaticTranslationResult(translated, missing, cached);
    }
}
