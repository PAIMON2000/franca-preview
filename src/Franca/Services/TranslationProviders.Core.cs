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
    internal class RateLimitedException : Exception { }
    internal class ProviderException : Exception
    {
        public ProviderException(string message) : base(message) { }
    }

    private static readonly HttpClient Http = CreateClient();
    private static readonly Random Rng = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        return client;
    }

    internal static Func<CancellationToken, Task<string>>? CreateFreeRequest(string endpointId, string text, string sourceLanguage, string targetLanguage) =>
        endpointId switch
        {
            "BingTranslate" => token => TranslateBingAsync(text, sourceLanguage, targetLanguage, token),
            "GoogleTranslateV2" => token => TranslateGoogleGtxAsync(text, sourceLanguage, targetLanguage, token),
            "YandexTranslate" => token => TranslateYandexAsync(text, sourceLanguage, targetLanguage, token),
            "DeepLTranslate" => token => TranslateDeepLWebAsync(text, sourceLanguage, targetLanguage, token),
            "PapagoTranslate" => token => TranslatePapagoWebAsync(text, sourceLanguage, targetLanguage, token),
            "TranSmartTranslate" => token => TranslateTranSmartAsync(text, sourceLanguage, targetLanguage, token),
            "YoudaoWebTranslate" => token => TranslateYoudaoAsync(text, sourceLanguage, targetLanguage, token),
            "SogouTranslate" => token => TranslateSogouAsync(text, sourceLanguage, targetLanguage, token),
            "MyMemoryTranslate" => token => TranslateMyMemoryAsync(text, sourceLanguage, targetLanguage, token),
            _ => null
        };

    internal static Func<CancellationToken, Task<string>>? CreateKeyedRequest(string endpointId,
        IReadOnlyDictionary<string, string> keys, string text, string sourceLanguage, string targetLanguage) =>
        endpointId switch
        {
            "BaiduTranslate" => token => TranslateBaiduAsync(keys, text, sourceLanguage, targetLanguage, token),
            "DeepLTranslateLegitimate" => token => TranslateDeepLAsync(keys, text, sourceLanguage, targetLanguage, token),
            "BingTranslateLegitimate" => token => TranslateBingLegitimateAsync(keys, text, sourceLanguage, targetLanguage, token),
            "GoogleTranslateLegitimate" => token => TranslateGoogleLegitimateAsync(keys, text, sourceLanguage, targetLanguage, token),
            "LingoCloudTranslate" => token => TranslateLingoCloudAsync(keys, text, sourceLanguage, targetLanguage, token),
            "WatsonTranslate" => token => TranslateWatsonAsync(keys, text, sourceLanguage, targetLanguage, token),
            "LlmDeepSeek" or "LlmDoubao" or "LlmGlm" or "LlmKimi" or "LlmOpenAI" or "LlmCustom"
                => token => TranslateOpenAiCompatibleAsync(endpointId, keys, text, sourceLanguage, targetLanguage, token),
            _ => null
        };

    // 任意文本翻译：不支持的端点或任何失败均返回 null，不抛（供 AppTranslator 驱动软件内翻译）
    public static async Task<string?> TranslateFreeAsync(string endpointId, string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (CreateFreeRequest(endpointId, text, sourceLanguage, targetLanguage) is not { } probe)
            return null;
        try
        {
            var result = await probe(ct);
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

    public static async Task<string?> TranslateByokAsync(string endpointId, IReadOnlyDictionary<string, string> keys,
        string text, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (CreateKeyedRequest(endpointId, keys, text, sourceLanguage, targetLanguage) is not { } probe)
            return null;
        try
        {
            var result = await probe(ct);
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

    // 嵌入手写 JSON 正文时转义任意文本（含引号），返回带引号的 JSON 字符串字面量
    private static string JsonLiteral(string text) => JsonSerializer.Serialize(text);


    // ---- 语言码映射 ----

    private static string BingLanguage(string code) => code switch
    {
        "auto" => "auto-detect",
        "zh-CN" => "zh-Hans",
        "zh-TW" => "zh-Hant",
        "pt-BR" => "pt",
        _ => code
    };

    private static string GoogleLanguage(string code) => code switch
    {
        "pt-BR" => "pt",
        _ => code
    };

    private static string YandexLanguage(string code) => code switch
    {
        "zh-CN" or "zh-TW" => "zh",
        "pt-BR" => "pt",
        _ => code
    };

    private static string BaiduLanguage(string code) => code switch
    {
        "zh-CN" => "zh", "zh-TW" => "cht", "ja" => "jp", "ko" => "kor",
        "fr" => "fra", "es" => "spa", "vi" => "vie", "pt-BR" => "pt",
        _ => code
    };

    private static string DeepLLanguage(string code) => code switch
    {
        "zh-CN" or "zh-TW" => "ZH", "en" => "EN-US", "pt-BR" => "PT-BR",
        _ => code.ToUpperInvariant()
    };

    private static string CaiyunLanguage(string code) => code switch
    {
        "zh-CN" or "zh-TW" => "zh",
        _ => code
    };

    private static readonly Dictionary<string, string> TranSmartLanguages = new()
    {
        ["auto"] = "auto",
        ["zh-CN"] = "zh", ["zh-TW"] = "zh-TW", ["en"] = "en",
        ["ja"] = "ja", ["ko"] = "ko", ["pt-BR"] = "pt",
        ["es"] = "es", ["id"] = "id", ["vi"] = "vi",
        ["th"] = "th", ["tr"] = "tr", ["ru"] = "ru",
        ["ar"] = "ar", ["fr"] = "fr", ["de"] = "de",
    };

    private static readonly Dictionary<string, string> SogouLanguages = new()
    {
        ["zh-CN"] = "zh-CHS", ["zh-TW"] = "zh-CHT", ["en"] = "en",
        ["ja"] = "ja", ["ko"] = "ko", ["pt-BR"] = "pt",
        ["es"] = "es", ["id"] = "id", ["vi"] = "vi",
        ["th"] = "th", ["tr"] = "tr", ["ru"] = "ru",
        ["ar"] = "ar", ["fr"] = "fr", ["de"] = "de",
    };

    private static readonly Dictionary<string, string> PapagoWebLanguages = new()
    {
        ["auto"] = "auto",
        ["zh-CN"] = "zh-CN", ["zh-TW"] = "zh-TW", ["en"] = "en",
        ["ja"] = "ja", ["ko"] = "ko", ["pt-BR"] = "pt",
        ["es"] = "es", ["id"] = "id", ["vi"] = "vi",
        ["th"] = "th", ["ru"] = "ru", ["fr"] = "fr", ["de"] = "de",
    };

    private static string RequireLanguage(Dictionary<string, string> map, string code) =>
        map.TryGetValue(code, out var lang)
            ? lang
            : throw new ProviderException("不支持该语言");


}
