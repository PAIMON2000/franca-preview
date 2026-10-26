using System.IO;
using System.Text.Json;

namespace Franca.Services;

public class LanguageOption
{
    public required string Display { get; init; }
    public required string Code { get; init; }

    // XUnity.AutoTranslator 各端点实际接受的语言码（多数与 Code 相同）
    public string XUnityCode { get; init; } = "";

    public string EffectiveXUnityCode => string.IsNullOrEmpty(XUnityCode) ? Code : XUnityCode;
}

public class AppSettings
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsTransient { get; set; }
    public string TargetLanguage { get; set; } = "";
    public string SourceLanguage { get; set; } = "en";
    // 仅参与游戏官方语言支持判断，不作为翻译请求或游戏配置的目标语言。
    public string SecondaryLanguage { get; set; } = "";
    public bool PrioritizeTranslation { get; set; } = true;
    public bool HideNonGameSoftware { get; set; } = true;
    public int? LastLaunchedAppId { get; set; }
    public string Endpoint { get; set; } = "BingTranslate";
    public string FallbackEndpoint { get; set; } = "GoogleTranslateV2";
    public bool FallbackManual { get; set; }

    // 翻译模式（lab/010）：speed = 词典→包缓存→在线；quality = 包缓存→在线（完全绕过词典）。
    // 旧设置文件无此字段时反序列化默认为 speed。
    public string TranslationMode { get; set; } = "speed";

    // 高速模式下词典命中后不再发词典命中的后台在线升级请求；不保证其他文本的翻译覆盖
    public bool NoOptimizeAfterGenerate { get; set; }

    // 翻译状态悬浮窗（lab/011）：默认关；位置为 WPF DIP 坐标，null 表示尚未拖动过
    public bool OverlayEnabled { get; set; }

    // 界面主题：default / yellow / orange / white / blue / bluewhite（见 Themes/ThemeCatalog）
    public string Theme { get; set; } = "default";
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }

    public bool QualityMode => string.Equals(TranslationMode, "quality", StringComparison.OrdinalIgnoreCase);

    // 端点 Id → (字段 Id → Key 值)，明文仅保存在本机
    public Dictionary<string, Dictionary<string, string>> EndpointKeys { get; set; } = new();

    // 用户禁用的端点 Id
    public HashSet<string> DisabledEndpoints { get; set; } = new();

    // 用户新增的自定义端点
    public List<CustomEndpointDef> CustomEndpoints { get; set; } = new();

    // Legacy fields retained for old settings files; relay attribution now comes from the request route.
    public int ActiveGameAppId { get; set; }
    public string ActiveGameName { get; set; } = "";

    public static readonly LanguageOption[] Languages =
    {
        new() { Display = "简体中文", Code = "zh-CN" },
        new() { Display = "繁體中文", Code = "zh-TW" },
        new() { Display = "English", Code = "en" },
        new() { Display = "日本語", Code = "ja" },
        new() { Display = "한국어", Code = "ko" },
        // XUnity 的 Bing/GoogleV2 端点均只认 pt，不认 pt-BR
        new() { Display = "Português (Brasil)", Code = "pt-BR", XUnityCode = "pt" },
        new() { Display = "Español", Code = "es" },
        new() { Display = "Bahasa Indonesia", Code = "id" },
        new() { Display = "Tiếng Việt", Code = "vi" },
        new() { Display = "ไทย", Code = "th" },
        new() { Display = "Türkçe", Code = "tr" },
        new() { Display = "Русский", Code = "ru" },
        new() { Display = "العربية", Code = "ar" },
        new() { Display = "Français", Code = "fr" },
        new() { Display = "Deutsch", Code = "de" },
    };

    public static LanguageOption FindLanguage(string code) =>
        Languages.FirstOrDefault(l => l.Code == code) ?? Languages.First(l => l.Code == "en");

    public static LanguageOption[] GetSourceLanguages() =>
        new[] { new LanguageOption { Display = UiStrings.Get("language.auto"), Code = "auto" } }
            .Concat(Languages)
            .ToArray();

    public static LanguageOption[] GetSecondaryLanguages() =>
        new[] { new LanguageOption { Display = UiStrings.Get("settings.activegame.none"), Code = "" } }
            .Concat(Languages).ToArray();

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "franca-settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    if (string.IsNullOrWhiteSpace(settings.TargetLanguage))
                        settings.TargetLanguage = DefaultLanguage();
                    if (string.IsNullOrWhiteSpace(settings.SourceLanguage))
                        settings.SourceLanguage = "en";
                    if (!Languages.Any(l => l.Code == settings.SecondaryLanguage))
                        settings.SecondaryLanguage = "";
                    if (string.IsNullOrWhiteSpace(settings.Endpoint))
                        settings.Endpoint = "BingTranslate";
                    // 端点可能已被移除（如本地大模型 LocalQwen*）：未知 id 回退自动免费池，
                    // 主用/备用不指向空气；Auto*（与 EndpointAutoService.IsAutoEndpoint 保持同步）、
                    // 内置目录与 custom: 端点视为已知
                    static bool KnownEndpoint(string id) => id.Length == 0
                        || id is "AutoFree" or "AutoByok" or "AutoLlm" or "AutoOffline"
                        || id.StartsWith("custom:", StringComparison.Ordinal)
                        || EndpointCatalog.Find(id) is not null;
                    if (!KnownEndpoint(settings.Endpoint)) settings.Endpoint = "AutoFree";
                    if (!KnownEndpoint(settings.FallbackEndpoint)) settings.FallbackEndpoint = "";
                    // 旧文件/非法值归一到 speed，保证查找链分流有确定语义
                    settings.TranslationMode = settings.QualityMode ? "quality" : "speed";
                    return settings;
                }
            }
        }
        catch
        {
            // 配置文件损坏时回退默认设置
        }

        return new AppSettings { TargetLanguage = DefaultLanguage() };
    }

    internal event Action? Saved;

    public AppSettings Snapshot(string? effectiveEndpoint = null)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
        copy.IsTransient = true;
        if (effectiveEndpoint is not null) copy.Endpoint = effectiveEndpoint;
        return copy;
    }

    public void Save()
    {
        Saved?.Invoke();
        if (IsTransient) return;
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 设置保存失败不影响主流程
        }
    }

    public static string ResolveSupportedLanguage(System.Globalization.CultureInfo? culture)
    {
        if (culture is null)
            return "en";

        // 繁体中文环境必须先判，否则会被两字母匹配错配成 zh-CN
        var name = culture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || culture.Parent.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase))
                return "zh-TW";
            return "zh-CN";
        }

        var exact = Languages.FirstOrDefault(l => string.Equals(l.Code, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.Code;
        var twoLetter = culture.TwoLetterISOLanguageName;
        return Languages.FirstOrDefault(l => string.Equals(l.Code, twoLetter, StringComparison.OrdinalIgnoreCase))?.Code ?? "en";
    }

    private static string DefaultLanguage() =>
        ResolveSupportedLanguage(System.Globalization.CultureInfo.CurrentUICulture);
}

// 用户自定义端点：OpenAI 兼容 或 自定义 GET 纯文本（XUnity CustomTranslate 语义）
public class CustomEndpointDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // openai / get
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string Group { get; set; } = ""; // Free / Byok / Llm / Offline
}
