using System.Windows;
using System.Windows.Media;

namespace Franca.Themes;

// 六套配色：Steam / Cyberpunk 2077 / Claude / 黑白工作台 / Persona 5 / Arknights。
// 旧主题 ID 是持久化协议，保留 blue 对应 Persona，避免已有设置失效。
// 切换机制：XAML 里的画刷一律经 DynamicResource 引用，Apply 直接在 App 级资源字典里整只替换
// SolidColorBrush 实例（不能改 Color——资源画刷会被 WPF 冻结），全界面即时跟随。
// 硬编码颜色（如 OverlayWindow）不随主题切换，属于已知边界。
public static class ThemeCatalog
{
    // 键序即 DarkTheme.xaml 的画刷定义顺序（去 Brush 后缀）
    public static readonly string[] ColorKeys =
    {
        "WindowBackground", "NavBackground", "PageBackground", "CardBackground",
        "ControlBackground", "ControlHover", "Border", "TextPrimary", "TextSecondary",
        "Accent", "Ok", "Warn", "Fail",
        "NavSelected", "NavHover", "RowSelected", "RowAlternate", "GridLine", "ScrollThumb",
        "AccentFill", "OnAccent", "OnFail", "ActiveRow", "SwitchOff",
    };

    public sealed record ThemeInfo(string Id, string DisplayKey, IReadOnlyDictionary<string, string> Colors);

    public static readonly ThemeInfo[] All =
    {
        new("default", "theme.default", new Dictionary<string, string>
        {
            ["WindowBackground"] = "#171a21", ["NavBackground"] = "#202732",
            ["PageBackground"] = "#181e27", ["CardBackground"] = "#232c38",
            ["ControlBackground"] = "#30445a", ["ControlHover"] = "#34485c",
            ["Border"] = "#566477", ["TextPrimary"] = "#e7edf4", ["TextSecondary"] = "#b0bdcc",
            ["Accent"] = "#66c0f4", ["Ok"] = "#b4d86a", ["Warn"] = "#f0bf69", ["Fail"] = "#ffa59e",
            ["NavSelected"] = "#30445a", ["NavHover"] = "#293746",
            ["RowSelected"] = "#30445a", ["RowAlternate"] = "#1d2530",
            ["GridLine"] = "#303c4b", ["ScrollThumb"] = "#6b7d92",
            ["AccentFill"] = "#66c0f4", ["OnAccent"] = "#101b26", ["OnFail"] = "#291315",
            ["ActiveRow"] = "#253627", ["SwitchOff"] = "#718096",
        }),
        new("yellow", "theme.yellow", new Dictionary<string, string>
        {
            ["WindowBackground"] = "#090e13", ["NavBackground"] = "#101b23",
            ["PageBackground"] = "#0c141c", ["CardBackground"] = "#14232e",
            ["ControlBackground"] = "#233540", ["ControlHover"] = "#35434b",
            ["Border"] = "#60717a", ["TextPrimary"] = "#edf7f8", ["TextSecondary"] = "#a8c1ca",
            ["Accent"] = "#fcee0a", ["Ok"] = "#65e6b4", ["Warn"] = "#ffd074", ["Fail"] = "#ff8c9f",
            ["NavSelected"] = "#3a3816", ["NavHover"] = "#23323b",
            ["RowSelected"] = "#3a3816", ["RowAlternate"] = "#101c25",
            ["GridLine"] = "#263943", ["ScrollThumb"] = "#71939e",
            ["AccentFill"] = "#fcee0a", ["OnAccent"] = "#111820", ["OnFail"] = "#291019",
            ["ActiveRow"] = "#16382f", ["SwitchOff"] = "#78949f",
        }),
        new("orange", "theme.orange", new Dictionary<string, string>
        {
            ["WindowBackground"] = "#e8e4db", ["NavBackground"] = "#ede9e0",
            ["PageBackground"] = "#faf9f5", ["CardBackground"] = "#ffffff",
            ["ControlBackground"] = "#f0ebe3", ["ControlHover"] = "#e5d9cb",
            ["Border"] = "#948779", ["TextPrimary"] = "#302b27", ["TextSecondary"] = "#665c52",
            ["Accent"] = "#943e25", ["Ok"] = "#2d603e", ["Warn"] = "#784a08", ["Fail"] = "#a52a2a",
            ["NavSelected"] = "#e6d4c4", ["NavHover"] = "#e5dfd5",
            ["RowSelected"] = "#edddcf", ["RowAlternate"] = "#f3f0e9",
            ["GridLine"] = "#e3ddd3", ["ScrollThumb"] = "#968778",
            ["AccentFill"] = "#943e25", ["OnAccent"] = "#ffffff", ["OnFail"] = "#ffffff",
            ["ActiveRow"] = "#e4efdf", ["SwitchOff"] = "#82776c",
        }),
        new("white", "theme.white", new Dictionary<string, string>
        {
            ["WindowBackground"] = "#e7e7e7", ["NavBackground"] = "#f0f0f0",
            ["PageBackground"] = "#ffffff", ["CardBackground"] = "#f7f7f7",
            ["ControlBackground"] = "#ebebeb", ["ControlHover"] = "#dedede",
            ["Border"] = "#898989", ["TextPrimary"] = "#191919", ["TextSecondary"] = "#595959",
            ["Accent"] = "#262626", ["Ok"] = "#28683f", ["Warn"] = "#845606", ["Fail"] = "#b52b38",
            ["NavSelected"] = "#dbdbdb", ["NavHover"] = "#e5e5e5",
            ["RowSelected"] = "#e0e0e0", ["RowAlternate"] = "#f7f7f7",
            ["GridLine"] = "#dedede", ["ScrollThumb"] = "#858585",
            ["AccentFill"] = "#262626", ["OnAccent"] = "#ffffff", ["OnFail"] = "#ffffff",
            ["ActiveRow"] = "#e4efe6", ["SwitchOff"] = "#787878",
        }),
        new("blue", "theme.blue", new Dictionary<string, string>
        {
            ["WindowBackground"] = "#090909", ["NavBackground"] = "#111111",
            ["PageBackground"] = "#181818", ["CardBackground"] = "#242424",
            ["ControlBackground"] = "#363030", ["ControlHover"] = "#4c383b",
            ["Border"] = "#7d7174", ["TextPrimary"] = "#fafafa", ["TextSecondary"] = "#c1b9bc",
            ["Accent"] = "#ff8295", ["Ok"] = "#89d9ae", ["Warn"] = "#ffd27a", ["Fail"] = "#ff9b8c",
            ["NavSelected"] = "#64152b", ["NavHover"] = "#352126",
            ["RowSelected"] = "#541c2b", ["RowAlternate"] = "#202020",
            ["GridLine"] = "#393033", ["ScrollThumb"] = "#938087",
            ["AccentFill"] = "#dc143c", ["OnAccent"] = "#ffffff", ["OnFail"] = "#290f0b",
            ["ActiveRow"] = "#24382e", ["SwitchOff"] = "#96858a",
        }),
        new("bluewhite", "theme.bluewhite", new Dictionary<string, string>
        {
            ["WindowBackground"] = "#dce4ea", ["NavBackground"] = "#e1e9ef",
            ["PageBackground"] = "#f2f6f8", ["CardBackground"] = "#ffffff",
            ["ControlBackground"] = "#e2edf4", ["ControlHover"] = "#cddfea",
            ["Border"] = "#7c94a4", ["TextPrimary"] = "#172b38", ["TextSecondary"] = "#415968",
            ["Accent"] = "#005d89", ["Ok"] = "#1c6245", ["Warn"] = "#774b00", ["Fail"] = "#a32938",
            ["NavSelected"] = "#bbdbea", ["NavHover"] = "#d0e1eb",
            ["RowSelected"] = "#c9e3ef", ["RowAlternate"] = "#eaf1f5",
            ["GridLine"] = "#d6e2e9", ["ScrollThumb"] = "#758f9f",
            ["AccentFill"] = "#005d89", ["OnAccent"] = "#ffffff", ["OnFail"] = "#ffffff",
            ["ActiveRow"] = "#dfefe7", ["SwitchOff"] = "#687f8d",
        }),
    };

    public static ThemeInfo Find(string id) =>
        All.FirstOrDefault(t => t.Id == id) ?? All[0];

    // 整只替换 App 级画刷实例；未知主题回退默认，缺失画刷直接报错（主题与 DarkTheme.xaml 必须键齐）
    public static void Apply(string id)
    {
        var theme = Find(id);
        var resources = Application.Current.Resources.MergedDictionaries[0];
        foreach (var key in ColorKeys)
            resources[key + "Brush"] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(theme.Colors[key]));
    }
}
