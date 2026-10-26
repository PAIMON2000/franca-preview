using System.IO;
using System.Net.Http;
using System.Windows;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

internal static partial class ProbeRunner
{
    private static async Task RunProbeRelayAsync()
    {
        const int probeAppId = 999999;
        var packDir = Path.Combine(AppContext.BaseDirectory, "packs", probeAppId.ToString());
        try { Directory.Delete(packDir, recursive: true); } catch { }

        var settings = new AppSettings { TargetLanguage = "zh-CN" };
        using var relay = new RelayServer(() => settings.Snapshot());
        await relay.EnsureStartedAsync();
        Console.WriteLine($"RESULT\tRELAY_UP\tport={relay.Port}\tpid={Environment.ProcessId}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        async Task<string> TranslateAsync(string text)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await http.GetStringAsync(
                $"{relay.UrlForGame(probeAppId)}?from=en&to=zh-CN&text={Uri.EscapeDataString(text)}");
            sw.Stop();
            return $"{sw.ElapsedMilliseconds}ms\t{result}";
        }

        async Task<string> NewClientTranslateAsync(string text)
        {
            using var fresh = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await fresh.GetStringAsync(
                $"{relay.UrlForGame(probeAppId)}?from=en&to=zh-CN&text={Uri.EscapeDataString(text)}");
            sw.Stop();
            return $"{sw.ElapsedMilliseconds}ms\t{result}";
        }

        Console.WriteLine($"RESULT\tRELAY_MISS\t{await TranslateAsync("hello")}");
        // 每个请求用新连接，贴近 XUnity 实际行为（也避免复用连接干扰隔离变量）
        Console.WriteLine($"RESULT\tRELAY_HIT\t{await NewClientTranslateAsync("hello")}");
        Console.WriteLine($"RESULT\tRELAY_HIT2\t{await NewClientTranslateAsync("hello")}");
        Console.WriteLine($"RESULT\tRELAY_MISS2\t{await NewClientTranslateAsync("Load")}");
        Console.WriteLine($"RESULT\tRELAY_STATS\tserved={relay.ServedCount}\thits={relay.CacheHits}\thitrate={relay.HitRate:0.##}");

        using (var notFound = await http.GetAsync($"http://127.0.0.1:{relay.Port}/nope"))
            Console.WriteLine($"RESULT\tRELAY_404\t{(int)notFound.StatusCode}");

        var entriesDir = Path.Combine(packDir, "zh-CN", "entries");
        if (Directory.Exists(entriesDir))
        {
            foreach (var file in Directory.GetFiles(entriesDir, "*.jsonl"))
                foreach (var line in File.ReadLines(file))
                    Console.WriteLine($"RESULT\tJSONL\t{Path.GetFileName(file)}\t{line}");
        }
        else
        {
            Console.WriteLine("RESULT\tJSONL\tMISSING entries dir");
        }

        relay.Stop();
        try { Directory.Delete(packDir, recursive: true); } catch { }
        Console.WriteLine("RESULT\tRELAY_DOWN\tstopped+cleaned");
    }

}
