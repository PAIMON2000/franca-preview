using System.IO;
using System.Text;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

// 东方静态翻译适配器烟测（--probe-touhou-static）。
// 只读访问真实 th10/th11 目录做 Supports/IsReady 判定；完整启停管线跑在
// 临时目录的 th10.dat 副本上（翻译打桩为前缀），验证 解包→提取→回封→重打包
// 与 备份/停用/恢复/幂等，不修改任何真实游戏目录。
internal static class TouhouStaticProbe
{
    private static string Th10Dir => ProbePaths.GameDir("th10");
    private static string Th11Dir => ProbePaths.GameDir("th11");

    public static async Task RunAsync()
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tTOUHOU\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition) failures++;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sjis = Encoding.GetEncoding(932);
        var thdat = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thtk", "thdat.exe");
        var thmsg = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thtk", "thmsg.exe");

        // 1) 真实游戏目录只读判定
        GameInfo Real(string path) => new() { Name = Path.GetFileName(path), Path = path, Engine = GameEngine.Touhou };
        var th10 = Real(Th10Dir);
        var th11 = Real(Th11Dir);
        Check("thtk_bundled", File.Exists(thdat) && File.Exists(thmsg));
        Check("supports_th10_readonly", TouhouStaticService.FindTargetDat(th10) is not null);
        Check("supports_th11_readonly", TouhouStaticService.FindTargetDat(th11) is not null);
        // th10/th11 可能已被真实启用过：IsReady 必须与磁盘产物（状态文件 + .frorig 备份）一致
        foreach (var (dir, name, datName) in new[]
                 {
                     (Th10Dir, "th10", "th10.dat"),
                     (Th11Dir, "th11", "th11.dat"),
                 })
        {
            var installed = File.Exists(Path.Combine(dir, ".franca", "touhou-static.json"))
                && File.Exists(Path.Combine(dir, datName + ".frorig"));
            Check($"isready_{name}_consistent", TouhouStaticService.IsReady(dir) == installed);
        }
        var th06 = Directory.Exists(ProbePaths.GameDir("th06c"))
            ? Real(ProbePaths.GameDir("th06c")) : null;
        if (th06 is not null)
            Check("pkgl_th06_not_supported", TouhouStaticService.FindTargetDat(th06) is null);

        // 2) 临时副本上的端到端启停（翻译打桩：按行加前缀 【F】），同时收集结构化安装进度。
        // 主用端点设为 LLM 类以覆盖 20 条/批的批量路径；桩记录收到的请求文本。
        var root = Path.Combine(Path.GetTempPath(), "franca-touhou-probe-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(root, "th10");
        Directory.CreateDirectory(gameDir);
        var dat = Path.Combine(gameDir, "th10.dat");
        File.Copy(Path.Combine(Th10Dir, "th10.dat"), dat);
        var originalHash = InstallationTransaction.Hash(dat);
        var logs = new List<string>();
        var collect = new Collect(logs);
        var progressLog = new List<TouhouStaticService.TouhouInstallProgress>();
        var progressCollect = new Collect<TouhouStaticService.TouhouInstallProgress>(progressLog);
        var settings = new AppSettings { TargetLanguage = "zh-CN", SourceLanguage = "ja", Endpoint = "LlmDeepSeek" };
        var game = new GameInfo { Name = "th10", Path = gameDir, Engine = GameEngine.Touhou, AppId = null };
        var stubRequests = new List<string>();
        TouhouStaticService.TranslatorOverride = (text, _, _, _) =>
        {
            lock (stubRequests) stubRequests.Add(text);
            return Task.FromResult<string?>(string.Join("\n",
                text.Replace("\r\n", "\n").Split('\n').Select(l => "【F】" + l)));
        };

        try
        {
            await TouhouStaticService.EnableAsync(game, settings, collect, installProgress: progressCollect);
            Check("enable_state_and_backup",
                TouhouStaticService.IsReady(gameDir)
                && File.Exists(dat + ".frorig")
                && File.Exists(Path.Combine(gameDir, ".franca", "touhou-static.json")));
            Check("original_backed_up", InstallationTransaction.Hash(dat + ".frorig") == originalHash);

            // 2b) 进度断言：单调不减、首位覆盖、结尾 100、字节文本格式、权重合成
            var strings = 0;
            lock (logs)
                foreach (var l in logs)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(l, "共 (\\d+) 条文本|scripts, (\\d+) text lines");
                    if (m.Success) strings = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                }
            var events = progressCollect.Snapshot();
            Check("progress_reported", events.Count >= 5 && strings > 0);
            Check("progress_monotonic", events.Zip(events.Skip(1))
                .All(pair => pair.Second.Percent >= pair.First.Percent - 1e-9));
            Check("progress_reaches_100", events.Count > 0 && events[^1].Percent == 100.0);
            Check("progress_bytetext_format", events.All(p =>
                System.Text.RegularExpressions.Regex.IsMatch(p.ByteText, "^（[0-9.]+MB/[0-9.]+MB）$")
                || System.Text.RegularExpressions.Regex.IsMatch(p.ByteText, "^（翻译 [0-9]+/[0-9]+ 条）$")));
            // 阶段等额权重：解包完成 = 25%；首个事件为初始 0，次个为解包完成
            Check("progress_extract_weight", events.Count >= 2 && Math.Abs(events[1].Percent - 25.0) < 0.01);
            // 翻译阶段等额权重 25–50%：条数 N>0 的事件百分比必须落在 (25,50] 区间
            var translateEvents = events
                .Where(e => e.ByteText.Contains("（翻译 ", StringComparison.Ordinal))
                .Select(e => (Event: e, Count: long.Parse(System.Text.RegularExpressions.Regex
                    .Match(e.ByteText, "（翻译 ([0-9]+)/").Groups[1].Value)))
                .Where(t => t.Count > 0).Select(t => t.Event).ToList();
            Check("progress_translate_phase_band", translateEvents.Count >= 2
                && translateEvents.All(e => e.Percent > 25.0 && e.Percent <= 50.0)
                && translateEvents.Any(e => e.Percent > 25.0 && e.Percent < 50.0));
            // GameInfo 进度属性：设置后行内文本成形，清除后即隐藏
            game.SetInstallProgress(events[^1].Percent, events[^1].ByteText);
            var shown = game.IsInstalling && game.InstallProgressText.EndsWith(events[^1].ByteText);
            game.SetInstallProgress(null, "");
            Check("gameinfo_progress_set_and_clear", shown && !game.IsInstalling
                && game.InstallPercent is null && game.InstallProgressText == "");

            // 3) 重打包产物校验：可列目录、对话行数与原文一致、全部带前缀
            var repack = Path.Combine(root, "verify-extract");
            Directory.CreateDirectory(repack);
            Run(thdat, $"-l 10 \"{dat}\"", repack);
            Run(thdat, $"-x 10 \"{dat}\"", repack);
            var origDir = Path.Combine(root, "orig-extract");
            Directory.CreateDirectory(origDir);
            File.Copy(Path.Combine(Th10Dir, "th10.dat"), Path.Combine(origDir, "th10.dat"));
            Run(thdat, "-x 10 th10.dat", origDir);

            var countOk = true;
            var prefixOk = true;
            var totalLines = 0;
            foreach (var msg in Directory.GetFiles(origDir, "*.msg").Select(f => Path.GetFileName(f)!))
            {
                var (origLines, origDirective) = DumpAny(thmsg, 10, Path.Combine(origDir, msg));
                var (newLines, newDirective) = DumpAny(thmsg, 10, Path.Combine(repack, msg));
                if (origLines is null || newLines is null || origDirective != newDirective)
                {
                    countOk = false;
                    continue;
                }
                if (CountDialogue(newLines, origDirective) != CountDialogue(origLines, origDirective))
                    countOk = false;
                totalLines += CountDialogue(origLines, origDirective);
                foreach (var line in newLines)
                    if (line.StartsWith(origDirective, StringComparison.Ordinal)
                        && !line[origDirective.Length..].StartsWith("【F】", StringComparison.Ordinal)) prefixOk = false;
            }
            Check("dialogue_line_counts_match", countOk);
            Check("dialogue_all_prefixed", prefixOk && totalLines > 0);
            // 批量路径断言：桩收到过换行合并的批量请求，且拆分后行数一致（全部带前缀已证）
            lock (stubRequests)
                Check("llm_batch_used", stubRequests.Any(r => r.Contains('\n')));

            // 6) LLM 批量行数不一致回退：合并响应只有一行 → 该批逐条重试，最终仍全部带前缀
            var dirC = Path.Combine(root, "th10-mismatch");
            Directory.CreateDirectory(dirC);
            var datC = Path.Combine(dirC, "th10.dat");
            File.Copy(Path.Combine(Th10Dir, "th10.dat"), datC);
            var requestsC = new List<string>();
            TouhouStaticService.TranslatorOverride = (text, _, _, _) =>
            {
                lock (requestsC) requestsC.Add(text);
                return Task.FromResult<string?>(text.Contains('\n') ? "merged-single-line"
                    : "【F】" + text);
            };
            await TouhouStaticService.EnableAsync(
                new GameInfo { Name = "th10c", Path = dirC, Engine = GameEngine.Touhou, AppId = null },
                settings, new Collect(new List<string>()));
            List<string> snapshotC;
            lock (requestsC) snapshotC = requestsC.ToList();
            Check("mismatch_fallback_attempted", snapshotC.Any(r => r.Contains('\n'))
                && snapshotC.Any(r => !r.Contains('\n'))); // 批量请求与逐条回退都发生
            var checkDir = Path.Combine(root, "mismatch-check");
            Directory.CreateDirectory(checkDir);
            Run(thdat, $"-x 10 \"{datC}\" st01_00.msg", checkDir);
            var (cLines, cDirective) = DumpAny(thmsg, 10, Path.Combine(checkDir, "st01_00.msg"));
            Check("mismatch_fallback_prefixed", cLines is not null && cDirective.Length > 0
                && cLines.Count(l => l.StartsWith(cDirective, StringComparison.Ordinal)
                    && l[cDirective.Length..].StartsWith("【F】", StringComparison.Ordinal)) > 0);

            // 7) 端点全熔断：桩全失败 → 安装仍完成、状态 Incomplete、日志明确报告；换桩后续译增量补齐
            var dirB = Path.Combine(root, "th10-breaker");
            Directory.CreateDirectory(dirB);
            var datB = Path.Combine(dirB, "th10.dat");
            File.Copy(Path.Combine(Th10Dir, "th10.dat"), datB);
            TouhouStaticService.TranslatorOverride = (_, _, _, _) => Task.FromResult<string?>(null);
            var logsB = new List<string>();
            var progressB = new List<TouhouStaticService.TouhouInstallProgress>();
            var gameB = new GameInfo { Name = "th10b", Path = dirB, Engine = GameEngine.Touhou, AppId = null };
            await TouhouStaticService.EnableAsync(gameB, settings, new Collect(logsB),
                installProgress: new Collect<TouhouStaticService.TouhouInstallProgress>(progressB));
            Check("breaker_early_end_completes",
                TouhouStaticService.IsReady(dirB) && progressB.Count > 0 && progressB[^1].Percent == 100.0);
            Check("breaker_reported", logsB.Any(l =>
                l.Contains("all endpoints unavailable", StringComparison.OrdinalIgnoreCase)));
            var stateJson = File.ReadAllText(Path.Combine(dirB, ".franca", "touhou-static.json"));
            Check("breaker_marks_incomplete", stateJson.Contains("\"Incomplete\": true"));
            // 续译：同语言再启用走缓存增量，未译行补上前缀，Incomplete 清除
            TouhouStaticService.TranslatorOverride = (text, _, _, _) =>
                Task.FromResult<string?>(string.Join("\n",
                    text.Replace("\r\n", "\n").Split('\n').Select(l => "【F】" + l)));
            var logsB2 = new List<string>();
            await TouhouStaticService.EnableAsync(gameB, settings, new Collect(logsB2));
            Check("resume_retranslates", logsB2.Any(l =>
                    l.Contains("continuing translation", StringComparison.OrdinalIgnoreCase))
                && !File.ReadAllText(Path.Combine(dirB, ".franca", "touhou-static.json"))
                    .Contains("\"Incomplete\": true"));
            var resumeDir = Path.Combine(root, "resume-check");
            Directory.CreateDirectory(resumeDir);
            Run(thdat, $"-x 10 \"{datB}\" st01_00.msg", resumeDir);
            var (bLines, bDirective) = DumpAny(thmsg, 10, Path.Combine(resumeDir, "st01_00.msg"));
            Check("resume_prefixed", bLines is not null && bDirective.Length > 0
                && bLines.Count(l => l.StartsWith(bDirective, StringComparison.Ordinal)
                    && l[bDirective.Length..].StartsWith("【F】", StringComparison.Ordinal)) > 0);

            // 8) 卸载（临时夹具，真实翻译桩）：已安装 → 卸载 → 数据包字节与 .frorig 一致、产物清空、
            //    IsReady=false → 可再启用且缓存命中（零请求）→ state 丢失/dat 损坏两种部分损坏也能恢复
            var dirD = Path.Combine(root, "th10-uninstall");
            Directory.CreateDirectory(dirD);
            var datD = Path.Combine(dirD, "th10.dat");
            File.Copy(Path.Combine(Th10Dir, "th10.dat"), datD);
            var hashD = InstallationTransaction.Hash(datD);
            var settingsD = new AppSettings { TargetLanguage = "zh-CN", SourceLanguage = "ja", Endpoint = "LlmDeepSeek" };
            var gameD = new GameInfo { Name = "th10d", Path = dirD, Engine = GameEngine.Touhou, AppId = 999001 };
            TouhouStaticService.PacksRootForProbe = Path.Combine(root, "probe-packs");
            TouhouStaticService.TranslatorOverride = (text, _, _, _) =>
                Task.FromResult<string?>(string.Join("\n",
                    text.Replace("\r\n", "\n").Split('\n').Select(l => "【F】" + l)));
            await TouhouStaticService.EnableAsync(gameD, settingsD, new Collect(new List<string>()));
            Check("uninstall_preconditions", TouhouStaticService.IsReady(dirD)
                && File.Exists(datD + ".frorig") && InstallationTransaction.Hash(datD) != hashD);

            var report = await TouhouStaticService.UninstallAsync(dirD, _ => { });
            Check("uninstall_dat_restored", report.DatRestored
                && InstallationTransaction.Hash(datD) == hashD);
            Check("uninstall_artifacts_removed",
                !File.Exists(datD + ".frorig") && !File.Exists(datD + ".frpatched")
                && !File.Exists(Path.Combine(dirD, ".franca", "touhou-static.json"))
                && !Directory.Exists(Path.Combine(dirD, ".franca"))
                && !TouhouStaticService.IsReady(dirD) && !TouhouStaticService.HasArtifacts(dirD));
            Check("uninstall_cache_kept", Directory.Exists(TouhouStaticService.PacksRootForProbe!)
                && Directory.GetFiles(TouhouStaticService.PacksRootForProbe, "*.jsonl", SearchOption.AllDirectories).Length > 0);

            // 再启用：同语言全部命中探针缓存，桩零请求（秒装路径成立）
            lock (stubRequests) stubRequests.Clear();
            await TouhouStaticService.EnableAsync(gameD, settingsD, new Collect(new List<string>()));
            List<string> afterReenable;
            lock (stubRequests) afterReenable = stubRequests.ToList();
            Check("uninstall_reenable_cache_hit", TouhouStaticService.IsReady(dirD)
                && InstallationTransaction.Hash(datD) != hashD && afterReenable.Count == 0);

            // 部分损坏一：state 丢失但 .frorig 在 → 仍能恢复
            await TouhouStaticService.UninstallAsync(dirD, _ => { });
            await TouhouStaticService.EnableAsync(gameD, settingsD, new Collect(new List<string>()));
            File.Delete(Path.Combine(dirD, ".franca", "touhou-static.json"));
            await TouhouStaticService.UninstallAsync(dirD, _ => { });
            Check("uninstall_missing_state_recovers", InstallationTransaction.Hash(datD) == hashD
                && !File.Exists(datD + ".frorig") && !TouhouStaticService.HasArtifacts(dirD));

            // 部分损坏二：dat 被人为损坏 → 卸载恢复原始字节
            await TouhouStaticService.EnableAsync(gameD, settingsD, new Collect(new List<string>()));
            await File.WriteAllTextAsync(datD, "garbage-bytes");
            await TouhouStaticService.UninstallAsync(dirD, _ => { });
            Check("uninstall_corrupt_dat_recovers", InstallationTransaction.Hash(datD) == hashD);

            // 9) Unity 卸载（合成清单夹具）：逆操作恢复被覆盖文件、删除 Franca 新建文件，
            //    清单外的用户文件不动；与磁盘不符的条目保守跳过并报告
            var unityDir = Path.Combine(root, "unity-uninstall");
            var unityGame = Path.Combine(unityDir, "game");
            var unityStage = Path.Combine(unityDir, "stage");
            Directory.CreateDirectory(unityGame);
            Directory.CreateDirectory(Path.Combine(unityGame, "mods"));
            Directory.CreateDirectory(unityStage);
            File.WriteAllText(Path.Combine(unityGame, "pre.txt"), "original");
            File.WriteAllText(Path.Combine(unityGame, "mods", "user.dll"), "user mod");
            File.WriteAllText(Path.Combine(unityStage, "pre.txt"), "overwritten by franca");
            File.WriteAllText(Path.Combine(unityStage, "newfile.txt"), "new");
            InstallationTransaction.Install(unityGame, unityStage, () => { }, _ => { });
            File.WriteAllText(Path.Combine(unityGame, "mods", "later.dll"), "added after install");
            var unityLogs = new List<string>();
            var unityOk = TranslationInstaller.Uninstall(unityGame, unityLogs.Add);
            Check("unity_uninstall_reverses", unityOk
                && File.ReadAllText(Path.Combine(unityGame, "pre.txt")) == "original"
                && !File.Exists(Path.Combine(unityGame, "newfile.txt"))
                && File.ReadAllText(Path.Combine(unityGame, "mods", "user.dll")) == "user mod"
                && File.ReadAllText(Path.Combine(unityGame, "mods", "later.dll")) == "added after install"
                && !TranslationInstaller.HasInstalledArtifacts(unityGame));
            // 冲突保守：清单内文件被外部改动后拒绝删除/覆盖
            var unityDir2 = Path.Combine(root, "unity-uninstall-conflict");
            var unityGame2 = Path.Combine(unityDir2, "game");
            var unityStage2 = Path.Combine(unityDir2, "stage");
            Directory.CreateDirectory(unityGame2);
            Directory.CreateDirectory(unityStage2);
            File.WriteAllText(Path.Combine(unityStage2, "newfile2.txt"), "new2");
            InstallationTransaction.Install(unityGame2, unityStage2, () => { }, _ => { });
            File.WriteAllText(Path.Combine(unityGame2, "newfile2.txt"), "user modified");
            var unityLogs2 = new List<string>();
            var unityOk2 = TranslationInstaller.Uninstall(unityGame2, unityLogs2.Add);
            Check("unity_uninstall_conflict_conservative", !unityOk2
                && File.ReadAllText(Path.Combine(unityGame2, "newfile2.txt")) == "user modified"
                && TranslationInstaller.HasInstalledArtifacts(unityGame2));

            // 4) 停用/恢复/幂等
            TouhouStaticService.SetDisabled(gameDir, true);
            Check("disabled_swaps_original_back",
                InstallationTransaction.Hash(dat) == originalHash && File.Exists(dat + ".frpatched"));
            Check("isdisabled_flag", TouhouStaticService.IsDisabled(gameDir));
            TouhouStaticService.SetDisabled(gameDir, false);
            Check("reenabled_swaps_patch_back",
                InstallationTransaction.Hash(dat) != originalHash && !File.Exists(dat + ".frpatched"));

            var logs2 = new List<string>();
            await TouhouStaticService.EnableAsync(game, settings, new Collect(logs2));
            Check("re_enable_idempotent_no_retranslate",
                logs2.Any(l => l.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("已存在"))
                && InstallationTransaction.Hash(dat) != originalHash);

            // 5) 换语言重建（仍走缓存/打桩，产物仍是补丁）
            var settingsFr = new AppSettings { TargetLanguage = "fr", SourceLanguage = "ja" };
            await TouhouStaticService.EnableAsync(game, settingsFr, new Collect(logs));
            var stateHashChanged = InstallationTransaction.Hash(dat) != originalHash;
            Check("rebuild_for_new_language", stateHashChanged
                && TouhouStaticService.IsReady(gameDir) && !TouhouStaticService.IsDisabled(gameDir));

            TranslationInstaller.SetDisabled(gameDir, true);
            Check("installer_setdisabled_branches_static",
                InstallationTransaction.Hash(dat) == originalHash);
        }
        finally
        {
            TouhouStaticService.TranslatorOverride = null;
            TouhouStaticService.PacksRootForProbe = null;
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine($"RESULT\tTOUHOU\tfailures={failures}");
    }

    // WPF Progress<T> 走 Dispatcher 异步投递，短流程断言会先于日志到达；
    // 服务内部从多线程上报，收集器必须加锁并在断言前取快照
    private sealed class Collect : IProgress<string>
    {
        private readonly List<string> _lines;
        public Collect(List<string> lines) => _lines = lines;
        public void Report(string value) { lock (_lines) _lines.Add(value); }
    }

    private sealed class Collect<T> : IProgress<T>
    {
        private readonly List<T> _items;
        public Collect(List<T> items) => _items = items;
        public void Report(T value) { lock (_items) _items.Add(value); }
        public List<T> Snapshot() { lock (_items) return _items.ToList(); }
    }

    private static int CountDialogue(string[] lines, string directive) =>
        lines.Count(l => l.StartsWith(directive, StringComparison.Ordinal) && l.Length > directive.Length);

    // 常规格式优先（stderr 无 "left over" 告警才算真匹配），失败转结局格式；
    // 两种都失败返回 null（与服务判定规则一致）
    private static (string[]? Lines, string Directive) DumpAny(string thmsg, int version, string msg)
    {
        try
        {
            var (lines, stderr) = Dump(thmsg, version, msg, ending: false);
            if (!stderr.Contains("left over")) return (lines, "\t16;");
        }
        catch { /* 常规格式不适用 */ }
        try
        {
            var (lines, _) = Dump(thmsg, version, msg, ending: true);
            return (lines, "\t3;");
        }
        catch
        {
            return (null, "");
        }
    }

    private static (string[] Lines, string Stderr) Dump(string thmsg, int version, string msg, bool ending)
    {
        var work = Path.GetDirectoryName(msg)!;
        var name = Path.GetFileName(msg)!;
        var outName = name + (ending ? ".e.txt" : ".p.txt");
        var (_, stderr) = Run(thmsg, $"-{(ending ? "e" : "")}d {version} \"{name}\" \"{outName}\"", work);
        var lines = File.ReadAllLines(Path.Combine(work, outName), Encoding.GetEncoding(932)); // probe 已注册
        File.Delete(Path.Combine(work, outName));
        return (lines, stderr);
    }

    private static (int, string) Run(string exe, string arguments, string workingDir)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe, Arguments = arguments, WorkingDirectory = workingDir,
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit(120_000);
        if (!process.HasExited)
        {
            process.Kill();
            throw new InvalidOperationException($"{exe} 执行超时。");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {arguments} 退出码 {process.ExitCode}：{stderr}");
        return (process.ExitCode, stderr.ToString());
    }
}
