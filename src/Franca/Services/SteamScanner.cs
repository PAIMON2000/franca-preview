using System.IO;
using System.Text.RegularExpressions;
using Franca.Models;
using Microsoft.Win32;

namespace Franca.Services;

public static class SteamScanner
{
    // VDF 解析失败时的兜底库：默认标准安装路径，可用 FRANCA_STEAM_DIR 覆盖（自定义库位置）
    private static string FallbackLibrary =>
        Environment.GetEnvironmentVariable("FRANCA_STEAM_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");

    public static string? FindSteamPath()
    {
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return string.IsNullOrWhiteSpace(steamPath) ? null : steamPath.Replace('/', '\\');
    }

    public static string? FindSteamExecutable()
    {
        try
        {
            var steamPath = FindSteamPath();
            if (steamPath is null)
                return null;
            var exe = Path.Combine(steamPath, "steam.exe");
            return File.Exists(exe) ? exe : null;
        }
        catch
        {
            return null;
        }
    }

    public static List<string> FindLibraries(Action<string>? log = null)
    {
        var libraries = new List<string>();

        try
        {
            var steamPath = FindSteamPath();
            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    foreach (var path in ParseLibraryFolders(File.ReadAllText(vdfPath)))
                    {
                        if (Directory.Exists(Path.Combine(path, "steamapps", "common")))
                            libraries.Add(path);
                    }
                    log?.Invoke(UiStrings.Format("scan.vdf.found", libraries.Count));
                }
                else if (Directory.Exists(Path.Combine(steamPath, "steamapps", "common")))
                {
                    libraries.Add(steamPath);
                    log?.Invoke(UiStrings.Get("scan.vdf.missing"));
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke(UiStrings.Format("scan.vdf.fail", ex.Message));
        }

        if (libraries.Count == 0)
        {
            log?.Invoke(UiStrings.Format("scan.vdf.fallback", FallbackLibrary));
            if (Directory.Exists(Path.Combine(FallbackLibrary, "steamapps", "common")))
                libraries.Add(FallbackLibrary);
        }

        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // libraryfolders.vdf 是简单键值格式，只需提取 "path" 条目
    private static IEnumerable<string> ParseLibraryFolders(string vdf)
    {
        foreach (Match match in Regex.Matches(vdf, "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            var path = match.Groups[1].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path))
                yield return path;
        }
    }

    // appmanifest_*.acf 同样是键值格式，提取 appid / name / installdir
    private static (int AppId, string Name, string InstallDir)? ParseAppManifest(string acfPath)
    {
        try
        {
            var text = File.ReadAllText(acfPath);
            string? Get(string key) =>
                Regex.Match(text, $"\"{key}\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"") is { Success: true } m
                    ? m.Groups[1].Value
                    : null;

            var appIdText = Get("appid");
            var name = Get("name");
            var installDir = Get("installdir");
            if (int.TryParse(appIdText, out var appId) && !string.IsNullOrWhiteSpace(name)
                && !string.IsNullOrWhiteSpace(installDir))
                return (appId, name, installDir);
        }
        catch
        {
            // 单个 acf 损坏不影响其他游戏
        }
        return null;
    }

    public static List<GameInfo> ScanGames(Action<string>? log = null)
    {
        var games = new List<GameInfo>();
        var steamRoot = FindSteamPath();
        var appInfo = SteamAppInfoReader.Read(steamRoot);
        foreach (var library in FindLibraries(log))
        {
            var steamAppsDir = Path.Combine(library, "steamapps");
            var commonDir = Path.Combine(steamAppsDir, "common");

            // 优先用 appmanifest 拿到 appid 和官方英文名
            var coveredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var acfPath in Directory.GetFiles(steamAppsDir, "appmanifest_*.acf"))
            {
                if (ParseAppManifest(acfPath) is not { } manifest)
                    continue;
                var gameDir = Path.Combine(commonDir, manifest.InstallDir);
                if (!Directory.Exists(gameDir))
                    continue;
                coveredDirs.Add(manifest.InstallDir);
                games.Add(new GameInfo
                {
                    Name = manifest.Name,
                    Path = gameDir,
                    AppId = manifest.AppId,
                    SteamAppType = appInfo.GetValueOrDefault(manifest.AppId)?.Type ?? "",
                    Icon = SteamIconService.Load(steamRoot, manifest.AppId),
                    Engine = EngineDetector.Detect(gameDir),
                    IsReady = TranslationInstaller.IsReady(gameDir)
                });
            }

            // acf 解析失败或没有清单的目录回退用目录名；
            // 无 exe 的目录是卸载残留（只有存档/配置），不是可翻译的游戏，跳过
            foreach (var dir in Directory.GetDirectories(commonDir))
            {
                if (coveredDirs.Contains(Path.GetFileName(dir)))
                    continue;
                var exe = SteamIconService.FindExecutable(dir);
                if (exe is null)
                    continue;
                games.Add(new GameInfo
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    AppId = null,
                    SteamAppType = FindDirectoryType(appInfo.Values, Path.GetFileName(dir)),
                    IsAuxiliarySoftware = IsAuxiliaryOnlyDirectory(dir),
                    Icon = SteamIconService.LoadFromExe(exe),
                    Engine = EngineDetector.Detect(dir),
                    IsReady = TranslationInstaller.IsReady(dir)
                });
            }
        }
        return games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string FindDirectoryType(IEnumerable<SteamAppInfoReader.SteamAppMetadata> entries, string directory)
    {
        // The cache is not an installed manifest. Use only an unambiguous exact install-dir
        // match for classification; never synthesize an installed app id from it.
        var types = entries.Where(e => string.Equals(e.InstallDir, directory, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Type).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return types.Length == 1 ? types[0] : "";
    }

    internal static bool IsAuxiliaryOnlyDirectory(string directory)
    {
        try
        {
            // Glacier Events is an unmanifested EA anti-cheat directory. Do not infer
            // software from its name or hide a real game that also bundles anti-cheat.
            if (!File.Exists(Path.Combine(directory, "EAAntiCheat.GameServiceLauncher.exe"))) return false;
            var executables = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToArray();
            return executables.Length > 0 && executables.All(f => string.Equals(Path.GetFileName(f),
                "EAAntiCheat.GameServiceLauncher.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
