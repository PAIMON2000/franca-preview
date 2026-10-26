using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

// Deterministic integration checks: synthetic games/packs and loopback HTTP only.
internal static class ArchitectureProbe
{
    public static async Task RunAsync()
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tARCHITECTURE\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition) failures++;
        }

        var root = Path.Combine(Path.GetTempPath(), "franca-architecture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettings { TargetLanguage = "zh-CN", SourceLanguage = "en", IsTransient = true,
                ActiveGameAppId = 101, ActiveGameName = "Legacy setting must not control attribution" };
            settings.EndpointKeys["test"] = new() { ["ApiKey"] = "before" };
            settings.DisabledEndpoints.Add("disabled");
            var snapshot = settings.Snapshot("selected");
            settings.EndpointKeys["test"]["ApiKey"] = "after";
            settings.DisabledEndpoints.Clear();
            Check("settings_deep_copy", snapshot.EndpointKeys["test"]["ApiKey"] == "before"
                && snapshot.DisabledEndpoints.Contains("disabled") && snapshot.Endpoint == "selected");
            snapshot.Save();
            Check("snapshot_is_transient", snapshot.IsTransient);

            var auto = new EndpointAutoService((host, _) => Task.FromResult<long?>(host == "fast" ? 5 : 20));
            var probes = 0;
            Task<TestOutcome> Confirm(string id, CancellationToken token)
            {
                probes++;
                return Task.FromResult(new TestOutcome(EndpointStatus.Ok, 9, id));
            }
            var candidates = new List<(string, string)> { ("slow", "slow"), ("fast", "fast") };
            var pick = await auto.SelectAsync(candidates, Confirm, CancellationToken.None);
            Check("auto_selects_verified_fastest", pick is { Id: "fast", LatencyMs: 9 });
            await auto.SelectAsync(candidates, Confirm, CancellationToken.None);
            Check("auto_unchanged_skips_confirmation", probes == 1);
            Check("auto_instances_isolated", new EndpointAutoService().CurrentPick is null);
            auto.Reset();
            await auto.SelectAsync(candidates, (_, _) => Task.FromResult(
                new TestOutcome(EndpointStatus.Failed, null, "failed")), CancellationToken.None);
            Check("auto_rejects_failed_confirmation", auto.CurrentPick is null);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirm = new TaskCompletionSource<TestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = auto.SelectAsync(candidates, (_, _) => { started.SetResult(); return confirm.Task; }, CancellationToken.None);
            await started.Task;
            auto.Reset();
            confirm.SetResult(new TestOutcome(EndpointStatus.Ok, 3, "stale"));
            Check("auto_reset_rejects_inflight_result", await pending is null && auto.CurrentPick is null);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await auto.SelectAsync(candidates, Confirm, cancel.Token); Check("auto_cancellation", false); }
                catch (OperationCanceledException) { Check("auto_cancellation", true); }
            }

            var packs = new PackStore(Path.Combine(root, "packs"));
            packs.Store(101, "zh-CN", "same", "game A", "test", false);
            packs.Store(202, "zh-CN", "same", "game B", "test", false);
            var calls = 0;
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AppSettings published = settings.Snapshot();
            async Task<(string?, string?)> Translate(string text, string from, string to, AppSettings captured, CancellationToken ct)
            {
                Interlocked.Increment(ref calls);
                if (text == "unavailable") return (null, null);
                if (text == "pending") { seen.TrySetResult(); await release.Task.WaitAsync(ct); }
                return ($"{from}/{to}/{captured.Endpoint}:{text}", "mock");
            }
            using var relay = new RelayServer(() => Volatile.Read(ref published), packs, Translate,
                new DictionaryService(Path.Combine(root, "dictionaries")));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => relay.EnsureStartedAsync())));
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            Task<string> Get(int appId, string text) => http.GetStringAsync(
                $"{relay.UrlForGame(appId)}?from=en&to=zh-CN&text={Uri.EscapeDataString(text)}");
            Check("relay_concurrent_start", relay.IsRunning);
            var cached = await Task.WhenAll(Get(101, "same"), Get(202, "same"));
            Check("relay_game_cache_isolation", cached[0] == "game A" && cached[1] == "game B" && calls == 0);
            await Get(101, "new");
            Check("relay_writes_request_game", packs.Lookup(101, "zh-CN", "new") is not null
                && packs.Lookup(202, "zh-CN", "new") is null);
            var legacy = await http.GetStringAsync($"{relay.BaseUrl}?from=ja&to=fr&text=legacy");
            Check("legacy_does_not_attribute_to_active_game", packs.Lookup(101, "fr", "legacy") is null);
            Check("request_source_and_target_are_honored", legacy.StartsWith("ja/fr/", StringComparison.Ordinal));
            Check("relay_failure_returns_original", await Get(101, "unavailable") == "unavailable"
                && packs.Lookup(101, "zh-CN", "unavailable") is null);
            var inflight = Get(101, "pending");
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var changed = settings.Snapshot("changed");
            Volatile.Write(ref published, changed);
            release.SetResult();
            var response = await inflight;
            Check("relay_request_captures_settings", response.Contains("BingTranslate:pending")
                && packs.Lookup(101, "zh-CN", "pending") == response);
            Check("relay_next_request_uses_new_settings", (await Get(202, "next")).Contains("changed:next"));
            using (var invalid = await http.GetAsync($"{relay.BaseUrl}/-1?text=x"))
                Check("relay_rejects_invalid_game_route", invalid.StatusCode == HttpStatusCode.NotFound);
            using (var invalid = await http.GetAsync($"{relay.UrlForGame(101)}?text=x&to=..%2Fescape"))
                Check("relay_rejects_invalid_pack_path", invalid.StatusCode == HttpStatusCode.BadRequest);
            await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            {
                packs.Store(303, "fr", "concurrent", "value" + i, "test", false);
                _ = packs.Lookup(303, "fr", "concurrent");
            })));
            Check("pack_concurrent_read_write", packs.Lookup(303, "fr", "concurrent")?.StartsWith("value") == true);
            using (var other = new RelayServer(() => settings.Snapshot(), new PackStore(Path.Combine(root, "other")), Translate,
                new DictionaryService(Path.Combine(root, "other-dictionaries"))))
            {
                await other.EnsureStartedAsync();
                Check("relay_instances_have_distinct_lifetimes", other.Port != relay.Port && other.ServedCount == 0);
            }
            Check("disposing_other_relay_keeps_first_running", relay.IsRunning && await Get(101, "same") == "game A");

            // ---- 悬浮窗状态模型（lab/011）：注入假设置/中继/计数器，纯逻辑可测 ----
            var overlaySettings = new AppSettings { TargetLanguage = "zh-CN", SourceLanguage = "en",
                IsTransient = true, OverlayEnabled = true };
            var relayRunning = false;
            OverlayStatusModel.Counts overlayCounts = new(0, 0, 0, 0, 0);
            var overlay = new OverlayStatusModel(() => overlaySettings, () => relayRunning, () => overlayCounts);
            Check("overlay_hidden_by_default", !new OverlayStatusModel(
                () => new AppSettings { IsTransient = true }, () => true, () => overlayCounts).ShouldShow);
            Check("overlay_hidden_when_relay_stopped", !overlay.ShouldShow);
            relayRunning = true;
            Check("overlay_shown_when_enabled_and_running", overlay.ShouldShow);
            overlayCounts = new(80, 20, 12, 0, 0);
            var lines = overlay.BuildLines();
            Check("overlay_lines_speed_mode", lines[0].Contains("Speed") && lines[1].Contains("80")
                && lines[1].Contains("25") && lines[1].Contains("15")
                && !lines[2].Contains(UiStrings.Get("overlay.hint.wait"))
                && !lines[2].Contains(UiStrings.Get("overlay.hint.fail")));
            overlayCounts = new(80, 20, 12, 3, 0);
            lines = overlay.BuildLines();
            Check("overlay_hint_while_translating", lines[2].Contains("3")
                && lines[2].Contains(UiStrings.Get("overlay.hint.wait")));
            overlayCounts = new(80, 20, 12, 0, 2);
            lines = overlay.BuildLines();
            Check("overlay_hint_on_failures", lines[2].Contains(UiStrings.Get("overlay.hint.fail")));
            overlaySettings.TranslationMode = "quality";
            lines = overlay.BuildLines();
            Check("overlay_lines_quality_mode", lines[0].Contains("Quality"));
            overlaySettings.TranslationMode = "speed";
            var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(
                new AppSettings { OverlayEnabled = true, OverlayLeft = 12.5, OverlayTop = 34.5 }));
            Check("overlay_settings_roundtrip", roundtrip is { OverlayEnabled: true, OverlayLeft: 12.5, OverlayTop: 34.5 });
            var legacySettings = JsonSerializer.Deserialize<AppSettings>("{}");
            Check("overlay_settings_legacy_defaults", legacySettings is { OverlayEnabled: false, OverlayLeft: null,
                OverlayTop: null, TranslationMode: "speed", NoOptimizeAfterGenerate: false });

            var gameDir = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "config"));
            File.WriteAllText(Path.Combine(gameDir, "winhttp.dll"), "synthetic");
            File.WriteAllText(Path.Combine(gameDir, "doorstop_config.ini"), "synthetic");
            var game = new GameInfo { Name = "Synthetic", Path = gameDir, AppId = 404, Engine = GameEngine.UnityMono, IsReady = true };
            var config = TranslationHealthService.ConfigPath(gameDir);
            File.WriteAllText(config, "[General]\nLanguage=en\nFromLanguage=ja\n[CustomUser]\nKeep=sentinel\n");
            var workflow = new GameTranslationService(relay, settings);
            Check("workflow_detects_wrong_config", !workflow.Inspect(game).IsHealthy);
            var repaired = await workflow.RepairAsync(game);
            Check("workflow_repair_and_recheck", repaired.Report.IsHealthy && workflow.Inspect(game).IsHealthy);
            Check("workflow_assigns_game_route", File.ReadAllText(config).Contains($"Url={relay.UrlForGame(404)}"));
            Check("workflow_keeps_custom_fields_and_backup", File.ReadAllText(config).Contains("Keep=sentinel")
                && Directory.GetFiles(Path.Combine(gameDir, "BepInEx", "franca-backups")).Length == 1);
            var original = File.ReadAllBytes(config);
            var progress = new Progress<string>(_ => { });
            await workflow.ToggleAsync(game, progress);
            Check("workflow_disable_is_reversible", game.IsDisabledByUser);
            await workflow.ToggleAsync(game, progress);
            Check("workflow_reenable_preserves_config", !game.IsDisabledByUser && File.ReadAllBytes(config).SequenceEqual(original));
            await workflow.RepairAsync(game);
            Check("workflow_repeated_repair_preserves_backups", Directory.GetFiles(Path.Combine(gameDir, "BepInEx", "franca-backups")).Length == 2);

            using var mock = new MockProvider();
            var custom = new CustomEndpointDef { BaseUrl = mock.Url + "get", Type = "get", Id = "fixture", Name = "fixture" };
            const string tricky = "a & b \"quoted\" 中文";
            Check("custom_get_production_protocol", await AppTranslator.TranslateCustomAsync(custom, tricky, "fr", CancellationToken.None, "ja") == "translated:" + tricky);
            var outcome = await EndpointTestService.TestCustomGetAsync(custom.BaseUrl, tricky, "ja", "fr", CancellationToken.None);
            Check("custom_get_health_uses_same_protocol", outcome.Status is EndpointStatus.Ok or EndpointStatus.Slow && outcome.Detail == "translated:" + tricky);
            Check("custom_get_language_parameters", mock.LastFrom == "ja" && mock.LastTo == "fr");
            custom.Type = "openai";
            custom.BaseUrl = mock.Url.TrimEnd('/');
            custom.Model = "mock-model";
            custom.ApiKey = "fixture-key";
            Check("custom_openai_production_protocol", await AppTranslator.TranslateCustomAsync(custom, tricky, "fr", CancellationToken.None, "auto") == "mock translation");
            Check("custom_openai_json_and_auth", mock.LastText == tricky && mock.LastAuth == "Bearer fixture-key");
            outcome = await EndpointTestService.TestCustomOpenAiAsync(custom.BaseUrl, custom.ApiKey, custom.Model, tricky, "auto", "fr", CancellationToken.None);
            Check("custom_openai_health_uses_same_protocol", outcome.Status is EndpointStatus.Ok or EndpointStatus.Slow && outcome.Detail == "mock translation");
            outcome = await EndpointTestService.TestCustomGetAsync(mock.Url + "rate", "x", "en", "fr", CancellationToken.None);
            Check("provider_rate_limit_classification", outcome.Status == EndpointStatus.Limited);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { await EndpointTestService.TestCustomGetAsync(mock.Url + "get", "x", "en", "fr", cancel.Token); Check("health_cancellation_propagates", false); }
                catch (OperationCanceledException) { Check("health_cancellation_propagates", true); }
            }
            Check("unsupported_provider_returns_null", await AppTranslator.TranslateAsync("unknown", "x", "fr", null, CancellationToken.None) is null);
            relay.Stop();
            Check("relay_stop_releases_listener", !relay.IsRunning);
            await relay.EnsureStartedAsync();
            Check("relay_can_restart_after_stop", relay.IsRunning);
            relay.Dispose();
            try { await relay.EnsureStartedAsync(); Check("disposed_relay_cannot_restart", false); }
            catch (ObjectDisposedException) { Check("disposed_relay_cannot_restart", true); }

            Check("steam_launch_url", GameTranslationService.SteamLaunchUrl(1207650) == "steam://rungameid/1207650");
            Check("launch_requires_active_steam_game",
                new GameInfo { Name = "g", Path = root, AppId = 1207650, IsReady = true }.CanLaunch
                && !new GameInfo { Name = "g", Path = root, AppId = null, IsReady = true }.CanLaunch
                && !new GameInfo { Name = "g", Path = root, AppId = 1207650, IsReady = false }.CanLaunch);
        }
        finally { Directory.Delete(root, recursive: true); }
        Environment.ExitCode = failures == 0 ? 0 : 1;
        Console.WriteLine($"RESULT\tARCHITECTURE\tfailures={failures}");
    }

    private sealed class MockProvider : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public string? LastFrom { get; private set; }
        public string? LastTo { get; private set; }
        public string? LastText { get; private set; }
        public string? LastAuth { get; private set; }

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

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                try
                {
                    string body;
                    if (context.Request.Url?.AbsolutePath == "/rate")
                    {
                        context.Response.StatusCode = 429;
                        body = "limited";
                    }
                    else if (context.Request.HttpMethod == "POST")
                    {
                        using var reader = new StreamReader(context.Request.InputStream);
                        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                        LastText = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
                        LastAuth = context.Request.Headers["Authorization"];
                        body = "{\"choices\":[{\"message\":{\"content\":\"mock translation\"}}]}";
                    }
                    else
                    {
                        LastFrom = context.Request.QueryString["from"];
                        LastTo = context.Request.QueryString["to"];
                        body = "translated:" + context.Request.QueryString["text"];
                    }
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
