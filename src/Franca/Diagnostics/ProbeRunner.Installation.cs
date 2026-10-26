using System.IO;
using System.Net.Http;
using System.Windows;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

internal static partial class ProbeRunner
{
    private static void RunProbeToggle()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "probe-toggle-dir");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "BepInEx"));
            File.WriteAllText(Path.Combine(dir, "winhttp.dll"), "x");
            File.WriteAllText(Path.Combine(dir, "doorstop_config.ini"), "x");

            Console.WriteLine($"IsReady(installed): {TranslationInstaller.IsReady(dir)}");
            TranslationInstaller.SetDisabled(dir, true);
            Console.WriteLine($"after disable: frdisabled={File.Exists(Path.Combine(dir, "winhttp.dll.frdisabled"))}, IsReady={TranslationInstaller.IsReady(dir)}");
            TranslationInstaller.SetDisabled(dir, false);
            Console.WriteLine($"after re-enable: winhttp={File.Exists(Path.Combine(dir, "winhttp.dll"))}, IsReady={TranslationInstaller.IsReady(dir)}");
            TranslationInstaller.SetDisabled(dir, true);
            TranslationInstaller.SetDisabled(dir, true);
            Console.WriteLine($"idempotent disable x2: frdisabled={File.Exists(Path.Combine(dir, "winhttp.dll.frdisabled"))}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void RunProbeHealth()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "probe-health-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "BepInEx", "config"));
            File.WriteAllText(Path.Combine(dir, "winhttp.dll"), "x");
            File.WriteAllText(Path.Combine(dir, "doorstop_config.ini"), "x");
            File.WriteAllText(TranslationHealthService.ConfigPath(dir), "[general]\n language =en\nFromLanguage=ja\n[Service]\nEndpoint=BingTranslate\n[UserCustom]\nKeep=sentinel\n");
            var settings = new AppSettings { TargetLanguage = "zh-CN", SourceLanguage = "fr" };
            var before = TranslationHealthService.Inspect(dir, settings, "http://127.0.0.1:47820/translate");
            Console.WriteLine($"RESULT\tHEALTH_BEFORE\t{before.IsHealthy}\t{string.Join(" | ", before.Issues.Select(i => i.Code))}");
            if (!new[] { "target-language", "source-language", "relay-endpoint", "relay-url", "text-length", "paragraph-whitespace" }
                .All(code => before.Issues.Any(issue => issue.Code == code)))
                throw new InvalidOperationException("Health probe failed to detect incorrect configuration.");
            TranslationHealthService.Repair(dir, settings, "http://127.0.0.1:47820/translate", Console.WriteLine);
            var after = TranslationHealthService.Inspect(dir, settings, "http://127.0.0.1:47820/translate");
            Console.WriteLine($"RESULT\tHEALTH_AFTER\t{after.IsHealthy}\tbackup={Directory.GetFiles(Path.Combine(dir, "BepInEx", "franca-backups")).Length}");
            var backups = Directory.GetFiles(Path.Combine(dir, "BepInEx", "franca-backups"));
            if (!after.IsHealthy || backups.Length != 1 || !File.ReadAllText(backups[0]).Contains("FromLanguage=ja")
                || !File.ReadAllText(TranslationHealthService.ConfigPath(dir)).Contains("Keep=sentinel"))
                throw new InvalidOperationException("Health probe repair or backup verification failed.");
            TranslationHealthService.Repair(dir, settings, "http://127.0.0.1:47820/translate", Console.WriteLine);
            if (Directory.GetFiles(Path.Combine(dir, "BepInEx", "franca-backups")).Length != 2)
                throw new InvalidOperationException("Repeated repair overwrote a backup.");
            Console.WriteLine("RESULT\tHEALTH\tchecks_and_backup_preservation\tPASS");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }


}
