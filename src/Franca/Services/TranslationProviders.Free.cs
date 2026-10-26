// Portions adapted from XUnity.AutoTranslator, Copyright (c) 2018 Bepis.
// MIT License: see LICENSES/XUnity.AutoTranslator.txt for the full notice.
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Franca.Services;

// Shared production translation protocols.
public static partial class TranslationProviders
{
    // ---- 免费端点探测 ----

    // ---- 端点会话复用 ----
    // 必应/搜狗/有道每次调用都重做昂贵握手（抓整页、取 token、建 Cookie），
    // 会话按 TTL 缓存；并发初始化用每端点 SemaphoreSlim + 双重检查，避免 4 路并发重复握手。
    // 每次进程启动首测仍是冷会话，热会话只在同进程连续翻译（译名列批量场景）受益。

    private class BingSession
    {
        public required CookieContainer Cookies { get; init; }
        public required string Host { get; init; }
        public string? Ig { get; init; }
        public string? Iid { get; init; }
        public string? Token { get; init; }
        public string? Key { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private static readonly TimeSpan BingSessionTtl = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim BingSessionLock = new(1, 1);
    private static BingSession? _bingSession;

    private static async Task<BingSession> GetBingSessionAsync(CancellationToken ct)
    {
        if (_bingSession is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached;
        await BingSessionLock.WaitAsync(ct);
        try
        {
            if (_bingSession is { } fresh && fresh.ExpiresAt > DateTimeOffset.UtcNow)
                return fresh;
            return _bingSession = await CreateBingSessionAsync(ct);
        }
        finally
        {
            BingSessionLock.Release();
        }
    }

    private static async Task<BingSession> CreateBingSessionAsync(CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");

        // 抓 translator 页面：IG、IID、token/key（bing.com 会 302 到 cn.bing.com，取最终主机名）
        string host;
        string? ig = null, iid = null, token = null, key = null;
        using (var pageResponse = await client.GetAsync("https://www.bing.com/translator", ct))
        {
            pageResponse.EnsureSuccessStatusCode();
            var html = await pageResponse.Content.ReadAsStringAsync(ct);
            host = pageResponse.RequestMessage?.RequestUri?.Host ?? "www.bing.com";
            ig = Lookup("\",IG:\"", html);
            iid = Lookup("data-iid=\"", html);
            if (System.Text.RegularExpressions.Regex.Match(html,
                    @"params_AbusePreventionHelper = \[(\d+),""([^""]+)""") is { Success: true } abuse)
            {
                key = abuse.Groups[1].Value;
                token = abuse.Groups[2].Value;
            }
        }

        return new BingSession
        {
            Cookies = cookies,
            Host = host,
            Ig = ig,
            Iid = iid,
            Token = token,
            Key = key,
            ExpiresAt = DateTimeOffset.UtcNow + BingSessionTtl,
        };
    }

    // 必应免费端点流程移植自 XUnity.AutoTranslator（MIT License）
    // https://github.com/bbepis/XUnity.AutoTranslator  src/Translators/BingTranslate/BingTranslateEndpoint.cs
    // 会话复用：token 失效（401/403/业务错误码）时作废重建一次重试
    private static async Task<string> TranslateBingAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var session = await GetBingSessionAsync(ct);
            using var handler = new HttpClientHandler { CookieContainer = session.Cookies, UseCookies = true };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");

            var url = session.Ig is null || session.Iid is null
                ? $"https://{session.Host}/ttranslatev3?isVertical=1"
                : $"https://{session.Host}/ttranslatev3?isVertical=1&&IG={session.Ig}&IID={session.Iid}.1";
            var body = $"&fromLang={BingLanguage(sourceLanguage)}&text={Uri.EscapeDataString(text)}&to={BingLanguage(targetLanguage)}";
            if (session.Token is not null && session.Key is not null)
                body += $"&token={session.Token}&key={session.Key}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            request.Headers.Referrer = new Uri($"https://{session.Host}/translator");
            request.Headers.Add("Origin", $"https://{session.Host}");

            var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RateLimitedException();
            // 401/403 视为会话失效：作废重建一次重试
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 1)
            {
                _bingSession = null;
                continue;
            }
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("statusCode", out var statusCode))
            {
                // token 过期等业务错误：作废会话重建一次重试
                if (attempt == 1)
                {
                    _bingSession = null;
                    continue;
                }
                throw new ProviderException($"必应返回错误码 {statusCode.GetInt32()}");
            }
            return doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
        }
        throw new ProviderException("必应翻译失败");
    }

    private static string? Lookup(string lookFor, string html)
    {
        var index = html.IndexOf(lookFor, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var startIndex = index + lookFor.Length;
        var endIndex = html.IndexOf('"', startIndex);
        return endIndex > startIndex ? html[startIndex..endIndex] : null;
    }

    private static async Task<string> TranslateGoogleGtxAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var url = "https://translate.googleapis.com/translate_a/single" +
            $"?client=gtx&sl={Uri.EscapeDataString(sourceLanguage)}&tl={GoogleLanguage(targetLanguage)}&dt=t&q={Uri.EscapeDataString(text)}";
        var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Contains("unusual traffic", StringComparison.OrdinalIgnoreCase))
            throw new RateLimitedException();
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        return string.Concat(doc.RootElement[0].EnumerateArray()
            .Select(segment => segment[0].GetString() ?? ""));
    }

    // Yandex 免费网页接口（本机实测可用）：translate.yandex.net tr.json
    private static async Task<string> TranslateYandexAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var url = $"https://translate.yandex.net/api/v1/tr.json/translate?id={id}-0-0&srv=android" +
            $"&lang={YandexLanguage(sourceLanguage)}-{YandexLanguage(targetLanguage)}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = text });
        var response = await Http.PostAsync(url, content, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("code").GetInt32() != 200)
            throw new ProviderException("Yandex 返回非 200 业务码");
        return doc.RootElement.GetProperty("text")[0].GetString() ?? "";
    }

    // DeepL 网页版免费端点流程移植自 XUnity.AutoTranslator（MIT License）
    // https://github.com/bbepis/XUnity.AutoTranslator  src/Translators/DeepLTranslate/ExtDeepLTranslate.cs
    private static async Task<string> TranslateDeepLWebAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var target = DeepLLanguage(targetLanguage);
        var source = DeepLLanguage(sourceLanguage);
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");

        // 1. 打开翻译器页面拿 Cookie
        using (var pageResponse = await client.GetAsync("https://www.deepl.com/translator", ct))
        {
            if (pageResponse.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RateLimitedException();
            pageResponse.EnsureSuccessStatusCode();
        }

        long NextId() => Rng.NextInt64(100000, 99999999) * 1000;

        // 2. 初始化客户端状态
        var stateBody = "{\"jsonrpc\":\"2.0\",\"method\":\"getClientState\",\"params\":{\"v\":\"20180814\"},\"id\":" + NextId() + "}";
        using (var stateResponse = await client.PostAsync(
                   "https://w.deepl.com/web?request_type=jsonrpc&il=en&method=getClientState",
                   new StringContent(stateBody, Encoding.UTF8, "application/json"), ct))
        {
            if (stateResponse.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RateLimitedException();
            stateResponse.EnsureSuccessStatusCode();
        }

        // 3. 真实翻译请求（timestamp 按 XUnity 混淆公式计算）
        var r = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var n = 1L + text.Count(c => c == 'i');
        var timestamp = r + (n - r % n);
        var body = "{\"jsonrpc\":\"2.0\",\"method\":\"LMT_handle_jobs\",\"params\":{\"jobs\":[{\"kind\":\"default\"," +
            "\"preferred_num_beams\":1,\"raw_en_sentence\":" + JsonLiteral(text) + ",\"raw_en_context_before\":[],\"raw_en_context_after\":[]}]," +
            "\"lang\":{\"user_preferred_langs\":[\"" + target + "\",\"" + source + "\"],\"source_lang_user_selected\":\"" + source + "\"," +
            "\"target_lang\":\"" + target + "\"},\"priority\":-1,\"timestamp\":" + timestamp + "},\"id\":" + NextId() + "}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www2.deepl.com/jsonrpc")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Referrer = new Uri("https://www.deepl.com/translator");
        request.Headers.Add("Origin", "https://www.deepl.com");

        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out _))
            throw new ProviderException("DeepL 返回错误");
        return doc.RootElement.GetProperty("result").GetProperty("translations")[0]
            .GetProperty("beams")[0].GetProperty("postprocessed_sentence").GetString() ?? "";
    }

    // 腾讯交互翻译（TranSmart）网页接口
    private static async Task<string> TranslateTranSmartAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var target = RequireLanguage(TranSmartLanguages, targetLanguage);
        var clientKey = $"browser-chrome-126.0.0.0-{Guid.NewGuid()}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var body = "{\"header\":{\"fn\":\"auto_translation\",\"client_key\":\"" + clientKey + "\"}," +
            "\"type\":\"plain\",\"model_category\":\"normal\"," +
            "\"source\":{\"lang\":\"" + RequireLanguage(TranSmartLanguages, sourceLanguage) + "\",\"text_list\":[" + JsonLiteral(text) + "]},\"target\":{\"lang\":\"" + target + "\"}}";
        var response = await Http.PostAsync("https://transmart.qq.com/api/imt",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("auto_translation")[0].GetString() ?? "";
    }

    // 有道网页探测：流程共享自 YoudaoWebTranslator
    private static async Task<string> TranslateYoudaoAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (!YoudaoWebTranslator.SupportsLanguage(targetLanguage))
            throw new ProviderException("不支持该语言");
        return await YoudaoWebTranslator.TranslateAsync(text, targetLanguage, ct, sourceLanguage)
            ?? throw new ProviderException("有道翻译失败");
    }

    // 搜狗网页接口（2026-10 重新逆向 text.ecd30039.js）：页面文本翻译已切换到
    // 腾讯混元后端 /api/transpc/hunyuan/translate，免签名免 Cookie（form: text/from_lang/to_lang，上限 4000 字符）
    // 旧 /api/transpc/text/result（s=MD5(from+to+text+secretCode)）的 secretCode 未变但服务端
    // 已拒签：返回 errorCode 20 + 占位垃圾字段（from/to 颠倒、id="id"、dit=随机串），疑似蜜罐
    private static async Task<string> TranslateSogouAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var target = RequireLanguage(SogouLanguages, targetLanguage);
        var source = SogouLanguages.TryGetValue(sourceLanguage, out var mapped) ? mapped : sourceLanguage;
        // 页面行为：trim + 4000 字符截断
        var trimmed = text.Trim();
        if (trimmed.Length > 4000)
            trimmed = trimmed[..4000];

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = trimmed,
            ["from_lang"] = source,
            ["to_lang"] = target,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://fanyi.sogou.com/api/transpc/hunyuan/translate") { Content = content };
        request.Headers.Referrer = new Uri("https://fanyi.sogou.com/");
        var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetProperty("code").GetInt32() != 0)
            throw new ProviderException($"搜狗返回错误码 {data.GetProperty("code").GetInt32()}");
        return data.GetProperty("content").GetString() ?? "";
    }

    // 火山翻译网页接口已随网页版下线（2026 年全站 404，旧 /web/translate/v1/ 回 200 空 body），
    // 探测实现已移除；端点保留在"离线/其他"组如实标注，见 EndpointCatalog

    private static async Task<string> TranslateMyMemoryAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(sourceLanguage)}|{Uri.EscapeDataString(targetLanguage)}";
        var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("responseStatus").GetInt32() != 200)
            throw new ProviderException("MyMemory 返回非 200 业务码");
        return doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
    }

    // Papago 网页版免 Key 接口：旧 /apis/n2mt 已 404（XUnity 的 HMAC 版本号流程随旧站下线），
    // 改用新站内部接口 /api/text/translation，无需鉴权
    private static async Task<string> TranslatePapagoWebAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["source"] = RequireLanguage(PapagoWebLanguages, sourceLanguage),
            ["target"] = RequireLanguage(PapagoWebLanguages, targetLanguage),
            ["text"] = text,
        });
        var response = await Http.PostAsync("https://papago.naver.com/api/text/translation", content, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("errorCode", out var errorCode))
            throw new ProviderException($"Papago 返回错误 {errorCode.GetString()}");
        return doc.RootElement.GetProperty("translatedText").GetString() ?? "";
    }


}
