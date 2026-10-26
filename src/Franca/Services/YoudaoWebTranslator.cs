using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Franca.Services;

// 有道网页版翻译共享实现（dict.youdao.com/webtranslate）
// 当前流程（2026-10 重新逆向 app.5a9933f2.js）：
//   1. 访问 fanyi.youdao.com 拿 Cookie
//   2. GET /webtranslate/key（keyid=webfanyi-key-getter-2025，静态 key yU5nT5dK3eZ1pI4j 签名）
//      → 换取动态 secretKey + aesKey/aesIv（明文 JSON，不加密）
//   3. POST /webtranslate（sign=MD5(client&mysticTime&product&key=secretKey)）
//      → 响应为 base64url + AES-128-CBC 密文，用 aesKey/aesIv 的 MD5 派生 key/iv 解密
// 旧静态 key fsdsogkndfokasodnaso 已失效：签名错误时服务器不回错误码，
// 而是返回 code=0 + 词典例句语料的蜜罐译文（"hello"→随机中文例句）
public static class YoudaoWebTranslator
{
    private const string KeyGetterId = "webfanyi-key-getter-2025";
    private const string KeyGetterSecret = "yU5nT5dK3eZ1pI4j";

    // aesKey/aesIv 现由 /webtranslate/key 下发，此处仅作兜底
    private const string FallbackAesKey =
        "ydsecret://query/key/B*RGygVywfNBwpmBaZg*WT7SIOUP2T0C9WHMZN39j^DAdaZhAnxvGcCY6VYFwnHl";
    private const string FallbackAesIv =
        "ydsecret://query/iv/C@lZe2YzHtZ2CYgaXKSVfsb7Y4QWHjITPPZ0nQp87fBeJ!Iv6v^6fvi2WN@bYpJ4";

    private static readonly Dictionary<string, string> Languages = new()
    {
        ["zh-CN"] = "zh-CHS", ["zh-TW"] = "zh-CHT", ["en"] = "en",
        ["ja"] = "ja", ["ko"] = "ko", ["pt-BR"] = "pt",
        ["es"] = "es", ["id"] = "id", ["vi"] = "vi",
        ["th"] = "th", ["tr"] = "tr", ["ru"] = "ru",
        ["ar"] = "ar", ["fr"] = "fr", ["de"] = "de",
    };

    public static bool SupportsLanguage(string targetLanguage) => Languages.ContainsKey(targetLanguage);

    // 会话复用：Cookie + secretKey 握手 TTL 10 分钟，避免每次调用都重抓
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim SessionLock = new(1, 1);
    private static YoudaoSession? _session;

    private class YoudaoSession
    {
        public required CookieContainer Cookies { get; init; }
        public required string SecretKey { get; init; }
        public string AesKey { get; init; } = FallbackAesKey;
        public string AesIv { get; init; } = FallbackAesIv;
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private static HttpClient CreateClient(CookieContainer cookies)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseCookies = true });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        return client;
    }

    private static string SignOf(long mysticTime, string key) =>
        Md5Hex($"client=fanyideskweb&mysticTime={mysticTime}&product=webfanyi&key={key}");

    private static async Task<YoudaoSession> GetSessionAsync(CancellationToken ct)
    {
        if (_session is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached;
        await SessionLock.WaitAsync(ct);
        try
        {
            if (_session is { } fresh && fresh.ExpiresAt > DateTimeOffset.UtcNow)
                return fresh;

            var cookies = new CookieContainer();
            using var client = CreateClient(cookies);
            // 1. 访问首页拿 OUTFOX_SEARCH_USER_ID Cookie
            using (var pageResponse = await client.GetAsync("https://fanyi.youdao.com", ct))
                pageResponse.EnsureSuccessStatusCode();

            // 2. 用静态 key 换动态 secretKey（GET，明文 JSON）
            var keyTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var keyQuery = $"keyid={KeyGetterId}&client=fanyideskweb&product=webfanyi&appVersion=12.0.0" +
                $"&vendor=web&pointParam=client,mysticTime,product&mysticTime={keyTime}&keyfrom=fanyi.web" +
                $"&mid=1&screen=1&model=1&network=wifi&abtest=0&yduuid=abcdefg&sign={SignOf(keyTime, KeyGetterSecret)}";
            using var keyRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://dict.youdao.com/webtranslate/key?{keyQuery}");
            keyRequest.Headers.Referrer = new Uri("https://fanyi.youdao.com/");
            var keyJson = await (await client.SendAsync(keyRequest, ct)).Content.ReadAsStringAsync(ct);

            using var keyDoc = JsonDocument.Parse(keyJson);
            if (keyDoc.RootElement.GetProperty("code").GetInt32() != 0)
                throw new InvalidOperationException("webtranslate/key 返回非 0 业务码");
            var data = keyDoc.RootElement.GetProperty("data");
            var secretKey = data.GetProperty("secretKey").GetString();
            if (string.IsNullOrWhiteSpace(secretKey))
                throw new InvalidOperationException("webtranslate/key 未下发 secretKey");

            _session = new YoudaoSession
            {
                Cookies = cookies,
                SecretKey = secretKey,
                AesKey = data.TryGetProperty("aesKey", out var aesKey) && aesKey.GetString() is { Length: > 0 } k
                    ? k : FallbackAesKey,
                AesIv = data.TryGetProperty("aesIv", out var aesIv) && aesIv.GetString() is { Length: > 0 } v
                    ? v : FallbackAesIv,
                ExpiresAt = DateTimeOffset.UtcNow + SessionTtl,
            };
            return _session;
        }
        finally
        {
            SessionLock.Release();
        }
    }

    // 翻译任意文本；不支持的目标语言或任何失败均返回 null
    // sourceLanguage 非 en 时 from 用 AUTO 让有道自动检测
    public static async Task<string?> TranslateAsync(string text, string targetLanguage, CancellationToken ct,
        string sourceLanguage = "en")
    {
        var json = await RequestRawAsync(text, targetLanguage, ct, sourceLanguage);
        if (json is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetInt32() != 0)
                return null;
            // translateResult 是嵌套数组：外层=段落，内层=句段，全部 tgt 拼接（别只取第一段）
            var result = string.Concat(doc.RootElement.GetProperty("translateResult")
                .EnumerateArray()
                .SelectMany(paragraph => paragraph.ValueKind == JsonValueKind.Array
                    ? (IEnumerable<JsonElement>)paragraph.EnumerateArray()
                    : new[] { paragraph })
                .Select(segment => segment.GetProperty("tgt").GetString()));
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    // 无头调试：打印解密后的完整响应 JSON
    public static async Task DumpRawAsync(string text, string targetLanguage, CancellationToken ct)
    {
        var json = await RequestRawAsync(text, targetLanguage, ct, "en");
        Console.WriteLine(json ?? "(null)");
    }

    private static async Task<string?> RequestRawAsync(string text, string targetLanguage, CancellationToken ct,
        string sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(text) || !Languages.TryGetValue(targetLanguage, out var target))
            return null;
        try
        {
            var session = await GetSessionAsync(ct);
            using var client = CreateClient(session.Cookies);

            var mysticTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://dict.youdao.com/webtranslate")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["i"] = text,
                    ["from"] = sourceLanguage == "en" ? "en" : "AUTO",
                    ["to"] = target,
                    ["dictResult"] = "true",
                    ["keyid"] = "webfanyi",
                    ["sign"] = SignOf(mysticTime, session.SecretKey),
                    ["client"] = "fanyideskweb",
                    ["product"] = "webfanyi",
                    ["appVersion"] = "1.0.0",
                    ["vendor"] = "web",
                    ["pointParam"] = "client,mysticTime,product",
                    ["mysticTime"] = mysticTime.ToString(),
                    ["keyfrom"] = "fanyi.web",
                })
            };
            request.Headers.Referrer = new Uri("https://fanyi.youdao.com/");
            var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return DecryptResponse(await response.Content.ReadAsStringAsync(ct), session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string DecryptResponse(string base64Url, YoudaoSession session)
    {
        var data = Convert.FromBase64String(base64Url.Replace('-', '+').Replace('_', '/'));
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(session.AesKey));
        aes.IV = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(session.AesIv));
        using var decryptor = aes.CreateDecryptor();
        return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(data, 0, data.Length));
    }

    private static string Md5Hex(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
