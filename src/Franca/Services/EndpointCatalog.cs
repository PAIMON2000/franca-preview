namespace Franca.Services;

public enum EndpointCategory
{
    Free,
    Byok,
    Offline,
    Llm
}

public class KeyFieldDef
{
    public required string Label { get; init; }
    public required string FieldId { get; init; }
    public required string IniSection { get; init; }
    public required string IniKey { get; init; }

    // 卡片构造时的预填值（如 LLM 端点的默认 Model）
    public string? DefaultValue { get; init; }
}

public class EndpointDef
{
    public required string Id { get; init; }
    // UiStrings 的 key（endpoint.<Id>），显示名随语言切换
    public required string DisplayNameKey { get; init; }
    public required EndpointCategory Category { get; init; }

    // UiStrings 的 key，空串表示无备注
    public string NoteKey { get; init; } = "";
    public KeyFieldDef[] KeyFields { get; init; } = Array.Empty<KeyFieldDef>();

    // null 表示仅测速池端点：XUnity 不认识，不能写进 AutoTranslatorConfig.ini
    public string? XUnityId { get; init; }

    // LLM（OpenAI 兼容）端点的 BaseUrl；null 表示由 KeyFields 的 BaseUrl 字段提供（LlmCustom）
    public string? LlmBaseUrl { get; init; }
}

public static class EndpointCatalog
{
    private const string PoolOnlyNoteKey = "note.poolonly";

    public static readonly EndpointDef[] All =
    {
        new() { Id = "BingTranslate", DisplayNameKey = "endpoint.BingTranslate", Category = EndpointCategory.Free,
            XUnityId = "BingTranslate" },
        new() { Id = "GoogleTranslateV2", DisplayNameKey = "endpoint.GoogleTranslateV2", Category = EndpointCategory.Free,
            XUnityId = "GoogleTranslateV2" },
        new() { Id = "YandexTranslate", DisplayNameKey = "endpoint.YandexTranslate", Category = EndpointCategory.Free,
            XUnityId = "YandexTranslate", NoteKey = "note.yandex" },
        new() { Id = "DeepLTranslate", DisplayNameKey = "endpoint.DeepLTranslate", Category = EndpointCategory.Free,
            XUnityId = "DeepLTranslate", NoteKey = "note.deepl" },
        new() { Id = "PapagoTranslate", DisplayNameKey = "endpoint.PapagoTranslate", Category = EndpointCategory.Free,
            XUnityId = "PapagoTranslate", NoteKey = "note.papago" },
        new() { Id = "TranSmartTranslate", DisplayNameKey = "endpoint.TranSmartTranslate", Category = EndpointCategory.Free,
            NoteKey = PoolOnlyNoteKey },
        new() { Id = "YoudaoWebTranslate", DisplayNameKey = "endpoint.YoudaoWebTranslate", Category = EndpointCategory.Free,
            NoteKey = PoolOnlyNoteKey },
        new() { Id = "SogouTranslate", DisplayNameKey = "endpoint.SogouTranslate", Category = EndpointCategory.Free,
            NoteKey = PoolOnlyNoteKey },
        new() { Id = "MyMemoryTranslate", DisplayNameKey = "endpoint.MyMemoryTranslate", Category = EndpointCategory.Free,
            NoteKey = PoolOnlyNoteKey },
        new() { Id = "BaiduTranslate", DisplayNameKey = "endpoint.BaiduTranslate", Category = EndpointCategory.Byok,
            XUnityId = "BaiduTranslate",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "AppId", FieldId = "AppId", IniSection = "Baidu", IniKey = "BaiduAppId" },
                new() { Label = "AppSecret", FieldId = "AppSecret", IniSection = "Baidu", IniKey = "BaiduAppSecret" },
            }},
        new() { Id = "DeepLTranslateLegitimate", DisplayNameKey = "endpoint.DeepLTranslateLegitimate", Category = EndpointCategory.Byok,
            XUnityId = "DeepLTranslateLegitimate",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "ApiKey", FieldId = "ApiKey", IniSection = "DeepLLegitimate", IniKey = "ApiKey" },
            }},
        new() { Id = "BingTranslateLegitimate", DisplayNameKey = "endpoint.BingTranslateLegitimate", Category = EndpointCategory.Byok,
            XUnityId = "BingTranslateLegitimate",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "订阅 Key", FieldId = "SubscriptionKey", IniSection = "BingLegitimate", IniKey = "OcpApimSubscriptionKey" },
            }},
        new() { Id = "GoogleTranslateLegitimate", DisplayNameKey = "endpoint.GoogleTranslateLegitimate", Category = EndpointCategory.Byok,
            XUnityId = "GoogleTranslateLegitimate",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "API Key", FieldId = "ApiKey", IniSection = "GoogleLegitimate", IniKey = "GoogleAPIKey" },
            }},
        new() { Id = "LingoCloudTranslate", DisplayNameKey = "endpoint.LingoCloudTranslate", Category = EndpointCategory.Byok,
            XUnityId = "LingoCloudTranslate",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "Token", FieldId = "Token", IniSection = "LingoCloud", IniKey = "LingoCloudToken" },
            }},
        new() { Id = "WatsonTranslate", DisplayNameKey = "endpoint.WatsonTranslate", Category = EndpointCategory.Byok,
            XUnityId = "WatsonTranslate",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "Url", FieldId = "Url", IniSection = "Watson", IniKey = "Url" },
                new() { Label = "Key", FieldId = "Key", IniSection = "Watson", IniKey = "Key" },
            }},
        new() { Id = "LecPowerTranslator15", DisplayNameKey = "endpoint.LecPowerTranslator15", Category = EndpointCategory.Offline,
            XUnityId = "LecPowerTranslator15",
            NoteKey = "note.lec" },
        new() { Id = "ezTransXP", DisplayNameKey = "endpoint.ezTransXP", Category = EndpointCategory.Offline,
            XUnityId = "ezTransXP",
            NoteKey = "note.eztrans" },
        // LLM（OpenAI 兼容协议）：IniSection/IniKey 留空，不写 AutoTranslatorConfig.ini
        new() { Id = "LlmDeepSeek", DisplayNameKey = "endpoint.LlmDeepSeek", Category = EndpointCategory.Llm,
            LlmBaseUrl = "https://api.deepseek.com",
            NoteKey = "note.llm.common",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "ApiKey", FieldId = "ApiKey", IniSection = "", IniKey = "" },
                new() { Label = "Model", FieldId = "Model", IniSection = "", IniKey = "", DefaultValue = EndpointConfigurationService.DeepSeekModel },
            }},
        new() { Id = "LlmDoubao", DisplayNameKey = "endpoint.LlmDoubao", Category = EndpointCategory.Llm,
            LlmBaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
            NoteKey = "note.llm.doubao",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "ApiKey", FieldId = "ApiKey", IniSection = "", IniKey = "" },
                new() { Label = "Model", FieldId = "Model", IniSection = "", IniKey = "", DefaultValue = "doubao-1-5-pro-32k-250115" },
            }},
        new() { Id = "LlmGlm", DisplayNameKey = "endpoint.LlmGlm", Category = EndpointCategory.Llm,
            LlmBaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            NoteKey = "note.llm.glm",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "ApiKey", FieldId = "ApiKey", IniSection = "", IniKey = "" },
                new() { Label = "Model", FieldId = "Model", IniSection = "", IniKey = "", DefaultValue = "glm-4-flash" },
            }},
        new() { Id = "LlmKimi", DisplayNameKey = "endpoint.LlmKimi", Category = EndpointCategory.Llm,
            LlmBaseUrl = "https://api.moonshot.cn/v1",
            NoteKey = "note.llm.common",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "ApiKey", FieldId = "ApiKey", IniSection = "", IniKey = "" },
                new() { Label = "Model", FieldId = "Model", IniSection = "", IniKey = "", DefaultValue = "moonshot-v1-8k" },
            }},
        new() { Id = "LlmOpenAI", DisplayNameKey = "endpoint.LlmOpenAI", Category = EndpointCategory.Llm,
            LlmBaseUrl = "https://api.openai.com/v1",
            NoteKey = "note.llm.common",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "ApiKey", FieldId = "ApiKey", IniSection = "", IniKey = "" },
                new() { Label = "Model", FieldId = "Model", IniSection = "", IniKey = "", DefaultValue = "gpt-4o-mini" },
            }},
        new() { Id = "LlmCustom", DisplayNameKey = "endpoint.LlmCustom", Category = EndpointCategory.Llm,
            NoteKey = "note.llm.common",
            KeyFields = new KeyFieldDef[]
            {
                new() { Label = "ApiKey", FieldId = "ApiKey", IniSection = "", IniKey = "" },
                new() { Label = "BaseUrl", FieldId = "BaseUrl", IniSection = "", IniKey = "" },
                new() { Label = "Model", FieldId = "Model", IniSection = "", IniKey = "" },
            }},
    };

    public static EndpointDef? Find(string id) => All.FirstOrDefault(e => e.Id == id);
}
