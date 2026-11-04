using System.Windows;
using System.Windows.Controls;
using Franca.Services;

namespace Franca.Views;

// 更新页：选中即带加载动画重新检查（探针窗口跳过网络只读缓存），有新版提供下载入口
public partial class UpdatePage : UserControl
{
    private MainWindow _host = null!;
    private AvailableRelease? _release;
    private bool _loading;
    private int _spinnerFrame;
    private System.Windows.Threading.DispatcherTimer? _spinnerTimer;
    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public UpdatePage()
    {
        InitializeComponent();
    }

    public void Init(MainWindow host) => _host = host;

    public void ApplyUiLanguage()
    {
        Title.Text = UiStrings.Get("update.button");
        VersionText.Text = ReleaseUpdateService.DisplayVersion;
        DownloadButton.Content = UiStrings.Get("update.download");
        ShowResult();
    }

    public async Task ShowAsync()
    {
        if (_loading) return;
        if (_host.ServicesEnabled)
        {
            _loading = true;
            ResultText.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Visible;
            StartSpinner();
            await _host.RecheckReleaseAsync();
            StopSpinner();
            _loading = false;
        }
        _release = _host.CurrentAvailableRelease;
        ShowResult();
    }

    private void ShowResult()
    {
        if (_host is null) return;
        LoadingText.Visibility = Visibility.Collapsed;
        ResultText.Visibility = Visibility.Visible;
        ResultText.Text = _release is null
            ? UiStrings.Get("update.latest")
            : UiStrings.Format("update.available", _release.Name);
        DownloadButton.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_release is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_release.Page.AbsoluteUri)
                { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Write("Could not open release page: " + ex.GetType().Name); }
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
        LoadingText.Text = Spinner[_spinnerFrame] + " " + UiStrings.Get("update.button");
    }

    private void StopSpinner()
    {
        if (_spinnerTimer is null) return;
        _spinnerTimer.Tick -= SpinnerTick;
        _spinnerTimer.Stop();
        _spinnerTimer = null;
    }
}
