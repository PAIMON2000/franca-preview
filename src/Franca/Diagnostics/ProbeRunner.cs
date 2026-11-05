using System.IO;
using System.Net.Http;
using System.Text.Json;
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
        // 全局选项：--oodle=<本机 oo2core_*.dll 路径>，为 UE CLI 命令提供 Oodle 解压能力。
        var oodleArg = Array.Find(args, a => a.StartsWith("--oodle=", StringComparison.Ordinal));
        if (oodleArg != null)
        {
            args = args.Where(a => !a.StartsWith("--oodle=", StringComparison.Ordinal)).ToArray();
            var oodlePath = oodleArg[8..];
            try
            {
                Services.UePak.Oodle.TryLoad(oodlePath);
            }
            catch (Exception ex)
            {
                AttachConsole(-1);
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.WriteLine($"UE_ERROR\t无法加载 Oodle dll：{ex.Message}");
                app.Shutdown();
                Environment.ExitCode = 1;
                return true;
            }
        }
        if (args.Length > 0 && args[0] == "--probe-release-update")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await ReleaseUpdateProbe.RunAsync();
            app.Shutdown();
            return true;
        }
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
        if (args.Length > 0 && args[0] == "--probe-ue-fonts")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            UeFontProbe.Run();
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--probe-ue-install")
        {
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await UeInstallProbe.RunAsync();
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] is "--ue-install" or "--ue-uninstall" or "--ue-repair")
        {
            // Real game writes. Requires the game to be stopped.
            // --ue-install <gameDir> <prepared.pak> <pakDirRelative> <overrideFileName> <originalPakRelative> <originalSha256>
            // --ue-uninstall <gameDir> | --ue-repair <gameDir>
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                var service = new Franca.Services.UePakInstallService(args[1]);
                Franca.Services.UePakInstallService.Result result = args[0] switch
                {
                    "--ue-install" => args.Length == 7
                        ? service.Install(args[2], args[3], args[4], args[5], args[6], Console.WriteLine)
                        : throw new ArgumentException(
                            "--ue-install <gameDir> <prepared.pak> <pakDirRelative> <overrideFileName> <originalPakRelative> <originalSha256>"),
                    "--ue-uninstall" => service.Uninstall(Console.WriteLine),
                    _ => service.Repair(Console.WriteLine),
                };
                Console.WriteLine(JsonSerializer.Serialize(result));
                if (result.State is "not-installed") Environment.ExitCode = 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UE_ERROR\t{ex.GetType().Name}\t{ex.Message}");
                Environment.ExitCode = 1;
            }
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--ue-font-report")
        {
            // Read-only composite-chain health report: for every Font asset in the pak,
            // resolve its FontFace references to .ufont files in the same pak and report
            // characters missing from the whole fallback chain (union semantics).
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                if (args.Length < 3) throw new ArgumentException("--ue-font-report <pak> <textFile>... [--facesDir <dir>]");
                string? facesDir = args.FirstOrDefault(a => a.StartsWith("--facesDir=", StringComparison.Ordinal))?[11..];
                var files = args[2..].Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
                using var pak = Franca.Services.UePak.PakFileReader.Open(args[1]);
                var byFile = pak.Entries.GroupBy(e => e.Path.Split('/')[^1], StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
                var needed = Franca.Services.UeFontHealthService.NeededCodepoints(files.SelectMany(File.ReadLines));
                var reports = new List<object>();
                var unhealthy = 0; var skippedFaces = 0;
                foreach (var e in pak.Entries.Where(e => e.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
                {
                    var asset = Franca.Services.UePak.UeCookedPackageRefs.ReadFontAsset(
                        e.Path.Split('/')[^1][..^".uasset".Length], pak.Extract(e));
                    if (asset.FontFaces.Count == 0) continue;
                    var dir = e.Path[..(e.Path.LastIndexOf('/') + 1)];
                    var faces = new List<(string Name, byte[] Data)>();
                    var missing = new List<string>();
                    foreach (var face in asset.FontFaces)
                    {
                        byte[]? data = null;
                        if (facesDir is not null)
                        {
                            var local = Path.Combine(facesDir, face + ".ttf");
                            var grafted = Path.Combine(facesDir, "graft-" + face + ".ttf");
                            if (File.Exists(grafted)) data = File.ReadAllBytes(grafted);
                            else if (File.Exists(local)) data = File.ReadAllBytes(local);
                        }
                        data ??= (pak.Find(dir + face + ".ufont") ?? byFile.GetValueOrDefault(face + ".ufont")?.FirstOrDefault()) is { } entry
                            ? pak.Extract(entry) : null;
                        if (data is null) { missing.Add(face); continue; }
                        faces.Add((face, data));
                    }
                    if (missing.Count > 0) { skippedFaces++; reports.Add(new { Asset = e.Path, Note = "faces not in pak/dir: " + string.Join(", ", missing) }); continue; }
                    var report = Franca.Services.UeFontHealthService.AnalyzeChain(e.Path, faces, needed);
                    if (!report.Healthy) unhealthy++;
                    reports.Add(report);
                }
                Console.WriteLine(JsonSerializer.Serialize(reports,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                Console.WriteLine($"UE_FONT_REPORT\tchains={reports.Count(r => r is Franca.Services.UeFontHealthService.ChainReport)}\tskipped={skippedFaces}\tunhealthy={unhealthy}");
                if (unhealthy > 0) Environment.ExitCode = 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UE_ERROR\t{ex.GetType().Name}\t{ex.Message}");
                Environment.ExitCode = 1;
            }
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--ue-font-refs")
        {
            // Read-only: list every Font composite asset in a pak with its ordered
            // FontFace references (default typeface first, then fallbacks).
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                if (args.Length != 2) throw new ArgumentException("--ue-font-refs <pak>");
                using var pak = Franca.Services.UePak.PakFileReader.Open(args[1]);
                var result = new List<object>();
                foreach (var e in pak.Entries.Where(e => e.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
                {
                    var asset = Franca.Services.UePak.UeCookedPackageRefs.ReadFontAsset(
                        e.Path.Split('/')[^1][..^".uasset".Length], pak.Extract(e));
                    if (asset.FontFaces.Count > 0)
                        result.Add(new { Asset = e.Path, asset.FontFaces,
                            FacePaths = asset.FontFaces.ToDictionary(f => f, f => asset.PathOf(f)) });
                }
                Console.WriteLine(JsonSerializer.Serialize(result,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UE_ERROR\t{ex.GetType().Name}\t{ex.Message}");
                Environment.ExitCode = 1;
            }
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--ue-font-routes")
        {
            // Read-only routing approximation: for every *FontTable* asset in the pak,
            // list the Font asset paths referenced by its rows (cooked name table scan).
            // TalkFontTable_<LANG> decides which font chains serve talk/balloon text
            // per game language, so this narrows chain-level gaps to routed ones.
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                if (args.Length != 2) throw new ArgumentException("--ue-font-routes <pak>");
                using var pak = Franca.Services.UePak.PakFileReader.Open(args[1]);
                var result = new List<object>();
                foreach (var e in pak.Entries.Where(e => e.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                         && e.Path.Split('/')[^1].Contains("FontTable", StringComparison.OrdinalIgnoreCase)))
                {
                    var names = Franca.Services.UePak.UeCookedPackageRefs.ReadNames(pak.Extract(e));
                    var fonts = names.Where(n => n.StartsWith("/Game/", StringComparison.Ordinal)
                        && n.Contains("FONT_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
                    result.Add(new { Table = e.Path, Fonts = fonts });
                }
                Console.WriteLine(JsonSerializer.Serialize(result,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UE_ERROR\t{ex.GetType().Name}\t{ex.Message}");
                Environment.ExitCode = 1;
            }
            app.Shutdown();
            return true;
        }
        if (args.Length > 0 && args[0] == "--ue-font-check")
        {
            // Read-only inspection: parse every .ufont/.ttf in a directory and report
            // cmap coverage against the characters found in the given text files.
            AttachConsole(-1);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                if (args.Length < 3) throw new ArgumentException("--ue-font-check <fontsDir> <textFile>...");
                var faces = Directory.GetFiles(args[1], "*.*")
                    .Where(p => p.EndsWith(".ufont", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                    .Select(p => (Name: Path.GetFileNameWithoutExtension(p), Data: File.ReadAllBytes(p)))
                    .ToArray();
                if (faces.Length == 0) throw new ArgumentException("No .ufont/.ttf faces found");
                var texts = args[2..].SelectMany(f => File.ReadLines(f));
                var report = UeFontHealthService.Analyze(faces, texts);
                Console.WriteLine(JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                if (!report.Healthy) Environment.ExitCode = 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UE_ERROR\t{ex.GetType().Name}\t{ex.Message}");
                Environment.ExitCode = 1;
            }
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
