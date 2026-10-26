using System.Text.Json;
using System.Text.Json.Serialization;

namespace Franca.Services;

// 配置操作集中于服务；提示词不读取密钥，导入不执行代码、不发请求、不改当前主用。
public static class EndpointConfigurationService
{
    public const string DeepSeekId = "LlmDeepSeek";
    public const string DeepSeekModel = "deepseek-flash";
    public const string DeepSeekPlatform = "https://platform.deepseek.com/";
    public const string KimiId = "LlmKimi";
    public const string KimiPlatform = "https://platform.moonshot.cn/";
    public const string KimiIntlPlatform = "https://platform.moonshot.ai/";
    public const string KimiIntlBaseUrl = "https://api.moonshot.ai/v1";
    public const string OpenAiId = "LlmOpenAI";
    public const string OpenAiPlatform = "https://platform.openai.com/";

    // 大模型面板共用的 Key 保存：Model 取目录预设默认值，保存同时解除禁用
    public static void SaveLlmKey(AppSettings settings, string endpointId, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException(UiStrings.Get("ui.keyRequired"));
        var model = EndpointCatalog.Find(endpointId)?.KeyFields.FirstOrDefault(f => f.FieldId == "Model")?.DefaultValue
            ?? throw new ArgumentException(UiStrings.Get("ui.keyRequired"));
        settings.EndpointKeys[endpointId] = new()
        {
            ["ApiKey"] = key.Trim(), ["Model"] = model,
        };
        settings.DisabledEndpoints.Remove(endpointId);
        settings.Save();
    }

    public static void SaveDeepSeekKey(AppSettings settings, string key) => SaveLlmKey(settings, DeepSeekId, key);

    public static void DeleteKey(AppSettings settings, string endpointId)
    {
        settings.EndpointKeys.Remove(endpointId);
        if (settings.Endpoint == endpointId || (endpointId == DeepSeekId && settings.Endpoint == "AutoLlm"))
            settings.Endpoint = "AutoFree";
        if (settings.FallbackEndpoint == endpointId)
        {
            settings.FallbackEndpoint = "GoogleTranslateV2";
            settings.FallbackManual = false;
        }
        settings.Save();
    }

    public static string AgentPrompt => """
        Help me configure an additional game-translation model in Franca.
        Ask which provider, API base URL, model and key I want to use. Consult the provider's official API documentation.
        Franca currently supports the OpenAI chat/completions protocol for additional models.
        Return ONE JSON object with exactly these fields (no Markdown fences):
        {"name":"My model","baseUrl":"https://api.example.com/v1","model":"model-id","apiKey":""}
        baseUrl is the API root, without /chat/completions. Use HTTPS, or HTTP only for a loopback local server.
        Use an empty apiKey if I will fill it locally in Franca. Never invent a key or expose keys from other apps.
        Do not put keys in URLs. Do not change Franca source code or overwrite its settings file.
        I will paste the JSON into Franca's import box; it will validate it and add a new endpoint without selecting it.
        Do not claim a connection test passed unless a real request was authorized and actually succeeded.
        """;

    public static void AddEndpoint(AppSettings settings, CustomEndpointDef endpoint)
    {
        if (!endpoint.Id.StartsWith("custom:", StringComparison.Ordinal)
            || settings.CustomEndpoints.Any(existing => existing.Id == endpoint.Id))
            throw new ArgumentException(UiStrings.Get("ui.importInvalid"));
        settings.CustomEndpoints.Add(endpoint);
        settings.Save();
    }

    private sealed class ImportData
    {
        public string Name { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    public static CustomEndpointDef ParseModel(string json)
    {
        if (json.Length > 16384) throw new ArgumentException(UiStrings.Get("ui.importInvalid"));
        ImportData? data;
        try
        {
            data = JsonSerializer.Deserialize<ImportData>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            });
        }
        catch (JsonException) { throw new ArgumentException(UiStrings.Get("ui.importInvalid")); }
        if (data is null || string.IsNullOrWhiteSpace(data.Name) || data.Name.Length > 80
            || string.IsNullOrWhiteSpace(data.Model) || data.Model.Length > 160
            || !Uri.TryCreate(data.BaseUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            || data.Name.Any(char.IsControl) || data.Model.Any(char.IsControl)
            || (data.ApiKey?.Any(char.IsControl) ?? false))
            throw new ArgumentException(UiStrings.Get("ui.importInvalid"));
        return new CustomEndpointDef
        {
            Id = "custom:" + Guid.NewGuid().ToString("N"), Name = data.Name.Trim(), Type = "openai",
            Group = "Llm", BaseUrl = uri.AbsoluteUri.TrimEnd('/'), Model = data.Model.Trim(), ApiKey = data.ApiKey?.Trim() ?? "",
        };
    }
}
