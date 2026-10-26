using System.Diagnostics;
using System.IO;
using Franca.Models;

namespace Franca.Services;

public enum BepInExLogStatus
{
    Starting,
    Healthy,
    Stale,
    Unknown,
}

public sealed class GameRuntimeMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SlowStartupLimit = TimeSpan.FromSeconds(90);

    private readonly Func<KnownIssueDetection, Task> _onDetected;
    private readonly Func<GameInfo, Task>? _onGameStarted;
    private readonly object _sync = new();
    private List<MonitoredGame> _candidates = new();
    private HashSet<string> _processNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, TrackedGame> _tracked = new();
    private readonly HashSet<int> _finished = new();
    private CancellationTokenSource? _cts;

    public GameRuntimeMonitor(Func<KnownIssueDetection, Task> onDetected,
        Func<GameInfo, Task>? onGameStarted = null)
    {
        _onDetected = onDetected;
        _onGameStarted = onGameStarted;
    }

    public void UpdateGames(IEnumerable<GameInfo> games)
    {
        var candidates = games
            .Where(game => game.Engine.IsUnity() && game.IsActive && game.AppId is not null)
            .Select(game => new MonitoredGame(game, GetRootExecutables(game.Path)))
            .Where(candidate => candidate.ExecutablePaths.Count > 0)
            .ToList();
        var processNames = candidates.SelectMany(candidate => candidate.ExecutablePaths)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            _candidates = candidates;
            _processNames = processNames;
        }
        AppLog.Write($"[known-issue] monitor candidates={candidates.Count}");
    }

    public void Start()
    {
        if (_cts is not null)
            return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await ScanAsync(initial: true, ct);
            AppLog.Write("[known-issue] runtime monitor started");
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await ScanAsync(initial: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write($"[known-issue] runtime monitor stopped: {ex}");
        }
    }

    private async Task ScanAsync(bool initial, CancellationToken ct)
    {
        List<MonitoredGame> candidates;
        HashSet<string> processNames;
        lock (_sync)
        {
            candidates = _candidates.ToList();
            processNames = new HashSet<string>(_processNames, StringComparer.OrdinalIgnoreCase);
        }
        if (candidates.Count == 0)
            return;

        var activePids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                ct.ThrowIfCancellationRequested();
                if (!processNames.Contains(process.ProcessName))
                    continue;
                var path = TryGetProcessPath(process);
                if (path is null)
                    continue;
                var candidate = candidates.FirstOrDefault(item => item.ExecutablePaths.Contains(path));
                if (candidate is null)
                    continue;

                activePids.Add(process.Id);
                if (_finished.Contains(process.Id))
                    continue;
                if (initial)
                {
                    _finished.Add(process.Id);
                    continue;
                }
                if (!_tracked.TryGetValue(process.Id, out var tracked))
                {
                    var logPath = BepInExLogPath(candidate.Game.Path);
                    DateTimeOffset startedAt;
                    try { startedAt = process.StartTime; }
                    catch { startedAt = DateTimeOffset.Now; }
                    tracked = new TrackedGame(
                        candidate.Game,
                        startedAt,
                        File.Exists(logPath));
                    _tracked[process.Id] = tracked;
                    AppLog.Write($"[known-issue] tracking {candidate.Game.Name} pid={process.Id} hadLog={tracked.HadLog}");
                    if (_onGameStarted is not null)
                        _ = NotifyGameStartedAsync(candidate.Game);
                }

                var health = EvaluateBepInExLog(candidate.Game.Path, tracked.StartedAt, DateTimeOffset.Now);
                if (health == BepInExLogStatus.Starting || health == BepInExLogStatus.Unknown)
                    continue;

                _tracked.Remove(process.Id);
                _finished.Add(process.Id);
                var localWinhttp = HasLocalWinhttp(process, candidate.Game.Path);
                AppLog.Write($"[known-issue] health={health} {candidate.Game.Name} pid={process.Id} localWinhttp={localWinhttp}");
                if (health == BepInExLogStatus.Healthy)
                    continue;
                if (!tracked.HadLog || !localWinhttp)
                {
                    AppLog.Write($"[known-issue] inconclusive BepInEx evidence for {candidate.Game.Name} pid={process.Id}");
                    continue;
                }

                var issue = KnownIssueCatalog.Find(KnownIssueCatalog.DoorstopSteamEnvironmentId)!;
                await _onDetected(new KnownIssueDetection(issue, candidate.Game, process.Id));
            }
        }

        foreach (var pid in _tracked.Keys.Where(pid => !activePids.Contains(pid)).ToList())
            _tracked.Remove(pid);
        foreach (var pid in _finished.Where(pid => !activePids.Contains(pid)).ToList())
            _finished.Remove(pid);
    }

    private async Task NotifyGameStartedAsync(GameInfo game)
    {
        // 游戏进程首次被发现：游戏启动前的译文目录同步挂点（lab/010 Slice 2）
        try { await (_onGameStarted?.Invoke(game) ?? Task.CompletedTask); }
        catch (Exception ex) { AppLog.Write($"[known-issue] onGameStarted failed: {ex.Message}"); }
    }

    public static BepInExLogStatus EvaluateBepInExLog(
        string gameDir, DateTimeOffset startedAt, DateTimeOffset observedAt)
    {
        var logPath = BepInExLogPath(gameDir);
        var fresh = false;
        var complete = false;
        try
        {
            if (File.Exists(logPath))
            {
                fresh = File.GetLastWriteTimeUtc(logPath) >= startedAt.UtcDateTime.AddSeconds(-2);
                if (fresh)
                {
                    complete = ReadLogText(logPath).Contains(
                        "Chainloader startup complete", StringComparison.Ordinal);
                    if (complete)
                        return BepInExLogStatus.Healthy;
                }
            }
        }
        catch
        {
            return BepInExLogStatus.Unknown;
        }

        var elapsed = observedAt - startedAt;
        if (elapsed < StartupGrace)
            return BepInExLogStatus.Starting;
        if (fresh && !complete && elapsed < SlowStartupLimit)
            return BepInExLogStatus.Starting;
        return BepInExLogStatus.Stale;
    }

    public static bool HasHealthyBepInExLog(string gameDir, DateTimeOffset startedAfter)
    {
        try
        {
            var logPath = BepInExLogPath(gameDir);
            return File.Exists(logPath)
                && File.GetLastWriteTimeUtc(logPath) >= startedAfter.UtcDateTime.AddSeconds(-2)
                && ReadLogText(logPath).Contains("Chainloader startup complete", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static GameInfo? MatchGameProcess(IEnumerable<GameInfo> games, string processPath)
    {
        var fullPath = Path.GetFullPath(processPath);
        return games.FirstOrDefault(game => GetRootExecutables(game.Path).Contains(fullPath));
    }

    private static string ReadLogText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string BepInExLogPath(string gameDir) =>
        Path.Combine(gameDir, "BepInEx", "LogOutput.log");

    private static HashSet<string> GetRootExecutables(string gameDir)
    {
        try
        {
            return Directory.GetFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .Where(path => !Path.GetFileName(path).Contains("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return path is null ? null : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasLocalWinhttp(Process process, string gameDir)
    {
        try
        {
            var expected = Path.GetFullPath(Path.Combine(gameDir, "winhttp.dll"));
            return process.Modules.Cast<ProcessModule>().Any(module =>
                string.Equals(module.FileName, expected, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private sealed record MonitoredGame(GameInfo Game, HashSet<string> ExecutablePaths);

    private sealed record TrackedGame(GameInfo Game, DateTimeOffset StartedAt, bool HadLog);
}
