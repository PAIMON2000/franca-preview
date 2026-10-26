using System.Text;

namespace Franca.Services;

public enum NameScript
{
    Latin,
    Cjk,
    Kana,
    Hangul,
    Cyrillic,
    Arabic,
    Other
}

// Translate=false 的段原样保留；SourceLanguage 为机翻源语言码（BCP-47）
public record NameSegment(NameScript Script, string Text, bool Translate, string? SourceLanguage);

// 游戏名按 Unicode 文字系统分段：Other（数字/空白/标点/符号）永不翻译；
// 已是目标文字的段保留原文；拉丁缩写与单字母保留原文
public static class NameSegmenter
{
    public static List<NameSegment> Segment(string name, string targetLanguage)
    {
        var segments = new List<NameSegment>();
        var current = new StringBuilder();
        var currentScript = NameScript.Other;

        for (var i = 0; i < name.Length; i++)
        {
            var script = ScriptAt(name, i);
            if (current.Length > 0 && script != currentScript)
            {
                segments.Add(MakeSegment(currentScript, current.ToString(), targetLanguage));
                current.Clear();
            }
            currentScript = script;
            current.Append(name[i]);
        }
        if (current.Length > 0)
            segments.Add(MakeSegment(currentScript, current.ToString(), targetLanguage));

        // 仅以空白相隔的相邻拉丁词合并为短语段：逐词翻译会丢上下文
        // （"Wild Hunt" 逐词 → "野生 亨特"，整段 → "狂猎"）
        for (var i = 0; i + 2 < segments.Count;)
        {
            if (segments[i].Script == NameScript.Latin
                && segments[i + 1].Script == NameScript.Other
                && string.IsNullOrWhiteSpace(segments[i + 1].Text)
                && segments[i + 2].Script == NameScript.Latin)
            {
                var merged = segments[i].Text + segments[i + 1].Text + segments[i + 2].Text;
                segments[i] = MakeSegment(NameScript.Latin, merged, targetLanguage);
                segments.RemoveRange(i + 1, 2);
            }
            else
            {
                i++;
            }
        }
        return segments;
    }

    // 撇号/连字符两侧都是拉丁字母时并入 Latin 段（Half-Life、Rock'n'Roll 不被劈开）
    private static NameScript ScriptAt(string name, int index)
    {
        var c = name[index];
        if (c is '\'' or '-' && index > 0 && index + 1 < name.Length
            && IsLatin(name[index - 1]) && IsLatin(name[index + 1]))
            return NameScript.Latin;
        return ScriptOf(c);
    }

    private static bool IsLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static NameScript ScriptOf(char c) => c switch
    {
        >= 'A' and <= 'Z' or >= 'a' and <= 'z' => NameScript.Latin,
        >= '一' and <= '鿿' => NameScript.Cjk,
        >= '぀' and <= 'ゟ' or >= '゠' and <= 'ヿ' => NameScript.Kana,
        >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' => NameScript.Hangul,
        >= 'Ѐ' and <= 'ӿ' => NameScript.Cyrillic,
        >= '؀' and <= 'ۿ' => NameScript.Arabic,
        _ => NameScript.Other
    };

    private static NameSegment MakeSegment(NameScript script, string text, string targetLanguage) => script switch
    {
        NameScript.Other => new(script, text, false, null),
        // 拉丁名对英文用户保持原名（文字系统级无法区分英语与其他拉丁文字语言，
        // 而英译英必然翻车，保持原名是两害相权）
        NameScript.Latin when targetLanguage == "en" => new(script, text, false, null),
        NameScript.Latin => new(script, text, !IsKeptLatin(text), IsKeptLatin(text) ? null : "en"),
        // 已是目标文字的段保留原文
        NameScript.Cjk when targetLanguage is "zh-CN" or "zh-TW" or "ja" => new(script, text, false, null),
        NameScript.Cjk => new(script, text, true, "zh-CN"),
        NameScript.Kana => new(script, text, true, "ja"),
        NameScript.Hangul when targetLanguage == "ko" => new(script, text, false, null),
        NameScript.Hangul => new(script, text, true, "ko"),
        NameScript.Cyrillic when targetLanguage == "ru" => new(script, text, false, null),
        NameScript.Cyrillic => new(script, text, true, "ru"),
        NameScript.Arabic when targetLanguage == "ar" => new(script, text, false, null),
        NameScript.Arabic => new(script, text, true, "ar"),
        _ => new(script, text, false, null)
    };

    // 全大写缩写（≤4 字母，HD/VR/GTA/F1）与单字母保留原文
    private static bool IsKeptLatin(string text)
    {
        var letters = text.Count(IsLatin);
        return letters <= 1 || letters <= 4 && text.Where(IsLatin).All(char.IsUpper);
    }
}
