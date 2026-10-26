using System.Diagnostics;
using System.IO;
using Franca.Models;

namespace Franca.Services;

public sealed class KnownIssueRepairService
{
    private static readonly TimeSpan GameCloseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SteamCloseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(120);

    private readonly Func<IReadOnlyList<GameInfo>> _gamesProvider;

    public KnownIssueRepairService(Func<IReadOnlyList<GameInfo>> gamesProvider)
    {
        _gamesProvider = gamesProvider;
    }

    public async Task<KnownIssueRepairOutcome> RepairAsync(
        KnownIssueDetection detection, CancellationToken ct = default)
    {
        if (detection.Issue.RepairKind != KnownIssueRepairKind.RestartSteamClean)
            return new KnownIssueRepairOutcome(false, "issue.status.failed");
        if (detection.Game.AppId is not { } appId)
            return new KnownIssueRepairOutcome(false, "issue.failure.no-appid");

        var steamExe = SteamScanner.FindSteamExecutable();
        if (steamExe is null)
            return new KnownIssueRepairOutcome(false, "issue.failure.steam");

        try
        {
            var otherGame = FindOtherRunningGame(_gamesProvider(), detection.Game);
            if (otherGame is not null)
            {
                AppLog.Write($"[known-issue] repair blocked by running game: {otherGame.Name}");
                return new KnownIssueRepairOutcome(false, "issue.failure.other-games");
            }

            if (!await CloseGameAsync(detection.ProcessId, ct))
                return new KnownIssueRepairOutcome(false, "issue.failure.close-game");

            if (!await ShutdownSteamAsync(steamExe, ct))
                return new KnownIssueRepairOutcome(false, "issue.failure.steam");

            using (StartSteam(steamExe, "-silent"))
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (Process.GetProcessesByName("steam").Length == 0)
                return new KnownIssueRepairOutcome(false, "issue.failure.steam");

            var launchedAfter = DateTimeOffset.Now;
            using (Process.Start(CreateSteamLaunchInfo(appId)))
            {
            }

            var healthy = await WaitForHealthyAsync(detection.Game.Path, launchedAfter, ct);
            return healthy
                ? new KnownIssueRepairOutcome(true, "issue.status.success")
                : new KnownIssueRepairOutcome(false, "issue.failure.verification");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[known-issue] repair failed for {detection.Game.Name}: {ex}");
            return new KnownIssueRepairOutcome(false, "issue.failure.launch");
        }
    }

    public static ProcessStartInfo CreateCleanSteamStartInfo(string steamExe, string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = steamExe,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(steamExe) ?? "",
            UseShellExecute = false,
        };
        foreach (var key in info.Environment.Keys
            .Where(key => key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase))
            .ToList())
            info.Environment.Remove(key);
        return info;
    }

    public static ProcessStartInfo CreateSteamLaunchInfo(int appId) => new()
    {
        FileName = $"steam://run/{appId}//-screen-fullscreen%200%20-screen-width%201280%20-screen-height%20720%20",
        UseShellExecute = true,
    };

    private static Process? StartSteam(string steamExe, string arguments) =>
        Process.Start(CreateCleanSteamStartInfo(steamExe, arguments));

    private static async Task<bool> CloseGameAsync(int processId, CancellationToken ct)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return true;
            if (!process.CloseMainWindow())
                return false;
            return await WaitForExitAsync(process, GameCloseTimeout, ct);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[known-issue] unable to close game pid={processId}: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> ShutdownSteamAsync(string steamExe, CancellationToken ct)
    {
        if (Process.GetProcessesByName("steam").Length == 0)
            return true;
        using (StartSteam(steamExe, "-shutdown"))
        {
        }
        var deadline = DateTimeOffset.Now + SteamCloseTimeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (Process.GetProcessesByName("steam").Length == 0)
                return true;
            await Task.Delay(500, ct);
        }
        return Process.GetProcessesByName("steam").Length == 0;
    }

    private static async Task<bool> WaitForHealthyAsync(string gameDir, DateTimeOffset launchedAfter, CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now + VerificationTimeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (GameRuntimeMonitor.HasHealthyBepInExLog(gameDir, launchedAfter))
                return true;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static GameInfo? FindOtherRunningGame(IEnumerable<GameInfo> games, GameInfo target)
    {
        var candidates = games.Select(game => new
            {
                Game = game,
                Paths = GetRootExecutables(game.Path),
            })
            .Where(candidate => candidate.Paths.Count > 0)
            .ToList();
        var names = candidates.SelectMany(candidate => candidate.Paths)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!names.Contains(process.ProcessName))
                    continue;
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }
                if (path is null)
                    continue;
                path = Path.GetFullPath(path);
                var match = candidates.FirstOrDefault(candidate => candidate.Paths.Contains(path));
                if (match is not null && !string.Equals(match.Game.Path, target.Path, StringComparison.OrdinalIgnoreCase))
                    return match.Game;
            }
        }
        return null;
    }

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
}
