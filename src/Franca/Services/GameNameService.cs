using System.IO;
using System.Net.Http;
using System.Text.Json;
using Franca.Models;

namespace Franca.Services;

// 游戏译名解析：Steam 官方译名优先，主用端点机翻兜底（有道/MyMemory 殿后），结果缓存到 exe 同目录
public class GameNameService
{
    private const string CacheFileName = "franca-names-cache.json";
    // 缓存条目版本：Load 时丢弃低版本 machine 条目，历史垃圾一次性出清
    private const int CurrentCacheVersion = 8;

    public class CacheEntry
    {
        public string Name { get; set; } = "";
        public string Source { get; set; } = ""; // official / machine / original / failed / en / none
        public string EnglishName { get; set; } = "";
        // 机翻结果来自哪个端点；主用端点变更时据此失效旧缓存
        public string SourceEndpoint { get; set; } = "";
        public int Version { get; set; }
        // Steam supported_languages 是否含目标语言（null=未查过）
        public bool? OfficialSupport { get; set; }
        // OfficialSupport 的查询时间（Unix 秒，0=未知）。官方支持数据按天缓存，过期重查
        public long SupportFetchedAt { get; set; }
        // support|appid 专用条目保存英文语言列表，可供主/第二语言共用；不保存机翻结果。
        public string? SupportedLanguages { get; set; }
    }

    // 官方语言支持数据的有效期（天）：游戏支持语种极少变化，无需每次启动重查
    private const int SupportCacheDays = 7;

    private static bool IsSupportStale(CacheEntry entry) =>
        entry.SupportFetchedAt <= 0
        || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - entry.SupportFetchedAt > SupportCacheDays * 86400L;

    private static readonly Dictionary<string, string> SteamLanguages = new()
    {
        ["zh-CN"] = "schinese", ["zh-TW"] = "tchinese", ["en"] = "english",
        ["ja"] = "japanese", ["ko"] = "koreana", ["pt-BR"] = "brazilian",
        ["es"] = "spanish", ["id"] = "indonesian", ["vi"] = "vietnamese",
        ["th"] = "thai", ["tr"] = "turkish", ["ru"] = "russian",
        ["ar"] = "arabic", ["fr"] = "french", ["de"] = "german",
    };

    private static readonly HttpClient Http = CreateClient();
    private static readonly Random Rng = new();
    private readonly HttpClient _steamHttp;

    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly HashSet<string> _failedThisRun = new();
    // 并行解析时保护 _cache / _failedThisRun / _resolvedSinceSave
    private readonly object _cacheLock = new();
    private int _resolvedSinceSave;
    private readonly string? _cacheFileName;
    private string CachePath => Path.Combine(AppContext.BaseDirectory, _cacheFileName ?? CacheFileName);

    private static HttpClient CreateClient()
    {
        // Steam 国内可直连（实测 1.2s），走系统代理（Clash）反而不稳：SSL EOF 频发
        var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Franca/0.1");
        return client;
    }

    // cacheFileName 为 null 时用默认缓存文件；无头探针传独立文件名，避免污染 GUI 缓存
    public GameNameService(string? cacheFileName = null, HttpClient? steamHttp = null)
    {
        _steamHttp = steamHttp ?? Http;
        _cacheFileName = cacheFileName;
        try
        {
            _cache = File.Exists(CachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath)) ?? new()
                : new();
        }
        catch
        {
            _cache = new();
        }

        // 旧版本（或无版本）的 machine 条目一次性丢弃；official/none/original 保留
        List<string> stale;
        lock (_cacheLock)
        {
            stale = _cache.Where(kv => kv.Value.Source == "machine" && kv.Value.Version < CurrentCacheVersion)
                .Select(kv => kv.Key).ToList();
            foreach (var key in stale)
                _cache.Remove(key);
        }
        if (stale.Count > 0)
            SaveCache();
    }

    private void SaveCache()
    {
        try
        {
            lock (_cacheLock)
                File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 缓存写盘失败不影响主流程
        }
    }

    internal static string CacheKey(GameInfo game, string sourceLanguage, string targetLanguage)
    {
        var languagePair = sourceLanguage == "en" ? targetLanguage : $"{sourceLanguage}>{targetLanguage}";
        return game.AppId is { } id ? $"{id}|{languagePair}" : $"dir|{game.Name}|{languagePair}";
    }

    // 主用端点变更后调用：来自其他端点的机翻条目降级为 "none"（保留"Steam 已查过无官译"的事实），
    // 译名列立刻跟随新主用且不再重复调 Steam API；official/original 条目保留
    public void InvalidateMachineNames(string keepEndpointId)
    {
        int changed;
        lock (_cacheLock)
        {
            changed = 0;
            foreach (var entry in _cache.Values)
                if (entry.Source == "machine" && entry.SourceEndpoint != keepEndpointId)
                {
                    entry.Source = "none";
                    changed++;
                }
        }
        if (changed > 0)
            SaveCache();
    }

    // 只使证据过期；网络失败时仍可显示上次已确认的结果。
    public void InvalidateOfficialSupport()
    {
        lock (_cacheLock)
        {
            // 手动刷新允许重试本次运行曾经失败的条目，不能被译名失败记录短路。
            _failedThisRun.Clear();
            foreach (var entry in _cache.Values)
            {
                entry.SupportFetchedAt = 0;
            }
        }
        SaveCache();
    }

    public void ApplyCachedNames(IReadOnlyList<GameInfo> games, string targetLanguage, string sourceLanguage)
    {
        lock (_cacheLock)
            foreach (var game in games)
                if (_cache.TryGetValue(CacheKey(game, sourceLanguage, targetLanguage), out var cached)
                    && cached.Source is "official" or "machine" or "original" && cached.Name.Length > 0)
                {
                    game.TranslatedName = cached.Name;
                    game.NameSource = cached.Source;
                    if (cached.Version >= CurrentCacheVersion)
                    {
                        var support = cached.OfficialSupport;
                        if (support is null && game.AppId is { } appId
                            && SteamSupportNames.TryGetValue(targetLanguage, out var supportName)
                            && _cache.TryGetValue($"support|{appId}", out var evidence)
                            && evidence.Version >= CurrentCacheVersion && evidence.SupportedLanguages is { } languages)
                            support = ParseOfficialSupport(languages, supportName);
                        SetOfficialSupport(game, targetLanguage, support);
                    }
                }
    }

    public async Task ResolveAsync(IReadOnlyList<GameInfo> games, string targetLanguage,
        string primaryEndpointId, IReadOnlyDictionary<string, Dictionary<string, string>> endpointKeys,
        Action<GameInfo> onUpdated, Action<string> log, CancellationToken ct,
        IReadOnlyList<CustomEndpointDef>? customEndpoints = null, string sourceLanguage = "en")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = games.Count;
        var done = 0;
        // 4 路并行：Steam API 与机翻端点都扛得住，QPS 仍然温和
        using var gate = new SemaphoreSlim(4);
        var tasks = games.Select(async game =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await ResolveOneGameAsync(game, targetLanguage, primaryEndpointId, endpointKeys, onUpdated, ct, customEndpoints, sourceLanguage);
            }
            finally
            {
                gate.Release();
                var finished = Interlocked.Increment(ref done);
                if (finished % 20 == 0)
                    log(UiStrings.Format("log.names.progress", finished, total));
            }
        });
        await Task.WhenAll(tasks);
        SaveCache();
        sw.Stop();
        log(UiStrings.Format("log.names.done", total, Math.Round(sw.Elapsed.TotalSeconds, 1)));
    }

    // 第二语言只查 Steam 官方支持证据。没有翻译端点参数，也不修改游戏译名/主目标语言。
    public Task ResolveSecondarySupportAsync(IReadOnlyList<GameInfo> games, string secondaryLanguage,
        Action<GameInfo, bool?> onUpdated, CancellationToken ct) =>
        ResolveOfficialSupportAsync(games, secondaryLanguage, onUpdated, ct);

    // 主/第二语言共用，只查询官方语言列表，不触发游戏名机翻。
    public async Task ResolveOfficialSupportAsync(IReadOnlyList<GameInfo> games, string language,
        Action<GameInfo, bool?> onUpdated, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(games.Select(async game =>
        {
            await gate.WaitAsync(ct);
            try
            {
                bool? support = null;
                if (game.AppId is { } appId && SteamSupportNames.TryGetValue(language, out var supportName))
                {
                    var evidence = await FetchSupportEvidenceAsync(appId, ct);
                    var refreshed = evidence is not null;
                    if (evidence is null)
                        lock (_cacheLock)
                            if (_cache.TryGetValue($"support|{appId}", out var cached)
                                && cached.Version >= CurrentCacheVersion)
                                evidence = cached.SupportedLanguages;
                    support = evidence is null ? null : ParseOfficialSupport(evidence, supportName);
                    if (refreshed)
                        lock (_cacheLock)
                            foreach (var (key, entry) in _cache)
                                if (key.StartsWith($"{appId}|", StringComparison.Ordinal)
                                    && (key.EndsWith($"|{language}", StringComparison.Ordinal)
                                        || key.EndsWith($">{language}", StringComparison.Ordinal)))
                                {
                                    entry.OfficialSupport = support;
                                    entry.SupportFetchedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                }
                }
                ct.ThrowIfCancellationRequested();
                onUpdated(game, support);
            }
            finally { gate.Release(); }
        }));
        SaveCache();
    }

    private async Task ResolveOneGameAsync(GameInfo game, string targetLanguage,
        string primaryEndpointId, IReadOnlyDictionary<string, Dictionary<string, string>> endpointKeys,
        Action<GameInfo> onUpdated, CancellationToken ct, IReadOnlyList<CustomEndpointDef>? customEndpoints,
        string sourceLanguage)
    {
        ct.ThrowIfCancellationRequested();
        var key = CacheKey(game, sourceLanguage, targetLanguage);

        // 无需翻译短路：由分段器按目标语言判断，避免把无需翻译的名字送入机翻管道
        // 放在缓存查询之前：旧缓存里命中这些规则的条目也按 original 重算覆盖
        if (NeedsNoTranslation(game.Name, targetLanguage))
        {
            CacheEntry? previous;
            lock (_cacheLock) _cache.TryGetValue(key, out previous);
            var support = previous?.OfficialSupport;
            var fetchedAt = previous?.SupportFetchedAt ?? 0;
            if (game.AppId is { } originalAppId && (previous is null || support is null
                || previous.Version < CurrentCacheVersion || IsSupportStale(previous)))
            {
                var (_, refreshedSupport) = await FetchSteamAppInfoAsync(originalAppId, targetLanguage, ct);
                ct.ThrowIfCancellationRequested();
                support = refreshedSupport ?? (previous?.Version >= CurrentCacheVersion
                    ? support ?? (game.OfficialSupportLanguage == targetLanguage ? game.HasOfficialSupport : null) : null);
                fetchedAt = refreshedSupport is not null ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0;
            }
            game.TranslatedName = game.Name;
            game.NameSource = "original";
            SetOfficialSupport(game, targetLanguage, support);
            bool shortCircuitSave;
            lock (_cacheLock)
            {
                _cache[key] = new CacheEntry
                    { Name = game.Name, Source = "original", EnglishName = game.Name, Version = CurrentCacheVersion,
                        OfficialSupport = support, SupportFetchedAt = fetchedAt };
                shortCircuitSave = ++_resolvedSinceSave >= 10;
                if (shortCircuitSave)
                    _resolvedSinceSave = 0;
            }
            if (shortCircuitSave)
                SaveCache();
            onUpdated(game);
            return;
        }

        var skipSteam = false;
        var cacheOnly = false;
        CacheEntry? backfillEntry = null;
        int? backfillAppId = null;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached) && !string.IsNullOrEmpty(cached.Name))
            {
                if (cached.Source is "official" or "machine" or "original")
                {
                    // 老缓存没有 OfficialSupport、版本过期或超过 SupportCacheDays：有 appid 就补查一次 Steam 回填
                    if ((cached.OfficialSupport is null || cached.Version < CurrentCacheVersion || IsSupportStale(cached)) && game.AppId is not null)
                    {
                        backfillEntry = cached;
                        backfillAppId = game.AppId;
                    }
                    else
                    {
                        game.TranslatedName = cached.Name;
                        game.NameSource = cached.Source;
                        SetOfficialSupport(game, targetLanguage, cached.OfficialSupport);
                        cacheOnly = true;
                    }
                }
                else
                {
                    // "none"：Steam 已查过且无官译，直接进机翻链；缺/过期 OfficialSupport 的旧条目重查一次
                    skipSteam = cached.Source == "none" && cached.OfficialSupport is not null
                        && cached.Version >= CurrentCacheVersion && !IsSupportStale(cached);
                }
            }
            if (backfillEntry is null && _failedThisRun.Contains(key))
                return;
        }

        // UI 回调不能持有缓存锁，否则 UI 同时读取缓存时可能相互等待。
        if (cacheOnly)
        {
            onUpdated(game);
            return;
        }

        if (backfillEntry is not null && backfillAppId is { } appId)
        {
            var (_, support) = await FetchSteamAppInfoAsync(appId, targetLanguage, ct);
            ct.ThrowIfCancellationRequested();
            lock (_cacheLock)
            {
                if (support is not null)
                {
                    backfillEntry.OfficialSupport = support;
                    backfillEntry.Version = CurrentCacheVersion;
                    backfillEntry.SupportFetchedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
                else if (backfillEntry.Version < CurrentCacheVersion)
                {
                    // v7 曾把商店译名当成语言支持证据，查询失败也不能继续沿用该推断。
                    backfillEntry.OfficialSupport = null;
                    backfillEntry.SupportFetchedAt = 0;
                }
            }
            game.TranslatedName = backfillEntry.Name;
            game.NameSource = backfillEntry.Source;
            SetOfficialSupport(game, targetLanguage, backfillEntry.OfficialSupport
                ?? (backfillEntry.Version >= CurrentCacheVersion && game.OfficialSupportLanguage == targetLanguage
                    ? game.HasOfficialSupport : null));
            onUpdated(game);
            return;
        }

        var (name, source, sourceEndpoint, officialSupport) = await ResolveOneAsync(game, targetLanguage,
            primaryEndpointId, endpointKeys, skipSteam, ct, customEndpoints, sourceLanguage);
        ct.ThrowIfCancellationRequested();
        game.TranslatedName = name;
        game.NameSource = source;
        SetOfficialSupport(game, targetLanguage, officialSupport);

        bool shouldSave;
        lock (_cacheLock)
        {
            _cache[key] = new CacheEntry { Name = name, Source = source, EnglishName = game.Name, SourceEndpoint = sourceEndpoint, Version = CurrentCacheVersion, OfficialSupport = officialSupport,
                SupportFetchedAt = officialSupport is not null ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0 };
            if (source == "failed")
                _failedThisRun.Add(key);
            // 每 10 款落一次盘，避免逐款写文件；结尾 ResolveAsync 再统一落一次
            shouldSave = ++_resolvedSinceSave >= 10;
            if (shouldSave)
                _resolvedSinceSave = 0;
        }
        if (shouldSave)
            SaveCache();
        onUpdated(game);
    }

    // 官方目标语言支持：有则填入本地化标注（"存在官方简体中文"），无/未知清空
    private static void SetOfficialSupport(GameInfo game, string targetLanguage, bool? officialSupport)
    {
        game.HasOfficialSupport = officialSupport;
        game.OfficialSupportLanguage = targetLanguage;
        game.OfficialSupportLabel = officialSupport == true
            ? UiStrings.FormatForLanguage(targetLanguage, "game.officialsupport", AppSettings.FindLanguage(targetLanguage).Display)
            : "";
    }

    private async Task<(string Name, string Source, string SourceEndpoint, bool? OfficialSupport)> ResolveOneAsync(GameInfo game,
        string targetLanguage, string primaryEndpointId, IReadOnlyDictionary<string, Dictionary<string, string>> endpointKeys,
        bool skipSteam, CancellationToken ct, IReadOnlyList<CustomEndpointDef>? customEndpoints, string sourceLanguage)
    {
        // 1. Steam 官方译名与官方语言支持（"none" 缓存表示已查过无官译，跳过）
        bool? officialSupport = null;
        if (skipSteam)
            lock (_cacheLock)
                if (_cache.TryGetValue(CacheKey(game, sourceLanguage, targetLanguage), out var cached))
                    officialSupport = cached.OfficialSupport;
        if (!skipSteam && game.AppId is { } appId)
        {
            var (officialName, support) = await FetchSteamAppInfoAsync(appId, targetLanguage, ct);
            officialSupport = support;
            // 商店译名不等于游戏内支持该语言，必须以 supported_languages 为准。
            if (officialName is not null && !NamesEqual(officialName, game.Name))
                return (officialName, "official", "", support);
        }

        // 2. 机翻兜底：按文字系统分段 → 逐段翻译 → 拼接；全段未译出则回原文
        var (machineName, machineEndpoint) = await TranslateBySegmentsAsync(game.Name, targetLanguage,
            primaryEndpointId, endpointKeys, ct, customEndpoints, sourceLanguage);
        // 全部兜底失败：回原文英文名并写缓存，避免每次启动重复请求垃圾结果
        return machineName is not null
            ? (machineName, "machine", machineEndpoint, officialSupport)
            : (game.Name, "original", "", officialSupport);
    }

    // 分段翻译：主用端点优先，段内降级 有道→MyMemory，再失败该段保留原文（段级失败开放）
    private async Task<(string? Name, string Endpoint)> TranslateBySegmentsAsync(string name, string targetLanguage,
        string primaryEndpointId, IReadOnlyDictionary<string, Dictionary<string, string>> endpointKeys, CancellationToken ct,
        IReadOnlyList<CustomEndpointDef>? customEndpoints, string sourceLanguage)
    {
        var segments = NameSegmenter.Segment(name, targetLanguage);
        var chain = MachineChain(primaryEndpointId).ToList();
        var builder = new System.Text.StringBuilder();
        var translatedEndpoint = "";
        var anyTranslated = false;

        foreach (var segment in segments)
        {
            if (!segment.Translate || segment.SourceLanguage is null)
            {
                builder.Append(segment.Text);
                continue;
            }

            string? translated = null;
            var segmentSource = sourceLanguage == "auto" ? segment.SourceLanguage! : sourceLanguage;
            foreach (var endpointId in chain)
            {
                var result = await TryEndpointAsync(endpointId, segment.Text, segmentSource,
                    targetLanguage, endpointKeys, ct, customEndpoints);
                if (!IsGarbageTranslation(segment.Text, result))
                {
                    translated = result;
                    if (translatedEndpoint == "")
                        translatedEndpoint = endpointId;
                    break;
                }
            }
            // 拉丁段短语失败（品牌名常被原样返回）→ 辅助分段：拆驼峰后逐词降级
            if (translated is null && segment.Script == NameScript.Latin)
            {
                var (byWords, byWordsEndpoint) = await TranslateLatinByWordsAsync(segment.Text,
                    targetLanguage, chain, endpointKeys, ct, customEndpoints, sourceLanguage);
                if (byWords is not null)
                {
                    translated = byWords;
                    if (translatedEndpoint == "")
                        translatedEndpoint = byWordsEndpoint ?? "";
                }
            }
            builder.Append(translated ?? segment.Text);
            anyTranslated |= translated is not null;
        }

        var assembled = builder.ToString();
        // 拼完与原文相同视为未翻（全部段保留原文或翻译结果等于原文）
        return anyTranslated && !string.Equals(assembled, name, StringComparison.Ordinal)
            ? (assembled, translatedEndpoint)
            : (null, "");
    }

    // 拉丁段辅助分段：短语整翻失败时，拆驼峰（MarvelRivals→Marvel Rivals）后按词逐词翻译，
    // 翻过和没翻过的词按序拼接；有任何词被翻译才产出，否则返回 null 保留原段
    private async Task<(string? Text, string? Endpoint)> TranslateLatinByWordsAsync(string text, string targetLanguage,
        List<string> chain, IReadOnlyDictionary<string, Dictionary<string, string>> endpointKeys, CancellationToken ct,
        IReadOnlyList<CustomEndpointDef>? customEndpoints, string sourceLanguage)
    {
        var words = SplitCamelCase(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
            return (null, null);

        var builder = new System.Text.StringBuilder();
        string? usedEndpoint = null;
        var anyTranslated = false;
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0)
                builder.Append(' ');
            var word = words[i];
            if (IsKeptWord(word))
            {
                builder.Append(word);
                continue;
            }

            string? translated = null;
            foreach (var endpointId in chain)
            {
                var result = await TryEndpointAsync(endpointId, word,
                    sourceLanguage == "auto" ? "en" : sourceLanguage,
                    targetLanguage, endpointKeys, ct, customEndpoints);
                if (!IsGarbageTranslation(word, result))
                {
                    translated = result;
                    usedEndpoint ??= endpointId;
                    break;
                }
            }
            builder.Append(translated ?? word);
            anyTranslated |= translated is not null;
        }
        return anyTranslated ? (builder.ToString(), usedEndpoint) : (null, null);
    }

    // 驼峰拆分：小写→大写边界插空格；全大写词、含数字词不动
    private static string SplitCamelCase(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + 4);
        foreach (var word in text.Split(' '))
        {
            if (builder.Length > 0)
                builder.Append(' ');
            if (word.Any(char.IsDigit) || word.All(c => !char.IsLower(c)))
            {
                builder.Append(word);
                continue;
            }
            for (var i = 0; i < word.Length; i++)
            {
                if (i > 0 && char.IsUpper(word[i]) && char.IsLower(word[i - 1]))
                    builder.Append(' ');
                builder.Append(word[i]);
            }
        }
        return builder.ToString();
    }

    // 单字母词、全大写 ≤4 字母缩写词、含数字词保留原文不翻
    private static bool IsKeptWord(string word)
    {
        if (word.Length <= 1 || word.Any(char.IsDigit))
            return true;
        var letters = word.Where(char.IsLetter).ToArray();
        return letters.Length <= 4 && letters.All(char.IsUpper);
    }

    private static IEnumerable<string> MachineChain(string primaryEndpointId)
    {
        yield return primaryEndpointId;
        if (primaryEndpointId != "YoudaoWebTranslate")
            yield return "YoudaoWebTranslate";
        if (primaryEndpointId != "MyMemoryTranslate")
            yield return "MyMemoryTranslate";
    }

    // 无需翻译判定：命中即直接显示原名，不进任何翻译管道
    private static bool NeedsNoTranslation(string name, string targetLanguage) =>
        // 分段器已完整表达"哪段该翻、哪段保留"：没有任何待翻段 = 无需翻译
        // （中文名对中文用户不翻、对英文用户翻；拉丁名对英文用户不翻——全部按目标语言相对判定）
        !NameSegmenter.Segment(name, targetLanguage).Any(s => s.Translate);

    // 质量护栏：机翻结果命中任一条视为垃圾，跳去下一个兜底
    public static bool IsGarbageTranslation(string original, string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return true;
        // 与原文相同视为未翻
        if (string.Equals(result.Trim(), original.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        // 原文不含数字而结果含数字（"Blender → 1996年总统选举" 这类众包垃圾）
        if (result.Any(char.IsDigit) && !original.Any(char.IsDigit))
            return true;
        // 短名字返回长句必有问题
        if (result.Length > Math.Max(original.Length * 2.5, original.Length + 20))
            return true;
        // 原文不含问号而结果含问号：游戏名极少带问号，问句基本是论坛/众库垃圾
        if ((result.Contains('?') || result.Contains('？')) && !original.Contains('?') && !original.Contains('？'))
            return true;
        // 原文无句读而译文以句读收尾：游戏名极少以句读结尾，完整句子基本是 LLM 抽风
        // （"The Witcher" → "我曾亲眼目睹过许多次的杀狗。"）
        // 半角 .!? 额外要求原文同样没有，避免误伤 "S.T.A.L.K.E.R." 这类本身带点的名字
        var trimmed = result.TrimEnd();
        if (trimmed.Length > 0)
        {
            var originalHasFullWidthStop = original.Contains('。') || original.Contains('！') || original.Contains('？');
            if (trimmed[^1] is '。' or '！' or '？' && !originalHasFullWidthStop)
                return true;
            if (trimmed[^1] is '.' or '!' or '?' && !originalHasFullWidthStop
                && !original.Contains('.') && !original.Contains('!') && !original.Contains('?'))
                return true;
        }
        return false;
    }

    private static async Task<string?> TryEndpointAsync(string endpointId, string text, string sourceLanguage,
        string targetLanguage, IReadOnlyDictionary<string, Dictionary<string, string>> endpointKeys, CancellationToken ct,
        IReadOnlyList<CustomEndpointDef>? customEndpoints)
    {
        await ThrottleAsync(ct);
        if (endpointId.StartsWith("custom:", StringComparison.Ordinal))
        {
            var def = customEndpoints?.FirstOrDefault(d => d.Id == endpointId);
            return def is null
                ? null
                : await AppTranslator.TranslateCustomAsync(def, text, targetLanguage, ct, sourceLanguage);
        }
        var keys = endpointKeys.TryGetValue(endpointId, out var saved) ? saved : null;
        return await AppTranslator.TranslateAsync(endpointId, text, targetLanguage, keys, ct, sourceLanguage);
    }

    private static bool NamesEqual(string a, string b) =>
        string.Equals(NormalizeName(a), NormalizeName(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string name) =>
        name.Replace("™", "").Replace("®", "").Trim();

    private static async Task ThrottleAsync(CancellationToken ct) =>
        await Task.Delay(Rng.Next(100, 200), ct);

    private async Task<string?> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await ThrottleAsync(ct);
                return await _steamHttp.GetStringAsync(url, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 网络抖动/超时（HttpClient.Timeout 的 TaskCanceled 也走这里），重试一次后放弃
            }
        }
        return null;
    }

    // supported_languages 随 l 参数本地化（l=schinese 返回"简体中文"而非 schinese 代码），
    // 必须用 l=english 的响应 + 英文显示名匹配，本地化响应无法用于判定
    private static readonly Dictionary<string, string> SteamSupportNames = new()
    {
        ["zh-CN"] = "Simplified Chinese", ["zh-TW"] = "Traditional Chinese", ["en"] = "English",
        ["ja"] = "Japanese", ["ko"] = "Korean", ["pt-BR"] = "Portuguese - Brazil",
        ["es"] = "Spanish - Spain", ["id"] = "Indonesian", ["vi"] = "Vietnamese",
        ["th"] = "Thai", ["tr"] = "Turkish", ["ru"] = "Russian",
        ["ar"] = "Arabic", ["fr"] = "French", ["de"] = "German",
    };

    private async Task<(string? Name, bool? OfficialSupport)> FetchSteamAppInfoAsync(int appId, string targetLanguage, CancellationToken ct)
    {
        if (!SteamLanguages.TryGetValue(targetLanguage, out var steamLang))
            return (null, null);
        // 注意：加 filters=name 会让该接口返回空 data（Steam API 现状），只能取全量再取 name
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={steamLang}";
        var json = await GetWithRetryAsync(url, ct);
        if (json is null)
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var appNode = doc.RootElement.GetProperty(appId.ToString());
            if (!appNode.GetProperty("success").GetBoolean())
                return (null, null);
            var data = appNode.GetProperty("data");
            // supported_languages 必须取英文版响应判定（本地化版本文字随 l 变化，无法匹配）
            bool? support = null;
            if (SteamSupportNames.TryGetValue(targetLanguage, out var supportName))
            {
                var evidence = await FetchSupportEvidenceAsync(appId, ct, steamLang == "english" ? json : null);
                if (evidence is not null) support = ParseOfficialSupport(evidence, supportName);
            }
            return (data.GetProperty("name").GetString(), support);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<string?> FetchSupportEvidenceAsync(int appId, CancellationToken ct, string? knownJson = null)
    {
        var key = $"support|{appId}";
        lock (_cacheLock)
            if (knownJson is null && _cache.TryGetValue(key, out var cached)
                && cached.SupportedLanguages is not null && cached.Version >= CurrentCacheVersion && !IsSupportStale(cached))
                return cached.SupportedLanguages;
        var json = knownJson ?? await GetWithRetryAsync(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english", ct);
        ct.ThrowIfCancellationRequested();
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var app = doc.RootElement.GetProperty(appId.ToString());
            if (!app.GetProperty("success").GetBoolean()
                || !app.GetProperty("data").TryGetProperty("supported_languages", out var languages)
                || languages.ValueKind != JsonValueKind.String) return null;
            var evidence = languages.GetString();
            lock (_cacheLock) _cache[key] = new CacheEntry
            {
                Source = "support-evidence", SupportedLanguages = evidence, Version = CurrentCacheVersion,
                SupportFetchedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            return evidence;
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // supported_languages 是 HTML 串（带 <strong>*</strong> 标记音频语言），去标签后按逗号分词匹配
    // 注意 * 字符本身会残留在 token 尾部（"Simplified Chinese*"），必须一并去掉；
    // <br> 换行（音频图例与语言列表的分隔）须先换成逗号再剥标签，否则图例会粘在最后一个语言上
    // （单语言游戏如"拖拖拉拉小菲镇"：去标签后变成 "Simplified Chineselanguages with full audio support"，误判无官中）
    private static bool ParseOfficialSupport(string html, string supportName)
    {
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(html, "<br\\s*/?>", ",");
        var plain = System.Text.RegularExpressions.Regex.Replace(withBreaks, "<[^>]+>", "").Replace("*", "");
        return plain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, supportName, StringComparison.OrdinalIgnoreCase));
    }
}
