using System.IO;
using System.Text.Json;
using Franca.Services;

namespace Franca.Diagnostics;

internal static partial class ProbeRunner
{
    // Explicit preparation commands, not synthetic probes. Inspect is read-only; prepare contacts
    // enabled free providers, writes game-scoped caches and an OUTSIDE-game staging directory only.
    private static async Task RunUeCommandAsync(string[] args)
    {
        try
        {
            var tables = args[0].StartsWith("--ue-table-", StringComparison.Ordinal);
            if (args[0] is "--ue-inspect" or "--ue-table-inspect")
            {
                if (args.Length is < 2 or > 3) throw new ArgumentException("--ue-inspect <source.pak> [targetCulture]");
                var slot = args.Length > 2 ? args[2] : "zh-Hans";
                var resources = tables ? UeStaticService.InspectTables(args[1], slot) : UeStaticService.Inspect(args[1], "en", slot);
                foreach (var resource in resources)
                    Console.WriteLine(JsonSerializer.Serialize(resource));
                return;
            }
            if (args.Length < 9) throw new ArgumentException(
                "--ue-prepare <gameDir> <source.pak> <appId> <outputDir> <settings.json> <packsDir> <seconds> [--language=ja] [--slot=zh-Hans] <sourcePath>...");
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[5]))
                ?? throw new InvalidDataException("Invalid settings file");
            settings = settings.Snapshot();
            settings.SourceLanguage = "en";
            var options = args[8..];
            settings.TargetLanguage = options.FirstOrDefault(a => a.StartsWith("--language=", StringComparison.Ordinal))?[11..] ?? "zh-CN";
            var targetSlot = options.FirstOrDefault(a => a.StartsWith("--slot=", StringComparison.Ordinal))?[7..] ?? "zh-Hans";
            if (options.Any(a => a.StartsWith("--", StringComparison.Ordinal)
                && !a.StartsWith("--language=", StringComparison.Ordinal) && !a.StartsWith("--slot=", StringComparison.Ordinal)))
                throw new ArgumentException("Unknown UE preparation option");
            // Explicit destinations for this experimental CLI; preserve saved settings and disabled choices.
            var cachedOnly = args[0].EndsWith("-cached", StringComparison.Ordinal);
            foreach (var endpoint in EndpointCatalog.All.Where(e =>
                         cachedOnly || e.Id is not ("GoogleTranslateV2" or "BingTranslate")))
                settings.DisabledEndpoints.Add(endpoint.Id);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(args[7])));
            var lastReport = DateTime.MinValue;
            var sync = new object();
            var progress = new UeConsoleProgress(p =>
            {
                lock (sync)
                {
                    if (DateTime.UtcNow - lastReport < TimeSpan.FromSeconds(5) && p.FragmentsDone != p.FragmentsTotal) return;
                    lastReport = DateTime.UtcNow;
                    Console.WriteLine($"UE_PROGRESS\ttexts={p.Completed}/{p.Total}\tcached={p.Cached}\tfragments={p.FragmentsDone}/{p.FragmentsTotal}\tmissing={p.Failed}");
                }
            });
            var service = new UeStaticService(args[6]);
            var paths = options.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            var report = tables ? await service.PrepareTablesAsync(args[1], args[2], int.Parse(args[3]), args[4],
                paths, settings, progress, stop.Token, targetSlot)
                : await service.PrepareAsync(args[1], args[2], int.Parse(args[3]), args[4],
                paths, settings, progress, stop.Token,
                targetCulture: targetSlot);
            Console.WriteLine(JsonSerializer.Serialize(report));
            if (report.Incomplete) Environment.ExitCode = 2;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("UE_CANCELLED\tSuccessful fragment cache retained; no incomplete publication.");
            Environment.ExitCode = 2;
        }
        catch (Exception ex)
        {
            // Do not dump provider settings or credentials.
            Console.WriteLine($"UE_ERROR\t{ex.GetType().Name}\t{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private sealed class UeConsoleProgress(Action<StaticTextTranslationService.StaticTranslationProgress> report)
        : IProgress<StaticTextTranslationService.StaticTranslationProgress>
    {
        public void Report(StaticTextTranslationService.StaticTranslationProgress value) => report(value);
    }
}
