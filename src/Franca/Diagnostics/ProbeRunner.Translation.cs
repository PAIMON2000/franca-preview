using System.IO;
using System.Net.Http;
using System.Windows;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

internal static partial class ProbeRunner
{
    private static async Task RunProbeNamesAsync(string targetLanguage, string primaryEndpointId)
    {
        UiStrings.Current = targetLanguage;
        // 独立探针缓存文件，不污染 GUI 的 franca-names-cache.json
        var service = new GameNameService("franca-names-cache.probe.json");
        var games = new List<GameInfo>
        {
            new() { Name = "Blender", Path = "", AppId = 365670 },
            new() { Name = "Suzerain", Path = "", AppId = 1207650 },
            new() { Name = "The Witcher 3: Wild Hunt", Path = "", AppId = 292030 },
            // 无 appid 的纯名字用例：验证"无需翻译"短路（应 Source=original 且无网络延迟）
            new() { Name = "三国搜打撤", Path = "" },
            new() { Name = "十三州行动 HD", Path = "" },
            // 无 appid 的分段翻译用例：强制走机翻链
            new() { Name = "The Witcher 3: Wild Hunt", Path = "" },
            new() { Name = "ドキドキ文芸部", Path = "" },
            new() { Name = "Metro Exodus", Path = "" },
            new() { Name = "F1® 24", Path = "" },
            // 拉丁段两级降级用例：品牌名短语常被原样返回，应走词级路径
            new() { Name = "MarvelRivals", Path = "" },
            new() { Name = "Marvel Rivals", Path = "" },
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.ResolveAsync(games, targetLanguage, primaryEndpointId,
            new Dictionary<string, Dictionary<string, string>>(),
            game => Console.WriteLine($"{game.Name}\t{game.TranslatedName}\t{game.NameSource}\tOfficialSupport={game.HasOfficialSupport?.ToString() ?? "?"}\t{sw.ElapsedMilliseconds} ms\t{DescribeSegments(game.Name, targetLanguage)}"),
            m => Console.WriteLine("[log] " + m),
            CancellationToken.None);
        sw.Stop();
        Console.WriteLine($"total\t{sw.ElapsedMilliseconds} ms");
    }

    // 分段明细，如 [CJK-keep:十三州行动][Other: ][Latin-keep:HD]
    private static string DescribeSegments(string name, string targetLanguage) =>
        string.Concat(NameSegmenter.Segment(name, targetLanguage).Select(s =>
            $"[{s.Script}{(s.Script == NameScript.Other || s.Translate ? "" : "-keep")}:{s.Text}]"));

    private static async Task RunProbeTranslateAsync(string endpointId, string targetLanguage, string text,
        string sourceLanguage, int repeat, IReadOnlyDictionary<string, string>? keys)
    {
        for (var i = 1; i <= repeat; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await AppTranslator.TranslateAsync(endpointId, text, targetLanguage, keys, CancellationToken.None, sourceLanguage);
            sw.Stop();
            // 附带译名质量护栏判定，便于无头验证 GameNameService 的兜底行为
            var verdict = result is null ? "FAILED"
                : GameNameService.IsGarbageTranslation(text, result) ? "护栏=拦截" : "护栏=通过";
            var line = $"# probe-translate {DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{endpointId}\t{sourceLanguage}>{targetLanguage}\t{text}\t{result ?? "FAILED"}\t{verdict}\t{sw.ElapsedMilliseconds}ms";
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "franca-probe.log"), line + Environment.NewLine);
            }
            catch
            {
                // 日志落盘失败不影响退出
            }
            Console.WriteLine($"#{i}\t{result ?? "FAILED"}\t{verdict}\t{sw.ElapsedMilliseconds} ms");
        }
    }

    private static async Task RunProbeFreeAsync(string targetLanguage)
    {
        // 先并行预热各端点域名 DNS，避免首连税串行污染测速数字（不等完成）
        DnsWarmup.KickOff();
        var logPath = Path.Combine(AppContext.BaseDirectory, "franca-probe.log");
        var missing = UiStrings.MissingKeys().ToList();
        var lines = new List<string>
        {
            $"# probe-free {DateTime.Now:yyyy-MM-dd HH:mm:ss} target={targetLanguage}",
            missing.Count == 0
                ? "# uistrings: all languages complete"
                : "# uistrings missing: " + string.Join(", ", missing)
        };
        foreach (var def in EndpointCatalog.All.Where(d => d.Category == EndpointCategory.Free))
        {
            var outcome = await EndpointTestService.TestFreeAsync(def.Id, targetLanguage, CancellationToken.None);
            var latency = outcome.LatencyMs?.ToString() ?? "-";
            var detail = outcome.Detail.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            lines.Add($"{def.Id}\t{outcome.Status}\t{latency}\t{detail}");
        }
        try
        {
            File.AppendAllLines(logPath, lines);
        }
        catch
        {
            // 日志落盘失败不影响退出
        }
    }
}
