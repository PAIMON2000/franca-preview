using System.Collections.ObjectModel;
using System.IO;

namespace Franca.Services;

// 应用级日志总线：各页/服务统一写这里，日志页绑定 Lines，状态条订阅最新行
public static class AppLog
{
    public static ObservableCollection<string> Lines { get; } = new();
    public static string Latest { get; private set; } = "";
    public static event Action<string>? LineAdded;

    private static SynchronizationContext? _ui;

    // 在 UI 线程调用一次，之后任意线程 Write 都会封送回 UI 线程
    public static void Init() => _ui = SynchronizationContext.Current;

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (_ui is null || SynchronizationContext.Current == _ui)
            Append(line);
        else
            _ui.Post(_ => Append(line), null);
    }

    private static void Append(string line)
    {
        Lines.Add(line);
        Latest = line;
        LineAdded?.Invoke(line);
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "franca-app.log"), line + Environment.NewLine);
        }
        catch
        {
            // 日志落盘失败不影响主流程
        }
    }

    public static void Clear() => Lines.Clear();
}
