using System.Windows;
using System.Windows.Controls;
using Franca.Services;

namespace Franca.Views;

// 公告页：选中即带加载动画重新拉取（探针窗口跳过网络只读缓存），展示后记为已读
public partial class AnnouncementPage : UserControl
{
    private MainWindow _host = null!;
    private bool _loading;
    private int _spinnerFrame;
    private System.Windows.Threading.DispatcherTimer? _spinnerTimer;
    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public AnnouncementPage()
    {
        InitializeComponent();
    }

    public void Init(MainWindow host) => _host = host;

    public void ApplyUiLanguage() => Title.Text = UiStrings.Get("announce.button");

    public async Task ShowAsync()
    {
        if (_loading) return;
        if (_host.ServicesEnabled)
        {
            _loading = true;
            ContentScroller.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Visible;
            StartSpinner();
            await _host.FetchAnnouncementAsync();
            StopSpinner();
            _loading = false;
        }
        var announcement = _host.CurrentAnnouncement;
        LoadingText.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Visible;
        ContentText.Text = announcement?.Text ?? UiStrings.Get("announce.none");
        if (announcement is not null)
            _host.MarkAnnouncementSeen();
    }

    private void StartSpinner()
    {
        _spinnerTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _spinnerTimer.Tick += SpinnerTick;
        _spinnerTimer.Start();
    }

    private void SpinnerTick(object? sender, EventArgs e)
    {
        _spinnerFrame = (_spinnerFrame + 1) % Spinner.Length;
        LoadingText.Text = Spinner[_spinnerFrame] + " " + UiStrings.Get("announce.button");
    }

    private void StopSpinner()
    {
        if (_spinnerTimer is null) return;
        _spinnerTimer.Tick -= SpinnerTick;
        _spinnerTimer.Stop();
        _spinnerTimer = null;
    }
}
