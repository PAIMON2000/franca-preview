using System.Windows.Controls;
using Franca.Services;

namespace Franca.Views;

// 支持引擎清单页（只读）：数据来自 EngineSupportCatalog，状态由引擎可安装性推导。
public partial class EnginesPage : UserControl
{
    public EnginesPage()
    {
        InitializeComponent();
    }

    public void ApplyUiLanguage()
    {
        TitleLabel.Text = UiStrings.Get("engines.title");
        EnginesList.ItemsSource = EngineSupportCatalog.All;
    }
}
