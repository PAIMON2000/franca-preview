using System.Net.Security;
using System.Net.Sockets;

namespace Franca.Services;

// 自动端点：每 10 秒对组内未禁用端点做轻量 TLS 握手测速（不发翻译请求，防止把网页端点打挂/限速），
// 最低延迟候选变化时才发一次真实翻译探测确认，通过后切换实际选中端点
public sealed class EndpointAutoService
{
    // 端点 Id → TLS 测速主机
    private static readonly Dictionary<string, string> Hosts = new()
    {
        ["BingTranslate"] = "www.bing.com",
        ["GoogleTranslateV2"] = "translate.googleapis.com",
        ["YandexTranslate"] = "translate.yandex.net",
        ["DeepLTranslate"] = "www.deepl.com",
        ["PapagoTranslate"] = "papago.naver.com",
        ["TranSmartTranslate"] = "transmart.qq.com",
        ["YoudaoWebTranslate"] = "fanyi.youdao.com",
        ["SogouTranslate"] = "fanyi.sogou.com",
        ["MyMemoryTranslate"] = "api.mymemory.translated.net",
        ["BaiduTranslate"] = "fanyi-api.baidu.com",
        ["DeepLTranslateLegitimate"] = "api-free.deepl.com",
        ["BingTranslateLegitimate"] = "api.cognitive.microsofttranslator.com",
        ["GoogleTranslateLegitimate"] = "translation.googleapis.com",
        ["LingoCloudTranslate"] = "api.interpreter.caiyunai.com",
        ["LlmDeepSeek"] = "api.deepseek.com",
        ["LlmDoubao"] = "ark.cn-beijing.volces.com",
        ["LlmGlm"] = "open.bigmodel.cn",
        ["LlmKimi"] = "api.moonshot.cn",
        ["LlmOpenAI"] = "api.openai.com",
    };

    private readonly Func<string, CancellationToken, Task<long?>> _measure;
    private int _generation;
    public string? CurrentPick { get; private set; }
    // 当前端点最近一次实测延迟；迟滞比较用
    private long? _currentLatencyMs;
    // 上次切换时刻：两次切换至少间隔该时长，TLS 测速噪声不再引发来回横跳
    private DateTime _lastSwitchAt = DateTime.MinValue;
    private static readonly TimeSpan MinSwitchInterval = TimeSpan.FromSeconds(45);
    // 新候选必须比当前端点快至少该比例才切换（30% 迟滞）
    private const double SwitchHysteresis = 0.7;

    public EndpointAutoService(Func<string, CancellationToken, Task<long?>>? measure = null) =>
        _measure = measure ?? MeasureTlsAsync;

    public static bool IsAutoEndpoint(string id) => id is "AutoFree" or "AutoByok" or "AutoLlm" or "AutoOffline";

    public static EndpointCategory? GroupOf(string id) => id switch
    {
        "AutoFree" => EndpointCategory.Free,
        "AutoByok" => EndpointCategory.Byok,
        "AutoLlm" => EndpointCategory.Llm,
        "AutoOffline" => EndpointCategory.Offline,
        _ => null
    };

    public string ResolveEffective(string endpointId) =>
        IsAutoEndpoint(endpointId) ? CurrentPick ?? "BingTranslate" : endpointId;

    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        CurrentPick = null;
        _currentLatencyMs = null;
        _lastSwitchAt = DateTime.MinValue;
    }

    public async Task<(string Id, long LatencyMs)?> SelectAsync(
        IReadOnlyList<(string Id, string Host)> candidates,
        Func<string, CancellationToken, Task<TestOutcome>> probe, CancellationToken ct)
    {
        var generation = Volatile.Read(ref _generation);
        var measured = new List<(string Id, long Ms)>();
        long? currentMs = null;
        foreach (var (id, host) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await _measure(host, ct) is not { } ms) continue;
            if (id == CurrentPick) currentMs = ms;
            measured.Add((id, ms));
        }
        if (measured.Count == 0) return null;
        measured.Sort((a, b) => a.Ms.CompareTo(b.Ms));

        // 尚无选中：从实测最快开始逐个真实探测，第一个通过即为选中。
        // 最快的候选探测失败就顺延下一个，避免被单个"TLS 快但翻译接口报错"的端点永久卡死
        if (CurrentPick is null)
        {
            foreach (var (id, ms) in measured)
            {
                var outcome = await probe(id, ct);
                ct.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref _generation)) return null;
                if (outcome.Status is (EndpointStatus.Ok or EndpointStatus.Slow))
                {
                    CurrentPick = id;
                    _currentLatencyMs = ms;
                    _lastSwitchAt = DateTime.UtcNow;
                    return (id, outcome.LatencyMs ?? ms);
                }
            }
            return null;
        }

        var best = measured[0];
        // 迟滞：当前端点仍可测且新最佳未快出 30% 余量，不切换（避免测速噪声来回横跳）
        if (best.Id == CurrentPick
            || DateTime.UtcNow - _lastSwitchAt < MinSwitchInterval
            || (currentMs is { } cur && best.Ms >= cur * SwitchHysteresis))
            return null;
        var switchOutcome = await probe(best.Id, ct);
        ct.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _generation) ||
            switchOutcome.Status is not (EndpointStatus.Ok or EndpointStatus.Slow)) return null;
        CurrentPick = best.Id;
        _currentLatencyMs = best.Ms;
        _lastSwitchAt = DateTime.UtcNow;
        return (best.Id, switchOutcome.LatencyMs ?? best.Ms);
    }

    private static async Task<long?> MeasureTlsAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(host))
            return null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, 443, cts.Token);
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    // 端点的 TLS 测速主机名；取不到返回空串（跳过该候选）
    public static string HostOf(string endpointId, CustomEndpointDef? custom, IReadOnlyDictionary<string, string>? keys)
    {
        if (custom is not null)
            return Uri.TryCreate(custom.BaseUrl, UriKind.Absolute, out var u) ? u.Host : "";
        if (endpointId == "WatsonTranslate" && keys is not null
            && keys.TryGetValue("Url", out var w) && Uri.TryCreate(w, UriKind.Absolute, out var wu))
            return wu.Host;
        if (endpointId == "LlmCustom" && keys is not null
            && keys.TryGetValue("BaseUrl", out var b) && Uri.TryCreate(b, UriKind.Absolute, out var bu))
            return bu.Host;
        return Hosts.TryGetValue(endpointId, out var h) ? h : "";
    }
}
