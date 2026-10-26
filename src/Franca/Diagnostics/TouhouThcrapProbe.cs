using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

// 东方 thcrap 运行时翻译路线烟测（--probe-touhou-thcrap）。
// 真实 th10/th11 目录只读（哈希识别）；完整启用/停用/恢复管线跑在临时目录的
// th11.exe/th11.dat 副本上，翻译打桩为按行加前缀，工作树与翻译缓存重定向到临时目录，
// 不修改任何真实游戏目录。
internal static class TouhouThcrapProbe
{
    private static string Th10Dir => ProbePaths.GameDir("th10");
    private static string Th11Dir => ProbePaths.GameDir("th11");

    public static async Task RunAsync()
    {
        var failures = 0;
        string? fixtureTsa = null;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tTHCRAP\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition) failures++;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        UiStrings.Current = "en";
        var bundledTree = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thcrap");
        var stubPath = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thcrap-stub", "franca-thcrap-stub.exe");
        var baseTsaManifest = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thcrap-repos", "base_tsa.files.json");
        // 补丁数据不随仓库分发（运行时下载），探针要求清单存在，实际数据用下方合成夹具
        Check("thcrap_tree_bundled", File.Exists(Path.Combine(bundledTree, "bin", "thcrap_loader.exe"))
            && File.Exists(baseTsaManifest)
            && File.Exists(Path.Combine(bundledTree, "fonts", "NotoSansSC.ttf")));
        Check("stub_bundled", File.Exists(stubPath) && TouhouThcrapService.HasStubMarker(stubPath));

        // 真实目录可能已启用（原位是存根，真实 exe 在 .frorig）：解析出真实 exe 再哈希/复制
        string RealExe(string gameDir, string name)
        {
            var onDisk = Path.Combine(gameDir, name);
            return TouhouThcrapService.HasStubMarker(onDisk) && File.Exists(onDisk + ".frorig")
                ? onDisk + ".frorig" : onDisk;
        }
        var realTh11Exe = RealExe(Th11Dir, "th11.exe");
        var realTh10Exe = RealExe(Th10Dir, "th10.exe");
        Check("real_th11_exe_resolvable", File.Exists(realTh11Exe)
            && !TouhouThcrapService.HasStubMarker(realTh11Exe));

        // 符卡名表提取（合成夹具：无效字节夹住的 175 项 SJIS 字符串表，含空槽）。
        // 键序与社区数据抽样比对：id0=怪奇「釣瓶落としの怪」、id2=罠符、id4=蜘蛛（原文，非译文）
        var sjis = Encoding.GetEncoding(932);
        var spellTable = new List<string>
            { "怪奇「釣瓶落としの怪」", "", "罠符「キャプチャーウェブ」", "", "蜘蛛「石窟の蜘蛛の巣」" };
        for (var n = 5; n < 175; n++) spellTable.Add($"練習「テスト{n}」");
        var fixtureExe = Path.Combine(Path.GetTempPath(), "franca-spellfixture-" + Guid.NewGuid().ToString("N") + ".bin");
        {
            using var fs = File.Create(fixtureExe);
            var guard = new byte[64];
            Array.Fill(guard, (byte)0xFF);
            fs.Write(guard);
            foreach (var name in spellTable)
            {
                fs.Write(sjis.GetBytes(name));
                fs.WriteByte(0);
            }
            fs.Write(guard);
        }
        var fixtureSpells = TouhouThcrapService.ExtractSpellNames(fixtureExe);
        Check("spell_extract_fixture", fixtureSpells is not null
            && fixtureSpells.Count == 175
            && fixtureSpells[0] == "怪奇「釣瓶落としの怪」"
            && fixtureSpells[2] == "罠符「キャプチャーウェブ」"
            && fixtureSpells[4] == "蜘蛛「石窟の蜘蛛の巣」"
            && fixtureSpells[100].StartsWith("練習「", StringComparison.Ordinal));
        try { File.Delete(fixtureExe); } catch { }

        // 真实 exe：Steam 版 .text 被 SteamStub 加密（th10 连 .rdata 也加密），预期提取失败走跳过路径；
        // 若在无 DRM 的 exe 上运行则必须提到 >100 条且 id0 为「怪奇」开头
        var realSpells = File.Exists(realTh11Exe) ? TouhouThcrapService.ExtractSpellNames(realTh11Exe) : null;
        Console.WriteLine(realSpells is null
            ? "NOTE\tTHCRAP\treal th11 exe: spell table not extractable (SteamStub-encrypted .text); skip path will be exercised"
            : $"NOTE\tTHCRAP\treal th11 exe: spell table extracted, {realSpells.Count} entries, id0={realSpells[0]}");
        Check("real_exe_spell_extract", realSpells is null
            || (realSpells.Count > 100 && realSpells[0].StartsWith("怪奇", StringComparison.Ordinal)));

        // 真实 exe 版本识别（只读哈希）。base_tsa 不再入库：用合成 versions.js 夹具
        // （只需 exe SHA-256 → 游戏 ID 映射），并让服务从夹具复制补丁数据、不走网络。
        var th11Hash = InstallationTransaction.Hash(realTh11Exe);
        var th10Hash = InstallationTransaction.Hash(realTh10Exe);
        fixtureTsa = Path.Combine(Path.GetTempPath(), "franca-basetsa-fixture-" + Guid.NewGuid().ToString("N"));
        var fixtureTsaRepo = Path.Combine(fixtureTsa, "repos", "nmlgc", "base_tsa");
        Directory.CreateDirectory(fixtureTsaRepo);
        File.WriteAllText(Path.Combine(fixtureTsaRepo, "versions.js"),
            "{\n\t\"" + th11Hash.ToLowerInvariant() + "\": [\n\t\t\"th11\"\n\t],\n\t\""
            + th10Hash.ToLowerInvariant() + "\": [\n\t\t\"th10\"\n\t]\n}\n");
        TouhouThcrapService.BaseTsaSourceForProbe = fixtureTsaRepo;
        Check("versions_recognize_th11", TouhouThcrapService.IdentifyGame(fixtureTsa, th11Hash) == "th11");
        Check("versions_recognize_th10", TouhouThcrapService.IdentifyGame(fixtureTsa, th10Hash) == "th10");
        Check("versions_reject_garbage", TouhouThcrapService.IdentifyGame(fixtureTsa, new string('0', 64)) is null);
        Check("patchid_mapping", TouhouThcrapService.PatchIdFor("zh-CN") == "zh_hans"
            && TouhouThcrapService.PatchIdFor("zh-TW") == "zh_hant"
            && TouhouThcrapService.PatchIdFor("pt-BR") == "pt_br");
        // 泛化格式表（thcrap msg_format_for 分档）抽样断言
        Check("msgfmt_table", TouhouThcrapService.MsgFormatFor(11).TextCodes.SequenceEqual(new[] { 17 })
            && TouhouThcrapService.MsgFormatFor(11).AutoEndCodes.SequenceEqual(new[] { 7, 8, 9, 11 })
            && TouhouThcrapService.MsgFormatFor(10).TextCodes.SequenceEqual(new[] { 16 })
            && TouhouThcrapService.MsgFormatFor(8).TextCodes.SequenceEqual(new[] { 3, 16, 19, 20 })
            && TouhouThcrapService.MsgFormatFor(6).TextCodes.SequenceEqual(new[] { 3, 8 })
            && TouhouThcrapService.MsgFormatFor(143).TextCodes.SequenceEqual(new[] { 17 })
            && TouhouThcrapService.EndingFormat.TextCodes.SequenceEqual(new[] { 3 })
            && TouhouThcrapService.EndingFormat.AutoEndCodes.SequenceEqual(new[] { 5, 6, 9 }));

        var root = Path.Combine(Path.GetTempPath(), "franca-thcrap-probe-" + Guid.NewGuid().ToString("N"));
        var workTree = Path.Combine(root, "work");
        TouhouThcrapService.WorkTreeRootForProbe = workTree;
        TouhouThcrapService.PacksRootForProbe = Path.Combine(root, "probe-packs");
        var settings = new AppSettings { TargetLanguage = "zh-CN", SourceLanguage = "ja", Endpoint = "LlmDeepSeek" };
        var stubRequests = new List<string>();
        TouhouThcrapService.TranslatorOverride = (text, _, _, _) =>
        {
            lock (stubRequests) stubRequests.Add(text);
            return Task.FromResult<string?>(string.Join("\n",
                text.Replace("\r\n", "\n").Split('\n').Select(l => "【F】" + l)));
        };
        // 端到端 spells.js 用合成符卡名表（真实 exe 的 .text 被 SteamStub 加密，见上方 NOTE）
        TouhouThcrapService.SpellNamesForProbe = _ => spellTable;

        // 夹具游戏目录：真实 th11.exe/th11.dat 的副本（启用态下真实 exe 取 .frorig）
        string MakeFixture(string name)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            File.Copy(realTh11Exe, Path.Combine(dir, "th11.exe"));
            File.Copy(Path.Combine(Th11Dir, "th11.dat"), Path.Combine(dir, "th11.dat"));
            return dir;
        }

        try
        {
            // 1) 端到端启用
            var gameDir = MakeFixture("th11");
            var exe = Path.Combine(gameDir, "th11.exe");
            var dat = Path.Combine(gameDir, "th11.dat");
            var exeHash = InstallationTransaction.Hash(exe);
            var datHash = InstallationTransaction.Hash(dat);
            var logs = new List<string>();
            var progressLog = new List<TouhouStaticService.TouhouInstallProgress>();
            var game = new GameInfo { Name = "th11", Path = gameDir, Engine = GameEngine.Touhou, AppId = 999011 };
            await TouhouThcrapService.EnableAsync(game, settings, new Collect(logs),
                installProgress: new Collect<TouhouStaticService.TouhouInstallProgress>(progressLog));

            Check("enable_ready", TouhouThcrapService.IsReady(gameDir)
                && File.Exists(Path.Combine(gameDir, ".franca", "touhou-thcrap.json")));
            Check("exe_swap_backup_hash", InstallationTransaction.Hash(exe + ".frorig") == exeHash);
            Check("stub_in_place", TouhouThcrapService.HasStubMarker(exe));
            Check("dat_untouched", InstallationTransaction.Hash(dat) == datHash);
            Check("progress_complete", progressLog.Count > 0 && progressLog[^1].Percent == 100.0);

            var cmdLines = File.ReadAllLines(Path.Combine(gameDir, "th11.thcrapcmd"));
            Check("thcrapcmd_three_lines", cmdLines.Length == 3
                && File.Exists(cmdLines[0]) && File.Exists(cmdLines[1])
                && cmdLines[2] == "franca_th11"
                && cmdLines[0].EndsWith(Path.Combine("bin", "thcrap_loader.exe"))
                && cmdLines[1].EndsWith(Path.Combine("config", "franca.js")));
            var gamesJs = File.ReadAllText(Path.Combine(workTree, "config", "games.js"));
            Check("games_js_real_exe", gamesJs.Contains("franca_th11")
                && gamesJs.Contains(".frorig"));

            var runConfig = File.ReadAllText(Path.Combine(workTree, "config", "franca.js"));
            Check("runconfig_patch_stack", runConfig.Contains("repos/nmlgc/base_tsa/")
                && runConfig.Contains("repos/franca/zh_hans/") && runConfig.Contains("\"dat_dump\": false"));

            var patchDir = Path.Combine(workTree, "repos", "franca", "zh_hans");
            using (var patchJs = JsonDocument.Parse(File.ReadAllText(Path.Combine(patchDir, "patch.js"))))
            {
                var r = patchJs.RootElement;
                Check("patchjs_metadata", r.GetProperty("id").GetString() == "zh_hans"
                    && r.GetProperty("dependencies").EnumerateArray().Any(d => d.GetString() == "nmlgc/base_tsa")
                    && r.GetProperty("update").GetBoolean() == false
                    && r.GetProperty("servers").GetArrayLength() == 0
                    && r.GetProperty("fonts").GetProperty("NotoSansSC.ttf").GetBoolean());
            }
            using (var globalJs = JsonDocument.Parse(File.ReadAllText(Path.Combine(patchDir, "global.js"))))
            {
                var r = globalJs.RootElement;
                Check("globaljs_font_and_binhacks", r.GetProperty("font").GetString() == "Noto Sans SC"
                    && r.GetProperty("binhacks").GetProperty("meiryo_disable").GetProperty("code").GetString() == "83c414 909090"
                    && r.GetProperty("binhacks").TryGetProperty("font_dialog_params", out _)
                    && r.GetProperty("binhacks").TryGetProperty("font_dialog_set", out _));
            }
            Check("font_copied", File.Exists(Path.Combine(patchDir, "NotoSansSC.ttf")));
            using (var stringdefs = JsonDocument.Parse(File.ReadAllText(Path.Combine(patchDir, "stringdefs.js"))))
            {
                var r = stringdefs.RootElement;
                Check("stringdefs_startup_dialog", r.GetProperty("th11 Dialog Game Start").GetString() == "启动游戏"
                    && r.GetProperty("th10 Dialog Prompt").GetString() == "请选择窗口模式："
                    && r.GetProperty("th11 JP").GetString() == "东方地灵殿");
            }

            // 2) jdiff 结构对照真实 dump：外层 entry 键、"34_0" 首盒两行、结局 -e 路径
            var jdiffPath = Path.Combine(patchDir, "th11", "st01_00a.msg.jdiff");
            Check("jdiff_exists", File.Exists(jdiffPath));
            using (var jdiff = JsonDocument.Parse(File.ReadAllText(jdiffPath)))
            {
                var r = jdiff.RootElement;
                var entry0 = r.TryGetProperty("0", out var e0) ? e0 : default;
                Check("jdiff_entry_keys", r.TryGetProperty("0", out _) && r.TryGetProperty("1", out _));
                var boxOk = entry0.ValueKind == JsonValueKind.Object
                    && entry0.TryGetProperty("34_0", out var box)
                    && box.GetProperty("lines") is { } lines
                    && lines.GetArrayLength() == 2
                    && lines.EnumerateArray().All(l => l.GetString()!.StartsWith("【F】", StringComparison.Ordinal));
                Check("jdiff_34_0_two_prefixed_lines", boxOk);
                var keysOk = entry0.ValueKind == JsonValueKind.Object
                    && entry0.EnumerateObject().All(p =>
                        System.Text.RegularExpressions.Regex.IsMatch(p.Name, @"^\d+_\d+$"));
                Check("jdiff_key_format", keysOk);
            }
            var endJdiffPath = Path.Combine(patchDir, "th11", "e00.msg.jdiff");
            using (var endJdiff = JsonDocument.Parse(File.ReadAllText(endJdiffPath)))
            {
                var ok = endJdiff.RootElement.TryGetProperty("0", out var e0)
                    && e0.TryGetProperty("0_0", out var box)
                    && box.GetProperty("lines").GetArrayLength() == 1
                    && box.GetProperty("lines")[0].GetString()!.StartsWith("【F】", StringComparison.Ordinal);
                Check("jdiff_ending_e00", ok);
            }
            var allJdiffValid = true;
            foreach (var f in Directory.GetFiles(Path.Combine(patchDir, "th11"), "*.jdiff"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    foreach (var entry in doc.RootElement.EnumerateObject())
                    foreach (var box in entry.Value.EnumerateObject())
                        if (box.Value.GetProperty("lines").GetArrayLength() == 0) allJdiffValid = false;
                }
                catch { allJdiffValid = false; }
            }
            Check("jdiff_all_valid_nonempty", allJdiffValid);

            // 2b) spells.js / musiccmt.js：结构、打桩译文前缀、UTF-8 无 BOM
            var patchGameDir = Path.Combine(patchDir, "th11");
            var spellsJsPath = Path.Combine(patchGameDir, "spells.js");
            Check("spells_js_exists", File.Exists(spellsJsPath));
            using (var spells = JsonDocument.Parse(File.ReadAllText(spellsJsPath)))
            {
                var r = spells.RootElement;
                Check("spells_js_structure", r.TryGetProperty("0", out var s0)
                    && s0.GetString() == "【F】怪奇「釣瓶落としの怪」"
                    && !r.TryGetProperty("1", out _) // 空槽不写入
                    && r.TryGetProperty("2", out var s2)
                    && s2.GetString() == "【F】罠符「キャプチャーウェブ」"
                    && r.EnumerateObject().Count() == 173
                    && r.EnumerateObject().All(p => p.Value.GetString()!.StartsWith("【F】", StringComparison.Ordinal)));
            }
            var musiccmtJsPath = Path.Combine(patchGameDir, "musiccmt.js");
            Check("musiccmt_js_exists", File.Exists(musiccmtJsPath));
            using (var cmt = JsonDocument.Parse(File.ReadAllText(musiccmtJsPath)))
            {
                var r = cmt.RootElement;
                Check("musiccmt_js_17_tracks", r.EnumerateObject().Count() == 17
                    && r.TryGetProperty("1", out _) && r.TryGetProperty("17", out _));
                var t1 = r.GetProperty("1");
                Check("musiccmt_js_track1_structure", t1.GetArrayLength() == 8
                    && t1[0].GetString() == "@"
                    && t1[1].GetString() == "　" // 纯空白行不译
                    && t1[2].GetString() == "　【F】タイトル画面のテーマです。" // 保留全角前缀
                    && t1[3].GetString() == "　");
                Check("musiccmt_js_all_prefixed", r.EnumerateObject().All(track =>
                    track.Value[0].GetString() == "@"
                    && track.Value.EnumerateArray().Skip(1).All(l =>
                    {
                        var s = l.GetString()!;
                        return s.Trim().Length == 0 || s.Contains("【F】", StringComparison.Ordinal);
                    })));
            }
            foreach (var f in new[] { spellsJsPath, musiccmtJsPath })
            {
                var head = File.ReadAllBytes(f);
                Check("no_bom_" + Path.GetFileName(f).Replace('.', '_'),
                    !(head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF));
            }

            // 2c) 菜单精灵图：全部 23 张生成、title01 尺寸/透明通道、zh 校译覆盖优先、管线回填
            var title01 = Path.Combine(patchGameDir, "title", "title01.png");
            Check("menuimg_title01_exists", File.Exists(title01));
            var expectedPngs = new[]
            {
                "title/title01.png", "title/rank00.png", "title/select01.png", "title/result00.png",
                "title/weapon.png", "title/sl_pl00b.png", "title/sl_pl01b.png",
                "title/weapon_00a.png", "title/weapon_00b.png", "title/weapon_00c.png",
                "title/weapon_01a.png", "title/weapon_01b.png", "title/weapon_01c.png",
                "ascii/pause.png", "ascii/loading.png", "front/front00.png",
                "front/st01logo.png", "front/st02logo.png", "front/st03logo.png", "front/st04logo.png",
                "front/st05logo.png", "front/st06logo.png", "front/st07logo.png",
            };
            Check("menuimg_all_images", expectedPngs
                .All(n => File.Exists(Path.Combine(patchGameDir, n.Replace('/', Path.DirectorySeparatorChar)))));
            var (tw, th, tTransparent, tOpaque) = PngStats(title01);
            Check("menuimg_title01_512_alpha", tw == 512 && th == 512 && tTransparent > 100000 && tOpaque > 10000);
            var (_, _, _, pauseOpaque) = PngStats(Path.Combine(patchGameDir, "ascii", "pause.png"));
            var (_, _, _, logoOpaque) = PngStats(Path.Combine(patchGameDir, "front", "st01logo.png"));
            Check("menuimg_pause_logo_nonempty", pauseOpaque > 3000 && logoOpaque > 1000);
            // zh-CN：校译表优先于打桩管线（不带 【F】 前缀），含按游戏区分的同文异译
            Check("menuimg_zh_curated_priority",
                TouhouMenuImageService.ResolveTerm("th11", "Game Start", "zh-CN", null) == "开始游戏"
                && TouhouMenuImageService.ResolveTerm("th11", "Weapon Select", "zh-CN", null) == "选择支援"
                && TouhouMenuImageService.ResolveTerm("th10", "Weapon Select", "zh-CN", null) == "选择武器"
                && TouhouMenuImageService.ResolveTerm("th11", "難易度を選べ", "zh-CN", null) == "选择难度"
                && TouhouMenuImageService.ResolveTerm("th11", "忘恩の地から吹く風", "zh-CN", null) == "从忘恩之地吹来的风"
                && TouhouMenuImageService.ResolveTerm("th11", "一時停止を解除する", "zh-CN", null) == "解除暂停"
                && TouhouMenuImageService.ResolveTerm("th11", "「頂門紫針」", "zh-CN", null) == "「顶门紫针」"
                && TouhouMenuImageService.ResolveTerm("th10", "九天の滝", "zh-CN", null) == "九天瀑布");
            // 非 zh 目标语言：管线结果优先，管线缺结果时保留原文
            var stubbed = new Dictionary<string, string> { ["Game Start"] = "【F】Game Start" };
            Check("menuimg_pipeline_fallback",
                TouhouMenuImageService.ResolveTerm("th11", "Game Start", "fr", stubbed) == "【F】Game Start"
                && TouhouMenuImageService.ResolveTerm("th11", "Game Start", "fr", null) == "Game Start");

            // 3) 幂等：同语言再启用零翻译请求，运行配置重写，dat 仍不动
            int requestCount;
            lock (stubRequests) requestCount = stubRequests.Count;
            var logs2 = new List<string>();
            await TouhouThcrapService.EnableAsync(game, settings, new Collect(logs2));
            lock (stubRequests) Check("idempotent_no_retranslate", stubRequests.Count == requestCount);
            Check("idempotent_logged", logs2.Any(l =>
                l.Contains("already exists", StringComparison.OrdinalIgnoreCase)));
            Check("dat_still_untouched", InstallationTransaction.Hash(dat) == datHash);

            // 3b) 重建保留游戏子目录的无关文件（未来随补丁捆绑的静态资源）；
            //     musiccmt.js 缺失视为补丁不完整，触发重建补齐
            var sentinel = Path.Combine(patchGameDir, "sentinel-static-asset.bin");
            File.WriteAllText(sentinel, "keep-me");
            File.Delete(musiccmtJsPath);
            await TouhouThcrapService.EnableAsync(game, settings, new Collect(new List<string>()));
            Check("rebuild_keeps_unrelated_file",
                File.Exists(sentinel) && File.ReadAllText(sentinel) == "keep-me");
            Check("rebuild_regenerates_musiccmt", File.Exists(musiccmtJsPath));
            Check("rebuild_regenerates_spells", File.Exists(spellsJsPath));
            Check("rebuild_jdiff_intact", File.Exists(jdiffPath));
            Check("rebuild_regenerates_menuimg", File.Exists(title01));

            // 3c) 换语言重建：fr 目标语言全量走打桩管线（校译表仅 zh-CN），
            //     新补丁目录的 title01.png 与 zh 版像素不同（Game Start 格含打桩前缀文字）
            var dirFr = MakeFixture("th11-fr");
            var settingsFr = new AppSettings { TargetLanguage = "fr", SourceLanguage = "ja", Endpoint = "LlmDeepSeek" };
            await TouhouThcrapService.EnableAsync(
                new GameInfo { Name = "th11fr", Path = dirFr, Engine = GameEngine.Touhou, AppId = 999013 },
                settingsFr, new Collect(new List<string>()));
            var title01Fr = Path.Combine(workTree, "repos", "franca", "fr", "th11", "title", "title01.png");
            Check("menuimg_relang_generated", File.Exists(title01Fr));
            Check("menuimg_relang_differs", File.Exists(title01Fr)
                && InstallationTransaction.Hash(title01Fr) != InstallationTransaction.Hash(title01)
                && CellDiffers(title01, title01Fr, 0, 0, 128, 32));
            lock (stubRequests)
                Check("menuimg_terms_piped",
                    stubRequests.Any(r => r.Contains("Game Start", StringComparison.Ordinal))
                    && stubRequests.Any(r => r.Contains("妖精級", StringComparison.Ordinal)));

            // 4) 停用/重启用（经 TranslationInstaller 分支）与卸载还原
            TranslationInstaller.SetDisabled(gameDir, true);
            Check("disabled_restores_original_exe", InstallationTransaction.Hash(exe) == exeHash
                && TouhouThcrapService.IsDisabled(gameDir) && File.Exists(exe + ".frorig"));
            TranslationInstaller.SetDisabled(gameDir, false);
            Check("reenabled_stub_back", TouhouThcrapService.HasStubMarker(exe)
                && !TouhouThcrapService.IsDisabled(gameDir));
            TranslationInstaller.SetDisabled(gameDir, true);
            TranslationInstaller.SetDisabled(gameDir, false); // 幂等第二次
            Check("toggle_idempotent", TouhouThcrapService.HasStubMarker(exe)
                && InstallationTransaction.Hash(exe + ".frorig") == exeHash);

            // 5) 未识别 exe 明确拒绝
            //    之后的小节走真实提取路径（去掉符卡名表桩）：Steam 版 exe 提取失败只跳过不阻断
            TouhouThcrapService.SpellNamesForProbe = null;
            var dirUnknown = Path.Combine(root, "th11-unknown");
            Directory.CreateDirectory(dirUnknown);
            File.WriteAllText(Path.Combine(dirUnknown, "th11.exe"), "not a real game exe");
            File.Copy(Path.Combine(Th11Dir, "th11.dat"), Path.Combine(dirUnknown, "th11.dat"));
            var logsUnknown = new List<string>();
            await TouhouThcrapService.EnableAsync(
                new GameInfo { Name = "th11x", Path = dirUnknown, Engine = GameEngine.Touhou },
                settings, new Collect(logsUnknown));
            Check("unknown_exe_rejected", logsUnknown.Any(l =>
                    l.Contains("not recognized", StringComparison.OrdinalIgnoreCase))
                && !TouhouThcrapService.IsReady(dirUnknown)
                && !File.Exists(Path.Combine(dirUnknown, ".franca", "touhou-thcrap.json"))
                && File.ReadAllText(Path.Combine(dirUnknown, "th11.exe")) == "not a real game exe");

            // 6) 静态补丁态前置恢复：模拟旧路线安装（touhou-static.json + dat.frorig），
            //    启用 thcrap 路线时先还原静态补丁再就位
            var dirStatic = MakeFixture("th11-static");
            File.Copy(Path.Combine(dirStatic, "th11.dat"), Path.Combine(dirStatic, "th11.dat.frorig"));
            Directory.CreateDirectory(Path.Combine(dirStatic, ".franca"));
            File.WriteAllText(Path.Combine(dirStatic, ".franca", "touhou-static.json"),
                """{"Dat":"th11.dat","Version":11,"Language":"zh-CN","AppliedAt":"2026-01-01T00:00:00+00:00","SourceHash":"","Incomplete":false}""");
            var logsStatic = new List<string>();
            await TouhouThcrapService.EnableAsync(
                new GameInfo { Name = "th11s", Path = dirStatic, Engine = GameEngine.Touhou, AppId = 999011 },
                settings, new Collect(logsStatic));
            Check("static_state_restored_first", logsStatic.Any(l =>
                    l.Contains("static patch", StringComparison.OrdinalIgnoreCase))
                && !File.Exists(Path.Combine(dirStatic, ".franca", "touhou-static.json"))
                && !File.Exists(Path.Combine(dirStatic, "th11.dat.frorig"))
                && TouhouThcrapService.IsReady(dirStatic)
                && TouhouThcrapService.HasStubMarker(Path.Combine(dirStatic, "th11.exe")));
            // 符卡名表提取失败（Steam 版 exe）只记日志跳过，不阻断启用；musiccmt.js 仍正常生成
            if (realSpells is null)
                Check("spell_extract_failure_graceful",
                    !File.Exists(Path.Combine(patchDir, "th11", "spells.js"))
                    && logsStatic.Any(l => l.Contains("spell card name table", StringComparison.OrdinalIgnoreCase))
                    && File.Exists(Path.Combine(patchDir, "th11", "musiccmt.js")));
            else
                Check("spell_extract_real_exe_used",
                    File.Exists(Path.Combine(patchDir, "th11", "spells.js")));

            // 7) 熔断续译：桩全失败 → Incomplete 状态但安装完成；桩恢复后续译清除标记
            var dirBreaker = MakeFixture("th11-breaker");
            var gameB = new GameInfo { Name = "th11b", Path = dirBreaker, Engine = GameEngine.Touhou, AppId = 999012 };
            TouhouThcrapService.TranslatorOverride = (_, _, _, _) => Task.FromResult<string?>(null);
            await TouhouThcrapService.EnableAsync(gameB, settings, new Collect(new List<string>()));
            var statePathB = Path.Combine(dirBreaker, ".franca", "touhou-thcrap.json");
            Check("breaker_completes_incomplete", TouhouThcrapService.IsReady(dirBreaker)
                && File.ReadAllText(statePathB).Contains("\"Incomplete\": true"));
            TouhouThcrapService.TranslatorOverride = (text, _, _, _) =>
                Task.FromResult<string?>(string.Join("\n",
                    text.Replace("\r\n", "\n").Split('\n').Select(l => "【F】" + l)));
            var logsB = new List<string>();
            await TouhouThcrapService.EnableAsync(gameB, settings, new Collect(logsB));
            Check("resume_clears_incomplete", logsB.Any(l =>
                    l.Contains("continuing translation", StringComparison.OrdinalIgnoreCase))
                && !File.ReadAllText(statePathB).Contains("\"Incomplete\": true")
                && File.Exists(Path.Combine(workTree, "repos", "franca", "zh_hans", "th11", "st01_00a.msg.jdiff")));
        }
        finally
        {
            TouhouThcrapService.TranslatorOverride = null;
            TouhouThcrapService.SpellNamesForProbe = null;
            TouhouThcrapService.PacksRootForProbe = null;
            TouhouThcrapService.WorkTreeRootForProbe = null;
            TouhouThcrapService.BaseTsaSourceForProbe = null;
            try { Directory.Delete(root, true); } catch { }
            if (fixtureTsa is not null) { try { Directory.Delete(fixtureTsa, true); } catch { } }
        }

        Console.WriteLine($"RESULT\tTHCRAP\tfailures={failures}");
    }

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
    }

    // PNG 尺寸与透明度统计（菜单贴图断言用）；OnLoad 缓存避免占用文件句柄
    private static (int W, int H, int Transparent, int Opaque) PngStats(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int stride = conv.PixelWidth * 4;
        var px = new byte[conv.PixelHeight * stride];
        conv.CopyPixels(px, stride, 0);
        int transparent = 0, opaque = 0;
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] == 0) transparent++;
            else if (px[i] > 128) opaque++;
        return (conv.PixelWidth, conv.PixelHeight, transparent, opaque);
    }

    private static bool CellDiffers(string a, string b, int x, int y, int w, int h)
    {
        byte[] Load(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            var px = new byte[conv.PixelHeight * conv.PixelWidth * 4];
            conv.CopyPixels(px, conv.PixelWidth * 4, 0);
            return px;
        }
        var pa = Load(a);
        var pb = Load(b);
        const int stride = 512 * 4;
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                int o = yy * stride + xx * 4;
                if (pa[o] != pb[o] || pa[o + 1] != pb[o + 1] || pa[o + 2] != pb[o + 2] || pa[o + 3] != pb[o + 3])
                    return true;
            }
        return false;
    }
}
