using System.Windows;
using System.Windows.Controls;
using Franca.ViewModels;

namespace Franca.Views;

// "+" 新增端点卡与普通卡片走不同模板
public class EndpointCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NormalTemplate { get; set; }
    public DataTemplate? AddTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is EndpointCard { IsAddCard: true } ? AddTemplate : NormalTemplate;
}
