using Franca.Models;
using System.Diagnostics;
using System.IO;

namespace Franca.Services;

// Application workflows own sequencing; pages only collect input and present results.
public sealed class GameTranslationService
{
    private readonly RelayServer _relay;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _operations = new(1, 1);

    // 译文目录同步（lab/010 Slice 2）：订阅中继后台升级通知，合并写入当前激活游戏的 Franca.txt
    public GameTextSyncService TextSync { get; }

    public GameTranslationService(RelayServer relay, AppSettings settings, GameTextSyncService? textSync = null)
    {
        _relay = relay;
        _settings = settings;
        TextSync = textSync ?? new GameTextSyncService();
        _relay.UpgradeCompleted += (appId, src, tgt) => TextSync.NotifyUpgrade(appId, src, tgt);
    }

    // 游戏启动前：激活译文目录同步，并把该游戏包条目全量写成 Franca.txt。
    // 多游戏按实例隔离：只有最近激活的 appId 接收升级通知。
    public async Task PrepareGameTextAsync(GameInfo game, CancellationToken ct = default)
    {
        if (game.AppId is not > 0) return;
        var language = AppSettings.FindLanguage(_settings.TargetLanguage);
        TextSync.Activate(game.AppId.Value, game.Path, language.EffectiveXUnityCode);
        try
        {
            await TextSync.PrepareForGameAsync(_relay.Packs, game.AppId.Value,
                _settings.TargetLanguage, game.Path, language.EffectiveXUnityCode, ct);
        }
        catch (Exception ex) { AppLog.Write($"Franca.txt prepare failed: {ex.Message}"); }
    }

    public TranslationHealthService.Report Inspect(GameInfo game) =>
        TranslationHealthService.Inspect(game.Path, _settings.Snapshot(),
            _relay.IsRunning ? _relay.UrlForGame(game.AppId) : null);

    // 经 Steam 协议启动游戏（Steam 未运行时会先唤起）；仅支持有 appId 的 Steam 游戏。
    // 启动失败（无协议关联等）记日志不抛，启动结果由 GameRuntimeMonitor 的进程检测闭环。
    public bool LaunchGame(GameInfo game)
    {
        if (!game.CanLaunch || game.AppId is not int appId) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                SteamLaunchUrl(appId)) { UseShellExecute = true });
            AppLog.Write(UiStrings.Format("log.launch", game.Name));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Steam launch failed: {ex.Message}");
            return false;
        }
    }

    public static string SteamLaunchUrl(int appId) => $"steam://rungameid/{appId}";

    public async Task<(string ConfigPath, TranslationHealthService.Report Report)> RepairAsync(
        GameInfo game, CancellationToken ct = default)
    {
        if (game.Engine == GameEngine.Touhou)
        {
            // thcrap 路线没有 ini 可修：「修复」= 恢复 exe 交换并用当前目标语言重建补丁（翻译缓存复用）
            await _operations.WaitAsync(ct);
            try
            {
                var snapshot = _settings.Snapshot();
                await Task.Run(() => TouhouThcrapService.EnableAsync(game, snapshot,
                    new Progress<string>(AppLog.Write), ct), ct);
                return ("touhou-thcrap", new TranslationHealthService.Report([]));
            }
            finally { _operations.Release(); }
        }
        await _operations.WaitAsync(ct);
        try
        {
            var settings = _settings.Snapshot();
            var url = await EnsureRelayAsync(game, ct);
            var path = await Task.Run(() => TranslationHealthService.Repair(game.Path, settings, url, AppLog.Write), ct);
            await PrepareGameTextAsync(game, ct);
            return (path, TranslationHealthService.Inspect(game.Path, settings, url));
        }
        finally { _operations.Release(); }
    }

    public sealed record LanguageApplyResult(int Updated, int Unchanged, int Skipped, int Failed);

    // Only existing, stopped Unity installations are eligible. Never installs or enables a game.
    public async Task<LanguageApplyResult> ApplyLanguagesAsync(IReadOnlyList<GameInfo> games,
        AppSettings settings, CancellationToken ct = default) =>
        await ApplyLanguagesAsync(games, settings, IsGameStopped, ct);

    internal async Task<LanguageApplyResult> ApplyLanguagesAsync(IReadOnlyList<GameInfo> games,
        AppSettings settings, Func<GameInfo, bool> isStopped, CancellationToken ct = default)
    {
        settings = settings.Snapshot();
        await _operations.WaitAsync(ct);
        int updated = 0, unchanged = 0, skipped = 0, failed = 0;
        try
        {
            foreach (var game in games.DistinctBy(g => Path.GetFullPath(g.Path), StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (!game.Engine.IsInstallable() || !TranslationInstaller.IsReady(game.Path)) continue;
                try
                {
                    if (game.Engine == GameEngine.Touhou)
                    {
                        // thcrap 补丁的语言应用 = 用新目标语言重建补丁（缓存复用），无 ini/中继概念
                        var applied = await Task.Run(async () =>
                        {
                            if (!isStopped(game)) return false;
                            using var installationLock = InstallationTransaction.AcquireLock(game.Path);
                            await TouhouThcrapService.EnableAsync(game, settings,
                                new Progress<string>(AppLog.Write), ct);
                            return true;
                        }, ct);
                        if (applied) updated++;
                        else skipped++;
                        continue;
                    }
                    // File inspection and writes stay off the UI thread; checks repeat immediately before writes.
                    var outcome = await Task.Run(async () =>
                    {
                        if (!isStopped(game)) return 2;
                        var url = await EnsureRelayAsync(game, ct);
                        if (url is null) throw new IOException("Local relay is unavailable.");
                        if (TranslationHealthService.Inspect(game.Path, settings, url).IsHealthy) return 1;
                        ct.ThrowIfCancellationRequested();
                        if (!isStopped(game)) return 2;
                        using var installationLock = InstallationTransaction.AcquireLock(game.Path);
                        if (!TranslationInstaller.IsReady(game.Path))
                            throw new IOException("Installation changed while waiting to apply languages.");
                        TranslationHealthService.Repair(game.Path, settings, url, AppLog.Write);
                        if (!TranslationHealthService.Inspect(game.Path, settings, url).IsHealthy)
                            throw new IOException("Configuration remains unhealthy after repair.");
                        // Do not activate every game during a batch. Its next launch activates text synchronization.
                        if (game.AppId is > 0)
                        {
                            TextSync.Deactivate(game.AppId.Value);
                            var language = AppSettings.FindLanguage(settings.TargetLanguage);
                            await TextSync.PrepareForGameAsync(_relay.Packs, game.AppId.Value,
                                settings.TargetLanguage, game.Path, language.EffectiveXUnityCode, ct);
                        }
                        return 0;
                    }, ct);
                    if (outcome == 0) updated++;
                    else if (outcome == 1) unchanged++;
                    else
                    {
                        skipped++;
                        AppLog.Write($"Language configuration skipped (running or process status unavailable): {game.Name}");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Write($"Language configuration failed for {game.Name}: {ex.Message}");
                }
            }
            return new(updated, unchanged, skipped, failed);
        }
        finally { _operations.Release(); }
    }

    internal static bool IsGameStopped(GameInfo game)
    {
        var root = Path.GetFullPath(game.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // Include executable names below the root (some Unity games launch from a subdirectory).
        var names = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                    if (path is null && names.Contains(process.ProcessName)) return false;
                }
                catch
                {
                    try { if (!process.HasExited && names.Contains(process.ProcessName)) return false; }
                    catch { /* The process exited while being inspected. */ }
                }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return true;
    }

    public async Task ToggleAsync(GameInfo game, IProgress<string> progress, CancellationToken ct = default)
    {
        await _operations.WaitAsync(ct);
        try
        {
            var disable = game.IsActive;
            if (TranslationInstaller.RecoverInterruptedInstallation(game.Path, progress.Report))
                game.IsReady = TranslationInstaller.IsReady(game.Path);
            if (disable)
            {
                TranslationInstaller.SetDisabled(game.Path, true);
                if (game.AppId is > 0) TextSync.Deactivate(game.AppId.Value);
                // Unity 停用是 winhttp.dll 重命名；thcrap 路线没有该文件，用引擎中立文案
                progress.Report(game.Engine == GameEngine.Touhou
                    ? UiStrings.Format("log.deactivated.static", game.Name)
                    : UiStrings.Format("log.deactivated", game.Name));
            }
            else if (game.IsDisabledByUser)
            {
                // Retain the reversible rename behavior; refreshing a pre-existing INI is explicit repair.
                TranslationInstaller.SetDisabled(game.Path, false);
                progress.Report(UiStrings.Format("log.reenabled", game.Name));
            }
            else
            {
                progress.Report(UiStrings.Format("log.enable.start", game.Name));
                var settings = _settings.Snapshot();
                // thcrap 路线不经中继翻译（内置批量端点直调），无需启动本地中继
                var url = game.Engine == GameEngine.Touhou
                    ? null
                    : await EnsureRelayAsync(game, ct);
                // 静态安装行内进度（百分比 + 字节文本）桥接到 GameInfo，完成后清除
                var installProgress = new Progress<TouhouStaticService.TouhouInstallProgress>(p =>
                    game.SetInstallProgress(p.Percent, p.ByteText));
                try
                {
                    await Task.Run(() => TranslationInstaller.EnableAsync(game, settings, progress, ct, url,
                        installProgress), ct);
                }
                finally
                {
                    game.SetInstallProgress(null, "");
                }
                if (game.Engine.IsUnity())
                    await PrepareGameTextAsync(game, ct);
            }
        }
        finally { _operations.Release(); }
    }

    // 卸载：整体还原到未安装基线（Unity 按安装清单逆操作 / Touhou 从 .frorig 恢复并清理产物）。
    // 不识别问题，与停用（可逆互换）和修复（针对性恢复）互补；翻译缓存保留，可快速重新启用。
    public async Task UninstallAsync(GameInfo game, IProgress<string> progress, CancellationToken ct = default)
    {
        await _operations.WaitAsync(ct);
        try
        {
            var installProgress = new Progress<TouhouStaticService.TouhouInstallProgress>(p =>
                game.SetInstallProgress(p.Percent, p.ByteText));
            game.SetInstallProgress(0, UiStrings.Get("uninstall.running"));
            try
            {
                if (game.Engine == GameEngine.Touhou)
                    await TouhouThcrapService.UninstallAsync(game.Path, progress.Report, installProgress, ct);
                else
                    await Task.Run(() => TranslationInstaller.Uninstall(game.Path, progress.Report, ct), ct);
            }
            finally
            {
                game.SetInstallProgress(null, "");
            }
            if (game.AppId is > 0) TextSync.Deactivate(game.AppId.Value);
            progress.Report(UiStrings.Format("uninstall.done", game.Name));
        }
        finally { _operations.Release(); }
    }

    private async Task<string?> EnsureRelayAsync(GameInfo game, CancellationToken ct)
    {
        try
        {
            await _relay.EnsureStartedAsync(ct);
            return _relay.UrlForGame(game.AppId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Write(UiStrings.Format("log.relay.fail", ex.Message));
            return null;
        }
    }
}
