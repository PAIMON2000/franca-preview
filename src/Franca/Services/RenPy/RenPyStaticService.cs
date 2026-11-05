using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Franca.Services.RenPy;

// Preparation only. Never writes into a game directory.
// Emits the official string-translation overlay: game/tl/<lang>/ with a single
// `translate <lang> strings:` block (Ren'Py applies it to dialogue that has no
// translate block), a glue script forcing config.language and a CJK font, and
// a manifest consumed by RenPyInstallService.
public sealed class RenPyStaticService
{
    public sealed record Report(string OutputDirectory, string Language, int UniqueTexts,
        int Translated, int Missing, int Cached, bool Incomplete);

    private readonly StaticTextTranslationService _translator;
    private readonly string _packsRoot;

    public RenPyStaticService(string packsRoot,
        Func<string, string, string, string, AppSettings, CancellationToken, Task<string?>>? provider = null)
    {
        _packsRoot = packsRoot;
        _translator = new StaticTextTranslationService(new PackStore(packsRoot), provider);
    }

    // Ren'Py engine-builtin UI strings (shipped in renpy/common, not in any game
    // .rpyc, so the extractor never sees them). Only zh-CN entries for now.
    private static readonly IReadOnlyDictionary<string, string> EngineBuiltinStringsZh =
        new Dictionary<string, string>
        {
            ["empty slot"] = "空槽位",
        };

    // Ren'Py language identifiers must be valid Python identifiers.
    public static string LanguageDirectory(string targetLanguage) => targetLanguage switch
    {
        "zh-CN" => "zh_cn",
        "zh-TW" => "zh_tw",
        "ja" => "japanese",
        _ => targetLanguage.ToLowerInvariant().Replace('-', '_'),
    };

    public static string StringsHash(IEnumerable<string> texts)
    {
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();
        foreach (var text in texts)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes);
            stream.WriteByte(0x0a);
        }
        stream.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    public async Task<Report> PrepareAsync(string gameDirectory, int appId, string outputRoot, AppSettings settings,
        IProgress<StaticTextTranslationService.StaticTranslationProgress>? progress, CancellationToken ct,
        string? fontFile = null)
    {
        var gameRoot = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        RejectReparseAncestors(gameRoot);
        RequireOutsideGame(gameRoot, output);
        RequireOutsideGame(gameRoot, _packsRoot);
        if (appId <= 0 || settings.SourceLanguage != "en" || settings.TargetLanguage is not ("zh-CN" or "ja"))
            throw new NotSupportedException("Experimental preparation currently supports English -> zh-CN or ja");

        var scan = RenPyExtractService.Scan(gameDirectory);
        if (scan.Scripts.Count == 0)
            throw new InvalidDataException("No Ren'Py compiled scripts found under the game directory.");
        var strings = RenPyExtractService.ExtractStrings(scan);
        var texts = strings.Select(s => s.Text).Where(t => t.Trim().Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var sourceHash = StringsHash(texts);

        var result = await _translator.TranslateAsync(appId, texts, settings.Snapshot(), freeOnly: true, progress, ct);
        var language = LanguageDirectory(settings.TargetLanguage);
        var translated = result.Translations;

        var stage = Path.Combine(output, "stage");
        var tlDir = Path.Combine(stage, "game", "tl", language);
        Directory.CreateDirectory(tlDir);

        WriteStringsRpy(Path.Combine(tlDir, "strings.rpy"), language, texts, translated);
        File.WriteAllText(Path.Combine(tlDir, "franca.rpy"), $"""
            # Franca translation glue. Generated file; reinstall instead of editing.
            init python:
                config.language = "{language}"

            translate {language} style default:
                font "tl/{language}/NotoSansSC.ttf"

            translate {language} python:
                style.default.font = "tl/{language}/NotoSansSC.ttf"

            init 9999 python:
                # Styles with an EXPLICIT font (game UI fonts, poem fonts) do
                # not inherit style.default; force our CJK font onto every
                # registered style AND onto every Text constructor call, so
                # text rendered through late-created styles or direct font=
                # arguments (e.g. single-choice menus) still shows CJK.
                try:
                    import renpy.style as _franca_style_mod
                    for _s in list(_franca_style_mod.styles.values()):
                        try:
                            _s.font = "tl/{language}/NotoSansSC.ttf"
                        except Exception:
                            pass
                except Exception:
                    pass
                try:
                    import renpy.text.text as _franca_text_mod
                    _franca_text_orig_init = _franca_text_mod.Text.__init__
                    def _franca_text_patched_init(self, text, *args, **kwargs):
                        kwargs["font"] = "tl/{language}/NotoSansSC.ttf"
                        _franca_text_orig_init(self, text, *args, **kwargs)
                    _franca_text_mod.Text.__init__ = _franca_text_patched_init
                except Exception:
                    pass

            """);

        var font = fontFile ?? Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thcrap", "fonts", "NotoSansSC.ttf");
        if (!File.Exists(font)) throw new FileNotFoundException("CJK font for the translation overlay not found.", font);
        File.Copy(font, Path.Combine(tlDir, "NotoSansSC.ttf"), overwrite: true);

        var manifest = new Dictionary<string, object?>
        {
            ["Status"] = "PreparedNotInstalled",
            ["Engine"] = "renpy",
            ["AppId"] = appId,
            ["Language"] = language,
            ["SourceStringsSha256"] = sourceHash,
            ["UniqueTexts"] = texts.Length,
            ["Translated"] = translated.Count,
            ["Missing"] = result.Missing,
        };
        File.WriteAllText(Path.Combine(output, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return new Report(output, language, texts.Length, translated.Count, result.Missing, result.Cached,
            result.Missing > 0);
    }

    internal static void WriteStringsRpy(string path, string language, IReadOnlyList<string> texts,
        IReadOnlyDictionary<string, string> translated)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("# Franca machine translation. Generated file; reinstall instead of editing.");
        writer.WriteLine($"translate {language} strings:");
        writer.WriteLine();
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            if (!translated.TryGetValue(text, out var target) || string.IsNullOrEmpty(target) || target == text)
                continue; // untranslated: absence is equivalent to identity
            writer.WriteLine($"    old {Quote(text)}");
            writer.WriteLine($"    new {Quote(target)}");
            writer.WriteLine();
            written.Add(text);
        }
        if (string.Equals(language.Replace('_', '-'), "zh-CN", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kv in EngineBuiltinStringsZh)
            {
                if (!written.Add(kv.Key))
                    continue; // extractor already emitted its own translation
                writer.WriteLine($"    old {Quote(kv.Key)}");
                writer.WriteLine($"    new {Quote(kv.Value)}");
                writer.WriteLine();
            }
        }
    }

    internal static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void RequireOutsideGame(string gameRoot, string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase) || gameRoot.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Preparation output and caches must be outside the game directory");
        RejectReparseAncestors(full);
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Game, output and cache directories cannot traverse a reparse point");
    }
}
