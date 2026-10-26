using System.IO;
using Franca.Desktop;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

// Temporary game fixtures and loopback relay only; no downloads, providers, or real game writes.
internal static class LanguageApplyProbe
{
    public static async Task RunAsync()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"RESULT\tLANGUAGE_APPLY\t{name}\t{(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }
        double now = 0;
        int calls = 0;
        using (var countdown = new LanguageApplyController(_ =>
        {
            calls++;
            return Task.FromResult(new GameTranslationService.LanguageApplyResult(1, 0, 0, 0));
        }, false, () => now))
        {
            countdown.Schedule();
            now = 2; countdown.Tick();
            Check("two_seconds_progress", calls == 0 && Math.Abs(countdown.Progress - .4) < .001);
            countdown.TogglePause();
            now = 20; countdown.Tick();
            Check("pause_freezes_countdown", calls == 0 && Math.Abs(countdown.Progress - .4) < .001);
            countdown.TogglePause();
            now = 22.99; countdown.Tick();
            Check("does_not_apply_early", calls == 0);
            now = 23; countdown.Tick(); await countdown.Completion;
            Check("applies_at_five_seconds", calls == 1 && countdown.Result?.Updated == 1);
            countdown.Schedule(); now = 27; countdown.Tick(); countdown.Schedule();
            now = 28; countdown.Tick();
            Check("new_selection_resets_full_countdown", calls == 1 && countdown.SecondsRemaining == 4);
            countdown.TogglePause(); countdown.Skip(); await countdown.Completion;
            Check("skip_applies_even_while_paused", calls == 2 && !countdown.Pending);
            countdown.Tick();
            Check("completed_does_not_repeat", calls == 2);
            countdown.TogglePause();
            Check("play_can_restart_batch_without_changing_languages", countdown.Pending && !countdown.Paused);
            countdown.Skip(); await countdown.Completion;
            Check("manual_retry_runs_whole_batch", calls == 3);
            countdown.Schedule(); countdown.Dispose(); now = 100; countdown.Tick();
            Check("close_cancels_pending", calls == 3);
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maxActive = 0, sequence = 0;
        bool cancelled = false;
        using (var queue = new LanguageApplyController(async ct =>
        {
            int order = ++sequence;
            maxActive = Math.Max(maxActive, ++active);
            try
            {
                if (order == 1) { await gate.Task; cancelled = ct.IsCancellationRequested; }
                return new GameTranslationService.LanguageApplyResult(order, 0, 0, 0);
            }
            finally { active--; }
        }, false, () => now))
        {
            queue.Schedule(); queue.Skip(); var first = queue.Completion;
            queue.Schedule(); queue.Skip();
            Check("new_selection_waits_for_inflight_write", sequence == 1 && queue.Pending);
            gate.SetResult(); await first; await queue.Completion;
            Check("latest_selection_wins_without_overlap", cancelled && maxActive == 1 && queue.Result?.Updated == 2);
        }
        using (var broken = new LanguageApplyController(_ => throw new IOException("fixture failure"), false, () => now))
        {
            broken.Schedule(); broken.Skip(); await broken.Completion;
            Check("failure_is_reported_and_retryable", broken.Error == "fixture failure" && !broken.Applying);
            broken.Schedule(); Check("new_selection_clears_error", broken.Error is null);
        }
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var timed = new LanguageApplyController(_ =>
        {
            fired.TrySetResult();
            return Task.FromResult(new GameTranslationService.LanguageApplyResult(0, 0, 0, 0));
        }))
        {
            timed.Schedule();
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await timed.Completion;
            Check("dispatcher_timer_applies_after_real_five_seconds", elapsed.Elapsed.TotalSeconds >= 5
                && !timed.Pending && timed.Result is not null);
        }

        var root = Path.Combine(Path.GetTempPath(), "franca-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettings { TargetLanguage = "fr", SourceLanguage = "ja", IsTransient = true };
            using var relay = new RelayServer(() => settings.Snapshot(), new PackStore(Path.Combine(root, "packs")),
                (_, _, _, _, _) => throw new InvalidOperationException("A provider must never be called."));
            var service = new GameTranslationService(relay, settings);
            GameInfo Fixture(string name, int id, bool installed = true)
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                if (installed)
                {
                    Directory.CreateDirectory(Path.Combine(path, "BepInEx", "config"));
                    File.WriteAllText(Path.Combine(path, "winhttp.dll"), "fixture");
                    File.WriteAllText(Path.Combine(path, "doorstop_config.ini"), "fixture");
                    File.WriteAllText(TranslationHealthService.ConfigPath(path), "[General]\nLanguage=en\nFromLanguage=en\n[Custom]\nKeep=sentinel\n");
                }
                return new GameInfo { Name = name, Path = path, AppId = id, Engine = GameEngine.UnityMono, IsReady = installed };
            }
            var enabled = Fixture("enabled", 1001);
            Check("process_detection_finds_stopped_fixture", GameTranslationService.IsGameStopped(enabled));
            Check("process_detection_finds_own_running_process", !GameTranslationService.IsGameStopped(
                new GameInfo { Name = "probe process", Path = AppContext.BaseDirectory }));
            var disabled = Fixture("disabled", 1002);
            TranslationInstaller.SetDisabled(disabled.Path, true);
            var running = Fixture("running", 1003);
            var absent = Fixture("not-installed", 1004, false);
            var bad = Fixture("locked-config", 1005);
            var later = Fixture("after-failure", 1006);
            relay.Packs.Store(1001, "fr", "hello", "bonjour", "test", false);
            var runningBefore = File.ReadAllText(TranslationHealthService.ConfigPath(running.Path));
            var badPath = TranslationHealthService.ConfigPath(bad.Path);
            File.Delete(badPath); Directory.CreateDirectory(badPath);
            var result = await service.ApplyLanguagesAsync([enabled, disabled, running, absent, bad, later, enabled], settings,
                game => game != running);
            Check("batch_updates_and_continues_after_failure", result == new GameTranslationService.LanguageApplyResult(3, 0, 1, 1));
            Check("source_and_target_applied", service.Inspect(enabled).IsHealthy && service.Inspect(later).IsHealthy);
            Check("custom_fields_preserved", File.ReadAllText(TranslationHealthService.ConfigPath(enabled.Path)).Contains("Keep=sentinel"));
            var backups = Directory.GetFiles(Path.Combine(enabled.Path, "BepInEx", "franca-backups"));
            Check("original_configuration_backed_up", backups.Length == 1 && File.ReadAllText(backups[0]).Contains("Language=en"));
            Check("disabled_remains_disabled", disabled.IsDisabledByUser && !File.Exists(Path.Combine(disabled.Path, "winhttp.dll")));
            Check("running_or_unknown_game_untouched", File.ReadAllText(TranslationHealthService.ConfigPath(running.Path)) == runningBefore
                && !Directory.Exists(Path.Combine(running.Path, "BepInEx", "franca-backups")));
            Check("does_not_install_missing_runtime", !Directory.EnumerateFileSystemEntries(absent.Path).Any());
            var textPath = GameTextSyncService.TextFilePath(enabled.Path, "fr");
            Check("target_language_cache_prepared", File.Exists(textPath) && File.ReadAllText(textPath).Contains("hello=bonjour"));
            var repeat = await service.ApplyLanguagesAsync([enabled, disabled], settings, _ => true);
            Check("healthy_games_do_not_write_again", repeat.Unchanged == 2 && repeat.Updated == 0
                && Directory.GetFiles(Path.Combine(enabled.Path, "BepInEx", "franca-backups")).Length == 1);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { await service.ApplyLanguagesAsync([running], settings, _ => true, cts.Token); Check("cancelled_batch_does_not_write", false); }
            catch (OperationCanceledException) { Check("cancelled_batch_does_not_write", File.ReadAllText(TranslationHealthService.ConfigPath(running.Path)) == runningBefore); }
            var snap = settings.Snapshot();
            await service.ApplyLanguagesAsync([running], snap, _ => { snap.TargetLanguage = "de"; return true; });
            Check("batch_uses_captured_language_snapshot", service.Inspect(running).IsHealthy);
        }
        finally { Directory.Delete(root, recursive: true); }
        Check("all_ui_languages_have_strings", !UiStrings.MissingKeys().Any());
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
