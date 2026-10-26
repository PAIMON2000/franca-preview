// Portions adapted from XUnity.AutoTranslator, Copyright (c) 2018 Bepis.
// MIT License: see LICENSES/XUnity.AutoTranslator.txt for the full notice.
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Franca.Services;

public record TestOutcome(EndpointStatus Status, long? LatencyMs, string Detail);

// Health checks measure shared production providers; protocols live in TranslationProviders.
public static class EndpointTestService
{
    private const int TimeoutMs = 8000;
    public static Task<TestOutcome> TestFreeAsync(string endpointId, string targetLanguage, CancellationToken ct,
        string sourceLanguage = "en") =>
        TranslationProviders.CreateFreeRequest(endpointId, "hello", sourceLanguage, targetLanguage) is { } probe
            ? RunProbeAsync(probe, ct)
            : Task.FromResult(new TestOutcome(EndpointStatus.Failed, null, UiStrings.Get("probe.noimpl.free")));

    public static Task<TestOutcome> TestByokAsync(string endpointId, IReadOnlyDictionary<string, string> keys,
        string targetLanguage, CancellationToken ct, string sourceLanguage = "en") =>
        TranslationProviders.CreateKeyedRequest(endpointId, keys, "hello", sourceLanguage, targetLanguage) is { } probe
            ? RunProbeAsync(probe, ct)
            : Task.FromResult(new TestOutcome(EndpointStatus.Failed, null, UiStrings.Get("probe.noimpl.byok")));

    // 自定义 GET 纯文本端点："{base}?from=en&to={lang}&text={text}"，整个 body 即译文（XUnity CustomTranslate 语义）
    public static Task<TestOutcome> TestCustomGetAsync(string baseUrl, string text, string sourceLanguage,
        string targetLanguage, CancellationToken ct) =>
        RunProbeAsync(token => TranslationProviders.TranslateCustomGetAsync(baseUrl, text, sourceLanguage, targetLanguage, token), ct);

    // 自定义 OpenAI 兼容端点：复用 LlmCustom 的 BYOK 通道
    public static Task<TestOutcome> TestCustomOpenAiAsync(string baseUrl, string apiKey, string model,
        string text, string sourceLanguage, string targetLanguage, CancellationToken ct) =>
        RunProbeAsync(token => TranslationProviders.TranslateOpenAiCompatibleAsync("LlmCustom",
            new Dictionary<string, string> { ["BaseUrl"] = baseUrl, ["ApiKey"] = apiKey, ["Model"] = model },
            text, sourceLanguage, targetLanguage, token), ct);

    private static async Task<TestOutcome> RunProbeAsync(Func<CancellationToken, Task<string>> probe, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            var text = await probe(timeoutCts.Token);
            sw.Stop();
            if (string.IsNullOrWhiteSpace(text))
                return new TestOutcome(EndpointStatus.Failed, null, UiStrings.Get("probe.empty"));
            var status = sw.ElapsedMilliseconds < 500 ? EndpointStatus.Ok : EndpointStatus.Slow;
            return new TestOutcome(status, sw.ElapsedMilliseconds, text);
        }
        catch (TranslationProviders.RateLimitedException)
        {
            return new TestOutcome(EndpointStatus.Limited, null, UiStrings.Get("probe.ratelimited"));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new TestOutcome(EndpointStatus.Limited, null, "HTTP 429");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TestOutcome(EndpointStatus.Timeout, null, UiStrings.Get("probe.timeout"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } statusCode)
        {
            // 已收到 HTTP 拒绝（例如余额不足 402），应与网络超时区分。
            return new TestOutcome(EndpointStatus.Failed, null, $"HTTP {(int)statusCode}");
        }
        catch (HttpRequestException ex)
        {
            // 连接失败按不可达处理
            return new TestOutcome(EndpointStatus.Timeout, null, ex.Message);
        }
        catch (TranslationProviders.ProviderException ex)
        {
            return new TestOutcome(EndpointStatus.Failed, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new TestOutcome(EndpointStatus.Failed, null, ex.Message);
        }
    }

}
