using System.IO;
using System.Net.Http;
using System.Windows;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

internal static partial class ProbeRunner
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    public static async Task<bool> TryRunAsync(App app, string[] args)
    {
        if (args.Length > 0 && args[0] is "--ue-inspect" or "--ue-prepare" or "--ue-prepare-cached" or "--probe-ue-static"
            or "--ue-table-inspect" or "--ue-table-prepare" or "--ue-table-prepare-cached")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args[0] == "--probe-ue-static") await UeStaticProbe.RunAsync();
            else await RunUeCommandAsync(args);
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-language-apply")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await LanguageApplyProbe.RunAsync();
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] is "--probe-installation" or "--probe-overlay")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args[0] == "--probe-installation") InstallationProbe.Run();
            else await OverlayProbe.RunAsync(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "franca-overlay-artifacts"));
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] is "--probe-endpoint-config" or "--probe-deepseek")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await EndpointConfigurationProbe.RunAsync(args[0] == "--probe-deepseek");
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-segmentation")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await SegmentationProbe.RunAsync();
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-touhou-static")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await TouhouStaticProbe.RunAsync();
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-touhou-thcrap")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await TouhouThcrapProbe.RunAsync();
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-base-tsa-fetch")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await BaseTsaFetchProbe.RunAsync();
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-ue-pak")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            UePakProbe.Run(args.Length > 1 ? args[1] : null);
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-touhou-live-uninstall")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await LiveUninstallProbe.RunAsync(enableMissingOnly: args.Contains("--enable-missing"));
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-architecture")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await ArchitectureProbe.RunAsync();
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-dictionary")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await DictionaryProbe.RunAsync();
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-library")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await LibraryProbe.RunAsync(args.Length > 1 ? args[1] : null);
            app.Shutdown();
            return true;
        }
        // 无头测速模式：Franca.exe --probe-free [lang]，不创建 MainWindow
        if (args.Length > 0 && args[0] == "--probe-free")
        {
            var lang = args.Length > 1 ? args[1] : "zh-CN";
            await RunProbeFreeAsync(lang);
            app.Shutdown();
            return true;
        }
        // 无头任意文本翻译：Franca.exe --probe-translate <endpointId> <lang> <text...> [--source <lang>]
        if (args.Length > 0 && args[0] == "--probe-translate" && args.Length >= 4)
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var rest = args.Skip(3).ToList();
            var sourceLanguage = "en";
            var repeat = 1;
            var keys = new Dictionary<string, string>();
            var sourceIndex = rest.IndexOf("--source");
            if (sourceIndex >= 0 && sourceIndex + 1 < rest.Count)
            {
                sourceLanguage = rest[sourceIndex + 1];
                rest.RemoveRange(sourceIndex, 2);
            }
            var repeatIndex = rest.IndexOf("--repeat");
            if (repeatIndex >= 0 && repeatIndex + 1 < rest.Count
                && int.TryParse(rest[repeatIndex + 1], out var parsedRepeat) && parsedRepeat > 0)
            {
                repeat = parsedRepeat;
                rest.RemoveRange(repeatIndex, 2);
            }
            // BYOK/LLM 端点协议验证用：--key <ApiKey> [--baseurl <url>] [--model <model>]
            foreach (var (flag, field) in new[] { ("--key", "ApiKey"), ("--baseurl", "BaseUrl"), ("--model", "Model") })
            {
                var flagIndex = rest.IndexOf(flag);
                if (flagIndex >= 0 && flagIndex + 1 < rest.Count)
                {
                    keys[field] = rest[flagIndex + 1];
                    rest.RemoveRange(flagIndex, 2);
                }
            }
            await RunProbeTranslateAsync(args[1], args[2], string.Join(' ', rest), sourceLanguage, repeat, keys);
            app.Shutdown();
            return true;
        }
        // 无头译名解析：Franca.exe --probe-names <lang> <primaryEndpointId>
        if (args.Length > 0 && args[0] == "--probe-names" && args.Length >= 3)
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await RunProbeNamesAsync(args[1], args[2]);
            app.Shutdown();
            return true;
        }
        // 无头可逆停用验证：Franca.exe --probe-toggle（在 exe 目录下造临时目录，不碰任何真实游戏）
        if (args.Length > 0 && args[0] == "--probe-toggle")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunProbeToggle();
            app.Shutdown();
            return true;
        }
        // 无头配置健康检查探针：构造错误 XUnity 配置 → 检查 → 备份修复 → 再检查
        if (args.Length > 0 && args[0] == "--probe-health")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunProbeHealth();
            app.Shutdown();
            return true;
        }
        // 无头中继验证：Franca.exe --probe-relay（自启中继、自发自收、缓存命中、JSONL 落盘）
        if (args.Length > 0 && args[0] == "--probe-relay")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await RunProbeRelayAsync();
            app.Shutdown();
            return true;
        }
        // 无头启动鸣谢目录与源语言设置验证，不打开浏览器或真实窗口
        if (args.Length > 0 && args[0] == "--probe-navigation")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await RunProbeNavigationAsync(args.Length > 1 ? args[1] : null);
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-startup")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args.Length > 2)
                UiStrings.Current = args[2];
            RunProbeStartup(args.Length > 1 ? args[1] : null);
            app.Shutdown();
            return true;
        }
        // 蓝图契约图：Franca.exe --probe-blueprint [repoRoot]（扫描真实跨模块引用、对账 dependsOn、重建 docs/BLUEPRINT.md）
        if (args.Length > 0 && args[0] == "--probe-blueprint")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Environment.ExitCode = BlueprintProbe.Run(args.Length > 1 ? args[1] : null);
            app.Shutdown();
            return true;
        }
        // 无头已知错误目录、系统语言与检测规则验证，不触碰真实进程
        if (args.Length > 0 && args[0] == "--probe-known-issues")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunProbeKnownIssues();
            app.Shutdown();
            return true;
        }
        // 无头对话框逻辑验证：Franca.exe --probe-dialog
        if (args.Length > 0 && args[0] == "--probe-dialog")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            RunProbeDialog();
            app.Shutdown();
            return true;
        }
        // 无头有道原始响应：Franca.exe --probe-youdao-raw <text...>（打印解密后的完整 JSON）
        if (args.Length > 0 && args[0] == "--probe-youdao-raw" && args.Length >= 2)
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await YoudaoWebTranslator.DumpRawAsync(string.Join(' ', args.Skip(1)), "zh-CN", CancellationToken.None);
            app.Shutdown();
            return true;
        }
        return false;
    }
}
