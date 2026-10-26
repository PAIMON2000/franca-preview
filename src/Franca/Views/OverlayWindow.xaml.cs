using System.Windows;
using System.Windows.Interop;
using Franca.Services;

namespace Franca.Views;

// 翻译状态悬浮窗（lab/011）：无边框、置顶、透明背景；WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
// 点击穿透——不抢焦点、不吃游戏按键；抓手条移入时可拖动。该窗口不注入游戏进程；这不构成反作弊或账号安全保证，游戏内交互仍需验证。
// 硬限制：独占全屏（exclusive fullscreen）的游戏看不到悬浮窗，建议游戏用无边框/窗口化模式。
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    private readonly Action<double, double>? _onPositionChanged;
    private readonly System.Windows.Threading.DispatcherTimer _gripTimer;

    public OverlayWindow(double? left, double? top, Action<double, double>? onPositionChanged = null)
    {
        InitializeComponent();
        _onPositionChanged = onPositionChanged;
        if (left is not null && top is not null)
        {
            Left = left.Value;
            Top = top.Value;
        }
        Grip.ToolTip = UiStrings.Get("settings.overlay");
        Grip.MouseLeftButtonDown += (_, _) =>
        {
            // DragMove 阻塞至拖动结束；结束后回报位置供设置持久化
            try { DragMove(); _onPositionChanged?.Invoke(Left, Top); }
            catch { /* 穿透态下拖动被取消 */ }
        };
        // A WS_EX_TRANSPARENT window cannot receive MouseEnter. Poll only the grip bounds,
        // without moving the pointer or activating any window.
        _gripTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _gripTimer.Tick += (_, _) =>
        {
            if (!IsVisible || !GetCursorPos(out var point)) return;
            var local = Grip.PointFromScreen(new Point(point.X, point.Y));
            SetClickThrough(!Grip.IsMouseCaptured &&
                !(local.X >= 0 && local.Y >= 0 && local.X < Grip.ActualWidth && local.Y < Grip.ActualHeight));
        };
        Loaded += (_, _) => _gripTimer.Start();
        Closed += (_, _) => _gripTimer.Stop();
    }

    public void SetLines(IReadOnlyList<string> lines)
    {
        ModeLine.Text = lines.Count > 0 ? lines[0] : "";
        StatsLine.Text = lines.Count > 1 ? lines[1] : "";
        FlightLine.Text = lines.Count > 2 ? lines[2] : "";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 初始即穿透 + 永不激活；游戏前台时不抢焦点
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GwlExStyle,
            GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExTransparent);
    }

    private void SetClickThrough(bool through)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        var style = GetWindowLong(source.Handle, GwlExStyle);
        SetWindowLong(source.Handle, GwlExStyle,
            through ? style | WsExTransparent : style & ~WsExTransparent);
    }
}
