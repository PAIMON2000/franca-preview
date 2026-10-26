using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Franca.Services;

// 只读 Steam 本地缓存；不联网，不提取或执行游戏程序。
public static class SteamIconService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    // 无 appid 的游戏目录（手动放置、无清单）：找目录内可执行文件提取其图标。
    // 根目录优先、其次一层子目录；优先与目录同名的 exe，其次最大的。
    // 排除卸载/运行库安装程序——它们的图标不代表游戏。
    public static string? FindExecutable(string gameDir)
    {
        try
        {
            foreach (var pool in ExePools(gameDir))
            {
                var sameName = pool.Where(exe =>
                    string.Equals(Path.GetFileNameWithoutExtension(exe), Path.GetFileName(gameDir),
                        StringComparison.OrdinalIgnoreCase)).ToList();
                if (sameName.Count > 0) return sameName[0];
            }
            foreach (var pool in ExePools(gameDir))
                if (pool.Count > 0) return pool.OrderByDescending(exe => new FileInfo(exe).Length).First();
        }
        catch { /* 目录不可读时按无 exe 处理 */ }
        return null;
    }

    // 根目录与一层子目录中的 exe 候选，分别成组（根目录组在前，保持优先级）
    private static IEnumerable<List<string>> ExePools(string gameDir)
    {
        string[] Junk = { "unins", "redist", "installer", "uninstall", "dxsetup", "vcredist" };
        bool IsGameExe(string path) =>
            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !Junk.Any(j => Path.GetFileName(path).Contains(j, StringComparison.OrdinalIgnoreCase));

        var root = new List<string>();
        if (!Directory.Exists(gameDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(gameDir))
            if (IsGameExe(file)) root.Add(file);
        yield return root;
        foreach (var sub in Directory.EnumerateDirectories(gameDir))
        {
            if (Path.GetFileName(sub).StartsWith('_')) continue;
            var pool = new List<string>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(sub))
                    if (IsGameExe(file)) pool.Add(file);
            }
            catch { /* 子目录不可读时跳过 */ }
            if (pool.Count > 0) yield return pool;
        }
    }

    // 从 exe 提取关联图标（shell32，不引入额外依赖）；失败返回 null
    public static ImageSource? LoadFromExe(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        lock (Sync)
        {
            if (Cache.TryGetValue(exePath, out var cached)) return cached;
        }
        var index = 0;
        var hIcon = ExtractAssociatedIcon(IntPtr.Zero, exePath, ref index);
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            lock (Sync) Cache[exePath] = bitmap;
            return bitmap;
        }
        catch { return null; }
        finally { DestroyIcon(hIcon); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, string iconPath, ref int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static ImageSource? Load(string? steamRoot, int? appId)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || appId is not > 0) return null;
        try
        {
            var root = Path.Combine(steamRoot, "appcache", "librarycache");
            var nested = Path.Combine(root, appId.Value.ToString());
            var candidates = new List<string>();
            foreach (var ext in new[] { "png", "jpg", "ico" })
            {
                candidates.Add(Path.Combine(nested, "icon." + ext));
                candidates.Add(Path.Combine(root, $"{appId}_icon.{ext}"));
            }
            if (Directory.Exists(nested))
                candidates.AddRange(Directory.EnumerateFiles(nested)
                    .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^[a-fA-F0-9]{40}\.(jpg|png|ico)$", RegexOptions.IgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase));
            foreach (var registryRoot in new[]
                     {
                         @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App ",
                         @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App ",
                         @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\Steam App ",
                     })
            {
                try
                {
                    if (Registry.GetValue(registryRoot + appId, "DisplayIcon", null) is string value)
                    {
                        var path = Regex.Replace(value.Trim(), @",\s*-?\d+$", "").Trim('"');
                        if (new[] { ".ico", ".png", ".jpg" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                            candidates.Add(path);
                    }
                }
                catch { /* 未注册或无读取权限时保留缓存回退。 */ }
            }
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                lock (Sync)
                {
                    if (Cache.TryGetValue(path, out var cached)) return cached;
                }
                try
                {
                    if (new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 32;
                    bitmap.UriSource = new Uri(Path.GetFullPath(path));
                    bitmap.EndInit();
                    if (bitmap.PixelWidth != bitmap.PixelHeight) continue;
                    bitmap.Freeze();
                    lock (Sync) Cache[path] = bitmap;
                    return bitmap;
                }
                catch { /* 损坏、被锁或格式不支持：尝试下一个缓存文件。 */ }
            }
        }
        catch { /* Steam 不在默认位置或缓存不可读时使用列表占位图。 */ }
        return null;
    }
}
