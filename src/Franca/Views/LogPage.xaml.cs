using System.Windows;
using System.Windows.Controls;
using Franca.Services;

namespace Franca.Views;

// 日志页：全屏日志 + 复制/清空
public partial class LogPage : UserControl
{
    public LogPage()
    {
        InitializeComponent();
    }

    public void Init(MainWindow host)
    {
        LogList.ItemsSource = AppLog.Lines;
        AppLog.LineAdded += OnLineAdded;
    }

    public void ApplyUiLanguage()
    {
        CopyButton.Content = UiStrings.Get("log.copy");
        ClearButton.Content = UiStrings.Get("log.clear");
    }

    private void OnLineAdded(string line)
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, AppLog.Lines));
        }
        catch
        {
            // 剪贴板被占用时不影响主流程
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => AppLog.Clear();
}
