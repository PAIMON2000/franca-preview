using System.IO;

namespace Franca.Services;

// 已安装不等于可翻译。此服务只检查可从磁盘确定的健康状态；
// 它不把网络端点探测或“真实游戏内已显示译文”误报为通过。
public static class TranslationHealthService
{
    public sealed record Issue(string Code, string Detail);

    public sealed record Report(IReadOnlyList<Issue> Issues)
    {
        public bool IsHealthy => Issues.Count == 0;
        public string Summary => IsHealthy ? "配置健康" : string.Join("；", Issues.Select(i => i.Detail));
    }

    public static Report Inspect(string gameDir, AppSettings settings, string? expectedRelayUrl)
    {
        var issues = new List<Issue>();
        if (InstallationTransaction.HasPending(gameDir))
            issues.Add(new("installation-incomplete", "安装事务未完成或待恢复，请重新启用并检查事务备份"));
        if (!TranslationInstaller.IsReady(gameDir))
            issues.Add(new("runtime-missing", "BepInEx 运行环境不完整"));

        var path = ConfigPath(gameDir);
        if (!File.Exists(path))
        {
            issues.Add(new("config-missing", "缺少 AutoTranslatorConfig.ini"));
            return new Report(issues);
        }

        var values = ReadIni(path);
        var expectedLanguage = AppSettings.FindLanguage(settings.TargetLanguage).EffectiveXUnityCode;
        Check(values, "General", "Language", expectedLanguage, "target-language", "目标语言", issues);
        Check(values, "General", "FromLanguage", settings.SourceLanguage, "source-language", "源语言", issues);

        // 中继已运行时，游戏必须明确指向中继；否则可能悄悄退化为不稳定的直连网页端点。
        if (!string.IsNullOrWhiteSpace(expectedRelayUrl))
        {
            Check(values, "Service", "Endpoint", "CustomTranslate", "relay-endpoint", "翻译端点", issues);
            Check(values, "Custom", "Url", expectedRelayUrl, "relay-url", "本机中继地址", issues);
            Check(values, "Behaviour", "MaxCharactersPerTranslation",
                TranslationTextSegmenter.MaxTextLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "text-length", "单次文本上限", issues);
            Check(values, "Behaviour", "IgnoreWhitespaceInDialogue", "False",
                "paragraph-whitespace", "忽略段落空白", issues);
            Check(values, "Behaviour", "ReloadTranslationsOnFileChange", "True",
                "reload-on-change", "译文文件变更重载", issues);
        }

        return new Report(issues);
    }

    public static string Repair(string gameDir, AppSettings settings, string? relayUrl, Action<string> log)
    {
        return TranslationInstaller.RepairConfiguration(gameDir, settings, relayUrl, log);
    }

    public static string ConfigPath(string gameDir) =>
        Path.Combine(gameDir, "BepInEx", "config", "AutoTranslatorConfig.ini");

    private static void Check(IReadOnlyDictionary<string, string> values, string section, string key,
        string expected, string code, string label, List<Issue> issues)
    {
        var fullKey = $"{section}/{key}";
        values.TryGetValue(fullKey, out var actual);
        if (!string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            issues.Add(new(code, $"{label}应为 {expected}，当前为 {actual ?? "(缺失)"}"));
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0 || section.Length == 0)
                continue;
            values[$"{section}/{line[..separator].Trim()}"] = line[(separator + 1)..].Trim();
        }
        return values;
    }
}
