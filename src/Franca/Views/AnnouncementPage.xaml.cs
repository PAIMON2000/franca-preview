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

    public void ApplyUiLanguage()
    {
        Title.Text = UiStrings.Get("announce.button");
        RefreshButton.Content = UiStrings.Get("github.refresh");
    }

    // 选中页：8 小时缓存窗口内直接展示缓存（不联网）；手动刷新按钮强制重查
    public async Task ShowAsync(bool force = false)
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        var showSpinner = force || !GitHubHttp.IsFresh(_host.Settings.LastAnnouncementFetchUtc);
        if (showSpinner)
        {
            ContentScroller.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Visible;
            StartSpinner();
        }
        if (_host.ServicesEnabled)
            await _host.FetchAnnouncementAsync(force);
        if (showSpinner)
            StopSpinner();
        _loading = false;
        RefreshButton.IsEnabled = true;
        var announcement = _host.CurrentAnnouncement;
        LoadingText.Visibility = Visibility.Collapsed;
        ContentScroller.Visibility = Visibility.Visible;
        ContentText.Text = announcement?.Text ?? UiStrings.Get("announce.none");
        if (announcement is not null)
            _host.MarkAnnouncementSeen();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ShowAsync(force: true);

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
