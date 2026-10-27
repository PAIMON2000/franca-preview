using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Franca.Services;
using Franca.Views;
using Franca.Desktop;

namespace Franca;

public partial class MainWindow : Window
{
    internal readonly AppSettings Settings;
    internal readonly GameNameService NameService = new();
    internal readonly RelayServer Relay;
    internal readonly GameTranslationService GameTranslation;
    internal readonly LanguageApplyController LanguageApply;
    internal readonly EndpointAutoService AutoEndpoints = new();
    private readonly EndpointAutoController _autoController;
    private AppSettings _runtimeSettings = new();
    private readonly OverlayStatusModel _overlayStatus;
    private OverlayWindow? _overlayWindow;
    private bool _closed;
    private readonly CancellationTokenSource _releaseCheckCancellation = new();
    private AvailableRelease? _availableRelease;

    private void OnLogLineAdded(string line) => StatusLatestLog.Text = line;

    private void PublishRuntimeSettings() =>
        Volatile.Write(ref _runtimeSettings, Settings.Snapshot(AutoEndpoints.ResolveEffective(Settings.Endpoint)));

    private readonly GamesPage _gamesPage = new();
    private readonly EndpointsPage _endpointsPage = new();
    private readonly LogPage _logPage = new();
    private readonly SettingsPage _settingsPage = new();
    private readonly StatusPage _statusPage = new();
    private readonly EnginesPage _enginesPage = new();
    private readonly AcknowledgementsPage _acknowledgementsPage = new();
    private readonly ObservableCollection<NavItem> _navItems = new();
    private readonly GameRuntimeMonitor _runtimeMonitor;
    private readonly KnownIssueRepairService _issueRepair;
    public MainWindow() : this(startServices: true) { }

    internal MainWindow(bool startServices, AppSettings? settings = null)
    {
        InitializeComponent();
        AppLog.Init();

        Settings = settings ?? AppSettings.Load();
        if (!startServices)
            Settings.IsTransient = true;
        PublishRuntimeSettings();
        Settings.Saved += PublishRuntimeSettings;
        Relay = new RelayServer(() => Volatile.Read(ref _runtimeSettings));
        GameTranslation = new GameTranslationService(Relay, Settings);
        // Diagnostic windows never schedule real game writes, including when selectors are exercised.
        LanguageApply = new LanguageApplyController(async ct =>
        {
            var games = _gamesPage.GamesSnapshot;
            var snapshot = Settings.Snapshot();
            try
            {
                return startServices
                    ? await GameTranslation.ApplyLanguagesAsync(games, snapshot, ct)
                    : new GameTranslationService.LanguageApplyResult(0, 0, 0, 0);
            }
            finally { if (!_closed) _gamesPage.RefreshAllRows(); }
        }, startTimer: startServices);
        LanguageApply.Changed += RefreshLanguageApply;
        _overlayStatus = new OverlayStatusModel(() => Volatile.Read(ref _runtimeSettings),
            () => Relay.IsRunning,
            () => new OverlayStatusModel.Counts(Relay.ServedCount, Relay.CacheHits,
                Relay.DictHits, Relay.InFlight, Relay.Failures));
        _autoController = new EndpointAutoController(AutoEndpoints,
            GetAutoCandidates, ProbeEndpointOnceAsync, ApplyAutoPick);
        UiStrings.Current = Settings.TargetLanguage;
        Themes.ThemeCatalog.Apply(Settings.Theme);
        _issueRepair = new KnownIssueRepairService(() => _gamesPage.GamesSnapshot);
        _runtimeMonitor = new GameRuntimeMonitor(OnKnownIssueDetected,
            game => GameTranslation.PrepareGameTextAsync(game));

        _gamesPage.Init(this);
        _endpointsPage.Init(this);
        _logPage.Init(this);
        _settingsPage.Init(this);
        _statusPage.Init(this);

        _navItems.Add(new NavItem("\uE7FC", "nav.games"));
        _navItems.Add(new NavItem("\uE774", "nav.endpoints"));
        _navItems.Add(new NavItem("\uE713", "nav.settings"));
        _navItems.Add(new NavItem("\uEC05", "nav.status"));
        _navItems.Add(new NavItem("\uE9D5", "nav.engines"));
        _navItems.Add(new NavItem("\uE8A5", "nav.log"));
        _navItems.Add(new NavItem("\uE946", "nav.acknowledgements"));
        NavList.ItemsSource = _navItems;
        NavList.SelectedIndex = 0;
        Closed += (_, _) =>
        {
            _closed = true;
            _releaseCheckCancellation.Cancel();
            _releaseCheckCancellation.Dispose();
            LanguageApply.Dispose();
            _gamesPage.StopBackgroundWork();
            _acknowledgementsPage.CancelStartupCountdown();
            _autoController.Dispose();
            _endpointsPage.Dispose();
            _runtimeMonitor.Dispose();
            _statusPage.Stop();
            _overlayWindow?.Close();
            Relay.Dispose();
            Settings.Saved -= PublishRuntimeSettings;
            AppLog.LineAdded -= OnLogLineAdded;
        };

        ApplyUiLanguage();
        _gamesPage.ApplyUiLanguage();
        _endpointsPage.ApplyUiLanguage();
        _logPage.ApplyUiLanguage();
        _settingsPage.ApplyUiLanguage();
        _statusPage.ApplyUiLanguage();
        _enginesPage.ApplyUiLanguage();
        AppLog.LineAdded += OnLogLineAdded;
        StateChanged += OnWindowStateChanged;

        // 导航探针只构建真实页面，不启动端点测速、中继或游戏监控。
        if (!startServices)
            return;

        // 恢复自动端点模式（主用是 Auto* 时重启测速 Timer）
        if (EndpointAutoService.GroupOf(Settings.Endpoint) is { } autoGroup)
            _autoController.Start(autoGroup);

        // Runtime lifetime is owned by this window, including diagnostic windows.
        _ = Task.Run(async () =>
        {
            try
            {
                await Relay.EnsureStartedAsync();
                AppLog.Write(UiStrings.Format("log.relay.start", Relay.Port));
            }
            catch (Exception ex)
            {
                AppLog.Write(UiStrings.Format("log.relay.fail", ex.Message));
            }
        });

        // 悬浮窗轮询刷新（lab/011）：约 1 秒；仅开关打开且中继在跑时显示
        var overlayTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        overlayTimer.Tick += (_, _) => RefreshOverlay();
        overlayTimer.Start();

        Loaded += async (_, _) =>
        {
            _ = CheckForReleaseAsync();
            DnsWarmup.KickOff();
            await _gamesPage.StartupAsync();
            if (_closed) return;
            _runtimeMonitor.Start();
            await _endpointsPage.StartupAsync();
        };
    }

    // ---- 导航与页签 ----

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedIndex != 6)
            _acknowledgementsPage.CancelStartupCountdown();
        PageHost.Content = NavList.SelectedIndex switch
        {
            0 => _gamesPage,
            1 => _endpointsPage,
            2 => _settingsPage,
            3 => _statusPage,
            4 => _enginesPage,
            5 => _logPage,
            6 => _acknowledgementsPage,
            _ => _gamesPage
        };
    }

    internal void NavigateTo(int pageIndex) => NavList.SelectedIndex = pageIndex;

    internal void UpdateRuntimeGames(IEnumerable<Franca.Models.GameInfo> games) =>
        _runtimeMonitor.UpdateGames(games);

    private Task OnKnownIssueDetected(KnownIssueDetection detection) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            var language = AppSettings.ResolveSupportedLanguage(System.Globalization.CultureInfo.CurrentUICulture);
            var dialog = new KnownIssueDialog(
                detection.Issue,
                detection.Game.Name,
                language,
                () => _issueRepair.RepairAsync(detection));
            dialog.Owner = this;
            dialog.ShowDialog();
        }).Task;

    private void OnStatusEndpointsClick(object sender, RoutedEventArgs e) => NavigateTo(1);

    // ---- 无边框窗口按钮 ----

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // 标题条手动拖拽：WindowChrome 命中链在本机失效的兜底；双击切换最大化
    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;
        if (e.ClickCount == 2)
        {
            OnMaximizeClick(sender, e);
            return;
        }
        if (WindowState == WindowState.Normal)
            DragMove();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) =>
        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

    // ---- 语言切换编排（游戏库与设置页同步） ----

    internal void OnLanguageChanged(string newLanguage)
    {
        if (newLanguage == Settings.TargetLanguage)
            return;
        Settings.TargetLanguage = newLanguage;
        Settings.Save();

        // 整个 UI 立即跟随新语言；已有日志不回溯
        UiStrings.Current = newLanguage;
        ApplyUiLanguage();
        _gamesPage.ApplyUiLanguage();
        _gamesPage.SyncLanguageCombo(newLanguage);
        _settingsPage.ApplyUiLanguage();
        _settingsPage.SyncLanguageCombo(newLanguage);
        _statusPage.ApplyUiLanguage();
        _enginesPage.ApplyUiLanguage();
        _logPage.ApplyUiLanguage();
        _endpointsPage.ApplyUiLanguage();
        _endpointsPage.RefreshAllCards();
        _gamesPage.RefreshAllRows();
        _gamesPage.RestartNameResolutionIfAny();
        LanguageApply.Schedule();
    }

    internal void OnSourceLanguageChanged(string newLanguage)
    {
        if (newLanguage == Settings.SourceLanguage)
            return;
        Settings.SourceLanguage = newLanguage;
        Settings.Save();
        _gamesPage.SyncSourceLanguageCombo(newLanguage);
        _settingsPage.SyncSourceLanguageCombo(newLanguage);
        _gamesPage.RestartNameResolutionIfAny();
        _gamesPage.RefreshAllRows();
        LanguageApply.Schedule();
    }

    private void RefreshLanguageApply()
    {
        _gamesPage.LanguageApply.Refresh();
        _settingsPage.LanguageApply.Refresh();
    }

    internal void OnSecondaryLanguageChanged(string language)
    {
        if (language != "" && !AppSettings.Languages.Any(item => item.Code == language)) return;
        if (Settings.SecondaryLanguage == language) return;
        Settings.SecondaryLanguage = language;
        Settings.Save();
        _gamesPage.SyncSecondaryLanguageCombo(language);
        _settingsPage.SyncSecondaryLanguageCombo(language);
        _gamesPage.RefreshSecondarySupport();
    }

    internal void OnTranslationModeChanged(string mode)
    {
        if (mode == Settings.TranslationMode)
            return;
        Settings.TranslationMode = mode;
        Settings.Save(); // Saved 事件已重新发布运行时快照，中继下一请求生效
    }

    internal void OnNoOptimizeChanged(bool value)
    {
        if (value == Settings.NoOptimizeAfterGenerate)
            return;
        Settings.NoOptimizeAfterGenerate = value;
        Settings.Save();
    }

    internal void OnHideNonGameSoftwareChanged(bool value)
    {
        if (value == Settings.HideNonGameSoftware) return;
        Settings.HideNonGameSoftware = value;
        Settings.Save();
        _gamesPage.ApplyLibraryView();
    }

    internal void OnOverlayEnabledChanged(bool value)
    {
        if (value == Settings.OverlayEnabled)
            return;
        Settings.OverlayEnabled = value;
        Settings.Save();
    }

    internal void OnThemeChanged(string themeId)
    {
        if (themeId == Settings.Theme)
            return;
        Settings.Theme = themeId;
        Settings.Save();
        Themes.ThemeCatalog.Apply(themeId);
        _endpointsPage.RefreshTheme();
        _gamesPage.RefreshTheme();
        // 状态页卡片在代码中建构、持有旧画刷实例，主题切换后重建（XAML 侧走 DynamicResource 自动跟随）
        _statusPage.ApplyUiLanguage();
    }

    // 悬浮窗显隐与刷新（lab/011）：隐藏即关闭到 null，重开时恢复记忆位置
    private void RefreshOverlay()
    {
        if (_closed) return;
        if (!_overlayStatus.ShouldShow)
        {
            _overlayWindow?.Hide();
            return;
        }
        if (_overlayWindow is null)
        {
            _overlayWindow = new OverlayWindow(Settings.OverlayLeft, Settings.OverlayTop, (left, top) =>
            {
                Settings.OverlayLeft = left;
                Settings.OverlayTop = top;
                Settings.Save();
            });
            _overlayWindow.Closed += (_, _) => _overlayWindow = null;
        }
        _overlayWindow.SetLines(_overlayStatus.BuildLines());
        if (!_overlayWindow.IsVisible) _overlayWindow.Show();
    }

    private void ApplyUiLanguage()
    {
        Title = UiStrings.Get("app.title") + " · " + ReleaseUpdateService.DisplayVersion;
        BrandLabel.ToolTip = ReleaseUpdateService.DisplayVersion;
        RefreshUpdateBanner();
        _acknowledgementsPage.ApplyUiLanguage();
        foreach (var item in _navItems)
            item.RefreshLabel();
        RefreshStatusBar();
    }

    private async Task CheckForReleaseAsync()
    {
        var release = await new ReleaseUpdateService().CheckAsync(_releaseCheckCancellation.Token);
        if (_closed || release is null) return;
        ShowAvailableRelease(release);
    }

    internal void ShowAvailableRelease(AvailableRelease release)
    {
        _availableRelease = release;
        RefreshUpdateBanner();
    }

    private void RefreshUpdateBanner()
    {
        if (_availableRelease is null) return;
        UpdateMessage.Text = UiStrings.Format("update.available", _availableRelease.Name);
        UpdateDownloadButton.Content = UiStrings.Get("update.download");
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnUpdateDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_availableRelease is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_availableRelease.Page.AbsoluteUri)
                { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Write("Could not open release page: " + ex.GetType().Name); }
    }

    // ---- 底部状态条 ----

    // 主用是自动端点时，软件内翻译与状态条用实际选中的端点
    internal string EffectiveEndpoint => AutoEndpoints.ResolveEffective(Settings.Endpoint);

    // 状态页读取：游戏文本同步链路活动计数
    internal GameTextSyncService TextSync => GameTranslation.TextSync;

    internal void RefreshStatusBar()
    {
        var effective = EffectiveEndpoint;
        var primaryName = Settings.Endpoint switch
        {
            var id when EndpointAutoService.IsAutoEndpoint(id) =>
                UiStrings.Get("endpoint.auto")
                + (AutoEndpoints.CurrentPick is { } pick
                    ? " · " + DisplayNameOf(pick)
                    : ""),
            var id => DisplayNameOf(id)
        };
        var fallbackName = DisplayNameOf(Settings.FallbackEndpoint);
        var latency = _endpointsPage.GetLatencyText(effective);
        StatusEndpointsText.Text =
            $"{UiStrings.Get("badge.primary")} {primaryName}{(latency is null ? "" : " " + latency)}" +
            $" · {UiStrings.Get("badge.fallback")} {fallbackName}";
    }

    internal static string DisplayNameOf(string endpointId)
    {
        var def = EndpointCatalog.Find(endpointId);
        return def is not null ? UiStrings.Get(def.DisplayNameKey) : endpointId;
    }

    // 主用端点变更后的联动：机翻缓存失效、译名列跟随、状态条同步、自动服务启停
    internal void NotifyPrimaryChanged(string endpointId)
    {
        NameService.InvalidateMachineNames(EffectiveEndpoint);
        _gamesPage.RestartNameResolutionIfAny();
        if (EndpointAutoService.GroupOf(Settings.Endpoint) is { } autoGroup)
            _autoController.Start(autoGroup);
        else
            _autoController.Stop();
        PublishRuntimeSettings();
        RefreshStatusBar();
    }

    // ---- 自动端点服务回调 ----

    internal List<(string Id, string Host)> GetAutoCandidates(EndpointCategory category) =>
        _endpointsPage.GetAutoCandidates(category);

    internal Task<Franca.Services.TestOutcome> ProbeEndpointOnceAsync(string endpointId, CancellationToken ct) =>
        _endpointsPage.ProbeOnceAsync(endpointId, ct);

    // 自动服务确认切换：写日志、更新卡片上的实际端点名、状态条同步。
    // 译名缓存不随之失效：游戏名翻译结果与翻译端点无关，自动切换不应触发整列重翻
    // （否则自动端点每次切换都把 152 款游戏的译名列打回"翻译中…"重新机翻）
    internal void ApplyAutoPick(string endpointId, long latencyMs)
    {
        PublishRuntimeSettings();
        var name = DisplayNameOf(endpointId);
        AppLog.Write(UiStrings.Format("log.auto.switch", name, latencyMs));
        _endpointsPage.SetAutoCurrentName(name);
        RefreshStatusBar();
    }
}

// 导航项：图标字形 + 随语言切换的文字
public class NavItem : INotifyPropertyChanged
{
    public string Glyph { get; }
    private readonly string _labelKey;

    public NavItem(string glyph, string labelKey)
    {
        Glyph = glyph;
        _labelKey = labelKey;
    }

    public string Label => UiStrings.Get(_labelKey);

    public void RefreshLabel()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SectionLabel)));
    }

    public bool StartsAdvancedSection => _labelKey == "nav.status";
    public string SectionLabel => UiStrings.Get("ui.advanced");

    public event PropertyChangedEventHandler? PropertyChanged;
}
