using System.IO;
using System.Text;
using System.Text.Json;

namespace Franca.Services;

// 全局内置词典（lab/010）：跨游戏、按语言对存储 dictionaries/<from>-<to>.json
// 查找：key 归一化（Trim 两端空白；OrdinalIgnoreCase 精确匹配），命中立即返回
// 内置种子与沉淀条目同文件，条目带 source 标记（builtin/promoted），后写优先
// 沉淀：TryPromote 追加（已存在不覆盖），异步落盘、失败仅记 AppLog
public sealed class DictionaryService
{
    // 沉淀长度上限（lab/010 决策 5）：仅短文本进词典，长文本/人名/台词只进游戏包
    public const int MaxPromoteLength = 30;

    private readonly string Root;
    public DictionaryService(string? root = null) => Root = root ?? Path.Combine(AppContext.BaseDirectory, "dictionaries");

    private sealed class Entry
    {
        public required string Src;
        public required string Tgt;
        public required string Source;
    }

    private sealed class PairState
    {
        public Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, PairState> _pairs = new(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string text) => text.Trim();
    private static bool ValidPair(string from, string to) =>
        PackStore.IsValidLanguage(from) && PackStore.IsValidLanguage(to);
    private static string PathFor(string root, string from, string to) =>
        Path.Combine(root, $"{from}-{to}.json");

    // 词典命中返回译文；未命中/空 key/非法语言对返回 null
    public string? Lookup(string from, string to, string text)
    {
        var key = Normalize(text);
        if (key.Length == 0 || !ValidPair(from, to)) return null;
        lock (_sync)
        {
            return LoadPair(from, to).Entries.TryGetValue(key, out var entry) ? entry.Tgt : null;
        }
    }

    // 在线翻译成功后沉淀短文本；词典 key 已存在（builtin 或 promoted）不覆盖。
    // 内存即时生效，落盘异步 fire-and-forget，失败仅记日志。
    public bool TryPromote(string from, string to, string src, string tgt)
    {
        var key = Normalize(src);
        if (key.Length == 0 || tgt.Length == 0 || !ValidPair(from, to)) return false;
        string line;
        lock (_sync)
        {
            var pair = LoadPair(from, to);
            if (pair.Entries.ContainsKey(key)) return false;
            pair.Entries[key] = new Entry { Src = key, Tgt = tgt, Source = "promoted" };
            line = ToJsonLine(key, tgt, "promoted");
        }
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.AppendAllText(PathFor(Root, from, to), line + "\n");
            }
            catch (Exception ex)
            {
                AppLog.Write($"dictionary promote flush failed: {ex.Message}");
            }
        });
        return true;
    }

    // 首次访问加载语言对：磁盘文件优先（无 source 字段的旧行视为 promoted），
    // 无文件则植入内置种子并同步落盘一次（量小、每语言对仅一次，失败不影响内存词条）。
    private PairState LoadPair(string from, string to)
    {
        var pairKey = $"{from}|{to}";
        if (_pairs.TryGetValue(pairKey, out var existing)) return existing;

        var state = new PairState();
        var path = PathFor(Root, from, to);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var src = doc.RootElement.GetProperty("src").GetString();
                    var tgt = doc.RootElement.GetProperty("tgt").GetString();
                    if (src is null || tgt is null) continue;
                    var source = doc.RootElement.TryGetProperty("by", out var by) ? by.GetString() ?? "promoted" : "promoted";
                    state.Entries[Normalize(src)] = new Entry { Src = Normalize(src), Tgt = tgt, Source = source };
                }
                catch
                {
                    // 单行损坏不影响整对
                }
            }
        }
        else if (DictionarySeeds.TryGet(from, to) is { } seed)
        {
            foreach (var (src, tgt) in seed)
                state.Entries[src] = new Entry { Src = src, Tgt = tgt, Source = "builtin" };
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(path, string.Join("\n", seed.Select(s => ToJsonLine(s.Key, s.Value, "builtin"))) + "\n");
            }
            catch (Exception ex)
            {
                AppLog.Write($"dictionary seed flush failed: {ex.Message}");
            }
        }
        _pairs[pairKey] = state;
        return state;
    }

    private static string ToJsonLine(string src, string tgt, string source)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("src", src);
            writer.WriteString("tgt", tgt);
            writer.WriteString("by", source);
            writer.WriteNumber("ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
