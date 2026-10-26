using System.Windows;
using Franca.Services;

namespace Franca.Views;

public partial class AgentEndpointDialog : Window
{
    public CustomEndpointDef? Result { get; private set; }

    public AgentEndpointDialog()
    {
        InitializeComponent();
        Title = UiStrings.Get("ui.agentTitle");
        HintText.Text = UiStrings.Get("ui.agentHint");
        PromptText.Text = EndpointConfigurationService.AgentPrompt;
        CopyButton.Content = UiStrings.Get("ui.copyPrompt");
        ImportButton.Content = UiStrings.Get("ui.import");
        CancelButton.Content = UiStrings.Get("dlg.cancel");
        JsonText.Text = "";
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(PromptText.Text); }
        catch { ErrorText.Text = UiStrings.Get("ui.copyFailed"); }
    }

    internal bool TryImport()
    {
        Result = null;
        try
        {
            Result = EndpointConfigurationService.ParseModel(JsonText.Text);
            ErrorText.Text = "";
            return true;
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; return false; }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (TryImport()) DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
