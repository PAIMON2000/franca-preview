using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Franca.Services;

// Franca 本地翻译中继（lab/006）：XUnity CustomTranslate 把翻译请求 GET 到 127.0.0.1，
// 由中继统一走端点池（主用→备用→其余免费/已配 Key 端点，失败退避转移），
// 命中包缓存直接返回；每条机翻产物按 lab/004 格式沉淀到 packs/<appid>/<lang>/entries/
public sealed class RelayServer : IDisposable
{
    public const int BasePort = 47820;
    private const int MaxPortAttempts = 10;
    private readonly TimeSpan EndpointCooldown = TimeSpan.FromSeconds(15);
    private readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(8);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    private readonly Func<AppSettings> _settings;
    private readonly PackStore _packs;
    private readonly DictionaryService _dictionary;
    private readonly SegmentedTranslationService _documents;
    private readonly object _lifecycle = new();
    private bool _disposed;

    public RelayServer(Func<AppSettings> settings, PackStore? packs = null,
        Func<string, string, string, AppSettings, CancellationToken, Task<(string? Text, string? Endpoint)>>? translate = null,
        DictionaryService? dictionary = null)
    {
        _settings = settings;
        _packs = packs ?? new PackStore();
        _dictionary = dictionary ?? new DictionaryService();
        _documents = new SegmentedTranslationService(_packs, translate ?? TranslateViaPoolAsync);
    }

    public string UrlForGame(int? appId) => appId is > 0 ? $"{BaseUrl}/{appId}" : BaseUrl;
    public PackStore Packs => _packs;

    // 后台在线升级完成通知（appId, 原文, 译文），由译文目录同步服务消费；
    // 中继只报告翻译结果，不感知游戏目录路径（lab/010 Slice 2 解耦边界）
    public event Action<int, string, string>? UpgradeCompleted;
    public void Dispose()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            Stop();
        }
    }

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;
    public long ServedCount => Interlocked.Read(ref _servedCount);
    // 包缓存命中率（不含词典命中），保持原语义
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public double HitRate => ServedCount == 0 ? 0 : (double)CacheHits / ServedCount;
    // 词典命中数（仅高速模式可能非零；质量模式完全绕过词典）
    public long DictHits => Interlocked.Read(ref _dictHits);
    // 词典命中占全部请求的比例
    public double DictRate => ServedCount == 0 ? 0 : (double)DictHits / ServedCount;
    // 在途在线请求数：前台分段翻译与后台升级进出都计数，供悬浮窗展示
    public long InFlight => Interlocked.Read(ref _inFlight);
    // 全部端点失败数：在线结果为空（端点池耗尽或全部失败），前台与后台升级都计数
    public long Failures => Interlocked.Read(ref _failures);
    private long _servedCount;
    private long _cacheHits;
    private long _dictHits;
    private long _inFlight;
    private long _failures;

    // 高速模式后台升级去重："<appId>|<to>|<text>" 在途时不重复发在线请求
    private readonly ConcurrentDictionary<string, byte> _upgradeInflight = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _endpointLocks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new();

    public string BaseUrl => $"http://127.0.0.1:{Port}/translate";

    public Task EnsureStartedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return Task.CompletedTask;
            for (var attempt = 0; attempt < MaxPortAttempts; attempt++)
            {
                var port = BasePort + attempt;
                var listener = new HttpListener();
                try
                {
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();
                    var cts = new CancellationTokenSource();
                    _listener = listener;
                    _cts = cts;
                    Port = port;
                    _ = AcceptLoopAsync(listener, cts.Token);
                    return Task.CompletedTask;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                    if (attempt == MaxPortAttempts - 1) throw;
                }
            }
        }
        throw new InvalidOperationException("No relay port available");
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            _cts?.Cancel();
            _listener?.Close();
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                break; // listener 已停止
            }
            _ = Task.Run(() => HandleAsync(context, ct));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            var appId = 0;
            var identified = path.StartsWith("/translate/", StringComparison.Ordinal)
                && int.TryParse(path[11..], out appId) && appId > 0;
            if (path == "/translate" || identified)
                await HandleTranslateAsync(context, identified ? appId : 0, ct);
            else
                await WriteResponseAsync(context, 404, "not found");
        }
        catch
        {
            try { await WriteResponseAsync(context, 500, "error"); } catch { }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task HandleTranslateAsync(HttpListenerContext context, int appId, CancellationToken ct)
    {
        var query = context.Request.QueryString;
        var text = query["text"] ?? "";
        var settings = _settings();
        var from = query["from"] ?? settings.SourceLanguage;
        var to = query["to"] ?? settings.TargetLanguage;

        Interlocked.Increment(ref _servedCount);
        if (text.Length == 0)
        {
            await WriteResponseAsync(context, 200, "");
            return;
        }
        if (text.Length > TranslationTextSegmenter.MaxTextLength)
        {
            // Retain the upstream input ceiling; do not send oversized text to providers or packs.
            await WriteResponseAsync(context, 200, text);
            return;
        }

        // Legacy requests without an explicit game ID never read or write game packs.
        if (!PackStore.IsValidLanguage(to))
        {
            await WriteResponseAsync(context, 400, "invalid language");
            return;
        }

        // 0. 高速模式词典命中（lab/010）：立即返回；优先级高于包缓存（全游戏一致性）。
        //    质量模式完全绕过词典；后台升级按游戏归属，legacy 请求不发。
        if (!settings.QualityMode && _dictionary.Lookup(from, to, text) is { } dictHit)
        {
            Interlocked.Increment(ref _dictHits);
            if (!settings.NoOptimizeAfterGenerate && appId > 0)
                _ = UpgradeInBackgroundAsync(appId, text, from, to, settings);
            await WriteResponseAsync(context, 200, dictHit);
            return;
        }

        // 1. 包缓存命中（毫秒级）；tgt 为空的 retry 草稿视为未命中，自然重试
        if (appId > 0 && _packs.Lookup(appId, to, text) is { } cached)
        {
            Interlocked.Increment(ref _cacheHits);
            await WriteResponseAsync(context, 200, cached);
            return;
        }

        // 2. Split below the provider limit, queue each fragment, then reconstruct once.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(120));
        SegmentedTranslationService.Result document;
        Interlocked.Increment(ref _inFlight);
        try
        {
            document = await _documents.TranslateAsync(appId, text, from, to, settings, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            context.Response.Headers["Retry-After"] = "1";
            await WriteResponseAsync(context, 503, "translation pending");
            return;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
        var translated = document.Text;
        var usedEndpoint = document.Endpoint;

        // 2b. 沉淀（lab/010，与模式无关）：在线成功且原文 Trim 后 ≤30 字符进词典，长文本只进游戏包
        if (translated is not null && text.Trim().Length <= DictionaryService.MaxPromoteLength)
            _dictionary.TryPromote(from, to, text, translated);
        if (translated is null)
            Interlocked.Increment(ref _failures);

        // 3. 沉淀条目 + 响应；全挂返回原文（失败开放），条目留 tgt 空 + retry 标记
        if (appId > 0)
        {
            _packs.Store(appId, to, text,
                translated ?? "", usedEndpoint is null ? "mt:none" : $"mt:{usedEndpoint}",
                retry: translated is null);
        }
        if (translated is null && document.TranslationParts > 1)
        {
            // A 200 response would make XUnity persist partial/original text as a finished result.
            context.Response.Headers["Retry-After"] = "1";
            await WriteResponseAsync(context, 503, "translation incomplete");
            return;
        }
        await WriteResponseAsync(context, 200, translated ?? text);
    }

    // 高速模式词典命中的后台在线升级（lab/010）：fire-and-forget，本切片只写游戏包
    // （写游戏目录 Translation\<lang>\Text\ 与运行时重载属 Slice 2）。
    // 同一原文在途去重；成功/失败/取消都移除在途标记，避免钉死；异常不抛出、不影响已返回的词典译文。
    private async Task UpgradeInBackgroundAsync(int appId, string text, string from, string to, AppSettings settings)
    {
        var key = $"{appId}|{to}|{text}";
        if (!_upgradeInflight.TryAdd(key, 0)) return;
        Interlocked.Increment(ref _inFlight);
        try
        {
            // 升级只发生在词条首次沉淀时：游戏包已有该文本译文则跳过（词典命中仍立即返回）
            if (_packs.Lookup(appId, to, text) is not null) return;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var document = await _documents.TranslateAsync(appId, text, from, to, settings, budget.Token);
            if (document.Text is { } upgraded)
            {
                _packs.Store(appId, to, text, upgraded,
                    document.Endpoint is null ? "mt:none" : $"mt:{document.Endpoint}", retry: false);
                try { UpgradeCompleted?.Invoke(appId, text, upgraded); }
                catch (Exception ex) { AppLog.Write($"upgrade notify failed: {ex.Message}"); }
            }
            else
                Interlocked.Increment(ref _failures);
        }
        catch
        {
            // 后台升级失败不抛出：词典译文已返回给游戏，端点冷却由正常请求路径维护
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _upgradeInflight.TryRemove(key, out _);
        }
    }

    // 池顺序：主用（自动端点解析后）→ 备用 → 其余免费端点 → 已配齐 Key 的 BYOK；禁用与冷却中的跳过
    private List<string> PoolOrder(AppSettings settings)
    {
        var order = new List<string>();
        void Add(string id)
        {
            if (id.Length > 0 && !order.Contains(id) && !settings.DisabledEndpoints.Contains(id))
                order.Add(id);
        }

        Add(settings.Endpoint);
        Add(settings.FallbackEndpoint);
        foreach (var def in EndpointCatalog.All)
        {
            if (def.Category == EndpointCategory.Free)
                Add(def.Id);
            else if (def.Category == EndpointCategory.Byok
                && def.KeyFields.Length > 0
                && def.KeyFields.All(f => settings.EndpointKeys.TryGetValue(def.Id, out var keys)
                    && keys.TryGetValue(f.FieldId, out var value) && !string.IsNullOrWhiteSpace(value)))
                Add(def.Id);
        }
        return order;
    }

    private async Task<(string? Text, string? Endpoint)> TranslateViaPoolAsync(
        string text, string from, string to, AppSettings settings, CancellationToken ct)
    {
        foreach (var endpointId in PoolOrder(settings))
        {
            if (_cooldowns.TryGetValue(endpointId, out var until) && until > DateTimeOffset.UtcNow)
                continue;

            var endpointLock = _endpointLocks.GetOrAdd(endpointId, _ => new SemaphoreSlim(2));
            await endpointLock.WaitAsync(ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(EndpointTimeout);
                var keys = settings.EndpointKeys.TryGetValue(endpointId, out var k) ? k : null;
                var result = await AppTranslator.TranslateAsync(endpointId, text, to, keys, timeout.Token, from);
                if (!string.IsNullOrWhiteSpace(result))
                    return (result, endpointId);
                _cooldowns[endpointId] = DateTimeOffset.UtcNow + EndpointCooldown;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _cooldowns[endpointId] = DateTimeOffset.UtcNow + EndpointCooldown;
            }
            finally
            {
                endpointLock.Release();
            }
        }
        return (null, null);
    }

    private async Task WriteResponseAsync(HttpListenerContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }
}
