using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Franca.Desktop;
using Franca.Services;

namespace Franca.Views;

public partial class LanguageApplyBar : UserControl
{
    private LanguageApplyController? _controller;
    public LanguageApplyBar() => InitializeComponent();
    internal void Attach(LanguageApplyController controller) { _controller = controller; Refresh(); }
    internal void Refresh()
    {
        if (_controller is not { } state) return;
        var playKey = state.Pending && !state.Paused ? "language.apply.pause" : "language.apply.play";
        PlayPauseIcon.Text = state.Pending && !state.Paused ? "\uE769" : "\uE768";
        PlayPauseButton.ToolTip = UiStrings.Get(playKey);
        SkipButton.ToolTip = UiStrings.Get("language.apply.skip");
        AutomationProperties.SetName(PlayPauseButton, UiStrings.Get(playKey));
        AutomationProperties.SetName(SkipButton, UiStrings.Get("language.apply.skip"));
        PlayPauseButton.IsEnabled = SkipButton.IsEnabled = state.Pending || !state.Applying;
        CountdownProgress.Value = state.Progress;
        StatusText.Text = state.Pending
            ? UiStrings.Format(state.Paused ? "language.apply.paused" : "language.apply.countdown", state.SecondsRemaining)
            : state.Applying ? UiStrings.Get("language.apply.applying")
            : state.Error is { } error ? UiStrings.Format("language.apply.error", error)
            : state.Result is { } result ? UiStrings.Format("language.apply.result", result.Updated, result.Unchanged, result.Skipped, result.Failed)
            : UiStrings.Get("language.apply.idle");
    }
    private void OnPlayPause(object sender, RoutedEventArgs e) => _controller?.TogglePause();
    private void OnSkip(object sender, RoutedEventArgs e) => _controller?.Skip();
}
