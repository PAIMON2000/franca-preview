using System.IO;
using System.Text;
using System.Text.Json;

namespace Franca.Services;

// 翻译包存储（lab/004 格式 v1）：packs/<appid>/<lang>/entries/<hh>.jsonl
// 查找键 h = xxHash64(src 的 UTF-8 字节) 小写 16 位 hex，按首字节（h 前两位）分片
public sealed class PackStore
{
    private readonly string Root;
    public PackStore(string? root = null) => Root = root ?? Path.Combine(AppContext.BaseDirectory, "packs");

    public static bool IsValidLanguage(string language) => language.Length is > 0 and <= 35
        && language.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static void Validate(int appId, string language)
    {
        if (appId <= 0 || !IsValidLanguage(language)) throw new ArgumentException("Invalid pack identity");
    }
    private readonly object Sync = new();

    // 分片缓存："appId/lang/hh" → (h → tgt)；后加载的行覆盖先加载的（后写优先）
    private readonly Dictionary<string, Dictionary<string, string>> _shards = new();

    public static string HashOf(string src) => XxHash64.HashHex(src);

    // 命中返回译文；条目不存在或 tgt 为空（retry 草稿）都返回 null
    public string? Lookup(int appId, string lang, string src)
    {
        Validate(appId, lang);
        var h = HashOf(src);
        lock (Sync)
        {
            var shard = LoadShard(appId, lang, h[..2]);
            return shard.TryGetValue(h, out var tgt) && tgt.Length > 0 ? tgt : null;
        }
    }

    // 追加条目（st=draft）；retry=true 表示端点全挂、先记"已见未翻"（tgt 留空）
    public void Store(int appId, string lang, string src, string tgt, string by, bool retry)
    {
        Validate(appId, lang);
        var h = HashOf(src);
        lock (Sync)
        {
            var shard = LoadShard(appId, lang, h[..2]);
            // 已有非空译文且新条目没有提供更好内容时不重复写
            if (shard.TryGetValue(h, out var existing) && existing.Length > 0 && tgt.Length == 0)
                return;

            var dir = Path.Combine(Root, appId.ToString(), lang, "entries");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"{h[..2]}.jsonl"), ToJsonLine(h, src, tgt, by, retry) + "\n");
            shard[h] = tgt;
        }
    }

    // 枚举该游戏语言包的全部非空译文（启动前全量同步译文目录用）；按 src 排序，后写优先
    public List<KeyValuePair<string, string>> EnumerateEntries(int appId, string language)
    {
        Validate(appId, language);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var dir = Path.Combine(Root, appId.ToString(), language, "entries");
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var src = doc.RootElement.GetProperty("src").GetString();
                        var tgt = doc.RootElement.TryGetProperty("tgt", out var t) ? t.GetString() : null;
                        if (src is null || string.IsNullOrEmpty(tgt)) continue;
                        entries[src] = tgt;
                    }
                    catch
                    {
                        // 单行损坏不影响整包
                    }
                }
            }
        }
        return entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
    }

    private static string ToJsonLine(string h, string src, string tgt, string by, bool retry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("h", h);
            writer.WriteString("src", src);
            writer.WriteString("tgt", tgt);
            writer.WriteString("st", "draft");
            writer.WriteString("by", by);
            writer.WriteNumber("ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.WriteString("gv", ""); // 中继不知道游戏版本号，留空
            if (retry)
                writer.WriteBoolean("retry", true);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private Dictionary<string, string> LoadShard(int appId, string lang, string hh)
    {
        lock (Sync)
        {
            var key = $"{appId}/{lang}/{hh}";
            if (_shards.TryGetValue(key, out var cached))
                return cached;

            var shard = new Dictionary<string, string>();
            var path = Path.Combine(Root, appId.ToString(), lang, "entries", $"{hh}.jsonl");
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var h = doc.RootElement.GetProperty("h").GetString();
                        if (h is null)
                            continue;
                        shard[h] = doc.RootElement.TryGetProperty("tgt", out var tgt)
                            ? tgt.GetString() ?? ""
                            : "";
                    }
                    catch
                    {
                        // 单行损坏不影响整片
                    }
                }
            }
            _shards[key] = shard;
            return shard;
        }
    }
}
