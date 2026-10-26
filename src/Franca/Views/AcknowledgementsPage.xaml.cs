using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Franca.Services;

namespace Franca.Views;

public partial class AcknowledgementsPage : UserControl
{
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _startupCountdownUsed;
    private long _deadline;

    public event Action? StartupCompleted;
    public string ThanksText => UiStrings.Get("splash.thanks");
    internal bool AutoAdvanceEnabled => _countdown.IsEnabled;
    internal int SecondsRemaining { get; private set; }

    public AcknowledgementsPage()
    {
        InitializeComponent();
        ApplyUiLanguage();
        _countdown.Tick += (_, _) => UpdateCountdown();
        Unloaded += (_, _) => CancelStartupCountdown();
    }

    public void ApplyUiLanguage()
    {
        FlowDirection = UiStrings.Current == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        TitleText.Text = UiStrings.Get("splash.title");
        SubtitleText.Text = UiStrings.Get("splash.subtitle");
        ProjectLicenseText.Text = UiStrings.Format("splash.license", AttributionCatalog.ProjectLicense);
        LicenseScopeText.Text = UiStrings.Get("splash.scope");
        UpstreamRiskText.Text = UiStrings.Get("splash.upstream");
        NoticesButton.Content = UiStrings.Get("splash.notices");
        AttributionItems.ItemsSource = null;
        AttributionItems.ItemsSource = AttributionCatalog.All;
        CountdownText.Text = UiStrings.Format("splash.countdown", SecondsRemaining);
    }

    public void BeginStartupCountdown()
    {
        if (_startupCountdownUsed)
            return;
        _startupCountdownUsed = true;
        _deadline = Environment.TickCount64 + 5000;
        CountdownText.Visibility = Visibility.Visible;
        UpdateCountdown();
        _countdown.Start();
    }

    private void UpdateCountdown()
    {
        SecondsRemaining = Math.Max(0, (int)Math.Ceiling((_deadline - Environment.TickCount64) / 1000d));
        CountdownText.Text = UiStrings.Format("splash.countdown", SecondsRemaining);
        if (SecondsRemaining == 0)
        {
            CancelStartupCountdown();
            StartupCompleted?.Invoke();
        }
    }

    public void CancelStartupCountdown()
    {
        _countdown.Stop();
        CountdownText.Visibility = Visibility.Collapsed;
    }

    private void OnAttributionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private void OnNoticesClick(object sender, RoutedEventArgs e)
    {
        var resumeCountdown = _countdown.IsEnabled;
        var pausedAt = Environment.TickCount64;
        _countdown.Stop();
        try
        {
            new LicenseWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        finally
        {
            if (resumeCountdown && IsLoaded && CountdownText.Visibility == Visibility.Visible)
            {
                _deadline += Environment.TickCount64 - pausedAt;
                _countdown.Start();
            }
        }
    }
}