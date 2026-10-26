namespace Franca.Services;

// 软件内翻译统一入口：任意端点（Free 10 个 + BYOK 6 个 + LLM 5 个 + 自定义）翻译任意文本
// 不支持的端点/语言、缺 Key、网络或解析失败均返回 null，不抛
public static class AppTranslator
{
    public static async Task<string?> TranslateAsync(string endpointId, string text, string targetLanguage,
        IReadOnlyDictionary<string, string>? keys, CancellationToken ct, string sourceLanguage = "en")
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var def = EndpointCatalog.Find(endpointId);
        if (def is null)
            return null;

        if (def.Category == EndpointCategory.Free)
            return await TranslationProviders.TranslateFreeAsync(endpointId, text, sourceLanguage, targetLanguage, ct);

        if (def.Category != EndpointCategory.Byok && def.Category != EndpointCategory.Llm)
            return null;
        // 有 DefaultValue 的字段（如 LLM 默认 Model）允许缺省
        if (keys is null
            || def.KeyFields.Any(f => f.DefaultValue is null
                && (!keys.TryGetValue(f.FieldId, out var value) || string.IsNullOrWhiteSpace(value))))
            return null;
        return await TranslationProviders.TranslateByokAsync(endpointId, keys, text, sourceLanguage, targetLanguage, ct);
    }

    // 自定义端点（AppSettings.CustomEndpoints 里的定义）：OpenAI 兼容 / GET 纯文本
    public static async Task<string?> TranslateCustomAsync(CustomEndpointDef def, string text,
        string targetLanguage, CancellationToken ct, string sourceLanguage = "en")
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = def.Type == "openai"
                ? await TranslationProviders.TranslateOpenAiCompatibleAsync("LlmCustom",
                    new Dictionary<string, string> { ["BaseUrl"] = def.BaseUrl,
                        ["ApiKey"] = def.ApiKey, ["Model"] = def.Model },
                    text, sourceLanguage, targetLanguage, timeout.Token)
                : await TranslationProviders.TranslateCustomGetAsync(def.BaseUrl, text, sourceLanguage, targetLanguage, timeout.Token);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
