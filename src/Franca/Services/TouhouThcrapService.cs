using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Franca.Models;

namespace Franca.Services;

// 东方 Project 自研引擎 thcrap 运行时翻译路线（替换静态改包的启用路径）：
// 不改游戏任何数据文件——thtk 解包/提取仍是只读操作，译文写成 thcrap jdiff 补丁
// （对话）+ spells.js（符卡名，提取自真实 exe 的 SJIS 字符串表）+ musiccmt.js
// （音乐室评论，提取自 dat 内 musiccmt.txt）+ 菜单精灵图 PNG，游戏启动经
// thcrap_loader 注入渲染中文字体与译文。
// 覆盖面：th07+ 正作按能力自适应——启用门槛是能力探测（thdat 能列目录 + 至少一个
// msg 能 dump），指令码按 thcrap msg_format_for 分档解析（见 MsgFormatFor）；
// th06 的 PKGL 容器在数据包定位处明确拒绝；thtk 不支持的版本（th18+）在探测处拒绝。
// 工作树：首用把 ThirdParty/thcrap 整树复制到 <程序目录>/thcrap/，补丁生成到
// thcrap/repos/franca/<patchid>/，运行配置 thcrap/config/franca.js。
// 游戏目录侧只交换 exe：thNN.exe → thNN.exe.frorig，原位写入存根（franca-thcrap-stub.exe）
// 与命令文件 thNN.thcrapcmd（存根读它启动 loader；Steam 直接启动游戏也走 thcrap）。
// jdiff 键结构对照 thcrap_tsa/src/th06_msg.cpp：外层 entry 索引，内层 <time>_<ind>，
// ind 在 time 变化时归零、按对话盒递增。thcrap 运行时按"已命中的盒"推进 ind
// （未命中盒逐行消耗 ind），所以同一时间段内的盒要么全部写入、要么全部不写，
// 且 lines 数必须与原文行数一致（少则运行时被截断丢行，多则盒尾插行）。
public static class TouhouThcrapService
{
    private const string SourceTreeName = "ThirdParty/thcrap";
    private const string WorkTreeDirName = "thcrap";
    private const string StateRelativePath = ".franca/touhou-thcrap.json";
    private const string StubMarker = "FRANCA_THCRAP_STUB";
    private const string FontFileName = "NotoSansSC.ttf";
    private const string FontFamilyName = "Noto Sans SC";
    // 东方补丁数据（nmlgc/base_tsa）不随仓库/安装包分发：仓库只保留文件清单
    // （路径 + 大小 + SHA-256 + 固定上游 commit），首次启用时按清单下载。
    private const string BaseTsaRelativeDir = "repos/nmlgc/base_tsa";
    private const string BaseTsaManifestName = "ThirdParty/thcrap-repos/base_tsa.files.json";

    // 探针注入点：工作树重定向到临时目录；翻译桩与翻译缓存根复用静态服务的同名挂钩
    internal static string? WorkTreeRootForProbe;
    // 探针注入点：非 null 时补丁数据从本地目录复制（探针不联网）
    internal static string? BaseTsaSourceForProbe;
    // 探针注入点：非 null 时替换符卡名表提取（返回 null 走提取失败跳过路径）
    internal static Func<string, List<string>?>? SpellNamesForProbe;
    internal static Func<string, string, AppSettings, CancellationToken, Task<string?>>? TranslatorOverride
    {
        get => TouhouStaticService.TranslatorOverride;
        set => TouhouStaticService.TranslatorOverride = value;
    }
    internal static string? PacksRootForProbe
    {
        get => TouhouStaticService.PacksRootForProbe;
        set => TouhouStaticService.PacksRootForProbe = value;
    }

    private static string WorkTreeRoot =>
        WorkTreeRootForProbe ?? Path.Combine(AppContext.BaseDirectory, WorkTreeDirName);
    private static string BundledStubPath =>
        Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thcrap-stub", "franca-thcrap-stub.exe");
    private static string StatePath(string gameDir) => Path.Combine(gameDir, StateRelativePath);

    private static readonly Regex ExeName = new(@"^th\d+\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VersionEntry = new(
        "\"(?<hash>[0-9a-f]{64})\"\\s*:\\s*\\[\\s*\"(?<game>th\\d+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DumpEntryHeader = new(@"^entry (\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DumpTimeLine = new(@"^@(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MusicTrackNumber = new(@"^No\.\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Encoding Sjis = CreateSjis();
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static Encoding CreateSjis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    internal sealed class ThcrapState
    {
        public string Game { get; set; } = "";      // thcrap 游戏 ID（thNN/th095/th128 等）
        public string Exe { get; set; } = "";       // 相对游戏目录的 exe 名
        public string Language { get; set; } = "";  // 应用时的目标语言
        public string PatchId { get; set; } = "";   // repos/franca/ 下的补丁目录名
        public string SourceHash { get; set; } = ""; // 原 exe SHA256（大写 hex，版本识别依据）
        public string AppliedAt { get; set; } = ""; // ISO 时间
        public bool Incomplete { get; set; }         // 上次有端点全熔断放弃的未译文本，重试续译
    }

    // 一个对话盒：同一 time 下连续的文本行；Ind 为该 time 内第几个盒（thcrap 键 <time>_<ind>）
    internal sealed record MsgBox(int Entry, int Time, int Ind, List<string> Lines);

    // 已安装：状态文件 + exe 交换产物（.frorig）+ 工作树在位（停用态也算已装，对齐静态语义）
    public static bool IsReady(string gameDir)
    {
        var state = LoadState(gameDir);
        if (state is null || state.Exe.Length == 0) return false;
        var exe = Path.Combine(gameDir, state.Exe);
        return File.Exists(exe) && File.Exists(exe + ".frorig") && WorkTreeUsable();
    }

    // 停用态：原位是还原的原版 exe（无存根标记），.frorig 仍在
    public static bool IsDisabled(string gameDir)
    {
        var state = LoadState(gameDir);
        if (state is null || state.Exe.Length == 0) return false;
        var exe = Path.Combine(gameDir, state.Exe);
        return File.Exists(exe) && File.Exists(exe + ".frorig") && !HasStubMarker(exe);
    }

    // 是否存在 thcrap 路线产物（决定 UI 卸载按钮可见性）。
    // 只认 thNN 游戏 exe 的交换产物，其他程序的 *.exe.frorig（如汉化工具备份）不算
    public static bool HasArtifacts(string gameDir)
    {
        if (LoadState(gameDir) is not null) return true;
        try
        {
            return Directory.EnumerateFiles(gameDir, "*.exe.frorig")
                    .Any(f => ExeName.IsMatch(Path.GetFileName(f)[..^".frorig".Length]))
                || Directory.EnumerateFiles(gameDir, "th*.thcrapcmd").Any();
        }
        catch { return false; }
    }

    private static bool WorkTreeUsable() =>
        File.Exists(Path.Combine(WorkTreeRoot, "bin", "thcrap_loader.exe"));

    private static ThcrapState? LoadState(string gameDir)
    {
        try
        {
            var path = StatePath(gameDir);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ThcrapState>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveState(string gameDir, ThcrapState state)
    {
        var path = StatePath(gameDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOut));
        File.Move(tmp, path, overwrite: true);
    }

    // 存根识别：二进制内含标记串；超过 4MB 的文件必然不是存根，不读
    internal static bool HasStubMarker(string exe)
    {
        try
        {
            if (new FileInfo(exe).Length > 4 * 1024 * 1024) return false;
            var bytes = File.ReadAllBytes(exe);
            var marker = Encoding.ASCII.GetBytes(StubMarker);
            for (var i = 0; i + marker.Length <= bytes.Length; i++)
            {
                var k = 0;
                while (k < marker.Length && bytes[i + k] == marker[k]) k++;
                if (k == marker.Length) return true;
            }
            return false;
        }
        catch { return false; }
    }

    // 中断恢复：exe 交换途中崩溃（.frorig 在而 exe 缺失）把原版放回原位；残余临时存根清理
    internal static void Recover(string gameDir, Action<string> log)
    {
        foreach (var frorig in Directory.EnumerateFiles(gameDir, "*.exe.frorig"))
        {
            var exe = frorig[..^".frorig".Length];
            if (!ExeName.IsMatch(Path.GetFileName(exe))) continue; // 只管 thNN 游戏 exe 的交换
            if (!File.Exists(exe))
            {
                File.Move(frorig, exe);
                log($"已恢复中断的 exe 交换：{Path.GetFileName(exe)}");
            }
        }
        foreach (var leftover in Directory.EnumerateFiles(gameDir, "*.exe.frnew"))
            File.Delete(leftover);
    }

    // 可逆停用/重新启用（幂等）：停用 = 存根让位、原版 exe 回位；启用 = 重新写入存根。
    // 补丁文件与工作树全部保留，幂等重启用不再翻译。
    public static void SetDisabled(string gameDir, bool disabled)
    {
        if (InstallationTransaction.HasPending(gameDir))
            throw new InvalidOperationException("请先恢复未完成的安装事务，再启用或停用翻译。");
        var state = LoadState(gameDir);
        if (state is null || state.Exe.Length == 0) return;
        var exe = Path.Combine(gameDir, state.Exe);
        var frorig = exe + ".frorig";
        if (disabled)
        {
            if (!File.Exists(frorig) || !File.Exists(exe) || !HasStubMarker(exe)) return; // 未安装或已停用
            File.Delete(exe);
            File.Copy(frorig, exe);
        }
        else
        {
            if (!File.Exists(frorig)) return;
            if (File.Exists(exe) && HasStubMarker(exe)) return; // 已是启用态
            WriteStub(exe);
        }
    }

    public static async Task EnableAsync(GameInfo game, AppSettings settings, IProgress<string> progress,
        CancellationToken ct = default, string? relayUrl = null,
        IProgress<TouhouStaticService.TouhouInstallProgress>? installProgress = null)
    {
        void Log(string message) => progress.Report(message);

        var dat = TouhouStaticService.FindTargetDat(game);
        if (dat is null)
        {
            var anyDat = EngineDetector.FindDatFilesWithinDepth(game.Path, 2);
            Log(anyDat.Count > 0
                ? UiStrings.Format("inst.th.pkgl", game.Name)
                : UiStrings.Format("inst.th.nodat", game.Name));
            return;
        }
        var exe = FindGameExe(game.Path);
        if (exe is null)
        {
            Log(UiStrings.Format("inst.tc.noexe", game.Name));
            return;
        }
        var exeRelative = Path.GetRelativePath(game.Path, exe);
        var frorig = exe + ".frorig";

        // 前置：静态补丁态必须先还原并清理（thcrap 要求干净原版；静态服务自持安装锁）
        if (TouhouStaticService.HasArtifacts(game.Path))
        {
            Log(UiStrings.Format("inst.tc.restore", game.Name));
            await TouhouStaticService.UninstallAsync(game.Path, Log, installProgress, ct);
        }

        using var installationLock = InstallationTransaction.AcquireLock(game.Path);
        InstallationTransaction.Recover(game.Path, Log);
        Recover(game.Path, Log);

        Log(UiStrings.Get("inst.tc.prepare"));
        var workTree = EnsureWorkTree(Log, ct);

        // 版本识别：启用态下原位是存根，真实 exe 在 .frorig；停用态下原位即原版
        var realExe = File.Exists(frorig) && HasStubMarker(exe) ? frorig : exe;
        var exeHash = InstallationTransaction.Hash(realExe);
        var gameId = IdentifyGame(workTree, exeHash);
        if (gameId is null)
        {
            Log(UiStrings.Format("inst.tc.unknownexe", game.Name, Path.GetFileName(exe)));
            return;
        }
        var version = int.Parse(gameId[2..], CultureInfo.InvariantCulture);

        var state = LoadState(game.Path);
        var patchId = PatchIdFor(settings.TargetLanguage);
        var patchDir = Path.Combine(workTree, "repos", "franca", patchId);
        // 幂等判定：jdiff 与 musiccmt.js（dat 内，必定可提取）缺一不可，缺则重建一次补齐；
        // title/title01.png（菜单精灵图本地化产物）同样必需，语言变更时随重建换图。
        // spells.js 容忍缺失——Steam 版 exe 的 .text 被 SteamStub 加密，符卡名表静态提取
        // 会确定性失败，若强制要求将永远无法进入幂等路径。
        var patchGameDirReady = Path.Combine(patchDir, gameId);
        var patchReady = Directory.Exists(patchDir)
            && Directory.EnumerateFiles(patchDir, "*.jdiff", SearchOption.AllDirectories).Any()
            && File.Exists(Path.Combine(patchGameDirReady, "musiccmt.js"))
            && File.Exists(Path.Combine(patchGameDirReady, "title", "title01.png"));

        if (state is not null && state.Language == settings.TargetLanguage && !state.Incomplete && patchReady)
        {
            // 幂等：补丁与缓存完整，只确保运行配置与启用态（停用中被启用则换回补丁存根）
            WriteRunConfig(workTree, patchId);
            WriteGamesJs(workTree, gameId, frorig);
            if (!HasStubMarker(exe)) WriteStub(exe);
            WriteCommandFile(exe, workTree, gameId);
            Log(UiStrings.Format("inst.tc.already", game.Name));
            return;
        }
        if (state is not null && state.Language != settings.TargetLanguage)
            Log(UiStrings.Format("inst.tc.relang", game.Name, state.Language, settings.TargetLanguage));
        else if (state?.Incomplete == true)
            Log(UiStrings.Format("inst.tc.resume", game.Name));

        if (EndpointCatalog.Find(settings.Endpoint)?.Category == EndpointCategory.Offline)
            Log(UiStrings.Get("inst.offlinewarn"));

        Log(UiStrings.Format("inst.tc.patch", settings.TargetLanguage));
        var tracker = new TouhouStaticService.InstallTracker(installProgress, new FileInfo(dat).Length);
        var stage = Path.Combine(Path.GetTempPath(), "franca-thcrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var abandoned = await BuildPatchAsync(dat, realExe, version, gameId, patchDir, stage,
                game, settings, Log, tracker, ct);
            if (abandoned is null) return; // 能力探测失败：原因已记录，不交换 exe、不写状态
            WritePatchMetadata(workTree, patchDir, patchId, settings.TargetLanguage);
            WriteRunConfig(workTree, patchId);
            WriteGamesJs(workTree, gameId, frorig);
            tracker.RepackDone();

            // exe 交换（可逆）：原版让位 .frorig，原位写入存根与命令文件
            if (!HasStubMarker(exe))
            {
                if (File.Exists(exe))
                {
                    if (!File.Exists(frorig)) File.Move(exe, frorig);
                    else File.Delete(exe); // 停用态：原位是还原副本，.frorig 才是权威原版
                }
                WriteStub(exe);
            }
            WriteCommandFile(exe, workTree, gameId);
            Log(UiStrings.Format("inst.tc.swap", Path.GetFileName(exe)));

            SaveState(game.Path, new ThcrapState
            {
                Game = gameId,
                Exe = exeRelative.Replace('\\', '/'),
                Language = settings.TargetLanguage,
                PatchId = patchId,
                SourceHash = exeHash,
                AppliedAt = DateTimeOffset.Now.ToString("o"),
                Incomplete = abandoned.Value > 0,
            });
            tracker.Complete();
            Log(UiStrings.Format("inst.tc.done", game.Name));
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { }
        }
    }

    // 卸载：exe 交换还原 + 清理游戏目录内的 thcrap 路线产物；若存在旧静态补丁态先按静态卸载恢复。
    // 程序目录的 thcrap 工作树与补丁、AppData 翻译缓存保留，重装时幂等复用。
    public static async Task<TouhouStaticService.TouhouUninstallReport> UninstallAsync(string gameDir,
        Action<string> log, IProgress<TouhouStaticService.TouhouInstallProgress>? installProgress = null,
        CancellationToken ct = default)
    {
        var restored = new List<string>();
        var failed = new List<string>();
        if (TouhouStaticService.HasArtifacts(gameDir))
        {
            var staticReport = await TouhouStaticService.UninstallAsync(gameDir, log, installProgress, ct);
            restored.AddRange(staticReport.Restored);
            failed.AddRange(staticReport.Failed);
        }
        installProgress?.Report(new TouhouStaticService.TouhouInstallProgress(50, "（还原 exe）"));
        await Task.Yield();

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

        var state = LoadState(gameDir);
        if (state is not null && state.Exe.Length > 0)
        {
            var exe = Path.Combine(gameDir, state.Exe);
            var frorig = exe + ".frorig";
            try
            {
                if (File.Exists(frorig))
                {
                    File.Copy(frorig, exe, overwrite: true);
                    File.Delete(frorig);
                    restored.Add(Path.GetFileName(exe));
                }
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
                TryDelete(Path.Combine(gameDir, Path.GetFileNameWithoutExtension(state.Exe) + ".thcrapcmd"),
                    Path.GetFileNameWithoutExtension(state.Exe) + ".thcrapcmd");
                TryDelete(StatePath(gameDir), "touhou-thcrap.json");
                TryDelete(Path.Combine(gameDir, ".franca", "install.lock"), "install.lock");
            }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(exe)}：{ex.Message}"); }
        }
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
        installProgress?.Report(new TouhouStaticService.TouhouInstallProgress(100, "（卸载完成）"));
        foreach (var r in restored) log(UiStrings.Format("uninstall.restored", r));
        foreach (var f in failed) log(UiStrings.Format("uninstall.failed", f));
        return new TouhouStaticService.TouhouUninstallReport(failed.Count == 0, restored, failed);
    }

    // 补丁 ID 由目标语言派生：zh-CN→zh_hans、zh-TW→zh_hant；通用规则小写、非字母数字转下划线
    internal static string PatchIdFor(string language)
    {
        if (language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return "zh_hans";
        if (language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)) return "zh_hant";
        return new string(language.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
    }

    // thNN.exe（排除 custom.exe 等伴生程序）；找不到返回 null
    internal static string? FindGameExe(string gameDir) =>
        Directory.EnumerateFiles(gameDir, "*.exe")
            .Where(f => ExeName.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    // exe SHA-256（小写）命中 base_tsa versions.js → thcrap 游戏 ID；未命中返回 null
    internal static string? IdentifyGame(string workTree, string exeHashUpper)
    {
        var versionsFile = Path.Combine(workTree, "repos", "nmlgc", "base_tsa", "versions.js");
        if (!File.Exists(versionsFile)) return null;
        var hash = exeHashUpper.ToLowerInvariant();
        foreach (Match m in VersionEntry.Matches(File.ReadAllText(versionsFile)))
            if (m.Groups["hash"].Value.Equals(hash, StringComparison.Ordinal))
                return m.Groups["game"].Value;
        return null;
    }

    // thmsg dump 的指令码表，对照 thcrap_tsa/src/th06_msg.cpp 的 msg_format_for 分档
    // （游戏号取 thNN 数字，th095→95、th128→128；同档合并：th128 与 th14 档对我们的
    // 解析等价——7/8/9 是左右侧标而非闭盒，25=DELETE、28/32=BUBBLE 均非文本码自然忽略）。
    // TextCodes=文本行指令（AUTO_LINE 与 HARD_LINE 均收），AutoEndCodes=闭盒指令；
    // time/entry 变化同样结束当前盒。th06/th07 档无闭盒码（盒随 time 推进结束）。
    internal sealed record MsgFormat(int[] TextCodes, int[] AutoEndCodes);
    internal static MsgFormat MsgFormatFor(int game) =>
        game >= 128 ? new([17], [11])           // TH128/TH14/TH19 档
        : game >= 11 ? new([17], [7, 8, 9, 11]) // TH11/TH12/TH125 档
        : game >= 10 ? new([16], [7, 8, 10])    // TH10
        : game >= 9 ? new([16], [4, 15])        // TH09/TH095
        : game >= 8 ? new([3, 16, 19, 20], [4, 15]) // TH08（3 为硬行）
        : new([3, 8], []);                      // TH06/TH07（3 与 h1=8 硬行）
    // 结局（END_TH10，th10+）：文本=3，闭盒=5/6/9；th06-th09 结局无可靠对应格式
    internal static readonly MsgFormat EndingFormat = new([3], [5, 6, 9]);

    // 解析 thmsg dump 为对话盒序列。dump 结构：entry N 头、@time 行、制表符缩进的指令行。
    // 文本码/闭盒码按 MsgFormatFor(version) 分档；time 变化同样结束当前盒。
    internal static List<MsgBox> ParseMsgDump(string[] lines, int version, bool ending)
    {
        var format = ending ? EndingFormat : MsgFormatFor(version);
        var boxes = new List<MsgBox>();
        var entry = 0;
        var time = -1;
        var ind = 0;
        List<string>? open = null;
        void CloseBox()
        {
            if (open is { Count: > 0 }) boxes.Add(new MsgBox(entry, time, ind++, open));
            open = null;
        }
        foreach (var raw in lines)
        {
            var entryMatch = DumpEntryHeader.Match(raw);
            if (entryMatch.Success)
            {
                CloseBox();
                entry = int.Parse(entryMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                time = -1;
                ind = 0;
                continue;
            }
            var timeMatch = DumpTimeLine.Match(raw);
            if (timeMatch.Success)
            {
                CloseBox();
                time = int.Parse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                ind = 0;
                continue;
            }
            if (raw.Length == 0 || raw[0] != '\t') continue;
            var body = raw[1..];
            var semi = body.IndexOf(';');
            if (!int.TryParse(semi >= 0 ? body[..semi] : body, out var code)) continue;
            if (format.TextCodes.Contains(code) && semi >= 0)
            {
                open ??= new List<string>();
                open.Add(body[(semi + 1)..]);
            }
            else if (format.AutoEndCodes.Contains(code))
            {
                CloseBox();
            }
        }
        CloseBox();
        return boxes;
    }

    // 补丁构建管线：能力探测（thdat 能列目录 + 至少一个 msg 能 dump）→ thdat 解包
    // （只读副本）→ 逐 msg dump 解析对话盒（格式未识别的脚本跳过记日志）→ 批量翻译
    // （复用静态服务的缓存/熔断/续译）→ 写 jdiff。随后追加：真实 exe 提取符卡名表
    // → spells.js；stage 里的 musiccmt.txt → musiccmt.js。两者提取失败只记日志跳过。
    // 返回端点全熔断放弃的条数；能力探测失败（容器/脚本不可读）返回 null，原因已记录。
    private static async Task<int?> BuildPatchAsync(string dat, string realExe, int version, string gameId, string patchDir,
        string stage, GameInfo game, AppSettings settings, Action<string> log,
        TouhouStaticService.InstallTracker tracker, CancellationToken ct)
    {
        var thdat = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thtk", "thdat.exe");
        var thmsg = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thtk", "thmsg.exe");
        if (!File.Exists(thdat) || !File.Exists(thmsg))
            throw new FileNotFoundException("缺少 thtk 工具（ThirdParty/thtk），无法提取对话文本。");

        // 能力探测①：thdat 能列出目录即容器可读（thtk 不支持的版本如 th18+ 在此明确拒绝；
        // th06 的 PKGL 更早在 FindTargetDat 被拒）
        try
        {
            TouhouStaticService.RunTool(thdat, $"-l {version} \"{dat}\"", stage);
        }
        catch
        {
            log(UiStrings.Format("inst.tc.datunreadable", Path.GetFileName(dat)));
            return null;
        }

        var localDat = Path.Combine(stage, Path.GetFileName(dat));
        File.Copy(dat, localDat);
        TouhouStaticService.RunTool(thdat, $"-x {version} \"{Path.GetFileName(localDat)}\"", stage);
        tracker.ExtractDone();

        var scripts = Directory.GetFiles(stage, "*.msg").Order().ToList();
        if (scripts.Count == 0)
        {
            log(UiStrings.Format("inst.tc.msgsfail", Path.GetFileName(dat)));
            return null;
        }

        var plans = new List<(string Script, List<MsgBox> Boxes)>();
        var texts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in scripts)
        {
            ct.ThrowIfCancellationRequested();
            // 能力探测②：逐脚本三态判定常规/结局；两种 dump 都失败的（未适配格式）跳过。
            // th06-th09 的结局格式与 END_TH10 无可靠对应，识别为结局同样跳过。
            var format = TouhouStaticService.DetectFormat(thmsg, script, version, stage);
            if (format is null || (format.Value && version < 10))
            {
                log(UiStrings.Format("inst.tc.scriptskip", Path.GetFileName(script)));
                continue;
            }
            var ending = format.Value;
            var dump = TouhouStaticService.ReadDump(thmsg, script, version, ending, stage, deleteAfter: true);
            var boxes = ParseMsgDump(dump, version, ending);
            plans.Add((script, boxes));
            foreach (var box in boxes)
            foreach (var line in box.Lines)
                if (line.Length > 0 && seen.Add(line)) texts.Add(line);
        }
        if (plans.Count == 0 || plans.All(p => p.Boxes.Count == 0))
        {
            // 能力探测②汇总：没有任何可解析的对话盒，脚本格式未适配
            log(UiStrings.Format("inst.tc.msgsfail", Path.GetFileName(dat)));
            return null;
        }
        // 符卡名表（spells.js）：真实 exe 内连续 NUL 结尾的 SJIS 字符串表，id 从 0 起按表序。
        // Steam 版 exe 的 .text 被 SteamStub 加密（th10 连 .rdata 也加密），提取失败只记日志跳过。
        var spellNames = SpellNamesForProbe is { } spellHook
            ? spellHook(realExe)
            : ExtractSpellNames(realExe);
        if (spellNames is null)
            log(UiStrings.Format("inst.tc.spells.skip", Path.GetFileName(realExe)));
        else
            foreach (var name in spellNames)
                if (name.Length > 0 && seen.Add(name)) texts.Add(name);

        // 音乐室评论（musiccmt.js）：dat 内的 musiccmt.txt 已随 thdat 解包到 stage。
        // 结构：@bgm/... 行起块、No.N 标题行、♪标题行，之后为全角空格前缀的评论行。
        var musicCmt = ParseMusicCmt(Path.Combine(stage, "musiccmt.txt"));
        if (musicCmt is null)
            log(UiStrings.Get("inst.tc.musiccmt.skip"));
        else
            foreach (var (_, lines) in musicCmt)
            foreach (var line in lines)
                if (TrySplitCmtLine(line, out _, out var content) && seen.Add(content))
                    texts.Add(content);

        log(UiStrings.Format("inst.th.msgs", scripts.Count, texts.Count));

        var (translations, abandoned) = await TouhouStaticService.TranslateBatchAsync(
            game, texts, settings, log, tracker, ct, "mt:touhou-thcrap");

        // 写 jdiff：同一 (entry, time) 的盒全写或全不写（键稳定性见文件头注释）；
        // 完全未译的时间段省略，失败行保留原文。重建只清理本管线生成的文件
        // （jdiff/spells.js/musiccmt.js/title 下生成的 PNG），保留游戏子目录里
        // 随补丁捆绑的其他静态资源。
        var patchGameDir = Path.Combine(patchDir, gameId);
        if (Directory.Exists(patchGameDir))
        {
            foreach (var stale in Directory.EnumerateFiles(patchGameDir, "*.jdiff"))
                File.Delete(stale);
            foreach (var name in new[] { "spells.js", "musiccmt.js" })
            {
                var stale = Path.Combine(patchGameDir, name);
                if (File.Exists(stale)) File.Delete(stale);
            }
            var staleTitle = Path.Combine(patchGameDir, "title");
            foreach (var sub in new[] { "title", "ascii", "front" })
            {
                var staleDir = Path.Combine(patchGameDir, sub);
                if (Directory.Exists(staleDir))
                    foreach (var stale in Directory.EnumerateFiles(staleDir, "*.png"))
                        File.Delete(stale);
            }
        }
        Directory.CreateDirectory(patchGameDir);
        var done = 0;
        var written = 0;
        foreach (var (script, boxes) in plans)
        {
            ct.ThrowIfCancellationRequested();
            var doc = new Dictionary<string, Dictionary<string, Dictionary<string, string[]>>>();
            foreach (var group in boxes.GroupBy(b => (b.Entry, b.Time)))
            {
                var rendered = group
                    .Select(b => b.Lines.Select(l => l.Length > 0 ? translations[l] : l).ToArray())
                    .ToList();
                var changed = group.Zip(rendered).Any(pair =>
                    pair.First.Lines.Zip(pair.Second).Any(l => l.First != l.Second));
                if (!changed) continue;
                var entryKey = group.Key.Entry.ToString(CultureInfo.InvariantCulture);
                if (!doc.TryGetValue(entryKey, out var entryDoc))
                    doc[entryKey] = entryDoc = new Dictionary<string, Dictionary<string, string[]>>();
                foreach (var (box, lines) in group.Zip(rendered))
                    entryDoc[$"{box.Time}_{box.Ind}"] = new Dictionary<string, string[]> { ["lines"] = lines };
            }
            if (doc.Count > 0)
            {
                var jdiff = Path.Combine(patchGameDir, Path.GetFileName(script) + ".jdiff");
                File.WriteAllText(jdiff, JsonSerializer.Serialize(doc, JsonOut), Utf8NoBom);
                written++;
            }
            tracker.WriteStep(++done, plans.Count);
        }
        log(UiStrings.Format("inst.tc.jdiff", written, plans.Count));

        // spells.js：id（十进制字符串）→ 译名；只写译文有变化的条目（对齐 jdiff 省略未译的语义）
        if (spellNames is not null)
        {
            var spellsDoc = new Dictionary<string, string>();
            for (var i = 0; i < spellNames.Count; i++)
            {
                var name = spellNames[i];
                if (name.Length == 0) continue; // 未用 id 的空槽
                var translated = translations.TryGetValue(name, out var t) ? t : name;
                if (translated != name)
                    spellsDoc[i.ToString(CultureInfo.InvariantCulture)] = translated;
            }
            File.WriteAllText(Path.Combine(patchGameDir, "spells.js"),
                JsonSerializer.Serialize(spellsDoc, JsonOut), Utf8NoBom);
            log(UiStrings.Format("inst.tc.spells", spellsDoc.Count));
        }

        // musiccmt.js：曲目号（1 起）→ ["@", 评论行…]；保留行结构，只译文本行，
        // "@" 与纯空白行原样保留，译文行保留原文的全角空格前缀
        if (musicCmt is not null)
        {
            var cmtDoc = new Dictionary<string, string[]>();
            foreach (var (number, lines) in musicCmt)
            {
                var arr = new string[lines.Count + 1];
                arr[0] = "@";
                for (var i = 0; i < lines.Count; i++)
                {
                    arr[i + 1] = TrySplitCmtLine(lines[i], out var prefix, out var content)
                        ? prefix + (translations.TryGetValue(content, out var t) ? t : content)
                        : lines[i];
                }
                cmtDoc[number.ToString(CultureInfo.InvariantCulture)] = arr;
            }
            File.WriteAllText(Path.Combine(patchGameDir, "musiccmt.js"),
                JsonSerializer.Serialize(cmtDoc, JsonOut), Utf8NoBom);
            log(UiStrings.Format("inst.tc.musiccmt", cmtDoc.Count));
        }

        // 菜单精灵图（title01/rank00/select01/result00/weapon）：thanm 解出原图后按布局表
        // 重绘为目标语言；词条走同一端点管线（zh-CN 优先内置校译表），渲染纯本地成像
        var menuAbandoned = await TouhouMenuImageService.GenerateAsync(gameId, stage, patchGameDir,
            game, settings, log, tracker, ct);
        return abandoned + menuAbandoned;
    }

    // 评论行拆分：前缀（连续全角/半角空格）+ 正文。纯空白或空行返回 false（不译）
    private static bool TrySplitCmtLine(string line, out string prefix, out string content)
    {
        var i = 0;
        while (i < line.Length && (line[i] == '　' || line[i] == ' ')) i++;
        prefix = line[..i];
        content = line[i..];
        return content.Trim().Length > 0;
    }

    // 解析 musiccmt.txt 为 (曲目号, 评论行) 序列：@bgm/... 起块，No.N 给曲目号，
    // ♪ 标题行之后为评论行（标题本身由 thcrap 其他机制处理，不进 musiccmt.js）。
    // 文件缺失或没有任何曲目块返回 null。
    internal static List<(int Number, List<string> Lines)>? ParseMusicCmt(string path)
    {
        if (!File.Exists(path)) return null;
        var tracks = new List<(int Number, List<string> Lines)>();
        List<string>? current = null;
        var number = 0;
        var inBody = false;
        void CloseBlock()
        {
            if (current is not null)
            {
                // 去掉块尾空行（原文块间空行不属于评论本体，与 thcrap 社区格式一致）
                while (current.Count > 0 && current[^1].Trim().Length == 0)
                    current.RemoveAt(current.Count - 1);
                tracks.Add((number > 0 ? number : tracks.Count + 1, current));
            }
            current = null;
            number = 0;
            inBody = false;
        }
        foreach (var line in File.ReadLines(path, Sjis))
        {
            if (line.StartsWith('@'))
            {
                CloseBlock();
                current = new List<string>();
                continue;
            }
            if (current is null || inBody) { if (inBody) current!.Add(line); continue; }
            var no = MusicTrackNumber.Match(line);
            if (no.Success)
            {
                number = int.Parse(no.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }
            if (line.StartsWith('♪')) inBody = true;
        }
        CloseBlock();
        return tracks.Count > 0 ? tracks : null;
    }

    // 从 exe 提取符卡名表：全文件扫描连续 NUL 结尾的 SJIS 字符串区段，id 为表内序号
    // （未用 id 是空串）。条目须为可打印 SJIS/ASCII、≤64 字节；区段内允许稀疏空槽
    // （连续空串 >8 视为表结束）。取满足 ≥100 条且 ≥50 条含「的最长区段；
    // 找不到（如 Steam 版 .text 被 SteamStub 加密）返回 null。
    internal static List<string>? ExtractSpellNames(string exePath)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(exePath); }
        catch { return null; }
        List<string>? best = null;
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            if (!(b is >= 0x20 and <= 0x7E || b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC))
            {
                i++;
                continue;
            }
            var entries = new List<string>();
            var pos = i;
            var emptyRun = 0;
            while (entries.Count < 512)
            {
                var end = pos;
                while (end < bytes.Length && bytes[end] != 0) end++;
                if (end >= bytes.Length) break; // 无结尾 NUL：表结束
                var len = end - pos;
                if (len == 0)
                {
                    if (++emptyRun > 8) break;
                    entries.Add("");
                }
                else
                {
                    if (len > 64 || !TryDecodeSjisText(bytes, pos, len, out var text)) break;
                    emptyRun = 0;
                    entries.Add(text);
                }
                pos = end + 1;
            }
            var named = 0;
            foreach (var e in entries)
                if (e.Contains('「')) named++;
            if (entries.Count >= 100 && named >= 50 && (best is null || entries.Count > best.Count))
                best = entries;
            i = Math.Max(pos, i + 1);
        }
        return best;
    }

    // 严格 SJIS 文本校验并解码：ASCII 0x20-0x7E 或合法双字节对（不含半角片假名），
    // 解码后不含控制字符
    private static bool TryDecodeSjisText(byte[] bytes, int offset, int length, out string text)
    {
        text = "";
        var j = offset;
        var end = offset + length;
        while (j < end)
        {
            var c = bytes[j];
            if (c is >= 0x20 and <= 0x7E) { j++; continue; }
            if (c is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC && j + 1 < end)
            {
                var c2 = bytes[j + 1];
                if (c2 is >= 0x40 and <= 0x7E or >= 0x80 and <= 0xFC) { j += 2; continue; }
            }
            return false;
        }
        text = Sjis.GetString(bytes, offset, length);
        foreach (var ch in text)
            if (char.IsControl(ch)) { text = ""; return false; }
        return true;
    }

    // patch.js / global.js / 随补丁字体（字体文件须在补丁目录根，patch.js fonts 表注册）
    private static void WritePatchMetadata(string workTree, string patchDir, string patchId, string language)
    {
        Directory.CreateDirectory(patchDir);
        var patchJs = new Dictionary<string, object>
        {
            ["id"] = patchId,
            ["title"] = $"Franca runtime translation ({language})",
            ["dependencies"] = new[] { "nmlgc/base_tsa" },
            ["update"] = false,
            ["servers"] = Array.Empty<string>(),
            ["fonts"] = new Dictionary<string, bool> { [FontFileName] = true },
        };
        File.WriteAllText(Path.Combine(patchDir, "patch.js"),
            JsonSerializer.Serialize(patchJs, JsonOut), Utf8NoBom);
        // binhack 代码值照抄社区补丁；地址由 base_tsa 的 per-version js 提供，无地址的项 thcrap 跳过
        var globalJs = new Dictionary<string, object>
        {
            ["font"] = FontFamilyName,
            ["binhacks"] = new Dictionary<string, object>
            {
                ["meiryo_disable"] = new Dictionary<string, string> { ["code"] = "83c414 909090" },
                ["font_dialog_params"] = new Dictionary<string, string>
                    { ["code"] = "6a036a006a0068000000006a006a006a006890010000" },
                ["font_dialog_set"] = new Dictionary<string, string> { ["code"] = "6a019090" },
            },
        };
        File.WriteAllText(Path.Combine(patchDir, "global.js"),
            JsonSerializer.Serialize(globalJs, JsonOut), Utf8NoBom);
        WriteStringDefs(patchDir, language);
        var font = Path.Combine(workTree, "fonts", FontFileName);
        if (File.Exists(font))
            File.Copy(font, Path.Combine(patchDir, FontFileName), overwrite: true);
    }

    // stringdefs：对话框等硬编码串的译文。键来自 base_tsa 的 dialog_203.js（启动时的
    // 窗口模式选择框）等规格文件；简中为手工校译，其他语言暂不生成（缺文件即保持原文）。
    private static void WriteStringDefs(string patchDir, string language)
    {
        if (!language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return;
        var defs = new Dictionary<string, string>
        {
            ["th10 JP"] = "东方风神录",
            ["th11 JP"] = "东方地灵殿",
            ["th10 Dialog Prompt"] = "请选择窗口模式：",
            ["th10 Dialog Always ask"] = "每次启动时询问",
            ["th10 Dialog Fullscreen"] = "全屏",
            ["th06 windowed"] = "窗口",
            ["th11 Dialog Fullscreen 640"] = "全屏(640×480)",
            ["th11 Dialog Window 640"] = "窗口(640×480)",
            ["th11 Dialog Window 960"] = "窗口(960×720)",
            ["th11 Dialog Window 1280"] = "窗口(1280×960)",
            ["th11 Dialog Game Start"] = "启动游戏",
        };
        File.WriteAllText(Path.Combine(patchDir, "stringdefs.js"),
            JsonSerializer.Serialize(defs, JsonOut), Utf8NoBom);
    }

    // 运行配置：补丁栈 base_tsa + franca 补丁，archive 相对 thcrap 根、结尾带斜杠
    private static void WriteRunConfig(string workTree, string patchId)
    {
        var configDir = Path.Combine(workTree, "config");
        Directory.CreateDirectory(configDir);
        var runConfig = new Dictionary<string, object>
        {
            ["console"] = false,
            ["dat_dump"] = false,
            ["patches"] = new object[]
            {
                new Dictionary<string, string> { ["archive"] = "repos/nmlgc/base_tsa/" },
                new Dictionary<string, string> { ["archive"] = $"repos/franca/{patchId}/" },
            },
        };
        File.WriteAllText(Path.Combine(configDir, "franca.js"),
            JsonSerializer.Serialize(runConfig, JsonOut), Utf8NoBom);
    }

    // 工作树：ThirdParty/thcrap 整树复制到 <程序目录>/thcrap/（已存在按长度增量补齐）；
    // 仓声明 repo.js 缺失时补写（离线，无 servers）；补丁数据（base_tsa）按清单下载。
    private static string EnsureWorkTree(Action<string>? log = null, CancellationToken ct = default)
    {
        var source = Path.Combine(AppContext.BaseDirectory, SourceTreeName.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(source))
            throw new FileNotFoundException("缺少 thcrap 运行时（ThirdParty/thcrap）。");
        var target = WorkTreeRoot;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(file).Length) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
        WriteIfMissing(Path.Combine(target, "repos", "nmlgc", "repo.js"),
            """{"id": "nmlgc", "title": "nmlgc", "servers": [], "update": false}""");
        WriteIfMissing(Path.Combine(target, "repos", "franca", "repo.js"),
            """{"id": "franca", "title": "Franca local patches", "servers": [], "update": false}""");
        EnsureBaseTsa(target, log, ct);
        return target;
    }

    // 补丁数据准备：已就位（versions.js 存在）直接跳过；探针走本地夹具；
    // 否则按仓库内清单逐个下载并校验 SHA-256（镜像优先，固定 commit 的 GitHub raw 兜底）。
    // 清单固定上游 commit：镜像随上游 master 更新时哈希不符会自动回退到固定版本。
    private static void EnsureBaseTsa(string workTree, Action<string>? log, CancellationToken ct)
    {
        var dir = Path.Combine(workTree, BaseTsaRelativeDir.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(Path.Combine(dir, "versions.js"))) return;

        if (BaseTsaSourceForProbe is { } localSource)
        {
            if (!Directory.Exists(localSource))
                throw new DirectoryNotFoundException($"缺少探针补丁数据夹具：{localSource}");
            foreach (var file in Directory.EnumerateFiles(localSource, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(dir, Path.GetRelativePath(localSource, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            return;
        }

        var manifestPath = Path.Combine(AppContext.BaseDirectory,
            BaseTsaManifestName.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("缺少东方补丁数据清单（ThirdParty/thcrap-repos/base_tsa.files.json）。");
        var manifest = JsonSerializer.Deserialize<BaseTsaManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("东方补丁数据清单无法解析。");
        var files = manifest.Files;
        var servers = manifest.Servers;
        if (files is null || files.Count == 0 || servers is null || servers.Count == 0)
            throw new InvalidDataException("东方补丁数据清单不完整。");

        var missing = files.Where(f => !BaseTsaFileValid(dir, f)).ToList();
        if (missing.Count == 0) return;
        var totalKb = Math.Max(1, missing.Sum(f => f.Size) / 1024);
        log?.Invoke($"正在准备东方补丁数据（首次启用需要联网，{missing.Count} 个文件，约 {totalKb} KB）…");

        try
        {
            Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, file =>
            {
                var dest = Path.Combine(dir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var bytes = FetchBaseTsaFile(servers, file, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var tmp = dest + ".frdownload";
                try
                {
                    File.WriteAllBytes(tmp, bytes);
                    if (!BaseTsaBytesValid(File.ReadAllBytes(tmp), file))
                        throw new InvalidDataException($"补丁数据校验失败：{file.Path}");
                    File.Move(tmp, dest, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
            });
        }
        catch (AggregateException ex)
        {
            var inner = ex.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
            if (inner is null) throw new OperationCanceledException(ct);
            throw new InvalidOperationException(
                "东方路线首次启用需要联网下载补丁数据（约 1 MB），当前下载失败。请检查网络后重试。", inner);
        }
        log?.Invoke("东方补丁数据已就绪。");
    }

    private static bool BaseTsaFileValid(string dir, BaseTsaFile file)
    {
        var path = Path.Combine(dir, file.Path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path)
            && new FileInfo(path).Length is var length
            && (length == file.Size || (file.MirrorSize is { } mirrorSize && length == mirrorSize))
            && (BaseTsaHashMatches(path, file.Sha256)
                || (file.MirrorSha256 is { } mirrorSha && BaseTsaHashMatches(path, mirrorSha)));
    }

    // 探针入口：在临时工作树里真实执行一次补丁数据准备（联网，仅手动烟测用）
    internal static void EnsureBaseTsaForProbe(string workTree) => EnsureBaseTsa(workTree, null, CancellationToken.None);

    private static bool BaseTsaHashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return actual.Equals(expected, StringComparison.Ordinal);
    }

    // 单文件下载：按清单顺序试每个下载源，逐个校验大小与 SHA-256。
    // 镜像可能随上游部署改写个别文件（实测 base_tsa/patch.js 与上游 commit 不同），
    // 因此哈希不符不算成功，继续试下一个源（固定 commit 的 GitHub raw 必定匹配）。
    private static byte[] FetchBaseTsaFile(IReadOnlyList<string> servers, BaseTsaFile file, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var server in servers)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var url = server + file.Path;
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    using var stream = response.Content.ReadAsStream(ct);
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    var bytes = buffer.ToArray();
                    if (!BaseTsaBytesValid(bytes, file))
                        throw new InvalidDataException($"补丁数据校验失败：{file.Path}");
                    return bytes;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
                {
                    last = ex;
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                }
            }
        }
        throw new InvalidOperationException($"下载补丁数据失败：{file.Path}", last);
    }

    // 清单可能记录两套期望值：上游 commit（GitHub raw）与 thpatch 镜像当前部署。
    // 镜像会改写个别文件（实测 base_tsa/patch.js），两套哈希命中任意一套都算成功。
    private static bool BaseTsaBytesValid(byte[] bytes, BaseTsaFile file)
    {
        var sha = Sha256Hex(bytes);
        if (string.Equals(sha, file.Sha256, StringComparison.Ordinal) && bytes.LongLength == file.Size) return true;
        return file.MirrorSha256 is { } mirrorSha
            && file.MirrorSize is { } mirrorSize
            && string.Equals(sha, mirrorSha, StringComparison.Ordinal)
            && bytes.LongLength == mirrorSize;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Franca/0.1 (+https://github.com/hongweihu184/franca)");
        return client;
    }

    private sealed record BaseTsaManifest(string? Id, string? Upstream, string? Commit,
        List<string>? Servers, List<BaseTsaFile>? Files);

    private sealed record BaseTsaFile(string Path, long Size, string Sha256, long? MirrorSize, string? MirrorSha256);

    private static void WriteIfMissing(string path, string content)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    // 写入存根 exe：先写临时名再原子就位，中断由 Recover 收尾
    private static void WriteStub(string exe)
    {
        if (!File.Exists(BundledStubPath))
            throw new FileNotFoundException("缺少 thcrap 存根（ThirdParty/thcrap-stub/franca-thcrap-stub.exe）。");
        var tmp = exe + ".frnew";
        File.Copy(BundledStubPath, tmp, overwrite: true);
        File.Move(tmp, exe, overwrite: true);
    }

    // games.js：游戏 ID → 真实 exe 绝对路径。备份名 thNN.exe.frorig 不以 .exe 结尾，
    // loader 命令行会把非 .exe 参数当游戏 ID 解析，所以经 games.js 间接指认真实 exe。
    private static void WriteGamesJs(string workTree, string gameId, string realExe)
    {
        var configDir = Path.Combine(workTree, "config");
        Directory.CreateDirectory(configDir);
        var gamesPath = Path.Combine(configDir, "games.js");
        var games = new Dictionary<string, string>();
        if (File.Exists(gamesPath))
        {
            try
            {
                games = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(gamesPath))
                    ?? new Dictionary<string, string>();
            }
            catch { games = new Dictionary<string, string>(); }
        }
        games[$"franca_{gameId}"] = realExe;
        File.WriteAllText(gamesPath, JsonSerializer.Serialize(games, JsonOut), Utf8NoBom);
    }

    // 命令文件（存根读取）：loader 绝对路径、config 绝对路径、games.js 里的游戏 ID
    private static void WriteCommandFile(string exe, string workTree, string gameId)
    {
        var commandPath = Path.Combine(
            Path.GetDirectoryName(exe)!,
            Path.GetFileNameWithoutExtension(exe) + ".thcrapcmd");
        var lines = string.Join('\n',
            Path.Combine(workTree, "bin", "thcrap_loader.exe"),
            Path.Combine(workTree, "config", "franca.js"),
            $"franca_{gameId}") + '\n';
        File.WriteAllText(commandPath, lines, Utf8NoBom);
    }
}
