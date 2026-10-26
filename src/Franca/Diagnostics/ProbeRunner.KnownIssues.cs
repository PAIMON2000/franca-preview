using System.IO;
using System.Net.Http;
using System.Windows;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

internal static partial class ProbeRunner
{
    private static void RunProbeKnownIssues()
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tKNOWN_ISSUE\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition)
                failures++;
        }

        var issue = KnownIssueCatalog.Find(KnownIssueCatalog.DoorstopSteamEnvironmentId);
        Check("catalog_lookup", issue is not null);
        Check("catalog_shape", issue is not null
            && !string.IsNullOrWhiteSpace(issue.TitleKey)
            && !string.IsNullOrWhiteSpace(issue.SummaryKey)
            && !string.IsNullOrWhiteSpace(issue.ReasonKey)
            && !string.IsNullOrWhiteSpace(issue.RepairKey)
            && issue.RequiresConfirmation);
        Check("language_zh_hant", AppSettings.ResolveSupportedLanguage(
            new System.Globalization.CultureInfo("zh-HK")) == "zh-TW");
        Check("language_region", AppSettings.ResolveSupportedLanguage(
            new System.Globalization.CultureInfo("fr-CA")) == "fr");
        Check("language_unsupported", AppSettings.ResolveSupportedLanguage(
            new System.Globalization.CultureInfo("pt-PT")) == "en");
        Check("language_null", AppSettings.ResolveSupportedLanguage(null) == "en");
        if (issue is not null)
        {
            Check("dialog_english_fallback",
                UiStrings.GetForLanguage("xx", issue.TitleKey) == UiStrings.GetForLanguage("en", issue.TitleKey));
            var dialog = new Views.KnownIssueDialog(issue, "Probe Game", "xx",
                () => Task.FromResult(new KnownIssueRepairOutcome(true, "issue.status.success")));
            Check("dialog_local_title", dialog.Title == UiStrings.GetForLanguage("en", issue.TitleKey));
        }
        var missing = UiStrings.MissingKeys().ToList();
        Check("local_strings_complete", missing.Count == 0);
        foreach (var item in missing.Take(10))
            Console.WriteLine($"RESULT\tMISSING_KEY\t{item}");

        var dir = Path.Combine(AppContext.BaseDirectory, "known-issue-probe");
        var started = DateTimeOffset.Now;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "BepInEx"));
            var log = Path.Combine(dir, "BepInEx", "LogOutput.log");
            Check("log_starting", GameRuntimeMonitor.EvaluateBepInExLog(dir, started, started.AddSeconds(10))
                == BepInExLogStatus.Starting);

            File.WriteAllText(log, "Chainloader startup complete");
            File.SetLastWriteTimeUtc(log, started.UtcDateTime);
            Check("log_healthy", GameRuntimeMonitor.EvaluateBepInExLog(dir, started, started.AddSeconds(10))
                == BepInExLogStatus.Healthy);
            using (var writer = new FileStream(log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                Check("log_shared_read", GameRuntimeMonitor.HasHealthyBepInExLog(dir, started.AddSeconds(-1)));
            }

            File.SetLastWriteTimeUtc(log, started.AddMinutes(-10).UtcDateTime);
            Check("log_stale", GameRuntimeMonitor.EvaluateBepInExLog(dir, started, started.AddSeconds(46))
                == BepInExLogStatus.Stale);

            File.WriteAllText(log, "Chainloader started");
            File.SetLastWriteTimeUtc(log, started.AddSeconds(10).UtcDateTime);
            Check("log_slow_start", GameRuntimeMonitor.EvaluateBepInExLog(dir, started, started.AddSeconds(46))
                == BepInExLogStatus.Starting);
            Check("log_slow_timeout", GameRuntimeMonitor.EvaluateBepInExLog(dir, started, started.AddSeconds(91))
                == BepInExLogStatus.Stale);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", "TRUE");
        Environment.SetEnvironmentVariable("DOORSTOP_TEST_PROBE", "1");
        try
        {
            var info = KnownIssueRepairService.CreateCleanSteamStartInfo("steam.exe", "-silent");
            Check("clean_steam_environment", !info.Environment.Keys.Any(key =>
                key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase)));
            Check("clean_steam_command", !info.UseShellExecute
                && info.FileName.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase)
                && info.Arguments == "-silent");
            var launch = KnownIssueRepairService.CreateSteamLaunchInfo(1207650);
            Check("steam_uri_launch", launch.UseShellExecute
                && launch.FileName.StartsWith("steam://run/1207650//", StringComparison.OrdinalIgnoreCase)
                && !launch.FileName.Contains("Suzerain.exe", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOORSTOP_DISABLE", null);
            Environment.SetEnvironmentVariable("DOORSTOP_TEST_PROBE", null);
        }

        Environment.ExitCode = failures == 0 ? 0 : 1;
    }


}
