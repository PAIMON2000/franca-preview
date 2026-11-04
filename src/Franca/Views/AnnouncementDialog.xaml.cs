using System.Windows;
using Franca.Services;

namespace Franca.Views;

// 公告查看：纯展示仓库 ANNOUNCEMENT.md 文本，关闭即返回
public partial class AnnouncementDialog : Window
{
    public AnnouncementDialog(string text)
    {
        InitializeComponent();
        Title = UiStrings.Get("announce.button");
        MessageText.Text = text;
        CloseButton.Content = UiStrings.Get("dlg.close");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
