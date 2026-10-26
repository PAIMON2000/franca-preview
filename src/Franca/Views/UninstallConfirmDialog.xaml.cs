using System.Windows;
using Franca.Services;

namespace Franca.Views;

// 卸载确认：说明"从 Franca 自己的备份还原游戏文件并清除翻译产物"，确认后才执行
public partial class UninstallConfirmDialog : Window
{
    public UninstallConfirmDialog()
    {
        InitializeComponent();
        Title = UiStrings.Get("uninstall.title");
        MessageText.Text = UiStrings.Get("uninstall.message");
        ConfirmButton.Content = UiStrings.Get("uninstall.confirm");
        CancelButton.Content = UiStrings.Get("dlg.cancel");
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
