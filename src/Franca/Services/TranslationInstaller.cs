using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Franca.Models;

namespace Franca.Services;

public static class TranslationInstaller
{
    private static readonly string CacheDir = Path.Combine(AppContext.BaseDirectory, "tools", "cache");

    private const string BepInExUnity6Url =
        "https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip";
    private const string BepInExIL2CppLegacyUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip";
    private const string BepInExMonoUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";
    private const string XUnityIL2CppUrl =
        "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/v5.6.2/XUnity.AutoTranslator-BepInEx-IL2CPP-5.6.2.zip";
    private const string XUnityMonoUrl =
        "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/v5.6.2/XUnity.AutoTranslator-BepInEx-5.6.2.zip";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Franca/0.1");
        return client;
    }

    // 已手工装好运行环境的游戏（BepInEx/、doorstop_config.ini，IL2CPP 还需 dotnet/）绝不能重复安装或覆盖
    // winhttp.dll 或 winhttp.dll.frdisabled（用户停用态）任一存在即视为已装环境；
    // 东方自研引擎：新安装走 thcrap 运行时补丁；既有静态安装仍按静态状态判定（迁移兼容）
    public static bool IsReady(string gameDir)
    {
        if (EngineDetector.Detect(gameDir) == GameEngine.Touhou)
            return (TouhouThcrapService.IsReady(gameDir) || TouhouStaticService.IsReady(gameDir))
                && !InstallationTransaction.HasPending(gameDir);
        return HasRuntimeFiles(gameDir) && !InstallationTransaction.HasPending(gameDir);
    }

    private static bool HasRuntimeFiles(string gameDir)
    {
        if (!Directory.Exists(Path.Combine(gameDir, "BepInEx")))
            return false;
        if (!File.Exists(Path.Combine(gameDir, "winhttp.dll"))
            && !File.Exists(Path.Combine(gameDir, "winhttp.dll.frdisabled")))
            return false;
        if (!File.Exists(Path.Combine(gameDir, "doorstop_config.ini")))
            return false;
        if (EngineDetector.Detect(gameDir) == GameEngine.UnityIL2CPP
            && !Directory.Exists(Path.Combine(gameDir, "dotnet")))
            return false;
        return true;
    }

    // 可逆停用/重新启用：重命名 winhttp.dll ↔ winhttp.dll.frdisabled（Unity 注入，幂等）；
    // 东方自研引擎按实际安装分流：thcrap 产物在则存根/原版 exe 互换（TouhouThcrapService），
    // 否则兼容旧静态安装的原包/补丁包互换（TouhouStaticService）
    public static void SetDisabled(string gameDir, bool disabled)
    {
        if (InstallationTransaction.HasPending(gameDir))
            throw new InvalidOperationException("请先恢复未完成的安装事务，再启用或停用翻译。");
        if (EngineDetector.Detect(gameDir) == GameEngine.Touhou)
        {
            if (TouhouThcrapService.HasArtifacts(gameDir))
                TouhouThcrapService.SetDisabled(gameDir, disabled);
            else
                TouhouStaticService.SetDisabled(gameDir, disabled);
            return;
        }
        var active = Path.Combine(gameDir, "winhttp.dll");
        var inactive = active + ".frdisabled";
        if (disabled && File.Exists(active))
            File.Move(active, inactive);
        else if (!disabled && File.Exists(inactive))
            File.Move(inactive, active);
    }

    public static bool RecoverInterruptedInstallation(string gameDir, Action<string> log)
    {
        if (!InstallationTransaction.HasPending(gameDir)) return false;
        using var installationLock = InstallationTransaction.AcquireLock(gameDir);
        InstallationTransaction.Recover(gameDir, log);
        return true;
    }

    // 是否存在 Franca 安装清单（Unity 卸载按钮可见性）
    public static bool HasInstalledArtifacts(string gameDir)
    {
        try
        {
            var records = Path.Combine(gameDir, ".franca", "installations");
            return Directory.Exists(records)
                && Directory.GetDirectories(records).Any(d => File.Exists(Path.Combine(d, "manifest.json")));
        }
        catch { return false; }
    }

    // 卸载（Unity 注入路线）：按 .franca/installations/<id>/manifest.json 的逐文件清单逆操作——
    // 安装时新建的文件（OriginalHash 为空）核对 InstalledHash 后删除；被覆盖的原文件
    // （OriginalHash 非空）从备份恢复。清单外文件一律不动（用户自己的 BepInEx 模组
    // 不会被误删）；与磁盘不符的条目保守跳过并报告，绝不强制覆盖外部改动。
    // 清单缺失/不可读时拒绝卸载——此时无法安全区分 Franca 产物与用户文件。
    // 全部清单逆操作成功且无冲突后，清除 .franca 安装记录与安装锁。
    public static bool Uninstall(string gameDir, Action<string> log, CancellationToken ct = default)
    {
        if (EngineDetector.Detect(gameDir) == GameEngine.Touhou)
            throw new InvalidOperationException("东方游戏请使用 TouhouThcrapService.UninstallAsync。");
        var recordsRoot = Path.Combine(gameDir, ".franca", "installations");
        if (!Directory.Exists(recordsRoot) || !HasInstalledArtifacts(gameDir))
        {
            log(UiStrings.Get("uninstall.no.manifest"));
            return false;
        }

        var failures = 0;
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestPath in Directory.GetDirectories(recordsRoot)
                     .Select(d => Path.Combine(d, "manifest.json")).Where(File.Exists)
                     .OrderByDescending(File.GetCreationTimeUtc))
        {
            InstallationTransaction.InstallationJournal journal;
            try
            {
                journal = JsonSerializer.Deserialize<InstallationTransaction.InstallationJournal>(
                    File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("空清单。");
            }
            catch (Exception ex) { failures++; log(UiStrings.Format("uninstall.failed", ex.Message)); continue; }
            if (journal.Status is not ("committed" or "rolled-back"))
            {
                failures++;
                log(UiStrings.Format("uninstall.failed", $"{manifestPath} 状态为 {journal.Status}，请先恢复未完成事务。"));
                continue;
            }
            var recordDir = Path.GetDirectoryName(manifestPath)!;
            foreach (var entry in journal.Files.AsEnumerable().Reverse())
            {
                ct.ThrowIfCancellationRequested();
                if (!handled.Add(entry.Path)) continue;
                try
                {
                    var target = InstallationTransaction.SafePath(gameDir, entry.Path);
                    var current = File.Exists(target) ? InstallationTransaction.Hash(target) : null;
                    if (entry.OriginalHash is null)
                    {
                        // Franca 新建的文件：未被外部改动才删除
                        if (current is null) continue;
                        if (current == entry.InstalledHash)
                        {
                            File.Delete(target);
                            log(UiStrings.Format("uninstall.restored", entry.Path));
                        }
                        else { failures++; log(UiStrings.Format("uninstall.conflict", entry.Path)); }
                    }
                    else
                    {
                        // 安装前已存在的文件：从备份恢复（已被外部改动的保守跳过）
                        if (current == entry.OriginalHash) continue;
                        if (current != entry.InstalledHash) { failures++; log(UiStrings.Format("uninstall.conflict", entry.Path)); continue; }
                        var backup = InstallationTransaction.SafePath(Path.Combine(recordDir, "backup"), entry.Path);
                        if (!File.Exists(backup) || InstallationTransaction.Hash(backup) != entry.OriginalHash)
                        { failures++; log(UiStrings.Format("uninstall.failed", $"{entry.Path} 备份缺失或不符")); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(backup, target, overwrite: true);
                        log(UiStrings.Format("uninstall.restored", entry.Path));
                    }
                }
                catch (Exception ex) { failures++; log(UiStrings.Format("uninstall.failed", $"{entry.Path}：{ex.Message}")); }
            }
            foreach (var relative in journal.Directories.OrderByDescending(p => p.Length))
            {
                try
                {
                    var dir = InstallationTransaction.SafePath(gameDir, relative);
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { /* 目录清理失败不阻塞 */ }
            }
        }

        if (failures > 0)
        {
            log(UiStrings.Get("uninstall.partial"));
            return false;
        }
        try
        {
            Directory.Delete(recordsRoot, recursive: true);
            var francaDir = Path.Combine(gameDir, ".franca");
            var lockPath = Path.Combine(francaDir, "install.lock");
            if (File.Exists(lockPath)) File.Delete(lockPath);
            if (Directory.Exists(francaDir) && !Directory.EnumerateFileSystemEntries(francaDir).Any())
                Directory.Delete(francaDir);
            log(UiStrings.Get("uninstall.cleaned"));
        }
        catch (Exception ex) { log(UiStrings.Format("uninstall.failed", ex.Message)); return false; }
        return true;
    }

    public static async Task EnableAsync(GameInfo game, AppSettings settings, IProgress<string> progress,
        CancellationToken ct = default, string? relayUrl = null,
        IProgress<TouhouStaticService.TouhouInstallProgress>? installProgress = null)
    {
        void Log(string message) => progress.Report(message);

        if (game.Engine == GameEngine.Touhou)
        {
            await TouhouThcrapService.EnableAsync(game, settings, progress, ct, relayUrl, installProgress);
            return;
        }
        if (!game.Engine.IsUnity())
        {
            Log(UiStrings.Format("inst.unsupported", game.Name, game.EngineLabel));
            return;
        }

        using var installationLock = InstallationTransaction.AcquireLock(game.Path);
        InstallationTransaction.Recover(game.Path, Log);

        if (IsReady(game.Path))
        {
            Log(UiStrings.Format("inst.already", game.Name));
            var health = TranslationHealthService.Inspect(game.Path, settings,
                relayUrl);
            if (!health.IsHealthy)
                Log($"检测到翻译配置异常：{health.Summary}。为保护现有设置，未自动覆盖；请先备份后修复。");
            return;
        }

        if (EndpointCatalog.Find(settings.Endpoint)?.Category == EndpointCategory.Offline)
            Log(UiStrings.Get("inst.offlinewarn"));

        // 1. 按 Unity 版本选择 BepInEx 发行包
        var unityVersion = ReadUnityVersion(game.Path);
        var bepInExUrl = PickBepInExUrl(game.Engine, unityVersion, Log);

        // Download and stage both packages before touching the game. Existing config stays intact.
        var bepInExZip = await DownloadAsync(bepInExUrl, Log, ct);
        var xUnityUrl = game.Engine == GameEngine.UnityIL2CPP ? XUnityIL2CppUrl : XUnityMonoUrl;
        var xUnityZip = await DownloadAsync(xUnityUrl, Log, ct);
        var stage = Path.Combine(Path.GetTempPath(), "franca-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            InstallationTransaction.Extract(bepInExZip, stage, ct);
            InstallationTransaction.Extract(xUnityZip, stage, ct);
            var existingConfig = TranslationHealthService.ConfigPath(game.Path);
            if (File.Exists(existingConfig))
            {
                var stagedConfig = TranslationHealthService.ConfigPath(stage);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedConfig)!);
                File.Copy(existingConfig, stagedConfig, true);
            }
            WriteAutoTranslatorConfig(stage, settings, Log, relayUrl: relayUrl);
            if (!File.Exists(Path.Combine(stage, "BepInEx", "core", "XUnity.Common.dll")))
                throw new InvalidDataException("运行时包缺少 XUnity.Common.dll，未写入游戏。");
            InstallationTransaction.Install(game.Path, stage, () =>
            {
                if (!HasRuntimeFiles(game.Path)) throw new IOException("安装运行时检查失败。");
            }, Log, ct);
            Log(UiStrings.Format("inst.done", game.Name));
        }
        finally
        {
            // Only remove the uniquely created staging directory, never game files or backups.
            try { Directory.Delete(stage, true); } catch { }
        }
    }

    private static int? ReadUnityVersion(string gameDir)
    {
        try
        {
            var playerPath = Path.Combine(gameDir, "UnityPlayer.dll");
            if (!File.Exists(playerPath))
                return null;
            var version = FileVersionInfo.GetVersionInfo(playerPath).ProductVersion ?? "";
            var dash = version.IndexOfAny(new[] { '.', 'f', 'b', 'a', 'p' });
            var majorText = dash > 0 ? version[..dash] : version;
            return int.TryParse(majorText, out var major) ? major : null;
        }
        catch
        {
            return null;
        }
    }

    private static string PickBepInExUrl(GameEngine engine, int? unityMajor, Action<string> log)
    {
        if (engine == GameEngine.UnityMono)
        {
            log(UiStrings.Get("inst.mono"));
            return BepInExMonoUrl;
        }

        if (unityMajor >= 6000)
        {
            log(UiStrings.Format("inst.unity6", unityMajor));
            return BepInExUnity6Url;
        }

        log(unityMajor is null
            ? UiStrings.Get("inst.nover")
            : UiStrings.Format("inst.il2cpp", unityMajor));
        return BepInExIL2CppLegacyUrl;
    }

    private static async Task<string> DownloadAsync(string url, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDir);
        var fileName = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).LocalPath));
        var cachePath = Path.Combine(CacheDir, fileName);
        if (File.Exists(cachePath) && IsValidArchive(cachePath))
        {
            log(UiStrings.Format("inst.cache", fileName));
            return cachePath;
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                log(UiStrings.Format("inst.download", fileName, attempt));
                var tempPath = cachePath + ".part";
                await using (var source = await Http.GetStreamAsync(url, ct))
                await using (var target = File.Create(tempPath))
                    await source.CopyToAsync(target, ct);
                if (!IsValidArchive(tempPath)) throw new InvalidDataException("下载的 ZIP 不完整或损坏。");
                File.Move(tempPath, cachePath, overwrite: true);
                log(UiStrings.Get("inst.dl.done"));
                return cachePath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                log(UiStrings.Format("inst.dl.retry", ex.Message));
                await Task.Delay(2000, ct);
            }
        }

        throw new InvalidOperationException($"下载 {fileName} 失败：{lastError?.Message}");
    }

    private static bool IsValidArchive(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count == 0) return false;
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                stream.CopyTo(Stream.Null);
            }
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }

    // 手动修复或语言切换倒计时完成后覆盖已有 ini；覆盖前备份到游戏目录内的 Franca 专用目录。
    public static string RepairConfiguration(string gameDir, AppSettings settings, string? relayUrl, Action<string> log)
    {
        var configPath = TranslationHealthService.ConfigPath(gameDir);
        if (File.Exists(configPath))
        {
            var backupDir = Path.Combine(gameDir, "BepInEx", "franca-backups");
            Directory.CreateDirectory(backupDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupPath = Path.Combine(backupDir, $"AutoTranslatorConfig.{stamp}.ini");
            for (var suffix = 2; File.Exists(backupPath); suffix++)
                backupPath = Path.Combine(backupDir, $"AutoTranslatorConfig.{stamp}.{suffix}.ini");
            File.Copy(configPath, backupPath, overwrite: false);
            log($"已备份原配置：{backupPath}");
        }

        WriteAutoTranslatorConfig(gameDir, settings, log, overwriteExisting: true, relayUrl);
        return configPath;
    }

    private static void WriteAutoTranslatorConfig(string gameDir, AppSettings settings, Action<string> log,
        bool overwriteExisting = false, string? relayUrl = null)
    {
        var configDir = Path.Combine(gameDir, "BepInEx", "config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "AutoTranslatorConfig.ini");
        if (File.Exists(configPath) && !overwriteExisting)
        {
            log(UiStrings.Get("inst.ini.keep"));
            return;
        }

        var xUnityLanguage = AppSettings.FindLanguage(settings.TargetLanguage).EffectiveXUnityCode;
        // 只写 XUnity 认识的端点名；仅测速池端点（XUnityId 为 null）回落到必应
        // 中继运行时（lab/006）：Endpoint=CustomTranslate，Url 指向本地中继，池逻辑收归中继内部
        // XUnity 5.6.2 源码（CustomTranslateEndpoint.cs）：[Custom] Url 是裸基址，
        // 框架自行追加 "?from={from}&to={to}&text={urlencoded}"，无占位符
        var customPrimary = settings.CustomEndpoints.FirstOrDefault(d => d.Id == settings.Endpoint);
        var useRelay = !string.IsNullOrWhiteSpace(relayUrl);
        var effectiveRelayUrl = relayUrl;
        var endpoint = useRelay || customPrimary?.Type == "get"
            ? "CustomTranslate"
            : EndpointCatalog.Find(settings.Endpoint)?.XUnityId ?? "BingTranslate";
        var fallbackEndpoint = string.IsNullOrWhiteSpace(settings.FallbackEndpoint)
            ? "GoogleTranslateV2"
            : EndpointCatalog.Find(settings.FallbackEndpoint)?.XUnityId ?? "BingTranslate";
        var content = (overwriteExisting && File.Exists(configPath) ? File.ReadAllText(configPath) : ConfigTemplate)
            .Replace("{Endpoint}", endpoint)
            .Replace("{FallbackEndpoint}", fallbackEndpoint)
            .Replace("{Language}", xUnityLanguage)
            .Replace("{FromLanguage}", settings.SourceLanguage);
        content = PatchIniValue(content, "General", "Language", xUnityLanguage);
        content = PatchIniValue(content, "General", "FromLanguage", settings.SourceLanguage);
        content = PatchIniValue(content, "Service", "Endpoint", endpoint);
        content = PatchIniValue(content, "Service", "FallbackEndpoint", fallbackEndpoint);
        if (useRelay)
        {
            content = PatchIniValue(content, "Behaviour", "MaxCharactersPerTranslation",
                TranslationTextSegmenter.MaxTextLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            content = PatchIniValue(content, "Behaviour", "IgnoreWhitespaceInDialogue", "False");
            // 运行时重载升级链路（lab/010 Slice 2）：后台在线结果写 Franca.txt 后 XUnity 原位升级 UI
            content = PatchIniValue(content, "Behaviour", "ReloadTranslationsOnFileChange", "True");
            content = PatchIniValue(content, "Custom", "Url", effectiveRelayUrl!);
            content = PatchIniValue(content, "Custom", "EnableShortDelay", "True");
            content = PatchIniValue(content, "Custom", "DisableSpamChecks", "True");
        }
        else if (customPrimary?.Type == "get")
        {
            content = PatchIniValue(content, "Custom", "Url", customPrimary.BaseUrl);
        }

        // 已填写的端点 Key 写入对应配置节（LLM 端点不写 ini：IniSection/IniKey 为空）
        foreach (var def in EndpointCatalog.All)
        {
            if (def.Category == EndpointCategory.Llm
                || def.KeyFields.Length == 0
                || !settings.EndpointKeys.TryGetValue(def.Id, out var keyValues))
                continue;
            foreach (var field in def.KeyFields)
            {
                if (keyValues.TryGetValue(field.FieldId, out var value) && !string.IsNullOrWhiteSpace(value))
                    content = PatchIniValue(content, field.IniSection, field.IniKey, value);
            }
        }

        var temporaryPath = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        var languageNote = xUnityLanguage == settings.TargetLanguage
            ? xUnityLanguage
            : UiStrings.Format("inst.langmapped", xUnityLanguage, settings.TargetLanguage);
        log(UiStrings.Format("inst.wrote", languageNote, endpoint, fallbackEndpoint));
    }

    // 在 ini 文本中定位 [section] 内的 key= 行并替换；缺 key 插入节首，缺节追加到末尾
    private static string PatchIniValue(string content, string section, string key, string value)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var sectionIndex = lines.FindIndex(l => string.Equals(l.Trim(), $"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return string.Join('\n', lines);
        }

        var sectionEnd = lines.FindIndex(sectionIndex + 1, l => l.TrimStart().StartsWith('['));
        if (sectionEnd < 0)
            sectionEnd = lines.Count;
        var keyIndex = lines.FindIndex(sectionIndex + 1, sectionEnd - sectionIndex - 1,
            l => l.Contains('=') && string.Equals(l[..l.IndexOf('=')].Trim(), key, StringComparison.OrdinalIgnoreCase));
        if (keyIndex >= 0)
            lines[keyIndex] = $"{key}={value}";
        else
            lines.Insert(sectionIndex + 1, $"{key}={value}");
        return string.Join('\n', lines);
    }

    // 参照 XUnity.AutoTranslator 5.6.2 默认模板（对照本机 Suzerain 实际生成的配置）
    private const string ConfigTemplate = """
        [Service]
        Endpoint={Endpoint}
        FallbackEndpoint={FallbackEndpoint}

        [General]
        Language={Language}
        FromLanguage={FromLanguage}

        [Files]
        Directory=Translation\{Lang}\Text
        OutputFile=Translation\{Lang}\Text\_AutoGeneratedTranslations.txt
        SubstitutionFile=Translation\{Lang}\Text\_Substitutions.txt
        PreprocessorsFile=Translation\{Lang}\Text\_Preprocessors.txt
        PostprocessorsFile=Translation\{Lang}\Text\_Postprocessors.txt

        [TextFrameworks]
        EnableIMGUI=False
        EnableUGUI=True
        EnableUIElements=True
        EnableNGUI=True
        EnableTextMeshPro=True
        EnableTextMesh=False
        EnableFairyGUI=True

        [Behaviour]
        MaxCharactersPerTranslation=200
        IgnoreWhitespaceInDialogue=True
        MinDialogueChars=20
        ForceSplitTextAfterCharacters=0
        CopyToClipboard=False
        MaxClipboardCopyCharacters=2500
        ClipboardDebounceTime=1.25
        EnableUIResizing=True
        EnableBatching=True
        UseStaticTranslations=True
        OverrideFont=
        OverrideFontSize=
        OverrideFontTextMeshPro=
        FallbackFontTextMeshPro=
        ResizeUILineSpacingScale=
        ForceUIResizing=False
        IgnoreTextStartingWith=\u180e;
        TextGetterCompatibilityMode=False
        GameLogTextPaths=
        RomajiPostProcessing=ReplaceMacronWithCircumflex;RemoveApostrophes;ReplaceHtmlEntities
        TranslationPostProcessing=ReplaceMacronWithCircumflex;ReplaceHtmlEntities
        RegexPostProcessing=
        CacheRegexPatternResults=False
        PersistRichTextMode=Final
        CacheRegexLookups=False
        CacheWhitespaceDifferences=False
        GenerateStaticSubstitutionTranslations=False
        GeneratePartialTranslations=False
        EnableTranslationScoping=True
        EnableSilentMode=True
        BlacklistedIMGUIPlugins=
        EnableTextPathLogging=False
        OutputUntranslatableText=False
        IgnoreVirtualTextSetterCallingRules=False
        MaxTextParserRecursion=1
        HtmlEntityPreprocessing=True
        HandleRichText=True
        EnableTranslationHelper=False
        ForceMonoModHooks=False
        InitializeHarmonyDetourBridge=False
        RedirectedResourceDetectionStrategy=AppendMongolianVowelSeparatorAndRemoveAll
        OutputTooLongText=False
        TemplateAllNumberAway=False
        ReloadTranslationsOnFileChange=True
        DisableTextMeshProScrollInEffects=False
        CacheParsedTranslations=False

        [Texture]
        TextureDirectory=Translation\{Lang}\Texture
        EnableTextureTranslation=False
        EnableTextureDumping=False
        EnableTextureToggling=False
        EnableTextureScanOnSceneLoad=False
        EnableSpriteRendererHooking=False
        LoadUnmodifiedTextures=False
        DetectDuplicateTextureNames=False
        DuplicateTextureNames=
        EnableLegacyTextureLoading=False
        TextureHashGenerationStrategy=FromImageName
        CacheTexturesInMemory=True
        EnableSpriteHooking=False

        [ResourceRedirector]
        PreferredStoragePath=Translation\{Lang}\RedirectedResources
        EnableTextAssetRedirector=False
        LogAllLoadedResources=False
        EnableDumping=False
        CacheMetadataForAllFiles=True

        [Http]
        UserAgent=
        DisableCertificateValidation=True

        [TranslationAggregator]
        Width=400
        Height=100
        EnabledTranslators=

        [Debug]
        EnableConsole=False

        [Migrations]
        Enable=True
        Tag=5.6.2

        [Baidu]
        BaiduAppId=
        BaiduAppSecret=
        DelaySeconds=1

        [BingLegitimate]
        OcpApimSubscriptionKey=

        [Custom]
        Url=
        EnableShortDelay=False
        DisableSpamChecks=False

        [DeepL]
        ExecutableLocation=
        MinDelaySeconds=2
        MaxDelaySeconds=6

        [DeepLLegitimate]
        ExecutableLocation=
        ApiKey=
        Free=False

        [ezTrans]
        InstallationPath=

        [Google]
        ServiceUrl=

        [GoogleV2]
        ServiceUrl=
        RPCID=MkEWBc
        VERSION=boq_translate-webserver_20210323.10_p0
        UseSimplest=False

        [GoogleLegitimate]
        GoogleAPIKey=

        [LecPowerTranslator15]
        InstallationPath=

        [LingoCloud]
        LingoCloudToken=

        [Watson]
        Url=
        Key=

        [Yandex]
        YandexAPIKey=
        """;
}
