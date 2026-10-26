using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Franca.Services;
using Franca.ViewModels;

namespace Franca.Views;

// 翻译端点页：四组卡片（含自动端点卡与"+"卡）、测速、主用/备用、禁用、Key 管理、自定义端点
public partial class EndpointsPage : UserControl, IDisposable
{
    private readonly ObservableCollection<EndpointCard> _freeCards = new();
    private readonly ObservableCollection<EndpointCard> _byokCards = new();
    private readonly ObservableCollection<EndpointCard> _offlineCards = new();
    private readonly ObservableCollection<EndpointCard> _llmCards = new();
    // 专属面板承载的大模型端点：不出现在通用卡片流里
    private static readonly string[] LlmPanelIds =
    {
        EndpointConfigurationService.DeepSeekId,
        EndpointConfigurationService.KimiId,
        EndpointConfigurationService.OpenAiId,
    };
    private MainWindow _host = null!;
    private bool _testing;
    private readonly Dictionary<string, CancellationTokenSource> _panelTestCts = new();

    public EndpointsPage()
    {
        InitializeComponent();
    }

    public void Init(MainWindow host)
    {
        _host = host;
        BuildEndpointCards();
        FreeCards.ItemsSource = _freeCards;
        ByokCards.ItemsSource = _byokCards;
        OfflineCards.ItemsSource = _offlineCards;
        var llmView = new ListCollectionView(_llmCards);
        llmView.Filter = item => item is EndpointCard card && !LlmPanelIds.Contains(card.Id)
            && (!card.IsAutoCard || card.IsPrimary);
        LlmCards.ItemsSource = llmView;
        InitLlmPanel(DeepSeekPanel, DeepSeekKeyBox, EndpointConfigurationService.DeepSeekId);
        InitLlmPanel(KimiPanel, KimiKeyBox, EndpointConfigurationService.KimiId);
        InitLlmPanel(OpenAiPanel, OpenAiKeyBox, EndpointConfigurationService.OpenAiId);

        // 中继状态行定时刷新（3s）
        _relayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _relayTimer.Tick += (_, _) => RefreshRelayStatus();
        _relayTimer.Start();
        RefreshRelayStatus();
    }

    private System.Windows.Threading.DispatcherTimer? _relayTimer;

    // 专属面板绑定对应卡片与本机已存 Key
    private void InitLlmPanel(Border panel, PasswordBox keyBox, string endpointId)
    {
        panel.DataContext = _llmCards.First(c => c.Id == endpointId);
        keyBox.Password = _host.Settings.EndpointKeys.TryGetValue(endpointId, out var keys)
            && keys.TryGetValue("ApiKey", out var key) ? key : "";
    }

    public void Dispose()
    {
        foreach (var cts in _panelTestCts.Values)
            cts.Cancel();
        _relayTimer?.Stop();
        _relayTimer = null;
    }

    private void RefreshRelayStatus()
    {
        var state = _host.Relay.IsRunning ? UiStrings.Get("relay.running") : UiStrings.Get("relay.stopped");
        RelayStatusText.Text = UiStrings.Format("relay.line",
            state,
            _host.Relay.IsRunning ? _host.Relay.Port.ToString() : "-",
            _host.Relay.ServedCount,
            (_host.Relay.HitRate * 100).ToString("0"));
        var selected = AllCards().FirstOrDefault(c => c.Id == _host.Settings.Endpoint)?.DisplayName
            ?? MainWindow.DisplayNameOf(_host.Settings.Endpoint);
        CurrentServiceText.Text = UiStrings.Get("ui.current") + " · " + selected;
    }

    public void ApplyUiLanguage()
    {
        EndpointsTitle.Text = UiStrings.Get("endpoints.title");
        TestAllButton.Content = UiStrings.Get("btn.testall");
        KeyLocalNote.Text = UiStrings.Get("ui.apiNote");
        AutoFreeButton.Content = UiStrings.Get("endpoint.auto");
        FreeAdvanced.Header = UiStrings.Get("ui.moreServices");
        SaveDeepSeekButton.Content = UiStrings.Get("key.save");
        TestDeepSeekButton.Content = UiStrings.Get("test.verify");
        UseDeepSeekButton.Content = UiStrings.Get("badge.primary");
        DeleteDeepSeekButton.Content = UiStrings.Get("ui.deleteKey");
        DeepSeekPlatformButton.Content = UiStrings.Get("ui.platform");
        KimiKeyNote.Text = UiStrings.Get("ui.apiNote");
        SaveKimiButton.Content = UiStrings.Get("key.save");
        TestKimiButton.Content = UiStrings.Get("test.verify");
        UseKimiButton.Content = UiStrings.Get("badge.primary");
        DeleteKimiButton.Content = UiStrings.Get("ui.deleteKey");
        KimiPlatformCnButton.Content = UiStrings.Get("platform.kimi.cn");
        KimiPlatformIntlButton.Content = UiStrings.Get("platform.kimi.intl");
        OpenAiKeyNote.Text = UiStrings.Get("ui.apiNote");
        SaveOpenAiButton.Content = UiStrings.Get("key.save");
        TestOpenAiButton.Content = UiStrings.Get("test.verify");
        UseOpenAiButton.Content = UiStrings.Get("badge.primary");
        DeleteOpenAiButton.Content = UiStrings.Get("ui.deleteKey");
        OpenAiPlatformButton.Content = UiStrings.Get("platform.openai");
        FreeSectionLabel.Text = UiStrings.Get("section.free");
        ByokSectionLabel.Text = UiStrings.Get("section.byok");
        LlmSectionLabel.Text = UiStrings.Get("section.llm");
        OfflineSectionLabel.Text = UiStrings.Get("section.offline");
        RefreshRelayStatus();
    }

    public Task StartupAsync() => TestAllEndpointsAsync();

    // 状态条摘要：主用/备用端点最近一次测速延迟
    public string? GetLatencyText(string endpointId)
    {
        var card = AllCards().FirstOrDefault(c => c.Id == endpointId);
        return card is { LatencyMs: not null } && card.Status is EndpointStatus.Ok or EndpointStatus.Slow
            ? $"✓{card.LatencyMs}ms"
            : null;
    }

    public void RefreshAllCards()
    {
        foreach (var card in AllCards())
            card.RefreshLocalized();
    }

    public void RefreshTheme()
    {
        foreach (var card in AllCards())
            card.RefreshTheme();
    }

    // ---- 卡片构造 ----

    private void BuildEndpointCards()
    {
        foreach (var def in EndpointCatalog.All)
        {
            // 专属面板端点始终建卡（面板 DataContext 依赖）；其余内置模型仅保留已有配置/主备选择，避免升级丢入口。
            if (def.Category == EndpointCategory.Llm && !LlmPanelIds.Contains(def.Id)
                && !_host.Settings.EndpointKeys.ContainsKey(def.Id)
                && def.Id != _host.Settings.Endpoint && def.Id != _host.Settings.FallbackEndpoint)
                continue;
            var keyFields = def.KeyFields.Select(f => new KeyFieldValue
            {
                Label = f.Label,
                FieldId = f.FieldId,
                Value = _host.Settings.EndpointKeys.TryGetValue(def.Id, out var saved)
                    && saved.TryGetValue(f.FieldId, out var value) ? value : f.DefaultValue ?? ""
            }).ToArray();

            var card = new EndpointCard
            {
                Id = def.Id,
                DisplayNameKey = def.DisplayNameKey,
                Category = def.Category,
                IsOffline = def.Category == EndpointCategory.Offline,
                NeedsKey = def.Category is EndpointCategory.Byok or EndpointCategory.Llm,
                NoteKey = def.NoteKey,
                KeyFields = keyFields,
                IsPrimary = def.Id == _host.Settings.Endpoint,
                IsFallback = def.Id == _host.Settings.FallbackEndpoint,
                IsEnabledByUser = !_host.Settings.DisabledEndpoints.Contains(def.Id),
            };
            if (card.NeedsKey && !card.HasAllKeys)
                card.Status = EndpointStatus.NoKey;

            GroupOf(def.Category).Add(card);
        }

        // 每组：自动端点卡（组首）+ 自定义端点卡 + "+"卡（组尾）
        foreach (EndpointCategory category in Enum.GetValues(typeof(EndpointCategory)))
        {
            var group = GroupOf(category);
            var autoId = category switch
            {
                EndpointCategory.Free => "AutoFree",
                EndpointCategory.Byok => "AutoByok",
                EndpointCategory.Llm => "AutoLlm",
                _ => "AutoOffline"
            };
            group.Insert(0, new EndpointCard
            {
                Id = autoId,
                DisplayNameKey = "endpoint.auto",
                IsAutoCard = true,
                Category = category,
                IsOffline = false,
                NeedsKey = false,
                NoteKey = category is EndpointCategory.Offline ? "note.auto.na" : "note.auto",
                IsPrimary = autoId == _host.Settings.Endpoint,
                IsEnabledByUser = category is not EndpointCategory.Offline
                    && !_host.Settings.DisabledEndpoints.Contains(autoId),
            });


            foreach (var custom in _host.Settings.CustomEndpoints.Where(d => GroupOf(d.Group) == category))
                group.Add(new EndpointCard
                {
                    Id = custom.Id,
                    DisplayNameKey = "",
                    CustomDef = custom,
                    Category = category,
                    IsOffline = false,
                    NeedsKey = false,
                    IsPrimary = custom.Id == _host.Settings.Endpoint,
                    IsEnabledByUser = !_host.Settings.DisabledEndpoints.Contains(custom.Id),
                });

            group.Add(new EndpointCard
            {
                Id = "Add" + category,
                DisplayNameKey = "endpoint.add",
                IsAddCard = true,
                Category = category,
                IsOffline = false,
                NeedsKey = false,
            });
        }
    }

    private ObservableCollection<EndpointCard> GroupOf(EndpointCategory category) => category switch
    {
        EndpointCategory.Free => _freeCards,
        EndpointCategory.Byok => _byokCards,
        EndpointCategory.Llm => _llmCards,
        _ => _offlineCards
    };

    private static EndpointCategory GroupOf(string group) => group switch
    {
        "Byok" => EndpointCategory.Byok,
        "Llm" => EndpointCategory.Llm,
        "Offline" => EndpointCategory.Offline,
        _ => EndpointCategory.Free
    };

    private IEnumerable<EndpointCard> AllCards() =>
        _freeCards.Concat(_byokCards).Concat(_llmCards).Concat(_offlineCards);

    private void UpdateEndpointBadges()
    {
        foreach (var card in AllCards())
        {
            card.IsPrimary = card.Id == _host.Settings.Endpoint;
            card.IsFallback = card.Id == _host.Settings.FallbackEndpoint;
        }
        if (LlmCards.ItemsSource is System.ComponentModel.ICollectionView view) view.Refresh();
    }

    // ---- 测速 ----

    private static string DescribeOutcome(TestOutcome outcome) => outcome.Status switch
    {
        EndpointStatus.Ok or EndpointStatus.Slow => UiStrings.Format("outcome.ok", outcome.LatencyMs, outcome.Detail),
        EndpointStatus.Limited => UiStrings.Format("outcome.limited", outcome.Detail),
        EndpointStatus.Timeout => UiStrings.Format("outcome.timeout", outcome.Detail),
        EndpointStatus.Failed => UiStrings.Format("outcome.failed", outcome.Detail),
        _ => outcome.Detail
    };

    private Task<TestOutcome> RunTestAsync(EndpointCard card, CancellationToken ct = default)
    {
        if (card.CustomDef is { } custom)
            return custom.Type == "openai"
                ? EndpointTestService.TestCustomOpenAiAsync(custom.BaseUrl, custom.ApiKey, custom.Model,
                    "hello", _host.Settings.SourceLanguage, _host.Settings.TargetLanguage, ct)
                : EndpointTestService.TestCustomGetAsync(custom.BaseUrl, "hello", _host.Settings.SourceLanguage,
                    _host.Settings.TargetLanguage, ct);
        if (card.NeedsKey)
        {
            var keys = card.KeyFields.ToDictionary(f => f.FieldId, f => f.Value);
            return EndpointTestService.TestByokAsync(card.Id, keys, _host.Settings.TargetLanguage, ct,
                _host.Settings.SourceLanguage);
        }
        return EndpointTestService.TestFreeAsync(card.Id, _host.Settings.TargetLanguage, ct,
            _host.Settings.SourceLanguage);
    }

    private async Task<TestOutcome?> TestEndpointAsync(EndpointCard card, CancellationToken ct = default)
    {
        card.Status = EndpointStatus.Testing;
        if (card.NeedsKey && !card.HasAllKeys)
        {
            card.Status = EndpointStatus.NoKey;
            AppLog.Write(UiStrings.Format("log.nokey", card.DisplayName));
            return null;
        }
        var outcome = await RunTestAsync(card, ct);
        card.SetResult(outcome.Status, outcome.LatencyMs);
        AppLog.Write($"{card.DisplayName}: {DescribeOutcome(outcome)}");
        _host.RefreshStatusBar();
        return outcome;
    }

    // 自动端点服务的单发确认探测（不写卡片状态、不刷状态条，避免每 10 秒刷屏）
    internal Task<TestOutcome> ProbeOnceAsync(string endpointId, CancellationToken ct = default)
    {
        var card = AllCards().FirstOrDefault(c => c.Id == endpointId);
        return card is null
            ? Task.FromResult(new TestOutcome(EndpointStatus.Failed, null, "unknown endpoint"))
            : RunTestAsync(card, ct);
    }

    private async Task TestAllEndpointsAsync()
    {
        if (_testing) return;
        _testing = true;
        try
        {
            AppLog.Write(UiStrings.Get("log.testall"));
            await Task.WhenAll(_freeCards
                .Where(c => c.IsEnabledByUser && !c.IsAutoCard && !c.IsAddCard)
                .Select(c => TestEndpointAsync(c)));
            AutoPickFallback();
        }
        finally
        {
            _testing = false;
        }
    }

    // 备用端点自动取最近一次测速中第二快的可用端点（即除主用外最快者）
    private void AutoPickFallback()
    {
        if (_host.Settings.FallbackManual)
            return;
        var best = _freeCards
            .Where(c => c.IsEnabledByUser && !c.IsAutoCard && !c.IsAddCard
                && c.Status is EndpointStatus.Ok or EndpointStatus.Slow && c.Id != _host.Settings.Endpoint
                && EndpointCatalog.Find(c.Id)?.XUnityId is not null)
            .OrderBy(c => c.LatencyMs)
            .FirstOrDefault();
        if (best is not null && best.Id != _host.Settings.FallbackEndpoint)
        {
            _host.Settings.FallbackEndpoint = best.Id;
            _host.Settings.Save();
            AppLog.Write(UiStrings.Format("log.autofallback", best.DisplayName));
        }
        UpdateEndpointBadges();
        _host.RefreshStatusBar();
    }

    private async void OnTestAllClick(object sender, RoutedEventArgs e) => await TestAllEndpointsAsync();

    private async void OnTestEndpointClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EndpointCard card } && !card.IsAddCard && !card.IsAutoCard)
            await TestEndpointAsync(card);
    }

    // ---- 主用/备用 ----

    private void OnSetPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EndpointCard card })
            return;
        if (!card.IsEnabledByUser)
        {
            AppLog.Write(UiStrings.Get("log.disabled.cannot"));
            return;
        }
        if (card.NeedsKey && !card.HasAllKeys)
        {
            DeepSeekStatus.Text = UiStrings.Get("ui.keyRequired");
            return;
        }
        _host.Settings.Endpoint = card.Id;
        _host.Settings.Save();
        // 任何 Free 端点都可设主用：XUnity 内置端点写进游戏运行时；仅测速池/自动/自定义 OpenAI 端点驱动软件内翻译
        AppLog.Write(card.IsOffline
            ? UiStrings.Format("log.primary.offline", card.DisplayName)
            : card.CustomDef?.Type == "get"
                ? UiStrings.Format("log.primary.runtime", card.DisplayName)
                : EndpointCatalog.Find(card.Id)?.XUnityId is not null
                    ? UiStrings.Format("log.primary.runtime", card.DisplayName)
                    : UiStrings.Format("log.primary.apponly", card.DisplayName));
        if (_host.Settings.FallbackEndpoint == card.Id)
        {
            _host.Settings.FallbackManual = false;
            AutoPickFallback();
        }
        UpdateEndpointBadges();
        // 主用变更后清掉来自其他端点的机翻缓存，译名列立刻跟随新主用；状态条同步；自动服务启停
        _host.NotifyPrimaryChanged(card.Id);
        RefreshRelayStatus();
    }

    private void OnSetFallbackClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EndpointCard card })
            return;
        if (!card.IsEnabledByUser)
        {
            AppLog.Write(UiStrings.Get("log.disabled.cannot"));
            return;
        }
        if (card.CustomDef?.Type != "get" && EndpointCatalog.Find(card.Id)?.XUnityId is null)
        {
            AppLog.Write(UiStrings.Format("log.fallback.poolonly", card.DisplayName));
            return;
        }
        if (card.Id == _host.Settings.Endpoint)
        {
            AppLog.Write(UiStrings.Get("log.fallback.same"));
            return;
        }
        _host.Settings.FallbackEndpoint = card.Id;
        _host.Settings.FallbackManual = true;
        _host.Settings.Save();
        AppLog.Write(UiStrings.Format("log.fallback", card.DisplayName));
        UpdateEndpointBadges();
        _host.RefreshStatusBar();
    }

    // ---- 禁用/启用 ----

    private void OnToggleEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EndpointCard card })
            return;
        var disabled = _host.Settings.DisabledEndpoints;
        if (card.IsEnabledByUser)
        {
            disabled.Add(card.Id);
            card.IsEnabledByUser = false;
            // 禁用当前主用 → 回落到第一个未禁用的 XUnity 内置端点（通常即必应）
            if (_host.Settings.Endpoint == card.Id)
            {
                _host.Settings.Endpoint = _freeCards
                    .FirstOrDefault(c => c.Id != card.Id && c.IsEnabledByUser
                        && EndpointCatalog.Find(c.Id)?.XUnityId is not null)
                    ?.Id ?? "BingTranslate";
                AppLog.Write(UiStrings.Get("log.primary.tobing"));
                _host.NotifyPrimaryChanged(_host.Settings.Endpoint);
            }
            // 禁用当前备用 → 重新自动挑选
            if (_host.Settings.FallbackEndpoint == card.Id)
            {
                _host.Settings.FallbackManual = false;
                AutoPickFallback();
            }
        }
        else
        {
            disabled.Remove(card.Id);
            card.IsEnabledByUser = true;
        }
        _host.Settings.Save();
        UpdateEndpointBadges();
        _host.RefreshStatusBar();
    }

    // ---- 自定义端点 ----

    private void OnAddEndpointClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EndpointCard card })
            return;
        CustomEndpointDef? result;
        if (card.Category == EndpointCategory.Llm)
        {
            var dialog = new AgentEndpointDialog { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            result = dialog.Result;
        }
        else
        {
            var dialog = new CustomEndpointDialog { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            result = dialog.Result;
        }
        if (result is null) return;
        result.Group = card.Category.ToString();
        EndpointConfigurationService.AddEndpoint(_host.Settings, result);
        GroupOf(card.Category).Insert(GroupOf(card.Category).Count - 1, new EndpointCard
        {
            Id = result.Id,
            DisplayNameKey = "",
            CustomDef = result,
            Category = card.Category,
            IsOffline = false,
            NeedsKey = false,
        });
    }

    private void OnDeleteCustomClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EndpointCard { CustomDef: { } custom } card })
            return;
        if (MessageBox.Show(Window.GetWindow(this),
                UiStrings.Format("dlg.delete.confirm", custom.Name), "",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _host.Settings.CustomEndpoints.RemoveAll(d => d.Id == custom.Id);
        if (_host.Settings.Endpoint == card.Id)
        {
            _host.Settings.Endpoint = _freeCards
                .FirstOrDefault(c => c.IsEnabledByUser && EndpointCatalog.Find(c.Id)?.XUnityId is not null)
                ?.Id ?? "BingTranslate";
            AppLog.Write(UiStrings.Get("log.primary.tobing"));
        }
        if (_host.Settings.FallbackEndpoint == card.Id)
        {
            _host.Settings.FallbackEndpoint = "GoogleTranslateV2";
            _host.Settings.FallbackManual = false;
        }
        _host.Settings.Save();
        GroupOf(card.Category).Remove(card);
        UpdateEndpointBadges();
        _host.NotifyPrimaryChanged(_host.Settings.Endpoint);
    }


    private void OnSaveKeysClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EndpointCard card })
            return;
        _host.Settings.EndpointKeys[card.Id] = card.KeyFields.ToDictionary(f => f.FieldId, f => f.Value);
        _host.Settings.Save();
        card.Status = card.HasAllKeys ? EndpointStatus.Untested : EndpointStatus.NoKey;
        AppLog.Write(UiStrings.Format("log.keyssaved", card.DisplayName));
    }

    private void OnUseAutoFree(object sender, RoutedEventArgs e) => OnSetPrimaryClick(
        new Button { Tag = _freeCards.First(c => c.Id == "AutoFree") }, e);

    private void ReloadCard(string endpointId)
    {
        var card = _llmCards.First(c => c.Id == endpointId);
        var modelDefault = EndpointCatalog.Find(card.Id)?.KeyFields.FirstOrDefault(f => f.FieldId == "Model")?.DefaultValue ?? "";
        foreach (var field in card.KeyFields)
            field.Value = _host.Settings.EndpointKeys.TryGetValue(card.Id, out var keys)
                && keys.TryGetValue(field.FieldId, out var value) ? value : field.FieldId == "Model" ? modelDefault : "";
        card.IsEnabledByUser = !_host.Settings.DisabledEndpoints.Contains(card.Id);
        card.Status = card.HasAllKeys ? EndpointStatus.Untested : EndpointStatus.NoKey;
        UpdateEndpointBadges();
        _host.RefreshStatusBar();
        RefreshRelayStatus();
    }

    // ---- 大模型专属面板（DeepSeek / Kimi / OpenAI 共用） ----

    private bool SavePanelKey(string endpointId, PasswordBox keyBox, TextBlock status)
    {
        try
        {
            EndpointConfigurationService.SaveLlmKey(_host.Settings, endpointId, keyBox.Password);
            ReloadCard(endpointId);
            status.Text = UiStrings.Get("ui.keySaved");
            return true;
        }
        catch (ArgumentException ex) { status.Text = ex.Message; return false; }
    }

    private void UsePanelKey(string endpointId, PasswordBox keyBox, TextBlock status, object sender, RoutedEventArgs e)
    {
        if (SavePanelKey(endpointId, keyBox, status)) OnSetPrimaryClick(sender, e);
    }

    private async void TestPanelKey(string endpointId, Border panel, PasswordBox keyBox, TextBlock status,
        Button saveButton, Button useButton, Button testButton)
    {
        if (!SavePanelKey(endpointId, keyBox, status)) return;
        using var cancellation = new CancellationTokenSource();
        _panelTestCts[endpointId] = cancellation;
        testButton.IsEnabled = false;
        saveButton.IsEnabled = useButton.IsEnabled = keyBox.IsEnabled = false;
        try
        {
            status.Text = UiStrings.Get("status.testing");
            var card = (EndpointCard)panel.DataContext;
            var outcome = await TestEndpointAsync(card, cancellation.Token);
            status.Text = outcome is null ? card.StatusText : DescribeOutcome(outcome);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _panelTestCts.Remove(endpointId);
            testButton.IsEnabled = true;
            saveButton.IsEnabled = useButton.IsEnabled = keyBox.IsEnabled = true;
        }
    }

    private void DeletePanelKey(string endpointId, PasswordBox keyBox, TextBlock status)
    {
        if (_panelTestCts.TryGetValue(endpointId, out var cts)) cts.Cancel();
        var previous = _host.Settings.Endpoint;
        EndpointConfigurationService.DeleteKey(_host.Settings, endpointId);
        keyBox.Clear();
        ReloadCard(endpointId);
        if (previous != _host.Settings.Endpoint) _host.NotifyPrimaryChanged(_host.Settings.Endpoint);
        status.Text = UiStrings.Get("ui.keyDeleted");
    }

    private void OpenPlatform(string url, TextBlock status)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { status.Text = url; }
    }

    private void OnSaveDeepSeek(object sender, RoutedEventArgs e) =>
        SavePanelKey(EndpointConfigurationService.DeepSeekId, DeepSeekKeyBox, DeepSeekStatus);

    private void OnUseDeepSeek(object sender, RoutedEventArgs e) =>
        UsePanelKey(EndpointConfigurationService.DeepSeekId, DeepSeekKeyBox, DeepSeekStatus, sender, e);

    private void OnTestDeepSeek(object sender, RoutedEventArgs e) =>
        TestPanelKey(EndpointConfigurationService.DeepSeekId, DeepSeekPanel, DeepSeekKeyBox, DeepSeekStatus,
            SaveDeepSeekButton, UseDeepSeekButton, TestDeepSeekButton);

    private void OnDeleteDeepSeek(object sender, RoutedEventArgs e) =>
        DeletePanelKey(EndpointConfigurationService.DeepSeekId, DeepSeekKeyBox, DeepSeekStatus);

    private void OnOpenDeepSeekPlatform(object sender, RoutedEventArgs e) =>
        OpenPlatform(EndpointConfigurationService.DeepSeekPlatform, DeepSeekStatus);

    private void OnSaveKimi(object sender, RoutedEventArgs e) =>
        SavePanelKey(EndpointConfigurationService.KimiId, KimiKeyBox, KimiStatus);

    private void OnUseKimi(object sender, RoutedEventArgs e) =>
        UsePanelKey(EndpointConfigurationService.KimiId, KimiKeyBox, KimiStatus, sender, e);

    private void OnTestKimi(object sender, RoutedEventArgs e) =>
        TestPanelKey(EndpointConfigurationService.KimiId, KimiPanel, KimiKeyBox, KimiStatus,
            SaveKimiButton, UseKimiButton, TestKimiButton);

    private void OnDeleteKimi(object sender, RoutedEventArgs e) =>
        DeletePanelKey(EndpointConfigurationService.KimiId, KimiKeyBox, KimiStatus);

    private void OnOpenKimiPlatform(object sender, RoutedEventArgs e) =>
        OpenPlatform(EndpointConfigurationService.KimiPlatform, KimiStatus);

    private void OnOpenKimiIntlPlatform(object sender, RoutedEventArgs e) =>
        OpenPlatform(EndpointConfigurationService.KimiIntlPlatform, KimiStatus);

    private void OnSaveOpenAi(object sender, RoutedEventArgs e) =>
        SavePanelKey(EndpointConfigurationService.OpenAiId, OpenAiKeyBox, OpenAiStatus);

    private void OnUseOpenAi(object sender, RoutedEventArgs e) =>
        UsePanelKey(EndpointConfigurationService.OpenAiId, OpenAiKeyBox, OpenAiStatus, sender, e);

    private void OnTestOpenAi(object sender, RoutedEventArgs e) =>
        TestPanelKey(EndpointConfigurationService.OpenAiId, OpenAiPanel, OpenAiKeyBox, OpenAiStatus,
            SaveOpenAiButton, UseOpenAiButton, TestOpenAiButton);

    private void OnDeleteOpenAi(object sender, RoutedEventArgs e) =>
        DeletePanelKey(EndpointConfigurationService.OpenAiId, OpenAiKeyBox, OpenAiStatus);

    private void OnOpenOpenAiPlatform(object sender, RoutedEventArgs e) =>
        OpenPlatform(EndpointConfigurationService.OpenAiPlatform, OpenAiStatus);

    // ---- 自动端点服务 ----

    // 组内参与 TLS 测速的候选：(端点 Id, 主机)
    internal List<(string Id, string Host)> GetAutoCandidates(EndpointCategory category)
    {
        if (category == EndpointCategory.Offline)
            return new List<(string, string)>();
        return GroupOf(category)
            .Where(c => c.IsEnabledByUser && !c.IsAutoCard && !c.IsAddCard
                && (category == EndpointCategory.Free || c.CustomDef is not null || c.HasAllKeys))
            .Select(c => (c.Id, EndpointAutoService.HostOf(c.Id, c.CustomDef,
                c.KeyFields.Length > 0 ? c.KeyFields.ToDictionary(f => f.FieldId, f => f.Value) : null)))
            .Where(c => c.Item2 != "")
            .ToList();
    }

    // 自动端点卡显示当前选中的实际端点名
    public void SetAutoCurrentName(string name)
    {
        foreach (var card in AllCards().Where(c => c.IsAutoCard))
            card.AutoCurrentName = name;
    }
}
