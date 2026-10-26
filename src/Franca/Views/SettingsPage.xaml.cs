using System.IO;
using System.Windows;
using System.Windows.Controls;
using Franca.Services;

namespace Franca.Views;

// 设置页：源语言/目标语言、Key 安全说明、开发者/Agent 指引
public partial class SettingsPage : UserControl
{
    private MainWindow _host = null!;
    private bool _suppressLangEvent;
    private bool _suppressSourceEvent;
    private bool _suppressSecondaryEvent;
    private bool _suppressModeEvent;
    private bool _suppressThemeEvent;
    private sealed record ThemeOption(string Code, string Display, string Background, string Accent);


    public SettingsPage()
    {
        InitializeComponent();
    }

    public void Init(MainWindow host)
    {
        _host = host;
        LanguageApply.Attach(host.LanguageApply);
        LanguageCombo.ItemsSource = AppSettings.Languages;
        LanguageCombo.DisplayMemberPath = nameof(LanguageOption.Display);
        LanguageCombo.SelectedValuePath = nameof(LanguageOption.Code);
        LanguageCombo.SelectedValue = _host.Settings.TargetLanguage;
        SourceLanguageCombo.DisplayMemberPath = nameof(LanguageOption.Display);
        SourceLanguageCombo.SelectedValuePath = nameof(LanguageOption.Code);
        RefreshSourceLanguages();
        SyncSecondaryLanguageCombo(_host.Settings.SecondaryLanguage);
        RefreshModeCombo();
        RefreshThemeCombo();
        NoOptimizeCheck.IsChecked = _host.Settings.NoOptimizeAfterGenerate;
        OverlayCheck.IsChecked = _host.Settings.OverlayEnabled;
        HideNonGameSoftwareCheck.IsChecked = _host.Settings.HideNonGameSoftware;
        AgentText.Text = BuildAgentText();
    }

    public void ApplyUiLanguage()
    {
        SettingsTitle.Text = UiStrings.Get("nav.settings");
        ManageEndpointsButton.Content = UiStrings.Get("nav.endpoints");
        SourceLangLabel.Text = UiStrings.Get("label.sourcelang");
        TargetLangLabel.Text = UiStrings.Get("label.targetlang");
        SecondaryLangLabel.Text = UiStrings.Get("label.secondarylang");
        SecondaryLanguageCombo.ToolTip = UiStrings.Get("settings.secondary.hint");
        SecondaryHint.Text = UiStrings.Get("settings.secondary.hint");
        ModeLabel.Text = UiStrings.Get("label.translationmode");
        ModeHint.Text = UiStrings.Get("settings.mode.hint");
        NoOptimizeCheck.Content = UiStrings.Get("settings.nooptimize");
        HideNonGameSoftwareCheck.Content = UiStrings.Get("settings.hidesoftware");
        HideNonGameSoftwareHint.Text = UiStrings.Get("settings.hidesoftware.hint");
        OverlayCheck.Content = UiStrings.Get("settings.overlay");
        OverlayHint.Text = UiStrings.Get("settings.overlay.hint");
        ThemeLabel.Text = UiStrings.Get("label.theme");
        RefreshThemeCombo();
        KeyLocalNote.Text = UiStrings.Get("note.keylocal");
        AgentTitle.Text = UiStrings.Get("settings.agent.title");
        AgentDesc.Text = UiStrings.Get("settings.agent.desc");
        AgentCopyButton.Content = UiStrings.Get("log.copy");
        RefreshSourceLanguages();
        SyncSecondaryLanguageCombo(_host.Settings.SecondaryLanguage);
        RefreshModeCombo();
    }

    private void RefreshSourceLanguages()
    {
        _suppressSourceEvent = true;
        SourceLanguageCombo.ItemsSource = AppSettings.GetSourceLanguages();
        SourceLanguageCombo.SelectedValue = _host.Settings.SourceLanguage;
        _suppressSourceEvent = false;
    }

    private void OnManageEndpoints(object sender, RoutedEventArgs e) => _host.NavigateTo(1);

    // 语言在别处（游戏库页）切换时同步下拉框，不触发事件
    public void SyncLanguageCombo(string code)
    {
        _suppressLangEvent = true;
        LanguageCombo.SelectedValue = code;
        _suppressLangEvent = false;
    }

    public void SyncSecondaryLanguageCombo(string code)
    {
        _suppressSecondaryEvent = true;
        SecondaryLanguageCombo.DisplayMemberPath = nameof(LanguageOption.Display);
        SecondaryLanguageCombo.SelectedValuePath = nameof(LanguageOption.Code);
        SecondaryLanguageCombo.ItemsSource = AppSettings.GetSecondaryLanguages();
        SecondaryLanguageCombo.SelectedValue = code;
        _suppressSecondaryEvent = false;
    }

    private void OnSecondaryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSecondaryEvent) return;
        _host.OnSecondaryLanguageChanged(SecondaryLanguageCombo.SelectedValue as string ?? "");
    }

    public void SyncSourceLanguageCombo(string code)
    {
        _suppressSourceEvent = true;
        SourceLanguageCombo.SelectedValue = code;
        _suppressSourceEvent = false;
    }

    private void OnSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressLangEvent) return;
        _host.OnLanguageChanged(LanguageCombo.SelectedValue as string ?? _host.Settings.TargetLanguage);
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSourceEvent) return;
        _host.OnSourceLanguageChanged(SourceLanguageCombo.SelectedValue as string ?? _host.Settings.SourceLanguage);
    }

    // 语言切换时重建模式下拉，保持显示文案跟随 UI 语言
    private void RefreshModeCombo()
    {
        _suppressModeEvent = true;
        var selected = _host?.Settings.TranslationMode ?? "speed";
        ModeCombo.ItemsSource = new[]
        {
            new LanguageOption { Display = UiStrings.Get("mode.speed"), Code = "speed" },
            new LanguageOption { Display = UiStrings.Get("mode.quality"), Code = "quality" },
        };
        ModeCombo.DisplayMemberPath = nameof(LanguageOption.Display);
        ModeCombo.SelectedValuePath = nameof(LanguageOption.Code);
        ModeCombo.SelectedValue = selected;
        _suppressModeEvent = false;
    }

    // 语言切换时重建主题下拉，保持显示文案跟随 UI 语言
    private void RefreshThemeCombo()
    {
        _suppressThemeEvent = true;
        var selected = _host?.Settings.Theme ?? "default";
        ThemeCombo.ItemsSource = Themes.ThemeCatalog.All
            .Select(t => new ThemeOption(t.Id, UiStrings.Get(t.DisplayKey), t.Colors["PageBackground"], t.Colors["AccentFill"]))
            .ToArray();
        ThemeCombo.SelectedValuePath = nameof(LanguageOption.Code);
        ThemeCombo.SelectedValue = selected;
        _suppressThemeEvent = false;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressThemeEvent || _host is null) return;
        _host.OnThemeChanged(ThemeCombo.SelectedValue as string ?? _host.Settings.Theme);
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressModeEvent) return;
        _host.OnTranslationModeChanged(ModeCombo.SelectedValue as string ?? _host.Settings.TranslationMode);
    }

    private void OnNoOptimizeClick(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _host is null) return;
        _host.OnNoOptimizeChanged(NoOptimizeCheck.IsChecked == true);
    }

    private void OnHideNonGameSoftwareClick(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _host is null) return;
        _host.OnHideNonGameSoftwareChanged(HideNonGameSoftwareCheck.IsChecked == true);
    }

    private void OnOverlayClick(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _host is null) return;
        _host.OnOverlayEnabledChanged(OverlayCheck.IsChecked == true);
    }

    // 中英双语指引：路径动态拼接，其余为模板
    private static string BuildAgentText()
    {
        var baseDir = AppContext.BaseDirectory;
        var directory = new DirectoryInfo(baseDir);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "architecture.json")))
            directory = directory.Parent;
        var repo = directory?.FullName ?? "(no source checkout / 无源码检出)";
        return
            "[English]\n" +
            $"Franca local path: {baseDir}\n" +
            $"Source checkout (if present): {repo}\n" +
            "Start with AGENTS.md and architecture.json (feature-to-source index). Current status: docs/DEVELOPMENT-PROGRESS.md. " +
            "Docs map: README.md (project overview & build), docs/HANDOFF.md (handoff and troubleshooting), " +
            "lab/*.md (design docs: lab/004 translation package format v1).\n" +
            "For developers / LLM agents: to add an endpoint, edit 3 places - Services/EndpointCatalog.cs (catalog entry), " +
            "Services/TranslationProviders.*.cs (shared protocols), Services/UiStrings.cs (15-language keys). " +
            "Health checks: EndpointTestService.cs. Translation package format: lab/004. " +
            "Point your LLM at these files to extend or add nodes.\n" +
            "\n" +
            "[中文]\n" +
            $"软件本地路径：{baseDir}\n" +
            $"源码仓库（如有检出）：{repo}\n" +
            "先读 AGENTS.md 和 architecture.json（功能源码索引）；当前状态见 docs/DEVELOPMENT-PROGRESS.md。" +
            "文档地图：README.md（说明与构建）、docs/HANDOFF.md（换机与排障）、lab/*.md（设计记录）。\n" +
            "添加端点：Services/EndpointCatalog.cs（目录）、Services/TranslationProviders.*.cs（共享协议）、Services/UiStrings.cs（15 语言 key）。" +
            "健康探测见 EndpointTestService.cs；翻译包格式见 lab/004。按索引定位后局部修改。";
    }

    private void OnAgentCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AgentText.Text);
            AppLog.Write(UiStrings.Get("log.copied"));
        }
        catch
        {
            // 剪贴板被占用时不影响主流程
        }
    }
}
