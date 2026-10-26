using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

// Dictionary dual-mode checks (lab/010 slice 1): temp dictionaries/packs and loopback mock HTTP only.
// "立即返回"用阻塞型 mock 验证：响应完成时在线端点仍被闸门挡住，证明未等待在线。
internal static class DictionaryProbe
{
    public static async Task RunAsync()
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tDICTIONARY\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition) failures++;
        }

        var root = Path.Combine(Path.GetTempPath(), "franca-dictionary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dictionaries = new DictionaryService(Path.Combine(root, "dictionaries"));
            var packs = new PackStore(Path.Combine(root, "packs"));
            using var mock = new MockProvider();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

            AppSettings published = Settings(speed: true, noOptimize: false);

            static AppSettings Settings(bool speed, bool noOptimize) => new()
            {
                TargetLanguage = "zh-CN", SourceLanguage = "en", IsTransient = true,
                TranslationMode = speed ? "speed" : "quality",
                NoOptimizeAfterGenerate = noOptimize,
            };

            async Task<(string?, string?)> Translate(string text, string from, string to, AppSettings captured, CancellationToken ct)
            {
                if (text == "unavailable") return (null, null); // 模拟端点池全挂
                var body = await http.GetStringAsync(
                    $"{mock.Url}get?from={from}&to={to}&text={Uri.EscapeDataString(text)}", ct);
                return (body, "mock");
            }

            using var relay = new RelayServer(() => Volatile.Read(ref published), packs, Translate, dictionaries);
            await relay.EnsureStartedAsync();
            Task<string> Get(int appId, string text) => http.GetStringAsync(
                $"{relay.UrlForGame(appId)}?from=en&to=zh-CN&text={Uri.EscapeDataString(text)}");

            // 线程安全 + 去重：并发沉淀同一 key 只允许一个胜出
            var promoted = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                dictionaries.TryPromote("en", "zh-CN", "ConcurrentWord", "并发词"))));
            Check("promote_concurrent_single_winner", promoted.Count(x => x) == 1
                && dictionaries.Lookup("en", "zh-CN", "ConcurrentWord") == "并发词");

            // 内置种子命中返回词典译文；种子文件带 builtin 标记落盘
            var seed = await Get(501, "Settings");
            Check("dict_builtin_seed_hit", seed == "设置");
            var seedFile = Path.Combine(root, "dictionaries", "en-zh-CN.json");
            Check("dict_seed_file_written", File.Exists(seedFile)
                && File.ReadAllText(seedFile).Contains("\"by\":\"builtin\""));

            // 归一化：两端空白 Trim + 大小写不敏感
            var normalized = await Get(501, "  settings ");
            Check("dict_lookup_normalized", normalized == "设置");

            // 词典优先级高于包缓存（同文本两边都有时以词典为准）
            packs.Store(501, "zh-CN", "Map", "包里的地图译文", "test", false);
            var priority = await Get(501, "Map");
            Check("dict_beats_pack_cache", priority == "地图");

            // 立即返回：mock 闸门挡住 GateWord 的在线请求，词典响应必须先于闸门释放完成
            mock.Block("GateWord");
            dictionaries.TryPromote("en", "zh-CN", "GateWord", "门词");
            var gated = Get(501, "GateWord");
            var immediate = await Task.WhenAny(gated, Task.Delay(TimeSpan.FromSeconds(3))) == gated;
            Check("dict_hit_returns_immediately", immediate && await gated == "门词");
            await PollAsync(() => mock.CountOf("GateWord") == 1 ? (object?)true : null, TimeSpan.FromSeconds(5));
            Check("background_upgrade_request_fires", mock.CountOf("GateWord") == 1);
            mock.Release();
            var gatePack = await PollAsync(() => packs.Lookup(501, "zh-CN", "GateWord"), TimeSpan.FromSeconds(5));
            Check("background_upgrade_writes_pack", gatePack == "translated:GateWord");

            // NoOptimize=true：词典命中后零在线请求（等后台全部 settle 再断言）
            Volatile.Write(ref published, Settings(speed: true, noOptimize: true));
            dictionaries.TryPromote("en", "zh-CN", "FrozenWord", "冻结词");
            var frozenCount = mock.Count;
            var frozen = await Get(501, "FrozenWord");
            await Task.Delay(400);
            Check("nooptimize_skips_background_upgrade", frozen == "冻结词" && mock.Count == frozenCount
                && packs.Lookup(501, "zh-CN", "FrozenWord") is null);

            // NoOptimize=false：后台升级确实发出且结果写入包
            Volatile.Write(ref published, Settings(speed: true, noOptimize: false));
            dictionaries.TryPromote("en", "zh-CN", "UpgradeWord", "待升级");
            var upgradeHits = mock.Count;
            var upgraded = await Get(501, "UpgradeWord");
            var packValue = await PollAsync(() => packs.Lookup(501, "zh-CN", "UpgradeWord"), TimeSpan.FromSeconds(5));
            Check("background_upgrade_opt_in", upgraded == "待升级"
                && mock.Count == upgradeHits + 1 && packValue == "translated:UpgradeWord");

            // 包已有译文时不再重复升级（同一文本第二次命中零在线请求）
            var repeatCount = mock.Count;
            var repeat = await Get(501, "UpgradeWord");
            await Task.Delay(300);
            Check("pack_entry_not_reupgraded", repeat == "待升级" && mock.Count == repeatCount);

            // 在途去重：同一原文并发多次命中，在线只收到一次请求
            dictionaries.TryPromote("en", "zh-CN", "DedupWord", "去重词");
            var dedupHits = mock.Count;
            var burst = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Get(501, "DedupWord")));
            await PollAsync(() => relay.InFlight == 0 ? (object?)true : null, TimeSpan.FromSeconds(5));
            Check("inflight_dedup_single_request", burst.All(r => r == "去重词") && mock.Count == dedupHits + 1);

            // 质量模式完全绕过词典：有命中也走在线（FrozenWord 只有词典条目，包缓存未沾染）
            Volatile.Write(ref published, Settings(speed: false, noOptimize: false));
            var qualityHits = mock.Count;
            var quality = await Get(501, "FrozenWord");
            Check("quality_mode_bypasses_dictionary", quality == "translated:FrozenWord"
                && mock.Count == qualityHits + 1);

            // 沉淀与模式无关：质量模式下短文本在线成功也进词典；已有 key 不覆盖
            await Get(501, "Quality Sediment");
            Check("sedimentation_applies_in_quality_mode",
                dictionaries.Lookup("en", "zh-CN", "Quality Sediment") == "translated:Quality Sediment");
            dictionaries.TryPromote("en", "zh-CN", "KeepMe", "保留");
            var keep = await Get(501, "KeepMe");
            Check("existing_key_not_overwritten", keep == "translated:KeepMe"
                && dictionaries.Lookup("en", "zh-CN", "KeepMe") == "保留");

            // 高速模式：在线成功且 ≤30 字符 → 沉淀；>30 字符 → 不沉淀
            Volatile.Write(ref published, Settings(speed: true, noOptimize: false));
            var shortText = "Sediment Me Please";
            await Get(501, shortText);
            Check("short_online_result_promoted",
                dictionaries.Lookup("en", "zh-CN", shortText) == "translated:" + shortText);
            var longText = "This sentence is definitely longer than thirty characters total";
            await Get(501, longText);
            Check("long_online_result_not_promoted", dictionaries.Lookup("en", "zh-CN", longText) is null);

            // 失败开放：端点全挂返回原文、记失败数、不沉淀
            var failuresBefore = relay.Failures;
            var unavailable = await Get(501, "unavailable");
            Check("failure_returns_original_and_counts", unavailable == "unavailable"
                && relay.Failures == failuresBefore + 1
                && dictionaries.Lookup("en", "zh-CN", "unavailable") is null);

            // ---- Slice 2：译文目录同步（lab/010）----

            // PrepareForGame：游戏包条目全量写成 Franca.txt（原文=译文 每行一条，UTF-8）
            var sync = new GameTextSyncService();
            relay.UpgradeCompleted += (appId, src, tgt) => sync.NotifyUpgrade(appId, src, tgt);
            var gameA = Path.Combine(root, "gameA");
            Directory.CreateDirectory(Path.Combine(gameA, "BepInEx"));
            File.WriteAllText(Path.Combine(gameA, "winhttp.dll"), "synthetic");
            File.WriteAllText(Path.Combine(gameA, "doorstop_config.ini"), "synthetic");
            packs.Store(601, "zh-CN", "Alpha Line", "阿尔法行", "test", false);
            packs.Store(601, "zh-CN", "Settings", "包设置", "test", false); // 与词典种子同 key：写包条目，词典值不得出现
            packs.Store(601, "zh-CN", "Multi\nLine", "多\n行", "test", false);
            packs.Store(601, "zh-CN", "RetryDraft", "", "mt:none", true);
            sync.Activate(601, gameA, "zh-CN");
            await sync.PrepareForGameAsync(packs, 601, "zh-CN", gameA, "zh-CN");
            var francaPath = GameTextSyncService.TextFilePath(gameA, "zh-CN");
            var franca = File.ReadAllText(francaPath);
            Check("prepare_writes_franca_txt", File.Exists(francaPath)
                && franca.Contains("Alpha Line=阿尔法行\n") && franca.Contains("Settings=包设置\n"));
            Check("prepare_escapes_newlines", franca.Contains("Multi\\nLine=多\\n行"));
            Check("prepare_skips_empty_and_dict", !franca.Contains("RetryDraft")
                && !franca.Split('\n').Any(l => l == "Settings=设置"));

            // Reproduce XUnity's reload: opening its main cache on a Franca.txt creation must
            // succeed even with an entirely empty game pack and no previously generated text.
            var coldGame = Path.Combine(root, "cold-game");
            var coldText = Path.GetDirectoryName(GameTextSyncService.TextFilePath(coldGame, "id"))!;
            Directory.CreateDirectory(coldText);
            var reloadRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var watcher = new FileSystemWatcher(coldText, "Franca.txt"))
            {
                void ReadRuntimeCache()
                {
                    try
                    {
                        using var input = File.OpenRead(Path.Combine(coldText, "_AutoGeneratedTranslations.txt"));
                        reloadRead.TrySetResult(input.Length == 0);
                    }
                    catch { reloadRead.TrySetResult(false); }
                }
                watcher.Created += (_, _) => ReadRuntimeCache();
                watcher.Renamed += (_, _) => ReadRuntimeCache();
                watcher.EnableRaisingEvents = true;
                await sync.PrepareForGameAsync(packs, 603, "id", coldGame, "id");
                Check("cold_empty_pack_reload_can_read_runtime_cache",
                    await reloadRead.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            var runtimeCache = Path.Combine(Path.GetDirectoryName(francaPath)!, "_AutoGeneratedTranslations.txt");
            var existingRuntimeCache = "User edit=保留用户修正\r\n";
            File.WriteAllText(runtimeCache, existingRuntimeCache);
            await sync.PrepareForGameAsync(packs, 601, "zh-CN", gameA, "zh-CN");
            Check("prepare_preserves_existing_runtime_cache", File.ReadAllText(runtimeCache) == existingRuntimeCache);

            // 端到端：中继后台升级 → UpgradeCompleted 通知 → 缓冲合并 → 追加 Franca.txt
            dictionaries.TryPromote("en", "zh-CN", "LiveWord", "活词");
            var live = await Get(601, "LiveWord");
            var appended = await PollAsync(() =>
                File.ReadAllText(francaPath).Contains("LiveWord=translated:LiveWord")
                && File.ReadAllText(francaPath).Contains("Alpha Line=阿尔法行")
                    ? (object?)true : null, TimeSpan.FromSeconds(5));
            Check("upgrade_notify_appends_franca_txt", live == "活词" && appended is not null);
            Check("live_upgrade_preserves_runtime_cache", File.ReadAllText(runtimeCache) == existingRuntimeCache);

            // 游戏切换：旧 appId 与未激活 appId 的通知都被丢弃
            var gameB = Path.Combine(root, "gameB");
            sync.NotifyUpgrade(999, "GhostWord", "幽灵");
            sync.Activate(602, gameB, "zh-CN");
            sync.NotifyUpgrade(601, "StaleWord", "过期");
            sync.NotifyUpgrade(602, "NewWord", "新词");
            var bPath = GameTextSyncService.TextFilePath(gameB, "zh-CN");
            var switched = await PollAsync(() => File.Exists(bPath)
                && File.ReadAllText(bPath).Contains("NewWord=新词") ? (object?)true : null,
                TimeSpan.FromSeconds(5));
            await Task.Delay(1500); // 越窗确认丢弃的通知不会迟写
            var ghostLeaked = File.ReadAllText(francaPath).Contains("GhostWord");
            var staleLeaked = File.Exists(bPath) && File.ReadAllText(bPath).Contains("StaleWord");
            Check("inactive_game_notifications_dropped", switched is not null && !ghostLeaked && !staleLeaked);

            // Deactivate 后通知丢弃
            sync.Deactivate(602);
            sync.NotifyUpgrade(602, "AfterOff", "关闭后");
            await Task.Delay(1500);
            Check("deactivate_drops_notifications",
                !File.ReadAllText(bPath).Contains("AfterOff"));

            // 健康检查：ReloadTranslationsOnFileChange=False 报出并可修复为 True
            var healthDir = Path.Combine(root, "health-game");
            Directory.CreateDirectory(Path.Combine(healthDir, "BepInEx", "config"));
            File.WriteAllText(Path.Combine(healthDir, "winhttp.dll"), "synthetic");
            File.WriteAllText(Path.Combine(healthDir, "doorstop_config.ini"), "synthetic");
            File.WriteAllText(TranslationHealthService.ConfigPath(healthDir),
                "[General]\nLanguage=en\nFromLanguage=ja\n[Service]\nEndpoint=CustomTranslate\n"
                + "[Custom]\nUrl=http://127.0.0.1:1/translate/701\n[Behaviour]\n"
                + "MaxCharactersPerTranslation=200\nIgnoreWhitespaceInDialogue=False\n"
                + "ReloadTranslationsOnFileChange=False\n");
            var probeSettings = new AppSettings { TargetLanguage = "zh-CN", SourceLanguage = "en", IsTransient = true };
            var healthGame = new GameInfo { Name = "Synthetic", Path = healthDir, AppId = 701,
                Engine = GameEngine.UnityMono, IsReady = true };
            var workflow = new GameTranslationService(relay, probeSettings, sync);
            var flagged = workflow.Inspect(healthGame);
            Check("health_flags_reload_disabled", !flagged.IsHealthy
                && flagged.Issues.Any(i => i.Code == "reload-on-change"));
            await workflow.RepairAsync(healthGame);
            var repaired = workflow.Inspect(healthGame);
            Check("health_repair_enables_reload", repaired.IsHealthy
                && File.ReadAllText(TranslationHealthService.ConfigPath(healthDir))
                    .Contains("ReloadTranslationsOnFileChange=True"));

            // 计数器
            Check("counter_dict_hits", relay.DictHits >= 6);
            Check("counter_inflight_settles", relay.InFlight == 0);
            Check("counter_hitrate_excludes_dict", relay.HitRate >= 0 && relay.HitRate <= 1
                && relay.DictRate > relay.HitRate);
            relay.Stop();
            Check("relay_stop_releases_listener", !relay.IsRunning);
        }
        finally { Directory.Delete(root, recursive: true); }
        Environment.ExitCode = failures == 0 ? 0 : 1;
        Console.WriteLine($"RESULT\tDICTIONARY\tfailures={failures}");
    }

    private static async Task<T?> PollAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null) return value;
            await Task.Delay(50);
        }
        return null;
    }

    // 最小回环在线端点：计数全部请求（可按 text 统计），按 text 回译；
    // Block(text) 挂起该文本的响应直到 Release，用于验证"立即返回不等待在线"
    private sealed class MockProvider : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
        private readonly ConcurrentQueue<string> _requests = new();
        public string Url { get; }
        public int Count;

        public MockProvider()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = ServeAsync();
        }

        public void Block(string text) => _gates[text] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release()
        {
            foreach (var gate in _gates.Values) gate.TrySetResult();
        }
        public int CountOf(string text) => _requests.Count(r => r == text);

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { break; }
                try
                {
                    var text = context.Request.QueryString["text"] ?? "";
                    _requests.Enqueue(text);
                    Interlocked.Increment(ref Count);
                    if (_gates.TryGetValue(text, out var gate)) await gate.Task;
                    var body = "translated:" + text;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                finally { context.Response.Close(); }
            }
        }

        public void Dispose() => _listener.Close();
    }
}
