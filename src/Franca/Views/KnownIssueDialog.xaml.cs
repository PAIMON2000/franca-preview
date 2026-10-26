using System.Windows;
using System.Windows.Media;
using Franca.Services;

namespace Franca.Views;

public partial class KnownIssueDialog : Window
{
    private readonly string _language;
    private readonly Func<Task<KnownIssueRepairOutcome>> _repair;

    public KnownIssueDialog(KnownIssueDefinition issue, string gameName, string language, Func<Task<KnownIssueRepairOutcome>> repair)
    {
        InitializeComponent();
        _language = language;
        _repair = repair;

        Title = Text(issue.TitleKey);
        TitleText.Text = Text(issue.TitleKey);
        GameLabel.Text = Text("issue.label.game");
        GameText.Text = gameName;
        EvidenceLabel.Text = Text("issue.label.evidence");
        EvidenceText.Text = Text(issue.SummaryKey);
        ReasonLabel.Text = Text("issue.label.reason");
        ReasonText.Text = Text(issue.ReasonKey);
        RepairLabel.Text = Text("issue.label.repair");
        RepairText.Text = Text(issue.RepairKey);
        RuleIdText.Text = $"ID: {issue.Id}";
        RepairButton.Content = Text("issue.action.repair");
        CloseButton.Content = Text("issue.action.close");
        RepairButton.Visibility = issue.RepairKind == KnownIssueRepairKind.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private string Text(string key) => UiStrings.GetForLanguage(_language, key);

    private async void OnRepairClick(object sender, RoutedEventArgs e)
    {
        RepairButton.IsEnabled = false;
        RepairButton.Content = Text("issue.status.repairing");
        ResultText.Visibility = Visibility.Collapsed;
        try
        {
            var outcome = await _repair();
            ShowResult(outcome.Success, outcome.MessageKey);
        }
        catch
        {
            ShowResult(false, null);
        }
    }

    private void ShowResult(bool success, string? messageKey)
    {
        var statusKey = success ? "issue.status.success" : "issue.status.failed";
        var status = Text(statusKey);
        ResultText.Text = messageKey is null || messageKey == statusKey ? status : $"{status}: {Text(messageKey)}";
        ResultText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(0x3f, 0xb9, 0x50)
            : Color.FromRgb(0xf8, 0x51, 0x49));
        ResultText.Visibility = Visibility.Visible;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
