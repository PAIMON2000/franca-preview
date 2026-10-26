using System.Windows;
using Franca.Services;

namespace Franca.Views;

// 新增自定义端点对话框：名称、类型（OpenAI 兼容 / GET 纯文本）、BaseUrl、ApiKey（可空）、Model（可空）
public partial class CustomEndpointDialog : Window
{
    private readonly string[] _types = { "openai", "get" };

    public CustomEndpointDef? Result { get; private set; }

    public CustomEndpointDialog()
    {
        InitializeComponent();
        Title = UiStrings.Get("dlg.title");
        NameLabel.Text = UiStrings.Get("dlg.name");
        TypeLabel.Text = UiStrings.Get("dlg.type");
        BaseUrlLabel.Text = UiStrings.Get("dlg.baseurl");
        ApiKeyLabel.Text = UiStrings.Get("dlg.apikey");
        ModelLabel.Text = UiStrings.Get("dlg.model");
        SaveButton.Content = UiStrings.Get("dlg.save");
        CancelButton.Content = UiStrings.Get("dlg.cancel");
        TypeBox.ItemsSource = new[] { UiStrings.Get("dlg.type.openai"), UiStrings.Get("dlg.type.get") };
        TypeBox.SelectedIndex = 0;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(BaseUrlBox.Text))
        {
            ErrorText.Text = UiStrings.Get("dlg.required");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Result = new CustomEndpointDef
        {
            Id = "custom:" + Guid.NewGuid().ToString("N")[..8],
            Name = NameBox.Text.Trim(),
            Type = _types[TypeBox.SelectedIndex],
            BaseUrl = BaseUrlBox.Text.Trim().TrimEnd('/'),
            ApiKey = ApiKeyBox.Text.Trim(),
            Model = ModelBox.Text.Trim(),
        };
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
