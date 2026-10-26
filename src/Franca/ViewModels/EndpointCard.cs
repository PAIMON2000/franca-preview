using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Franca.Services;

namespace Franca.ViewModels;

public class KeyFieldValue : INotifyPropertyChanged
{
    public required string Label { get; init; }
    public required string FieldId { get; init; }

    private string _value = "";
    public string Value
    {
        get => _value;
        set { _value = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class EndpointCard : INotifyPropertyChanged
{
    public required string Id { get; init; }

    // UiStrings 的 key，显示名随语言切换
    public required string DisplayNameKey { get; init; }

    // 特殊卡片标记：自动端点卡 / "+"新增端点卡 / 用户自定义端点
    public bool IsAutoCard { get; init; }
    public bool IsAddCard { get; init; }
    public CustomEndpointDef? CustomDef { get; init; }

    public required EndpointCategory Category { get; init; }
    public required bool IsOffline { get; init; }
    public required bool NeedsKey { get; init; }

    // 用户启用/禁用（与测速状态 Status 解耦，禁用时显示优先）
    private bool _isEnabledByUser = true;
    public bool IsEnabledByUser
    {
        get => _isEnabledByUser;
        set
        {
            _isEnabledByUser = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(CardBorderBrush));
            OnPropertyChanged(nameof(DisableActionLabel));
        }
    }

    // 自动端点卡当前选中的实际端点名（EndpointAutoService 更新）
    private string _autoCurrentName = "";
    public string AutoCurrentName
    {
        get => _autoCurrentName;
        set { _autoCurrentName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    public string DisplayName => IsAddCard && Category == EndpointCategory.Llm
        ? UiStrings.Get("ui.agentTitle")
        : CustomDef is not null
        ? CustomDef.Name
        : IsAutoCard
            ? UiStrings.Get("endpoint.auto") + (string.IsNullOrEmpty(AutoCurrentName) ? "" : " · " + AutoCurrentName)
            : UiStrings.Get(DisplayNameKey);

    // UiStrings 的 key，空串表示无备注
    public string NoteKey { get; init; } = "";
    public KeyFieldValue[] KeyFields { get; init; } = Array.Empty<KeyFieldValue>();

    public bool HasAllKeys => KeyFields.Length > 0 && KeyFields.All(f => !string.IsNullOrWhiteSpace(f.Value));

    public long? LatencyMs { get; private set; }

    private EndpointStatus _status = EndpointStatus.Untested;
    public EndpointStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    public void SetResult(EndpointStatus status, long? latencyMs)
    {
        LatencyMs = latencyMs;
        Status = status;
    }

    public string CategoryLabel => Category switch
    {
        EndpointCategory.Free => UiStrings.Get("cat.free"),
        EndpointCategory.Byok => UiStrings.Get("cat.byok"),
        EndpointCategory.Llm => UiStrings.Get("cat.llm"),
        _ => UiStrings.Get("cat.offline")
    };

    public string Note => string.IsNullOrEmpty(NoteKey) ? "" : UiStrings.Get(NoteKey);

    public string StatusText => !IsEnabledByUser
        ? UiStrings.Get("status.disabled")
        : Status switch
        {
            EndpointStatus.Testing => UiStrings.Get("status.testing"),
            EndpointStatus.Ok => $"{LatencyMs} ms",
            EndpointStatus.Slow => $"{LatencyMs} ms",
            EndpointStatus.Limited => UiStrings.Get("status.limited"),
            EndpointStatus.Timeout => UiStrings.Get("status.timeout"),
            EndpointStatus.NoKey => UiStrings.Get("status.nokey"),
            EndpointStatus.Failed => UiStrings.Get("status.failed"),
            _ => UiStrings.Get(IsOffline ? "status.experimental" : "status.untested")
        };

    public Brush StatusColor => !IsEnabledByUser
        ? (Brush)System.Windows.Application.Current.FindResource("FailBrush")
        : Status switch
        {
            EndpointStatus.Ok => (Brush)System.Windows.Application.Current.FindResource("OkBrush"),
            EndpointStatus.Slow => (Brush)System.Windows.Application.Current.FindResource("WarnBrush"),
            EndpointStatus.Limited => (Brush)System.Windows.Application.Current.FindResource("WarnBrush"),
            EndpointStatus.Timeout => (Brush)System.Windows.Application.Current.FindResource("FailBrush"),
            EndpointStatus.Failed => (Brush)System.Windows.Application.Current.FindResource("FailBrush"),
            _ => (Brush)System.Windows.Application.Current.FindResource("TextSecondaryBrush")
        };

    private bool _isPrimary;
    public bool IsPrimary
    {
        get => _isPrimary;
        set { _isPrimary = value; OnPropertyChanged(); OnPropertyChanged(nameof(PrimaryBadge)); OnPropertyChanged(nameof(CardBorderBrush)); }
    }

    private bool _isFallback;
    public bool IsFallback
    {
        get => _isFallback;
        set { _isFallback = value; OnPropertyChanged(); OnPropertyChanged(nameof(FallbackBadge)); }
    }

    public string TestButtonLabel => UiStrings.Get(IsOffline ? "test.none" : NeedsKey ? "test.verify" : "test.run");
    public bool CanTest => !IsOffline && !IsAutoCard && !IsAddCard;

    // 大模型卡片把停用放在主用左侧、验证紧跟主用右侧；其余分类维持原顺序
    public bool IsLlm => Category == EndpointCategory.Llm;
    public bool NotLlm => Category != EndpointCategory.Llm;
    public bool ShowTailTest => NotLlm;

    public string PrimaryBadge => IsPrimary ? UiStrings.Get("badge.primary") : "";
    public string FallbackBadge => IsFallback ? UiStrings.Get("badge.fallback") : "";
    public Brush CardBorderBrush => !IsEnabledByUser
        ? (Brush)System.Windows.Application.Current.FindResource("FailBrush")
        : IsPrimary
            ? (Brush)System.Windows.Application.Current.FindResource("AccentBrush")
            : (Brush)System.Windows.Application.Current.FindResource("BorderBrush");

    public string PrimaryActionLabel => UiStrings.Get("badge.primary");
    public string FallbackActionLabel => UiStrings.Get("badge.fallback");
    public string KeyHeaderLabel => UiStrings.Get("key.header");
    public string SaveKeysLabel => UiStrings.Get("key.save");
    public string DisableActionLabel => UiStrings.Get(IsEnabledByUser ? "btn.disable" : "btn.enable");

    // 自定义端点卡显示删除按钮
    public bool ShowDelete => CustomDef is not null;

    public void RefreshTheme()
    {
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(CardBorderBrush));
    }

    // 语言切换后刷新所有随语言变化的显示属性
    public void RefreshLocalized()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CategoryLabel));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TestButtonLabel));
        OnPropertyChanged(nameof(PrimaryBadge));
        OnPropertyChanged(nameof(FallbackBadge));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(FallbackActionLabel));
        OnPropertyChanged(nameof(KeyHeaderLabel));
        OnPropertyChanged(nameof(SaveKeysLabel));
        OnPropertyChanged(nameof(DisableActionLabel));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
