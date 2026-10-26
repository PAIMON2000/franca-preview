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
    // ---- 需 Key 端点验证（无本机 Key，按各厂商公开 API 文档实现，未经实测）----

    private static string RequireKey(IReadOnlyDictionary<string, string> keys, string field) =>
        keys.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ProviderException($"缺少 {field}");

    private static async Task<string> TranslateBaiduAsync(IReadOnlyDictionary<string, string> keys, string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var appId = RequireKey(keys, "AppId");
        var secret = RequireKey(keys, "AppSecret");
        var salt = Rng.Next(100000, 999999).ToString();
        var sign = Md5Hex(appId + text + salt + secret);
        var url = "https://fanyi-api.baidu.com/api/trans/vip/translate" +
            $"?q={Uri.EscapeDataString(text)}&from={BaiduLanguage(sourceLanguage)}&to={BaiduLanguage(targetLanguage)}&appid={appId}&salt={salt}&sign={sign}";
        var json = await Http.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error_code", out var errorCode))
            throw new ProviderException($"百度错误 {errorCode.GetString()}");
        return doc.RootElement.GetProperty("trans_result")[0].GetProperty("dst").GetString() ?? "";
    }

    private static async Task<string> TranslateDeepLAsync(IReadOnlyDictionary<string, string> keys, string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["auth_key"] = RequireKey(keys, "ApiKey"),
            ["text"] = text,
            ["source_lang"] = DeepLLanguage(sourceLanguage),
            ["target_lang"] = DeepLLanguage(targetLanguage),
        });
        var response = await Http.PostAsync("https://api-free.deepl.com/v2/translate", content, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new ProviderException("Key 无效");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> TranslateBingLegitimateAsync(IReadOnlyDictionary<string, string> keys, string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var url = "https://api.cognitive.microsofttranslator.com/translate" +
            $"?api-version=3.0&from={BingLanguage(sourceLanguage)}&to={BingLanguage(targetLanguage)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("[{\"Text\":" + JsonLiteral(text) + "}]", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", RequireKey(keys, "SubscriptionKey"));
        var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ProviderException("Key 无效");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> TranslateGoogleLegitimateAsync(IReadOnlyDictionary<string, string> keys, string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var url = $"https://translation.googleapis.com/language/translate/v2?key={RequireKey(keys, "ApiKey")}";
        using var content = new StringContent(
            "{\"q\":" + JsonLiteral(text) + ",\"source\":\"" + GoogleLanguage(sourceLanguage) + "\",\"target\":\"" + GoogleLanguage(targetLanguage) + "\",\"format\":\"text\"}",
            Encoding.UTF8, "application/json");
        var response = await Http.PostAsync(url, content, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            throw new ProviderException("Key 无效");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty("translations")[0]
            .GetProperty("translatedText").GetString() ?? "";
    }

    private static async Task<string> TranslateLingoCloudAsync(IReadOnlyDictionary<string, string> keys, string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://api.interpreter.caiyunai.com/v1/translator")
        {
            Content = new StringContent(
                "{\"source\":[" + JsonLiteral(text) + "],\"trans_type\":\"" + CaiyunLanguage(sourceLanguage) + "2" + CaiyunLanguage(targetLanguage) + "\",\"request_id\":\"franca\",\"detect\":" + (sourceLanguage == "auto" ? "true" : "false") + "}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Authorization", $"token {RequireKey(keys, "Token")}");
        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("target")[0].GetString() ?? "";
    }

    private static async Task<string> TranslateWatsonAsync(IReadOnlyDictionary<string, string> keys, string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var baseUrl = RequireKey(keys, "Url").TrimEnd('/');
        string WatsonCode(string code) => code is "zh-CN" or "zh-TW" ? "zh" : code;
        var modelId = $"{WatsonCode(sourceLanguage)}-{WatsonCode(targetLanguage)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/translate?version=2018-05-01")
        {
            Content = new StringContent("{\"text\":[" + JsonLiteral(text) + "],\"model_id\":\"" + modelId + "\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"apikey:{RequireKey(keys, "Key")}")));
        var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ProviderException("Key 无效");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("translations")[0].GetProperty("translation").GetString() ?? "";
    }


}
