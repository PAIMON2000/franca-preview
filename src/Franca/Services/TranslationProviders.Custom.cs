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
    internal static async Task<string> TranslateCustomGetAsync(string baseUrl, string text, string sourceLanguage,
        string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ProviderException("缺少 BaseUrl");
        var separator = baseUrl.Contains('?') ? "&" : "?";
        var url = $"{baseUrl}{separator}from={Uri.EscapeDataString(sourceLanguage)}" +
            $"&to={Uri.EscapeDataString(targetLanguage)}&text={Uri.EscapeDataString(text)}";
        var response = await Http.GetAsync(url, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException("Key 无效");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync(ct)).Trim();
    }

    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["zh-CN"] = "Chinese Simplified", ["zh-TW"] = "Chinese Traditional", ["en"] = "English",
        ["ja"] = "Japanese", ["ko"] = "Korean", ["pt-BR"] = "Portuguese (Brazil)",
        ["es"] = "Spanish", ["id"] = "Indonesian", ["vi"] = "Vietnamese",
        ["th"] = "Thai", ["tr"] = "Turkish", ["ru"] = "Russian",
        ["ar"] = "Arabic", ["fr"] = "French", ["de"] = "German",
    };

    private static string LanguageName(string code) =>
        LanguageNames.TryGetValue(code, out var name) ? name : code;

    // 通用 OpenAI 兼容实现：POST {baseUrl}/chat/completions，Bearer 鉴权
    // BaseUrl 取目录预置值（LlmBaseUrl），LlmCustom 取 KeyFields 的 BaseUrl
    internal static async Task<string> TranslateOpenAiCompatibleAsync(string endpointId, IReadOnlyDictionary<string, string> keys,
        string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var def = EndpointCatalog.Find(endpointId) ?? throw new ProviderException("未知端点");
        var baseUrl = def.LlmBaseUrl
            ?? (keys.TryGetValue("BaseUrl", out var customUrl) && !string.IsNullOrWhiteSpace(customUrl)
                ? customUrl.TrimEnd('/') : null)
            ?? throw new ProviderException("缺少 BaseUrl");
        var model = keys.TryGetValue("Model", out var m) && !string.IsNullOrWhiteSpace(m)
            ? m
            : def.KeyFields.FirstOrDefault(f => f.FieldId == "Model")?.DefaultValue
              ?? throw new ProviderException("缺少 Model");

        // 自定义端点允许无 Key（本地服务等）；预置 BYOK 端点的 Key 完整性由卡片层把关
        var hasKey = keys.TryGetValue("ApiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey);

        var sourceInstruction = sourceLanguage == "auto"
            ? "the automatically detected source language"
            : LanguageName(sourceLanguage);
        var systemPrompt = "You are a translation engine for video games. Translate the user's text from "
            + sourceInstruction + " to " + LanguageName(targetLanguage)
            + ". Output ONLY the translated text, no explanations, no quotes."
            + " Preserve placeholders, formatting tags and punctuation style.";
        var body = "{\"model\":" + JsonLiteral(model) + ",\"messages\":[{\"role\":\"system\",\"content\":"
            + JsonLiteral(systemPrompt) + "},{\"role\":\"user\",\"content\":" + JsonLiteral(text)
            + "}],\"temperature\":0.3,\"max_tokens\":1024"
            + (endpointId == "LlmDeepSeek" ? ",\"thinking\":{\"type\":\"disabled\"}" : "") + "}";
        // Kimi 分国内/国际双平台：同一 Key 只有一边可用，凭 401/403 自动换另一边重试（Key 兼容性判断）
        var baseUrls = endpointId == "LlmKimi"
            ? new List<string> { baseUrl, EndpointConfigurationService.KimiIntlBaseUrl }
            : new List<string> { baseUrl };
        HttpResponseMessage response;
        var attempt = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrls[attempt] + "/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (hasKey)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            response = await Http.SendAsync(request, ct);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                || attempt == baseUrls.Count - 1)
                break;
            response.Dispose();
            attempt++;
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException("Key 无效");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitedException();
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new ProviderException(ExtractOpenAiError(await response.Content.ReadAsStringAsync(ct)));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var choice = doc.RootElement.GetProperty("choices")[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() is "length" or "content_filter")
            throw new ProviderException("Incomplete translation response");
        return choice.GetProperty("message")
            .GetProperty("content").GetString()?.Trim() ?? "";
    }

    // 400 业务错误取 error.message，截断 80 字符
    private static string ExtractOpenAiError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var message = doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "未知错误";
            return message.Length > 80 ? message[..80] : message;
        }
        catch
        {
            return "HTTP 400";
        }
    }

    private static string Md5Hex(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
