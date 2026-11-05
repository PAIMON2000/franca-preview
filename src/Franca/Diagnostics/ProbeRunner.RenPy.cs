using System.IO;
using System.Text.Json;
using Franca.Services;

namespace Franca.Diagnostics;

internal static partial class ProbeRunner
{
    // Explicit Ren'Py commands, mirroring the UE preparation CLI shape.
    // --renpy-prepare <gameDir> <appId> <outputDir> <settings.json> <packsDir> <seconds> [--language=zh-CN]
    // --renpy-install <gameDir> <preparedDir>   (game stopped)
    // --renpy-uninstall <gameDir>               (game stopped)
    private static IEnumerable<string> DumpPyCode(object? node)
    {
        switch (node)
        {
            case Services.RenPy.PyFake f:
                if (f.Name == "PyCode")
                {
                    var code = Services.RenPy.RenPyStringExtractor.PyStr(f.Get("source"))
                        ?? (f.State.TryGetValue("__state__", out var st) && st is Services.RenPy.PyTuple { Count: >= 2 } t
                            ? Services.RenPy.RenPyStringExtractor.PyStr(t[1]) : null);
                    if (code is not null) yield return code;
                }
                foreach (var kv in f.State)
                {
                    if (kv.Value is Services.RenPy.PyFake or Services.RenPy.PyTuple or List<object?>)
                        foreach (var s in DumpPyCode(kv.Value)) yield return s;
                }
                break;
            case Services.RenPy.PyTuple tuple:
                foreach (var item in tuple)
                    foreach (var s in DumpPyCode(item)) yield return s;
                break;
            case List<object?> list:
                foreach (var item in list)
                    foreach (var s in DumpPyCode(item)) yield return s;
                break;
        }
    }

    private static async Task RunRenPyCommandAsync(string[] args)
    {
        try
        {
            if (args[0] == "--renpy-prepare")
            {
                if (args.Length < 7) throw new ArgumentException(
                    "--renpy-prepare <gameDir> <appId> <outputDir> <settings.json> <packsDir> <seconds> [--language=zh-CN]");
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[4]))
                    ?? throw new InvalidDataException("Invalid settings file");
                settings = settings.Snapshot();
                settings.SourceLanguage = "en";
                settings.TargetLanguage = args.FirstOrDefault(a => a.StartsWith("--language=", StringComparison.Ordinal))?[11..] ?? "zh-CN";
                foreach (var endpoint in EndpointCatalog.All.Where(e => e.Id is not ("GoogleTranslateV2" or "BingTranslate")))
                    settings.DisabledEndpoints.Add(endpoint.Id);
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(args[6])));
                var lastReport = DateTime.MinValue;
                var sync = new object();
                var progress = new UeConsoleProgress(p =>
                {
                    lock (sync)
                    {
                        if (DateTime.UtcNow - lastReport < TimeSpan.FromSeconds(5) && p.FragmentsDone != p.FragmentsTotal) return;
                        lastReport = DateTime.UtcNow;
                        Console.WriteLine($"RENPY_PROGRESS\ttexts={p.Completed}/{p.Total}\tcached={p.Cached}\tfragments={p.FragmentsDone}/{p.FragmentsTotal}\tmissing={p.Failed}");
                    }
                });
                var service = new Services.RenPy.RenPyStaticService(args[5]);
                var report = await service.PrepareAsync(args[1], int.Parse(args[2]), args[3], settings, progress, stop.Token);
                Console.WriteLine(JsonSerializer.Serialize(report));
                if (report.Incomplete) Environment.ExitCode = 2;
                return;
            }
            if (args[0] == "--renpy-install")
            {
                if (args.Length != 3) throw new ArgumentException("--renpy-install <gameDir> <preparedDir>");
                var manifest = Path.Combine(Path.GetFullPath(args[2]), "manifest.json");
                var result = new Services.RenPy.RenPyInstallService(args[1]).Install(manifest, Console.WriteLine);
                Console.WriteLine(JsonSerializer.Serialize(result));
                return;
            }
            if (args.Length != 2) throw new ArgumentException("--renpy-uninstall <gameDir>");
            var uninstall = new Services.RenPy.RenPyInstallService(args[1]).Uninstall(Console.WriteLine);
            Console.WriteLine(JsonSerializer.Serialize(uninstall));
            if (uninstall.State is "not-installed") Environment.ExitCode = 2;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("RENPY_CANCELLED\tSuccessful fragment cache retained; no incomplete publication.");
            Environment.ExitCode = 2;
        }
        catch (Exception ex)
        {
            // Do not dump provider settings or credentials.
            Console.WriteLine($"RENPY_ERROR\t{ex.GetType().Name}\t{ex.Message}");
            Environment.ExitCode = 1;
        }
    }
}
