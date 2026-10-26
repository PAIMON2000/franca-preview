using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Franca.Models;
using Franca.Services;

namespace Franca.Views;

// 游戏库页：Steam 扫描、译名解析、启用翻译（业务逻辑自原 MainWindow 原样迁移）
public partial class GamesPage : UserControl
{
    private readonly ObservableCollection<GameInfo> _games = new();
    private MainWindow _host = null!;
    private CancellationTokenSource? _namesCts;
    private CancellationTokenSource? _secondarySupportCts;
    private Task _namesTask = Task.CompletedTask;
    private Task _secondarySupportTask = Task.CompletedTask;
    private readonly CancellationTokenSource _pageLifetime = new();
    private bool _refreshingSupport;
    private int _supportSpinnerFrame;
    private static readonly string[] SupportSpinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private bool _busy;
    private bool _suppressLangEvent;
    private bool _suppressSourceEvent;
    private bool _suppressSecondaryEvent;
    private ListCollectionView? _view;

    public GamesPage()
    {
        InitializeComponent();
    }

    private void OnGameGridPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        for (var source = e.OriginalSource as DependencyObject; source is not null;
             source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
        {
            // 行内动作和详情区域保留原交互，只有再次点击同一行的概览才收起。
            if (source is ButtonBase or DataGridDetailsPresenter) return;
            if (source is DataGridRow row)
            {
                if (row.IsSelected)
                {
                    GameGrid.UnselectAll();
                    e.Handled = true; // 阻止 DataGrid 在后续鼠标事件中立即重新选中。
                }
                return;
            }
        }
    }

    public void Init(MainWindow host)
    {
        _host = host;
        LanguageApply.Attach(host.LanguageApply);
        LanguageCombo.ItemsSource = AppSettings.Languages;
        LanguageCombo.DisplayMemberPath = nameof(LanguageOption.Display);
        LanguageCombo.SelectedValuePath = nameof(LanguageOption.Code);
        LanguageCombo.SelectedValue = _host.Settings.TargetLanguage;
        SourceLanguageCombo.DisplayMemberPath = nameof(LanguageOption.Display);
        SourceLanguageCombo.SelectedValuePath = nameof(LanguageOption.Code);
        RefreshSourceLanguages();
        SyncSecondaryLanguageCombo(_host.Settings.SecondaryLanguage);
        _view = new ListCollectionView(_games);
        GameGrid.ItemsSource = _view;
        PriorityToggle.IsChecked = _host.Settings.PrioritizeTranslation;
        ApplyLibraryView();
    }

    public void ApplyUiLanguage()
    {
        SourceLangLabel.Text = UiStrings.Get("label.sourcelang");
        LibraryTitle.Text = UiStrings.Get("nav.games");
        LibraryHint.Text = UiStrings.Get("ui.libraryHint");
        TargetLangLabel.Text = UiStrings.Get("label.targetlang");
        SecondaryLangLabel.Text = UiStrings.Get("label.secondarylang");
        SecondaryLanguageCombo.ToolTip = UiStrings.Get("settings.secondary.hint");
        RescanButton.Content = UiStrings.Get("btn.rescan");
        UpdateRefreshSupportButton();
        SearchHintText.Text = UiStrings.Get("games.search");
        GameSearchBox.ToolTip = UiStrings.Get("games.search");
        System.Windows.Automation.AutomationProperties.SetName(GameSearchBox, UiStrings.Get("games.search"));
        ClearSearchButton.ToolTip = UiStrings.Get("games.search.clear");
        System.Windows.Automation.AutomationProperties.SetName(ClearSearchButton, UiStrings.Get("games.search.clear"));
        PriorityLabel.Text = UiStrings.Get("games.priority");
        PriorityToggle.ToolTip = UiStrings.Get("games.priority.hint");
        AutomaticLaunchNote.Text = UiStrings.Get("games.auto.note");
        ColName.Header = UiStrings.Get("col.name");
        ColTranslated.Header = UiStrings.Get("col.translated");
        ColEngine.Header = UiStrings.Get("col.engine");
        ColStatus.Header = UiStrings.Get("col.status");
        ColHealth.Header = UiStrings.Get("health.column");
        ColAction.Header = UiStrings.Get("col.action");
        RefreshSourceLanguages();
        SyncSecondaryLanguageCombo(_host.Settings.SecondaryLanguage);
        ApplyLibraryView();
        UpdateStats();
    }

    private void RefreshSourceLanguages()
    {
        _suppressSourceEvent = true;
        SourceLanguageCombo.ItemsSource = AppSettings.GetSourceLanguages();
        SourceLanguageCombo.SelectedValue = _host.Settings.SourceLanguage;
        _suppressSourceEvent = false;
    }

    // 语言在别处（设置页）切换时同步下拉框，不触发事件
    public void SyncLanguageCombo(string code)
    {
        _suppressLangEvent = true;
        LanguageCombo.SelectedValue = code;
        _suppressLangEvent = false;
    }

    public void SyncSecondaryLanguageCombo(string code)
    {
        _suppressSecondaryEvent = true;
        SecondaryLanguageCombo.DisplayMemberPath = nameof(LanguageOption.Display);
        SecondaryLanguageCombo.SelectedValuePath = nameof(LanguageOption.Code);
        SecondaryLanguageCombo.ItemsSource = AppSettings.GetSecondaryLanguages();
        SecondaryLanguageCombo.SelectedValue = code;
        _suppressSecondaryEvent = false;
    }

    private void OnSecondaryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSecondaryEvent) return;
        _host.OnSecondaryLanguageChanged(SecondaryLanguageCombo.SelectedValue as string ?? "");
    }

    public void SyncSourceLanguageCombo(string code)
    {
        _suppressSourceEvent = true;
        SourceLanguageCombo.SelectedValue = code;
        _suppressSourceEvent = false;
    }

    public void RefreshAllRows()
    {
        foreach (var game in _games.ToList())
        {
            RefreshTranslationHealth(game);
            RefreshRow(game);
        }
    }

    // 只重新求值显示画刷，不做健康检查、扫描或译名解析。
    public void RefreshTheme() => GameGrid.Items.Refresh();

    public void RestartNameResolutionIfAny()
    {
        if (_games.Count > 0)
            StartNameResolution();
    }

    public Task StartupAsync() => ScanAsync();

    internal IReadOnlyList<GameInfo> GamesSnapshot => _games.ToList();

    internal void ReplaceGames(IEnumerable<GameInfo> games)
    {
        _games.Clear();
        foreach (var game in games) _games.Add(game);
        UpdateStats();
    }

    private void UpdateStats()
    {
        var eligible = _games.Where(g => GameLibraryOrder.IsVisible(g, _host.Settings.HideNonGameSoftware)).ToList();
        StatsLabel.Text = string.IsNullOrWhiteSpace(GameSearchBox.Text)
            ? UiStrings.Format("games.count", eligible.Count)
            : UiStrings.Format("games.visiblecount", _view?.Count ?? 0, eligible.Count);
        var recent = eligible.FirstOrDefault(g => g.AppId == _host.Settings.LastLaunchedAppId && g.CanLaunch);
        RecentGameButton.Tag = recent;
        RecentGameButton.Content = recent is null ? "" : UiStrings.Get("ui.recent") + " · " + recent.LibraryTitle;
        RecentGameButton.Visibility = recent is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void ApplyLibraryView()
    {
        if (_view is null || _host is null) return;
        using (_view.DeferRefresh())
        {
            _view.CustomSort = new GameLibraryOrder(_host.Settings.PrioritizeTranslation, _host.Settings.TargetLanguage, _host.Settings.SecondaryLanguage);
            _view.Filter = item => item is GameInfo game
                && GameLibraryOrder.IsVisible(game, _host.Settings.HideNonGameSoftware)
                && GameLibraryOrder.Matches(game, GameSearchBox.Text);
        }
        UpdateStats();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateStats();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e) => GameSearchBox.Clear();

    private void OnPriorityChanged(object sender, RoutedEventArgs e)
    {
        if (_view is null || _host is null) return;
        _host.Settings.PrioritizeTranslation = PriorityToggle.IsChecked == true;
        _host.Settings.Save();
        ApplyLibraryView();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            AppLog.Write(UiStrings.Get("log.scan.start"));
            var games = await Task.Run(() =>
                SteamScanner.ScanGames(m => Dispatcher.Invoke(() => AppLog.Write(m))));

            foreach (var game in games) RefreshTranslationHealth(game);
            ReplaceGames(games);
            _host.UpdateRuntimeGames(games);

            AppLog.Write(UiStrings.Format("log.scan.done", games.Count));
            var suzerain = games.FirstOrDefault(g =>
                g.Name.Equals("Suzerain", StringComparison.OrdinalIgnoreCase));
            AppLog.Write(suzerain is null
                ? UiStrings.Get("log.scan.suzerain.missing")
                : UiStrings.Format("log.scan.suzerain.found", suzerain.EngineLabel, suzerain.StatusText));

            WriteScanLog(games);
            UpdateStats();
            StartNameResolution();
        }
        catch (Exception ex)
        {
            AppLog.Write(UiStrings.Format("log.scan.fail", ex.Message));
        }
        finally
        {
            _busy = false;
        }
    }

    // 扫描结果同时落盘，便于无界面环境下核对
    private void WriteScanLog(List<GameInfo> games)
    {
        try
        {
            var lines = games.Select(g => $"{g.Name}\t{g.EngineLabel}\t{g.StatusText}");
            File.WriteAllLines(
                Path.Combine(AppContext.BaseDirectory, "franca-scan.log"),
                new[] { $"扫描时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}，共 {games.Count} 款游戏" }.Concat(lines));
        }
        catch
        {
            // 日志写盘失败不影响主流程
        }
    }

    // 后台异步解析翻译名，逐行填充；语言切换时取消旧任务重来
    private void StartNameResolution()
    {
        _secondarySupportCts?.Cancel();
        _namesCts?.Cancel();
        _namesCts = new CancellationTokenSource();
        var ct = _namesCts.Token;
        var snapshot = _games.ToList();
        var targetLanguage = _host.Settings.TargetLanguage;
        var sourceLanguage = _host.Settings.SourceLanguage;

        foreach (var game in snapshot)
        {
            if (game.OfficialSupportLanguage != targetLanguage)
            {
                game.HasOfficialSupport = null;
                game.OfficialSupportLabel = "";
            }
            game.OfficialSupportLanguage = targetLanguage;
        }
        _host.NameService.ApplyCachedNames(snapshot, targetLanguage, sourceLanguage);

        // 解析期间译名列先显示"翻译中…"，逐行替换为结果
        if (targetLanguage != "en")
            foreach (var game in snapshot)
            {
                if (string.IsNullOrEmpty(game.TranslatedName)) game.NameSource = "resolving";
                RefreshRow(game);
            }

        RefreshSecondarySupport();

        _namesTask = Task.Run(async () =>
        {
            try
            {
                await _host.NameService.ResolveAsync(
                    snapshot,
                    targetLanguage,
                    _host.EffectiveEndpoint,
                    _host.Settings.EndpointKeys,
                    game => Dispatcher.Invoke(() => { if (!ct.IsCancellationRequested) RefreshRow(game); }),
                    m => Dispatcher.Invoke(() => AppLog.Write(m)),
                    ct,
                    _host.Settings.CustomEndpoints,
                    sourceLanguage);

            }
            catch (OperationCanceledException)
            {
                // 语言切换或重新扫描导致的取消，忽略
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppLog.Write(UiStrings.Format("log.names.fail", ex.Message)));
            }
        });
    }

    public void StopBackgroundWork()
    {
        _pageLifetime.Cancel();
        _namesCts?.Cancel();
        _secondarySupportCts?.Cancel();
    }

    public void RefreshSecondarySupport()
    {
        _secondarySupportCts?.Cancel();
        _secondarySupportCts = new CancellationTokenSource();
        var ct = _secondarySupportCts.Token;
        var language = _host.Settings.SecondaryLanguage;
        var games = _games.ToList();
        foreach (var game in games)
        {
            if (game.SecondaryOfficialSupportLanguage != language)
                game.HasSecondaryOfficialSupport = null;
            game.SecondaryOfficialSupportLanguage = language;
            RefreshRow(game);
        }
        _secondarySupportTask = Task.CompletedTask;
        if (string.IsNullOrEmpty(language)) return;
        _secondarySupportTask = Task.Run(async () =>
        {
            try
            {
                await _host.NameService.ResolveSecondarySupportAsync(games, language, (game, support) =>
                    Dispatcher.Invoke(() =>
                    {
                        if (ct.IsCancellationRequested || _host.Settings.SecondaryLanguage != language) return;
                        game.HasSecondaryOfficialSupport = support;
                        game.SecondaryOfficialSupportLanguage = language;
                        RefreshRow(game);
                    }), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(() => AppLog.Write(UiStrings.Format("log.names.fail", ex.Message)));
            }
        });
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e) => await ScanAsync();

    private void UpdateRefreshSupportButton()
    {
        var label = UiStrings.Get("btn.refreshsupport");
        RefreshSupportButton.Content = _refreshingSupport
            ? $"{SupportSpinner[_supportSpinnerFrame]} {label}" : label;
        RefreshSupportButton.IsEnabled = !_refreshingSupport;
    }

    private async void OnRefreshSupportClick(object sender, RoutedEventArgs e) => await RefreshOfficialSupportAsync();

    internal async Task RefreshOfficialSupportAsync()
    {
        if (_refreshingSupport || _pageLifetime.IsCancellationRequested) return;
        _refreshingSupport = true;
        _supportSpinnerFrame = 0;
        UpdateRefreshSupportButton();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            _supportSpinnerFrame = (_supportSpinnerFrame + 1) % SupportSpinner.Length;
            UpdateRefreshSupportButton();
        };
        timer.Start();
        var cooldown = Task.Delay(TimeSpan.FromSeconds(5), _pageLifetime.Token);
        try
        {
            // 先停止并等旧任务退出，再失效缓存，避免旧响应把失效记录重新填回。
            _namesCts?.Cancel();
            _secondarySupportCts?.Cancel();
            await Task.WhenAll(_namesTask, _secondarySupportTask);
            _pageLifetime.Token.ThrowIfCancellationRequested();
            _host.NameService.InvalidateOfficialSupport();
            AppLog.Write(UiStrings.Get("log.support.refresh"));
            if (_games.Count > 0)
            {
                var games = _games.ToList();
                var language = _host.Settings.TargetLanguage;
                _namesCts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetime.Token);
                var ct = _namesCts.Token;
                _namesTask = Task.Run(() => _host.NameService.ResolveOfficialSupportAsync(games, language,
                    (game, support) => Dispatcher.Invoke(() =>
                    {
                        if (ct.IsCancellationRequested || _host.Settings.TargetLanguage != language) return;
                        game.HasOfficialSupport = support;
                        game.OfficialSupportLanguage = language;
                        game.OfficialSupportLabel = support == true
                            ? UiStrings.FormatForLanguage(language, "game.officialsupport", AppSettings.FindLanguage(language).Display) : "";
                        RefreshRow(game);
                    }), ct));
                RefreshSecondarySupport();
                await Task.WhenAll(_namesTask, _secondarySupportTask);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write(UiStrings.Format("log.names.fail", ex.Message));
        }
        finally
        {
            try { await cooldown; }
            catch (OperationCanceledException) { }
            timer.Stop();
            _refreshingSupport = false;
            UpdateRefreshSupportButton();
        }
    }

    private void OnSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressLangEvent) return;
        _host.OnLanguageChanged(LanguageCombo.SelectedValue as string ?? _host.Settings.TargetLanguage);
    }

    private async void OnRepairConfigClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameInfo game } || _busy || !game.CanRepairTranslationConfig)
            return;

        _busy = true;
        game.IsToggling = true;
        RefreshRow(game);
        try
        {
            var (configPath, report) = await _host.GameTranslation.RepairAsync(game);
            AppLog.Write(report.IsHealthy
                ? $"{game.Name}：翻译配置修复并复检通过。配置：{configPath}"
                : $"{game.Name}：修复后仍有问题：{report.Summary}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"{game.Name}：修复翻译配置失败：{ex.Message}");
        }
        finally
        {
            game.IsToggling = false;
            RefreshTranslationHealth(game);
            RefreshRow(game);
            _busy = false;
        }
    }

    private void RefreshTranslationHealth(GameInfo game)
    {
        if (!game.IsReady || !game.Engine.IsUnity())
        {
            game.TranslationHealthText = "";
            game.TranslationHealthDetail = "";
            game.NeedsTranslationRepair = false;
            return;
        }

        try
        {
            var report = _host.GameTranslation.Inspect(game);
            game.TranslationHealthText = UiStrings.Get(report.IsHealthy ? "health.ok" : "health.problem");
            game.NeedsTranslationRepair = !report.IsHealthy;
            game.TranslationHealthDetail = report.Summary;
        }
        catch (Exception ex)
        {
            game.TranslationHealthText = UiStrings.Get("health.failed");
            game.NeedsTranslationRepair = true;
            game.TranslationHealthDetail = ex.Message;
        }
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSourceEvent) return;
        _host.OnSourceLanguageChanged(SourceLanguageCombo.SelectedValue as string ?? _host.Settings.SourceLanguage);
    }

    // 开关三态：开=已启用 → 关（可逆停用，重命名 winhttp.dll）；关+已装环境 → 直接改回文件名；
    // 关+未装 → 走 EnableAsync 下载安装流程；非 Unity 引擎开关禁用
    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInfo game } || _busy || !game.CanUninstall)
            return;
        var dialog = new UninstallConfirmDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        _busy = true;
        game.IsToggling = true;
        game.SetInstallError(null);
        RefreshRow(game);
        try
        {
            await _host.GameTranslation.UninstallAsync(game, new Progress<string>(AppLog.Write));
        }
        catch (Exception ex)
        {
            AppLog.Write(UiStrings.Format("log.enable.fail", ex.Message));
            game.SetInstallError(ex.Message);
        }
        finally
        {
            game.IsToggling = false;
            game.IsReady = TranslationInstaller.IsReady(game.Path);
            RefreshTranslationHealth(game);
            _busy = false;
            RefreshRow(game);
        }
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GameInfo game }) await ToggleGameAsync(game);
    }

    private async void OnGamePrimaryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInfo game }) return;
        if (game.IsActive) OnLaunchClick(sender, e);
        else await ToggleGameAsync(game);
    }

    private async Task ToggleGameAsync(GameInfo game)
    {
        if (_busy || !game.CanToggle) return;

        _busy = true;
        game.IsToggling = true;
        game.SetInstallError(null); // 新一次操作清除上次的行内错误
        RefreshRow(game);
        try
        {
            await _host.GameTranslation.ToggleAsync(game, new Progress<string>(AppLog.Write));
        }
        catch (Exception ex)
        {
            AppLog.Write(UiStrings.Format("log.enable.fail", ex.Message));
            game.SetInstallError(ex.Message); // 失败落到行内可见状态，不只进日志
        }
        finally
        {
            game.IsToggling = false;
            game.IsReady = TranslationInstaller.IsReady(game.Path);
            RefreshTranslationHealth(game);
            _busy = false;
            RefreshRow(game);
        }
    }

    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInfo game }) return;
        if (_host.GameTranslation.LaunchGame(game))
        {
            _host.Settings.LastLaunchedAppId = game.AppId;
            _host.Settings.Save();
            UpdateStats();
        }
    }

    private void RefreshRow(GameInfo game)
    {
        var index = _games.IndexOf(game);
        if (index < 0)
            return;
        // 同引用 Replace 不保证 DataGrid 重算已实现行的绑定（换来换去会残留旧语言文本），
        // 先移除再插回强制重建行；重建会丢选中，选中过的行恢复选中以免详情区收起
        var wasSelected = ReferenceEquals(GameGrid.SelectedItem, game);
        _games.RemoveAt(index);
        _games.Insert(index, game);
        if (wasSelected)
            GameGrid.SelectedItem = game;
        UpdateStats();
    }
}
