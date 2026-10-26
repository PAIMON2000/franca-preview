using System.Text;

namespace Franca.Services;

// One document is sequential; different documents share a bounded queue per provider request.
// Completed fragments survive a retry, but an incomplete document is never a final translation.
public sealed class SegmentedTranslationService
{
    public sealed record Result(string? Text, string? Endpoint, int TranslationParts);
    private readonly PackStore _packs;
    private readonly SemaphoreSlim _queue;
    private readonly Func<string, string, string, AppSettings, CancellationToken, Task<(string? Text, string? Endpoint)>> _translate;

    public SegmentedTranslationService(PackStore packs,
        Func<string, string, string, AppSettings, CancellationToken, Task<(string? Text, string? Endpoint)>> translate,
        int concurrency = 8)
    {
        _packs = packs;
        _translate = translate;
        _queue = new SemaphoreSlim(concurrency, concurrency);
    }

    public async Task<Result> TranslateAsync(int appId, string text, string from, string to,
        AppSettings settings, CancellationToken ct)
    {
        var parts = TranslationTextSegmenter.Split(text);
        var count = parts.Count(p => p.NeedsTranslation);
        var output = new StringBuilder();
        var providers = new HashSet<string>();
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            if (!part.NeedsTranslation) { output.Append(part.Text); continue; }
            if (appId > 0 && _packs.Lookup(appId, to, part.Text) is { } cached)
            {
                output.Append(cached);
                continue;
            }
            await _queue.WaitAsync(ct);
            (string? Text, string? Endpoint) translated;
            try { translated = await _translate(part.Text, from, to, settings, ct); }
            finally { _queue.Release(); }
            if (string.IsNullOrWhiteSpace(translated.Text))
            {
                if (appId > 0 && part.Text != text)
                    _packs.Store(appId, to, part.Text, "", "mt:none", retry: true);
                return new Result(null, null, count);
            }
            if (translated.Endpoint is { } endpoint) providers.Add(endpoint);
            output.Append(translated.Text.Trim());
            if (appId > 0 && part.Text != text)
                _packs.Store(appId, to, part.Text, translated.Text.Trim(), $"mt:{translated.Endpoint ?? "unknown"}", retry: false);
        }
        return new Result(output.ToString(), providers.Count == 1 ? providers.First() : "segmented", count);
    }
}
