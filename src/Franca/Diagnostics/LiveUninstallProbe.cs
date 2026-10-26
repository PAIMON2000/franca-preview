using System.IO;
using System.Text.Json;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

// 真实往返验收（--probe-touhou-live-uninstall）：仅对用户授权的 th10/th11 执行一次——
// 已安装 → 卸载（从 .frorig 还原并清理产物）→ 重新启用（缓存增量、同语言）。
// 结束必须回到「已安装且与测试前同语言」；任一环节失败即 FAIL 并保留现场供人工处理。
internal static class LiveUninstallProbe
{
    private static string Th10Dir => ProbePaths.GameDir("th10");
    private static string Th11Dir => ProbePaths.GameDir("th11");

    public static async Task RunAsync(bool enableMissingOnly = false)
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tLIVEUNINSTALL\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition) failures++;
        }

        await RoundTrip(Th10Dir, "th10", enableMissingOnly, Check);
        await RoundTrip(Th11Dir, "th11", enableMissingOnly, Check);
        Console.WriteLine($"RESULT\tLIVEUNINSTALL\tfailures={failures}");
    }

    private static async Task RoundTrip(string gameDir, string name, bool enableMissingOnly, Action<string, bool> check)
    {
        var statePath = Path.Combine(gameDir, ".franca", "touhou-static.json");
        if (!File.Exists(statePath))
        {
            if (enableMissingOnly && File.Exists(Path.Combine(gameDir, name + ".dat")))
            {
                // 仅补装：卸载往返被中断后的收尾（同语言简中，缓存增量）
                var resumeSettings = new AppSettings { TargetLanguage = "zh-CN", SourceLanguage = "ja" };
                var resumeGame = new GameInfo { Name = name, Path = gameDir, Engine = GameEngine.Touhou, AppId = null };
                await TouhouStaticService.EnableAsync(resumeGame, resumeSettings, new Progress<string>(_ => { }));
                check($"{name}_enable_missing", TouhouStaticService.IsReady(gameDir));
                return;
            }
            check($"{name}_skipped_not_installed", true);
            return;
        }
        if (enableMissingOnly) { check($"{name}_already_installed", true); return; }
        var before = JsonSerializer.Deserialize<TouhouStaticService.TouhouState>(File.ReadAllText(statePath));
        if (before is null || string.IsNullOrWhiteSpace(before.Language) || string.IsNullOrWhiteSpace(before.SourceHash))
        {
            check($"{name}_state_readable", false);
            return;
        }
        var dat = Path.Combine(gameDir, before.Dat);
        var datHashBefore = InstallationTransaction.Hash(dat);
        check($"{name}_pre_installed", TouhouStaticService.IsReady(gameDir)
            && datHashBefore != before.SourceHash); // 补丁态（数据包 ≠ 原包哈希）

        // 卸载：还原 + 清理
        var report = await TouhouStaticService.UninstallAsync(gameDir, _ => { });
        check($"{name}_uninstall_restored", report.DatRestored
            && InstallationTransaction.Hash(dat) == before.SourceHash);
        check($"{name}_uninstall_cleaned", !TouhouStaticService.HasArtifacts(gameDir)
            && !File.Exists(dat + ".frorig") && !File.Exists(statePath)
            && !TouhouStaticService.IsReady(gameDir));

        // 重新启用：真实端点（全部命中既有缓存，仅个别未缓存行走网络）、同语言
        var settings = new AppSettings { TargetLanguage = before.Language, SourceLanguage = "ja" };
        var game = new GameInfo { Name = name, Path = gameDir, Engine = GameEngine.Touhou, AppId = null };
        await TouhouStaticService.EnableAsync(game, settings, new Progress<string>(_ => { }));
        var after = TouhouStaticService.IsReady(gameDir)
            ? JsonSerializer.Deserialize<TouhouStaticService.TouhouState>(File.ReadAllText(statePath)) : null;
        check($"{name}_reenabled_same_language", after is not null
            && after.Language == before.Language
            && InstallationTransaction.Hash(dat) != before.SourceHash);
    }
}
