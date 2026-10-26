using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Franca.Services;

namespace Franca.Views;

// 状态页（只读）：四条关键数据流的静态地图 + 中继计数器驱动的运行时高亮。
// 流程定义与 docs/BLUEPRINT-FLOWS.md 对齐；服务类名属专有名词，不翻译。
public partial class StatusPage : UserControl
{
    private sealed class Node
    {
        public required Border Box { get; init; }
        public required TextBlock Label { get; init; }
        public int FlashTicks;
        public int AlertTicks;
        public bool Active;
    }

    // (节点 id, 文案)：label 以 "key:" 前缀走 UiStrings，否则为原样类名
    private static readonly (string TitleKey, (string Id, string Label)[] Nodes)[] Flows =
    {
        ("status.flow.enable", new[]
        {
            ("page", "key:status.node.page"), ("gts", "GameTranslationService"),
            ("installer", "TranslationInstaller"), ("gamedir", "key:status.node.gamedir"),
        }),
        ("status.flow.request", new[]
        {
            ("req", "key:status.node.request"), ("relay", "RelayServer"),
            ("dict", "key:status.node.dict"), ("pack", "key:status.node.pack"),
            ("online", "key:status.node.online"), ("ret", "key:status.node.return"),
        }),
        ("status.flow.upgrade", new[]
        {
            ("bgup", "key:status.node.bgupgrade"), ("upevent", "UpgradeCompleted"),
            ("sync", "GameTextSyncService"), ("francatxt", "Franca.txt"), ("xunity", "XUnity"),
        }),
        ("status.flow.auto", new[]
        {
            ("timer", "key:status.node.timer"), ("auto", "EndpointAutoService"),
            ("pick", "key:status.node.pick"),
        }),
    };

    private MainWindow? _host;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly System.Windows.Threading.DispatcherTimer _timer =
        new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private long _lastServed = -1, _lastCache, _lastDict, _lastFailures, _lastWrites;
    private string _lastPick = "";

    public StatusPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _host is not null)
            {
                Refresh();
                _timer.Start();
            }
            else _timer.Stop();
        };
    }

    public void Init(MainWindow host)
    {
        _host = host;
        ApplyUiLanguage();
    }

    internal void Stop() => _timer.Stop();

    public void ApplyUiLanguage()
    {
        TitleLabel.Text = UiStrings.Get("ui.statusTitle");
        AdvancedTitle.Text = UiStrings.Get("ui.advanced");
        OpenEndpointsButton.Content = UiStrings.Get("nav.endpoints");
        LegendLabel.Text = UiStrings.Get("status.legend");
        _nodes.Clear();
        FlowsHost.Children.Clear();
        foreach (var (titleKey, nodes) in Flows)
            FlowsHost.Children.Add(BuildFlowCard(titleKey, nodes));
        UpdatePickLabel();
        Refresh();
    }

    private void OnOpenEndpoints(object sender, RoutedEventArgs e) => _host?.NavigateTo(1);

    private UIElement BuildFlowCard(string titleKey, (string Id, string Label)[] nodes)
    {
        var chain = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        for (var i = 0; i < nodes.Length; i++)
        {
            if (i > 0)
                chain.Children.Add(new TextBlock
                {
                    Text = "→",
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0),
                });
            var (id, label) = nodes[i];
            var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var box = new Border
            {
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(2),
                Child = text,
            };
            text.Text = label.StartsWith("key:", StringComparison.Ordinal)
                ? UiStrings.Get(label[4..])
                : label;
            chain.Children.Add(box);
            _nodes[id] = new Node { Box = box, Label = text };
        }
        return new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = UiStrings.Get(titleKey), FontWeight = FontWeights.Bold },
                    chain,
                },
            },
        };
    }

    private void Refresh()
    {
        if (_host is null) return;
        var relay = _host.Relay;
        HealthText.Text = UiStrings.Get(!relay.IsRunning ? "ui.serviceStopped" : relay.InFlight > 0 ? "ui.working" : "ui.serviceReady");
        CountersText.Text = UiStrings.Format("ui.summary", relay.ServedCount, relay.InFlight, relay.Failures);
        var served = relay.ServedCount;
        var cache = relay.CacheHits;
        var dict = relay.DictHits;
        var failures = relay.Failures;
        var writes = _host.TextSync.WriteCount;
        if (_lastServed >= 0) // 首个 tick 只建立基线，不把历史计数当新活动
        {
            if (dict > _lastDict) Flash("dict");
            if (cache > _lastCache) Flash("pack");
            if (failures > _lastFailures) Alert("online");
            if (served > _lastServed) Flash("ret");
            if (writes > _lastWrites)
            {
                Flash("bgup");
                Flash("sync");
                Flash("francatxt");
                Flash("xunity");
            }
        }
        _lastServed = served;
        _lastCache = cache;
        _lastDict = dict;
        _lastFailures = failures;
        _lastWrites = writes;

        SetActive("relay", relay.IsRunning);
        SetActive("online", relay.InFlight > 0);
        UpdatePickLabel();
        ApplyStates();
    }

    private void UpdatePickLabel()
    {
        if (_host is null || !_nodes.TryGetValue("pick", out var pick)) return;
        var name = _host.AutoEndpoints.CurrentPick is { } current
            ? MainWindow.DisplayNameOf(current)
            : "-";
        if (name != _lastPick)
        {
            _lastPick = name;
            pick.Label.Text = UiStrings.Get("status.node.pick") + " " + name;
            if (name != "-") Flash("pick");
        }
    }

    private void SetActive(string id, bool active)
    {
        if (_nodes.TryGetValue(id, out var node)) node.Active = active;
    }

    private void Flash(string id)
    {
        if (_nodes.TryGetValue(id, out var node)) node.FlashTicks = 2;
    }

    private void Alert(string id)
    {
        if (_nodes.TryGetValue(id, out var node)) node.AlertTicks = 2;
    }

    private void ApplyStates()
    {
        var border = (Brush)FindResource("BorderBrush");
        var card = (Brush)FindResource("CardBackgroundBrush");
        var text = (Brush)FindResource("TextPrimaryBrush");
        var ok = (Brush)FindResource("OkBrush");
        var accent = (Brush)FindResource("AccentFillBrush");
        var fail = (Brush)FindResource("FailBrush");
        var onAccent = (Brush)FindResource("OnAccentBrush");
        var onFail = (Brush)FindResource("OnFailBrush");
        foreach (var node in _nodes.Values)
        {
            if (node.AlertTicks > 0)
            {
                node.AlertTicks--;
                node.Box.BorderBrush = fail;
                node.Box.Background = fail;
                node.Label.Foreground = onFail;
            }
            else if (node.FlashTicks > 0)
            {
                node.FlashTicks--;
                node.Box.BorderBrush = accent;
                node.Box.Background = accent;
                node.Label.Foreground = onAccent;
            }
            else if (node.Active)
            {
                node.Box.BorderBrush = ok;
                node.Box.Background = card;
                node.Label.Foreground = text;
            }
            else
            {
                node.Box.BorderBrush = border;
                node.Box.Background = card;
                node.Label.Foreground = text;
            }
        }
    }
}
