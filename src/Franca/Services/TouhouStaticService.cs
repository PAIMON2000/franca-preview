using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Franca.Models;

namespace Franca.Services;

// 东方 Project 自研引擎静态翻译适配器 v1（早期实验，现由 thcrap 路线取代）：Unity 游戏走注入，
// ZUN 引擎无法注入，改为 解包 thdat → thmsg 提取对话 → 翻译 → 回封 → 重打包 → 备份写回。
// 支持 th10/th11（THA1 容器，thtk BSD 工具链二进制随附 ThirdParty/thtk）；
// th06 系 PKGL 容器 thtk 不认识，明确拒绝。文本行 Shift-JIS，行结构不变。
// 游戏目录写入前先复制 .frorig 备份，换包采用 原包→.frold→新包 顺序，中断由 Recover 恢复；
// 全部准备（解包/翻译/回封/重打包/校验）在临时工作区完成，成功前不触碰游戏目录。
public static class TouhouStaticService
{
    // thtk 对 th10/th11 各两种 msg 格式：常规对话与结局（-e）。
    // 常规对话文本行指令码 th10=16;、th11=17;，结局格式统一为 3;。
    private const string ToolDirName = "ThirdParty/thtk";
    private const string StateRelativePath = ".franca/touhou-static.json";
    private static readonly Encoding Sjis = CreateSjis();
    // 翻译包缓存：默认根与中继 packs/ 共享，复用每游戏缓存；探针可指向临时根避免污染真实缓存
    private static readonly PackStore DefaultPacks = new();
    internal static string? PacksRootForProbe;
    private static PackStore Packs => PacksRootForProbe is null ? DefaultPacks : new PackStore(PacksRootForProbe);
    private static readonly Regex ThNameVersion = new(@"^th(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // 字段初始化先于任何使用执行，这里先注册 CodePages 提供程序再取 cp932
    private static Encoding CreateSjis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    internal sealed class TouhouState
    {
        public string Dat { get; set; } = "";        // 相对游戏目录的数据包路径
        public int Version { get; set; }              // thdat/thmsg 格式版本（10/11）
        public string Language { get; set; } = "";   // 应用时的目标语言
        public string AppliedAt { get; set; } = "";  // ISO 时间
        public string SourceHash { get; set; } = ""; // 原包 SHA256
        public bool Incomplete { get; set; }          // 上次安装有端点全熔断放弃的未译文本，重试时续译
    }

    // 已应用静态补丁：状态文件与 .frorig 备份同时存在（停用态也算已装，对齐 Unity 的 winhttp 语义）
    public static bool IsReady(string gameDir)
    {
        var state = LoadState(gameDir);
        return state is not null && File.Exists(FrorigPath(gameDir, state.Dat));
    }

    // 可逆停用态：补丁包被换到 <名>.dat.frpatched
    public static bool IsDisabled(string gameDir)
    {
        var state = LoadState(gameDir);
        return state is not null && File.Exists(Path.Combine(gameDir, state.Dat + ".frpatched"));
    }

    // 目标数据包：优先 th10.dat/th11.dat 命名，否则唯一的 THA1 容器
    internal static string? FindTargetDat(GameInfo game)
    {
        if (game.Engine != GameEngine.Touhou) return null;
        var archives = EngineDetector.FindDatFilesWithinDepth(game.Path, 2)
            .Where(EngineDetector.IsThDatArchive).ToList();
        var named = archives.Where(a => ThNameVersion.IsMatch(Path.GetFileNameWithoutExtension(a))).ToList();
        if (named.Count > 0) return named[0];
        return archives.Count == 1 ? archives[0] : null;
    }

    private static string StatePath(string gameDir) => Path.Combine(gameDir, StateRelativePath);

    private static string FrorigPath(string gameDir, string datRelative) =>
        Path.Combine(gameDir, datRelative + ".frorig");

    private static TouhouState? LoadState(string gameDir)
    {
        try
        {
            var path = StatePath(gameDir);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TouhouState>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveState(string gameDir, TouhouState state)
    {
        var path = StatePath(gameDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    // 中断恢复：换包途中（.frold 存在）把原包放回；补丁包孤立存在时重新就位
    private static void Recover(string gameDir, Action<string> log)
    {
        foreach (var dat in EngineDetector.FindDatFilesWithinDepth(gameDir, 2))
        {
            var frold = dat + ".frold";
            if (!File.Exists(frold)) continue;
            if (!File.Exists(dat))
            {
                File.Move(frold, dat);
                log($"已恢复中断的静态补丁写入：{dat}");
            }
            else File.Delete(frold);
        }
        var state = LoadState(gameDir);
        if (state is not null)
        {
            var dat = Path.Combine(gameDir, state.Dat);
            var frpatched = dat + ".frpatched";
            if (File.Exists(frpatched) && !File.Exists(dat) && !File.Exists(FrorigPath(gameDir, state.Dat)))
                File.Move(frpatched, dat);
        }
    }

    // 可逆停用/重新启用（幂等，语义对齐 TranslationInstaller.SetDisabled 的 winhttp.dll 模式）：
    // 停用 = 当前包与 .frorig 原包互换（补丁存到 .frpatched）；启用 = 换回补丁包
    public static void SetDisabled(string gameDir, bool disabled)
    {
        if (InstallationTransaction.HasPending(gameDir))
            throw new InvalidOperationException("请先恢复未完成的安装事务，再启用或停用翻译。");
        var state = LoadState(gameDir);
        if (state is null) return;
        var dat = Path.Combine(gameDir, state.Dat);
        var frorig = FrorigPath(gameDir, state.Dat);
        var frpatched = dat + ".frpatched";
        if (disabled)
        {
            if (!File.Exists(frorig) || File.Exists(frpatched)) return; // 未安装或已停用
            File.Move(dat, frpatched);
            File.Copy(frorig, dat);
        }
        else
        {
            if (!File.Exists(frpatched)) return; // 已是启用态
            File.Move(frpatched, dat, overwrite: true);
        }
    }

    // 静态安装结构化进度：百分比（0–100，保留两位）+ 阶段相关的进度文本。
    // 由 EnableAsync 内部各阶段上报；权重见 InstallTracker。
    public sealed record TouhouInstallProgress(double Percent, string ByteText);

    // 进度合成（线程安全，翻译并发任务同时推进）：阶段等额权重——
    // 解包 0–25%、翻译 25–50%、写回/回封 50–75%、重打包/备份 75–100%。
    // 各阶段内部按真实计量推进：I/O 阶段按数据包字节，翻译按 max(已开始,已完成) 条数。
    // 进度文本：I/O 阶段 "（12.5MB/27.7MB）"（真实字节），翻译阶段 "（翻译 320/814 条）"
    // （字节权重会让 ~30KB 文本在 27.7MB 数据包上不可见，条数更能表达等待量）。
    internal sealed class InstallTracker
    {
        private const double PhaseSpan = 25.0;

        private readonly object _sync = new();
        private readonly IProgress<TouhouInstallProgress>? _progress;
        private readonly long _datBytes;
        private long _stringTotal = 1;
        private double _extractFraction, _translateStarted, _translateDone, _writeFraction, _repackFraction;
        private long _displayDone, _displayTotal;
        private bool _translateDisplay; // 当前进度文本是否翻译条数口径
        private double _lastPercent; // 上报单调：总条数在解包后才可知，校准瞬间不回落

        public InstallTracker(IProgress<TouhouInstallProgress>? progress, long datBytes)
        {
            _progress = progress;
            _datBytes = datBytes;
            _displayTotal = datBytes;
            Report();
        }

        public void SetStringTotal(long total) { lock (_sync) _stringTotal = Math.Max(total, 1); }

        public void ExtractDone()
        {
            lock (_sync) { _extractFraction = 1; _translateDisplay = false; _displayDone = _datBytes; }
            Report();
        }

        public void BeginTranslate()
        {
            lock (_sync) { _translateDisplay = true; }
            Report();
        }

        // "已开始"计数：请求发出即推进百分比（槽位并发），不等单条完成，避免界面看似冻住
        public void StringStarted() { lock (_sync) { _translateStarted++; } Report(); }

        public void StringDone(long byteLen)
        {
            lock (_sync) { _translateDone++; }
            Report();
        }

        public void WriteStep(int done, int total)
        {
            lock (_sync)
            {
                var fraction = total > 0 ? (double)done / total : 1;
                _writeFraction = fraction;
                _translateDisplay = false;
                _displayTotal = _datBytes;
                _displayDone = (long)(_datBytes * fraction);
            }
            Report();
        }

        public void RepackDone()
        {
            lock (_sync) { _repackFraction = 1; _translateDisplay = false; _displayTotal = _datBytes; _displayDone = _datBytes; }
            Report();
        }

        public void Complete()
        {
            lock (_sync)
            {
                _extractFraction = 1;
                _translateStarted = _stringTotal;
                _translateDone = _stringTotal;
                _writeFraction = 1;
                _repackFraction = 1;
                _displayDone = _displayTotal;
            }
            Report();
        }

        private void Report()
        {
            double percent;
            string byteText;
            lock (_sync)
            {
                // 翻译阶段百分比按 max(已开始, 已完成) 口径，与 _lastPercent 钳制共同保证单调
                var translateFraction = Math.Min(1.0, Math.Max(_translateStarted, _translateDone) / _stringTotal);
                var raw = PhaseSpan * (_extractFraction + translateFraction + _writeFraction + _repackFraction);
                percent = Math.Min(100.0, raw);
                if (percent < 100) percent = Math.Min(percent, 99.9);
                percent = Math.Max(percent, _lastPercent);
                _lastPercent = percent;
                byteText = _translateDisplay
                    ? $"（翻译 {(long)Math.Max(_translateStarted, _translateDone)}/{_stringTotal} 条）"
                    : $"（{Mb(_displayDone)}MB/{Mb(_displayTotal)}MB）";
            }
            _progress?.Report(new TouhouInstallProgress(percent, byteText));
        }

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.##");
    }

    // 卸载结果：数据包是否已从备份恢复 + 已恢复/未能恢复清单（容错语义的汇总报告）
    public sealed record TouhouUninstallReport(
        bool DatRestored, IReadOnlyList<string> Restored, IReadOnlyList<string> Failed);

    // 是否存在 Franca 静态产物（决定 UI 卸载按钮可见性；不识别是否损坏）
    public static bool HasArtifacts(string gameDir)
    {
        if (LoadState(gameDir) is not null) return true;
        return EngineDetector.FindDatFilesWithinDepth(gameDir, 2).Any(d =>
            File.Exists(d + ".frorig") || File.Exists(d + ".frpatched") || File.Exists(d + ".frold"));
    }

    // 卸载：不识别问题、整体还原——从 Franca 自己的 .frorig 备份恢复数据包，
    // 清除 Franca 写入的全部产物（备份/状态/残余/空锁目录）。与停用（可逆互换、
    // 文件全保留）和修复（识别问题针对性恢复）互补，是未知损坏的通用恢复手段。
    // 容错语义：任何一步失败继续后续步骤，结尾汇总"已恢复/未能恢复"清单；
    // 部分损坏（state 丢失但 .frorig 在）也必须能完成恢复。
    // AppData 的 PackStore 翻译缓存不在游戏目录，保留不动，重装时增量复用。
    public static async Task<TouhouUninstallReport> UninstallAsync(string gameDir, Action<string> log,
        IProgress<TouhouInstallProgress>? installProgress = null, CancellationToken ct = default)
    {
        var restored = new List<string>();
        var failed = new List<string>();
        void Report(double percent, string text) =>
            installProgress?.Report(new TouhouInstallProgress(percent, text));
        await Task.Yield(); // 保持异步签名：文件操作很快，无需线程池

        // 安装锁覆盖恢复阶段；清理阶段前释放，才能删除锁文件本身
        var installationLock = InstallationTransaction.AcquireLock(gameDir);
        try
        {
            InstallationTransaction.Recover(gameDir, log);
            Recover(gameDir, log);
        }
        finally
        {
            installationLock.Dispose();
        }

        // 定位目标数据包：优先状态文件记录；state 丢失时回退 THA1 容器特征
        var state = LoadState(gameDir);
        string? dat = null;
        if (state is not null && !string.IsNullOrWhiteSpace(state.Dat))
        {
            var candidate = Path.Combine(gameDir, state.Dat);
            if (File.Exists(candidate) || File.Exists(candidate + ".frorig")) dat = candidate;
        }
        if (dat is null)
        {
            var archives = EngineDetector.FindDatFilesWithinDepth(gameDir, 2)
                .Where(EngineDetector.IsThDatArchive).ToList();
            var named = archives.Where(a => ThNameVersion.IsMatch(Path.GetFileNameWithoutExtension(a))).ToList();
            dat = named.Count > 0 ? named[0] : archives.Count == 1 ? archives[0] : null;
        }
        Report(10, "（准备卸载）");

        var datRestored = false;
        if (dat is not null)
        {
            var frorig = dat + ".frorig";
            if (File.Exists(frorig))
            {
                try
                {
                    if (state is not null && !string.IsNullOrWhiteSpace(state.SourceHash)
                        && InstallationTransaction.Hash(frorig) != state.SourceHash)
                        log(UiStrings.Get("uninstall.backup.mismatch")); // 备份与原记录不符，仍继续恢复
                    ct.ThrowIfCancellationRequested();
                    File.Copy(frorig, dat, overwrite: true);
                    datRestored = true;
                    restored.Add(Path.GetFileName(dat));
                }
                catch (Exception ex) { failed.Add($"{Path.GetFileName(dat)}：{ex.Message}"); }
            }
            else failed.Add(UiStrings.Format("uninstall.no.backup", Path.GetFileName(dat)));
        }
        else failed.Add(UiStrings.Get("uninstall.no.dat"));
        Report(50, "（已恢复数据包）");

        // 清理 Franca 产物（逐步容错，汇总报告）
        void TryDelete(string path, string label)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                restored.Add(label);
            }
            catch (Exception ex) { failed.Add($"{label}：{ex.Message}"); }
        }
        var dats = dat is not null
            ? new List<string> { dat }
            : EngineDetector.FindDatFilesWithinDepth(gameDir, 2);
        foreach (var d in dats)
        {
            TryDelete(d + ".frorig", Path.GetFileName(d) + ".frorig");
            TryDelete(d + ".frpatched", Path.GetFileName(d) + ".frpatched");
            TryDelete(d + ".frold", Path.GetFileName(d) + ".frold");
        }
        TryDelete(StatePath(gameDir), "touhou-static.json");
        TryDelete(Path.Combine(gameDir, ".franca", "install.lock"), "install.lock");
        Report(75, "（已清理产物）");
        try
        {
            var francaDir = Path.Combine(gameDir, ".franca");
            if (Directory.Exists(francaDir) && !Directory.EnumerateFileSystemEntries(francaDir).Any())
            {
                Directory.Delete(francaDir);
                restored.Add(".franca/");
            }
        }
        catch (Exception ex) { failed.Add($".franca/：{ex.Message}"); }
        Report(100, "（卸载完成）");

        foreach (var r in restored) log(UiStrings.Format("uninstall.restored", r));
        foreach (var f in failed) log(UiStrings.Format("uninstall.failed", f));
        return new TouhouUninstallReport(datRestored, restored, failed);
    }

    public static async Task EnableAsync(GameInfo game, AppSettings settings, IProgress<string> progress,
        CancellationToken ct = default, string? relayUrl = null,
        IProgress<TouhouInstallProgress>? installProgress = null)
    {
        void Log(string message) => progress.Report(message);

        var dat = FindTargetDat(game);
        if (dat is null)
        {
            var anyDat = EngineDetector.FindDatFilesWithinDepth(game.Path, 2);
            Log(anyDat.Count > 0
                ? UiStrings.Format("inst.th.pkgl", game.Name)   // 目录有数据包但不是 THA1（th06 PKGL 等）
                : UiStrings.Format("inst.th.nodat", game.Name));
            return;
        }

        var version = ParseVersion(dat);
        if (version is not (10 or 11))
        {
            Log(UiStrings.Format("inst.th.version", game.Name,
                version?.ToString() ?? Path.GetFileName(dat)));
            return;
        }

        using var installationLock = InstallationTransaction.AcquireLock(game.Path);
        InstallationTransaction.Recover(game.Path, Log);
        Recover(game.Path, Log);

        var state = LoadState(game.Path);
        var datRelative = Path.GetRelativePath(game.Path, dat);
        var frorig = FrorigPath(game.Path, datRelative);

        if (state is not null && File.Exists(frorig))
        {
            if (state.Language == settings.TargetLanguage && !state.Incomplete)
            {
                // 已应用同语言补丁：幂等恢复（停用态换回补丁包），翻译缓存直接复用
                if (File.Exists(dat + ".frpatched"))
                    File.Move(dat + ".frpatched", dat, overwrite: true);
                Log(UiStrings.Format("inst.th.already", game.Name));
                return;
            }
            // 目标语言变化：以 .frorig 原包为源重建补丁（启用态直接换包；停用态重写 .frpatched）
            if (state.Language != settings.TargetLanguage)
                Log(UiStrings.Format("inst.th.relang", game.Name, state.Language, settings.TargetLanguage));
            else
                // 同语言但未译完（上次端点全熔断）：续译，已缓存部分直接命中
                Log(UiStrings.Format("inst.th.resume", game.Name));
        }

        if (EndpointCatalog.Find(settings.Endpoint)?.Category == EndpointCategory.Offline)
            Log(UiStrings.Get("inst.offlinewarn"));

        var sourceDat = state is not null && File.Exists(frorig) ? frorig : dat;
        Log(UiStrings.Format("inst.th.extract", Path.GetFileName(dat)));
        var tracker = new InstallTracker(installProgress, new FileInfo(sourceDat).Length);
        var work = Path.Combine(Path.GetTempPath(), "franca-touhou-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var build = await BuildPatchedDatAsync(sourceDat, version.Value, work, game, settings, Log, tracker, ct);
            Publish(game.Path, dat, datRelative, frorig, build.Path, build.Abandoned > 0, version.Value, settings, Log);
            tracker.Complete();
            Log(UiStrings.Format("inst.th.done", game.Name));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // 文件名 thNN 里的数字即 thdat/thmsg 格式版本；无名可解析时报 null
    private static int? ParseVersion(string dat)
    {
        var m = ThNameVersion.Match(Path.GetFileNameWithoutExtension(dat));
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    // 完整管线：解包 → 逐 msg 判定格式并提取文本行 → 批量翻译（每游戏缓存）→
    // 写回（SJIS、行结构不变）→ thmsg 回封 → thdat 重打包并校验可列目录。
    // 全程只写临时工作区；返回 (重打包结果路径, 端点全熔断放弃未译的条数)。
    private static async Task<(string Path, int Abandoned)> BuildPatchedDatAsync(string sourceDat, int version, string work,
        GameInfo game, AppSettings settings, Action<string> log, InstallTracker tracker, CancellationToken ct)
    {
        var thdat = Path.Combine(AppContext.BaseDirectory, ToolDirName, "thdat.exe");
        var thmsg = Path.Combine(AppContext.BaseDirectory, ToolDirName, "thmsg.exe");
        if (!File.Exists(thdat) || !File.Exists(thmsg))
            throw new FileNotFoundException("缺少 thtk 工具（ThirdParty/thtk），无法执行静态翻译。");

        var stage = Path.Combine(Path.GetTempPath(), "franca-touhou-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var localDat = Path.Combine(stage, Path.GetFileName(sourceDat));
            File.Copy(sourceDat, localDat);
            RunTool(thdat, $"-x {version} \"{Path.GetFileName(localDat)}\"", stage); // 解包到当前目录
            tracker.ExtractDone();

            var scripts = Directory.GetFiles(stage, "*.msg").Order().ToList();
            if (scripts.Count == 0) throw new InvalidDataException("数据包内没有 .msg 对话脚本。");

            // 1) 逐脚本判定常规/结局格式并提取文本行行号（thtk 对格式不符的脚本会在
            //    stderr 报 "left over" 但退出码仍为 0；结局格式文本行指令码统一为 3;）
            var plan = new List<(string Script, bool Ending, string Directive, List<int> Indexes)>();
            var texts = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var script in scripts)
            {
                ct.ThrowIfCancellationRequested();
                var ending = DetectEndingFormat(thmsg, script, version, stage);
                var directive = ending ? "\t3;" : version == 10 ? "\t16;" : "\t17;";
                var lines = ReadDump(thmsg, script, version, ending, stage, deleteAfter: true);
                var indexes = new List<int>();
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].StartsWith(directive, StringComparison.Ordinal)) continue;
                    var text = lines[i][directive.Length..];
                    if (text.Length == 0) continue;
                    indexes.Add(i);
                    if (seen.Add(text)) texts.Add(text);
                }
                plan.Add((script, ending, directive, indexes));
            }
            log(UiStrings.Format("inst.th.msgs", scripts.Count, texts.Count));

            // 2) 批量翻译未缓存文本（东方原文为日语；失败保留原文，全端点熔断时提前结束）
            var (translations, abandoned) = await TranslateBatchAsync(game, texts, settings, log, tracker, ct);

            // 3) 写回文本行 → thmsg 回封 → 校验对话行数一致
            var done = 0;
            foreach (var (script, ending, directive, indexes) in plan)
            {
                ct.ThrowIfCancellationRequested();
                var lines = ReadDump(thmsg, script, version, ending, stage, deleteAfter: true);
                foreach (var i in indexes)
                    lines[i] = directive + translations[lines[i][directive.Length..]];
                var edited = script + ".edit.txt";
                File.WriteAllLines(edited, lines, Sjis);
                RunTool(thmsg, $"-{(ending ? "e" : "")}c {version} \"{Path.GetFileName(edited)}\" \"{Path.GetFileName(script)}\"", stage);
                VerifyRecompiled(thmsg, script, version, ending, stage, indexes.Count, directive);
                File.Delete(edited);
                tracker.WriteStep(++done, plan.Count);
            }

            // 4) 重打包并校验（自动检测对新生成的包不可靠，用显式版本列目录验证）
            var output = Path.Combine(work, Path.GetFileName(sourceDat) + ".frnew");
            var names = Directory.GetFiles(stage).Where(f => f != localDat)
                .Select(f => "\"" + Path.GetFileName(f) + "\"").ToList();
            RunTool(thdat, $"-c {version} \"{output}\" {string.Join(" ", names)}", stage);
            RunTool(thdat, $"-l {version} \"{output}\"", stage);
            tracker.RepackDone();
            log(UiStrings.Format("inst.th.repack", Path.GetFileName(sourceDat)));
            return (output, abandoned);
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { }
        }
    }

    // thmsg 解包到文本并读入（Sjis），deleteAfter 时顺手清理临时 dump
    internal static string[] ReadDump(string thmsg, string script, int version, bool ending,
        string stage, bool deleteAfter)
    {
        var dump = script + ".dump.txt";
        RunTool(thmsg, $"-{(ending ? "e" : "")}d {version} \"{Path.GetFileName(script)}\" \"{Path.GetFileName(dump)}\"", stage);
        var lines = File.ReadAllLines(dump, Sjis);
        if (deleteAfter) File.Delete(dump);
        return lines;
    }

    // thmsg 对格式不符的脚本会在 stderr 报 "left over" 但退出码仍为 0，
    // 以此判定该脚本属于结局格式（须带 -e）；两种都告警视为未知格式，回封时报错
    internal static bool DetectEndingFormat(string thmsg, string script, int version, string stage) =>
        DetectFormat(thmsg, script, version, stage) ?? false;

    // 三态格式探测：false=常规、true=结局、null=两种 dump 都报 "left over"
    // （未知/未适配格式，thcrap 路线的调用方跳过该脚本而不是硬失败）
    internal static bool? DetectFormat(string thmsg, string script, int version, string stage)
    {
        var probe = Path.Combine(stage, "probe.txt");
        var (_, stderr) = RunToolRaw(thmsg, $"-d {version} \"{Path.GetFileName(script)}\" probe.txt", stage);
        File.Delete(probe);
        if (!stderr.Contains("left over")) return false;
        (_, stderr) = RunToolRaw(thmsg, $"-ed {version} \"{Path.GetFileName(script)}\" probe.txt", stage);
        File.Delete(probe);
        if (!stderr.Contains("left over")) return true;
        return null;
    }

    // 回封后重解包，对话文本行数必须与提取时一致（防编码/结构损坏）
    private static void VerifyRecompiled(string thmsg, string script, int version, bool ending,
        string stage, int expected, string directive)
    {
        var check = script + ".check.txt";
        RunTool(thmsg, $"-{(ending ? "e" : "")}d {version} \"{Path.GetFileName(script)}\" \"{Path.GetFileName(check)}\"", stage);
        var count = File.ReadAllLines(check, Sjis).Count(l => l.StartsWith(directive, StringComparison.Ordinal)
            && l.Length > directive.Length);
        File.Delete(check);
        if (count != expected)
            throw new InvalidDataException($"回封校验失败：{Path.GetFileName(script)} 对话行 {count} ≠ 提取 {expected}。");
    }

    // 端点熔断：批次内单端点连续失败 3 次即从端点顺序中移除（线程安全），
    // 避免每条文本都把已死的端点链走一遍；成功一次即重置连续计数。
    internal sealed class EndpointBreaker
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, int> _consecutiveFailures = new();
        public bool IsOpen(string endpointId) { lock (_sync) return _consecutiveFailures.GetValueOrDefault(endpointId) >= 3; }
        public void RecordFailure(string endpointId)
        {
            lock (_sync) _consecutiveFailures[endpointId] = _consecutiveFailures.GetValueOrDefault(endpointId) + 1;
        }
        public void RecordSuccess(string endpointId) { lock (_sync) _consecutiveFailures.Remove(endpointId); }
    }

    // 批量翻译未缓存文本（东方原文为日语；失败保留原文）。
    // 主用端点为 LLM 类时按 20 条/批合并请求（行数不一致回退逐条）；
    // 其余走逐条并发（8 路）+ 端点熔断；全部端点熔断时提前结束，
    // 未译文本保留原文，翻译缓存逐条落盘，可在稍后重试/修复时续译。
    internal static async Task<(Dictionary<string, string> Translations, int Abandoned)> TranslateBatchAsync(
        GameInfo game, List<string> texts, AppSettings settings, Action<string> log,
        InstallTracker tracker, CancellationToken ct, string cacheTag = "mt:touhou-static",
        string sourceLang = "ja")
    {
        var language = settings.TargetLanguage;
        var appId = game.AppId is > 0 ? game.AppId.Value : 0;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in texts) result[text] = text; // 默认保留原文

        tracker.SetStringTotal(texts.Count);
        tracker.BeginTranslate();

        var pending = new List<string>();
        foreach (var text in texts)
        {
            if (appId > 0 && Packs.Lookup(appId, language, text) is { } cached)
            {
                result[text] = cached;
                tracker.StringDone(Sjis.GetByteCount(text)); // 缓存命中同样推进进度
            }
            else
            {
                pending.Add(text);
            }
        }
        if (pending.Count == 0)
        {
            log(UiStrings.Format("inst.th.translate", 0));
            return (result, 0);
        }

        log(UiStrings.Format("inst.th.translate", pending.Count));
        var breaker = new EndpointBreaker();
        var order = EndpointOrder(settings);
        var fallback = new List<string>();

        // LLM 批量路径：20 条/批，换行连接；响应按行拆回，行数不一致或含空行则该批逐条重试
        var llmId = settings.Endpoint;
        var useLlmBatch = EndpointCatalog.Find(llmId)?.Category == EndpointCategory.Llm;
        var consumed = 0;
        if (useLlmBatch)
        {
            const int batchSize = 20;
            for (var start = 0; start < pending.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                if (breaker.IsOpen(llmId)) break; // LLM 熔断：剩余全部转逐条
                var chunk = pending.GetRange(start, Math.Min(batchSize, pending.Count - start));
                consumed = start + chunk.Count;
                for (var k = 0; k < chunk.Count; k++) tracker.StringStarted();
                var value = await TranslateViaEndpointAsync(llmId, string.Join("\n", chunk),
                    language, settings, ct, timeoutSeconds: 60, sourceLang);
                if (value is not null)
                {
                    var lines = value.Replace("\r\n", "\n").Replace('\r', '\n')
                        .Trim('\n').Split('\n').Select(l => l.Trim()).ToArray();
                    // 译文本身单行（thmsg 行结构），条数不符即视为合并失败，回退逐条
                    if (lines.Length == chunk.Count && lines.All(l => l.Length > 0))
                    {
                        breaker.RecordSuccess(llmId);
                        for (var k = 0; k < chunk.Count; k++)
                        {
                            result[chunk[k]] = lines[k];
                            tracker.StringDone(Sjis.GetByteCount(chunk[k]));
                            if (appId > 0) Packs.Store(appId, language, chunk[k], lines[k], cacheTag, retry: false);
                        }
                        continue;
                    }
                }
                else
                {
                    breaker.RecordFailure(llmId);
                }
                fallback.AddRange(chunk); // 行数不一致或服务失败：该批回退逐条
            }
        }

        // 逐条路径：非 LLM 主用的全部文本，以及 LLM 批量失败回退的批次
        var linesToTranslate = useLlmBatch
            ? fallback.Concat(pending.Skip(consumed)).ToList()
            : pending;
        var translated = 0;
        var next = -1;
        if (linesToTranslate.Count > 0)
        {
            var workers = Enumerable.Range(0, 8).Select(async _ =>
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (order.All(breaker.IsOpen)) return; // 全部熔断：不再领取新文本
                    var i = Interlocked.Increment(ref next);
                    if (i >= linesToTranslate.Count) return;
                    var text = linesToTranslate[i];
                    tracker.StringStarted();
                    string? value = null;
                    try
                    {
                        value = await TranslateOneAsync(text, language, settings, breaker, order, ct, sourceLang);
                    }
                    finally
                    {
                        tracker.StringDone(Sjis.GetByteCount(text)); // 失败也推进：该行保留原文
                    }
                    if (value is null) continue;
                    lock (result) result[text] = value;
                    Interlocked.Increment(ref translated);
                    if (appId > 0) Packs.Store(appId, language, text, value, cacheTag, retry: false);
                }
            });
            await Task.WhenAll(workers);
        }

        var abandoned = Math.Max(0, linesToTranslate.Count - next - 1); // 全熔断时未领取的文本
        var failures = linesToTranslate.Count - translated - abandoned;
        log(UiStrings.Format("inst.th.translated", translated, failures));
        if (abandoned > 0)
            log(UiStrings.Format("inst.th.breaker", game.Name, abandoned));
        return (result, abandoned);
    }

    // 主用 → 备用 → 其余端点，对齐 RelayServer 池顺序；已熔断的端点跳过
    private static List<string> EndpointOrder(AppSettings settings)
    {
        var order = new List<string>();
        void Add(string id)
        {
            if (id.Length > 0 && !order.Contains(id) && !settings.DisabledEndpoints.Contains(id)) order.Add(id);
        }
        Add(settings.Endpoint);
        Add(settings.FallbackEndpoint);
        foreach (var def in EndpointCatalog.All) Add(def.Id);
        return order;
    }

    // 探针注入点（Diagnostics 烟测）：非 null 时替换真实端点调用，桩自行按行加前缀
    internal static Func<string, string, AppSettings, CancellationToken, Task<string?>>? TranslatorOverride;

    // 单端点单次调用：10 秒（逐条）/ 60 秒（LLM 批量）超时；桩优先
    private static async Task<string?> TranslateViaEndpointAsync(string endpointId, string text,
        string language, AppSettings settings, CancellationToken ct, int timeoutSeconds,
        string sourceLang = "ja")
    {
        if (TranslatorOverride is not null)
            return await TranslatorOverride(text, language, settings, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var keys = settings.EndpointKeys.TryGetValue(endpointId, out var k) ? k : null;
            var value = await AppTranslator.TranslateAsync(endpointId, text, language,
                keys, timeout.Token, sourceLang);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // 逐条翻译：按端点顺序重试，单端点连续失败由熔断器移除；全部不可用返回 null
    private static async Task<string?> TranslateOneAsync(string text, string language,
        AppSettings settings, EndpointBreaker breaker, List<string> order, CancellationToken ct,
        string sourceLang = "ja")
    {
        foreach (var endpointId in order)
        {
            ct.ThrowIfCancellationRequested();
            if (breaker.IsOpen(endpointId)) continue;
            var value = await TranslateViaEndpointAsync(endpointId, text, language, settings, ct, timeoutSeconds: 10, sourceLang);
            if (value is null) breaker.RecordFailure(endpointId);
            else
            {
                breaker.RecordSuccess(endpointId);
                return value;
            }
        }
        return null;
    }

    // 备份原包（.frorig）→ 原包让位 .frold → 新包就位 → 删除 .frold → 写状态文件。
    // 任何一步失败都尽力把原包放回，残余 .frold 由下次 Recover 收尾。
    // incomplete=true 表示本次有端点全熔断放弃的未译文本，状态标记后便于重试续译。
    private static void Publish(string gameDir, string dat, string datRelative, string frorig,
        string patchedDat, bool incomplete, int version, AppSettings settings, Action<string> log)
    {
        if (!File.Exists(frorig))
            File.Copy(dat, frorig);
        var frold = dat + ".frold";
        var frpatched = dat + ".frpatched";
        var disabled = File.Exists(frpatched);
        try
        {
            if (disabled)
            {
                // 停用态：游戏目录保持原包，新补丁写到 .frpatched
                File.Move(patchedDat, frpatched, overwrite: true);
            }
            else
            {
                File.Move(dat, frold);
                try { File.Move(patchedDat, dat); }
                catch { File.Move(frold, dat); throw; }
                File.Delete(frold);
            }
        }
        catch
        {
            if (File.Exists(frold) && !File.Exists(dat)) File.Move(frold, dat);
            throw;
        }

        var sourceForHash = frorig; // 两种形态 .frorig 都是原包
        SaveState(gameDir, new TouhouState
        {
            Dat = datRelative.Replace('\\', '/'),
            Version = version,
            Language = settings.TargetLanguage,
            AppliedAt = DateTimeOffset.Now.ToString("o"),
            SourceHash = InstallationTransaction.Hash(sourceForHash),
            Incomplete = incomplete,
        });
        log(UiStrings.Format("inst.th.backup", frorig));
    }

    // thtk 进程调用：静默、捕获 stderr、非零退出抛异常；工作目录为传入 stage
    internal static void RunTool(string exe, string arguments, string workingDir)
    {
        var (exitCode, stderr) = RunToolRaw(exe, arguments, workingDir);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} {arguments} 退出码 {exitCode}：{stderr.Trim()}");
    }

    private static (int ExitCode, string Stderr) RunToolRaw(string exe, string arguments, string workingDir)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginErrorReadLine();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(120_000);
        if (!process.HasExited)
        {
            process.Kill();
            throw new InvalidOperationException($"{Path.GetFileName(exe)} 执行超时。");
        }
        return (process.ExitCode, stderr.ToString());
    }
}
