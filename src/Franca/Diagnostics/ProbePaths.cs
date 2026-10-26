using System.IO;
using Franca.Services;

namespace Franca.Diagnostics;

// 探针用的真实游戏目录解析：先看环境变量 FRANCA_STEAM_DIR（可指向自定义库根），
// 再自动探测本机 Steam 库列表。源码不写死盘符，换机器也能跑，公开仓库不泄露个人库位置。
internal static class ProbePaths
{
    public static string GameDir(string folderName)
    {
        var libraries = Libraries().ToList();
        foreach (var library in libraries)
        {
            var dir = Path.Combine(library, "steamapps", "common", folderName);
            if (Directory.Exists(dir)) return dir;
        }
        return Path.Combine(libraries.FirstOrDefault() ?? "", "steamapps", "common", folderName);
    }

    private static IEnumerable<string> Libraries()
    {
        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable("FRANCA_STEAM_DIR");
        if (!string.IsNullOrWhiteSpace(env)) roots.Add(env);
        try { roots.AddRange(SteamScanner.FindLibraries()); } catch { /* 探针保持可运行 */ }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
