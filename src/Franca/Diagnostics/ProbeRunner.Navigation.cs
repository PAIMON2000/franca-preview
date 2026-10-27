using System.IO;
using System.Net.Http;
using System.Windows;
using Franca.Models;
using Franca.Services;

namespace Franca.Diagnostics;

internal static partial class ProbeRunner
{
    private static async Task RunProbeNavigationAsync(string? screenshotDirectory)
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tNAVIGATION\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition) failures++;
        }

        async Task Snapshot(Window window, string name, int width = 1180, int height = 720)
        {
            var surface = (FrameworkElement)window.Content;
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            surface.UpdateLayout();
            if (screenshotDirectory is null) return;
            Directory.CreateDirectory(screenshotDirectory);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(screenshotDirectory, name + ".png"));
            encoder.Save(output);
        }

        UiStrings.Current = "zh-CN";
        var main = new MainWindow(startServices: false, new AppSettings { TargetLanguage = "zh-CN" });
        Check("startup_direct_library", main.NavList.SelectedIndex == 0 && main.PageHost.Content is Views.GamesPage);
        Check("nav_order_engines_after_status", main.NavList.Items.Count == 7
            && ((NavItem)main.NavList.Items[3]).Label == UiStrings.Get("nav.status")
            && ((NavItem)main.NavList.Items[4]).Label == UiStrings.Get("nav.engines")
            && ((NavItem)main.NavList.Items[5]).Label == UiStrings.Get("nav.log")
            && ((NavItem)main.NavList.Items[6]).Label == UiStrings.Get("nav.acknowledgements"));
        await Snapshot(main, "startup-library");
        Check("release_banner_hidden_initially", main.UpdateBanner.Visibility == Visibility.Collapsed);
        main.ShowAvailableRelease(new AvailableRelease("Franca next", new Version(0, 2),
            new Uri(ReleaseUpdateService.RepositoryUrl + "/releases/tag/v0.2")));
        Check("release_banner_shown", main.UpdateBanner.Visibility == Visibility.Visible
            && main.UpdateMessage.Text.Contains("Franca next")
            && (string)main.UpdateDownloadButton.Content == "下载新版");
        await Snapshot(main, "release-update-banner");
        main.UpdateBanner.Visibility = Visibility.Collapsed;
        var earlyExit = new MainWindow(startServices: false, new AppSettings { TargetLanguage = "zh-CN" });
        earlyExit.NavigateTo(2);
        Check("settings_agent_collapsed", earlyExit.PageHost.Content is Views.SettingsPage settings && !settings.AgentAdvanced.IsExpanded);
        var secondarySettingsPage = (Views.SettingsPage)earlyExit.PageHost.Content;
        var originalSource = earlyExit.Settings.SourceLanguage;
        var originalTarget = earlyExit.Settings.TargetLanguage;
        Check("language_bar_idle_on_both_pages", !earlyExit.LanguageApply.Pending
            && secondarySettingsPage.LanguageApply.PlayPauseButton.IsEnabled);
        earlyExit.OnSecondaryLanguageChanged("zh-TW");
        earlyExit.NavigateTo(0);
        var libraryPage = (Views.GamesPage)earlyExit.PageHost.Content;
        Check("secondary_synced_both_pages", (string?)secondarySettingsPage.SecondaryLanguageCombo.SelectedValue == "zh-TW"
            && (string?)libraryPage.SecondaryLanguageCombo.SelectedValue == "zh-TW");
        Check("secondary_does_not_change_translation_languages", earlyExit.Settings.SourceLanguage == originalSource
            && earlyExit.Settings.TargetLanguage == originalTarget);
        earlyExit.OnSecondaryLanguageChanged("auto");
        Check("secondary_rejects_auto", earlyExit.Settings.SecondaryLanguage == "zh-TW");
        earlyExit.OnSecondaryLanguageChanged("");
        Check("secondary_clear_synced", (string?)secondarySettingsPage.SecondaryLanguageCombo.SelectedValue == ""
            && (string?)libraryPage.SecondaryLanguageCombo.SelectedValue == "");
        Check("secondary_does_not_schedule_configuration", !earlyExit.LanguageApply.Pending);
        earlyExit.OnSourceLanguageChanged("ja");
        Check("source_change_schedules_shared_countdown", earlyExit.LanguageApply.Pending
            && libraryPage.LanguageApply.PlayPauseButton.IsEnabled
            && secondarySettingsPage.LanguageApply.SkipButton.IsEnabled);
        libraryPage.LanguageApply.PlayPauseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("pause_button_shared_across_pages", earlyExit.LanguageApply.Paused
            && secondarySettingsPage.LanguageApply.PlayPauseIcon.Text == "\uE768");
        earlyExit.OnLanguageChanged("fr");
        Check("target_change_resets_and_resumes", earlyExit.LanguageApply.Pending && !earlyExit.LanguageApply.Paused
            && earlyExit.LanguageApply.Progress == 0);
        await Snapshot(earlyExit, "language-apply-library");
        earlyExit.NavigateTo(2);
        await Snapshot(earlyExit, "language-apply-settings");
        secondarySettingsPage.LanguageApply.SkipButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await earlyExit.LanguageApply.Completion;
        Check("skip_button_applies_once_in_diagnostic_sandbox", !earlyExit.LanguageApply.Pending
            && earlyExit.LanguageApply.Result is { Updated: 0, Failed: 0 });
        earlyExit.OnLanguageChanged(originalTarget);
        earlyExit.OnSourceLanguageChanged(originalSource);
        earlyExit.NavigateTo(2);
        var refreshClock = System.Diagnostics.Stopwatch.StartNew();
        var refreshTask = libraryPage.RefreshOfficialSupportAsync();
        var initialSpinner = libraryPage.RefreshSupportButton.Content?.ToString();
        Check("support_refresh_disables_immediately", !libraryPage.RefreshSupportButton.IsEnabled && !refreshTask.IsCompleted
            && libraryPage.RefreshSupportButton.Opacity >= 0.85);
        await Task.Delay(260);
        Check("support_refresh_spinner_advances", libraryPage.RefreshSupportButton.Content?.ToString() != initialSpinner);
        var duplicateRefresh = libraryPage.RefreshOfficialSupportAsync();
        Check("support_refresh_duplicate_ignored", duplicateRefresh.IsCompleted && !refreshTask.IsCompleted);
        libraryPage.ApplyUiLanguage();
        Check("support_refresh_language_change_keeps_busy", !libraryPage.RefreshSupportButton.IsEnabled
            && libraryPage.RefreshSupportButton.Content!.ToString()!.Contains(UiStrings.Get("btn.refreshsupport")));
        earlyExit.NavigateTo(0);
        await Snapshot(earlyExit, "support-refresh-busy");
        earlyExit.NavigateTo(2);
        var remainingBeforeCheck = TimeSpan.FromSeconds(4) - refreshClock.Elapsed;
        if (remainingBeforeCheck > TimeSpan.Zero) await Task.Delay(remainingBeforeCheck);
        Check("support_refresh_still_disabled_before_five_seconds", !libraryPage.RefreshSupportButton.IsEnabled && !refreshTask.IsCompleted);
        await refreshTask;
        Check("support_refresh_restores_after_minimum_five_seconds", refreshClock.Elapsed >= TimeSpan.FromSeconds(5)
            && libraryPage.RefreshSupportButton.IsEnabled
            && libraryPage.RefreshSupportButton.Content!.ToString() == UiStrings.Get("btn.refreshsupport"));
        await Snapshot(earlyExit, "settings");
        main.NavigateTo(6);
        var page = (Views.AcknowledgementsPage)main.PageHost.Content;
        Check("manual_acknowledgements_no_countdown", !page.AutoAdvanceEnabled && page.CountdownText.Visibility == Visibility.Collapsed);
        await Snapshot(main, "embedded-manual");
        main.NavigateTo(3);
        await Snapshot(main, "status");
        Check("status_diagnostics_always_visible", main.PageHost.Content is Views.StatusPage status
            && status.FlowsHost.ActualHeight > 0 && status.HealthText.Text.Length > 0);
        Check("status_page_has_no_engine_list", main.PageHost.Content is Views.StatusPage statusOnly
            && FindVisual<FrameworkElement>(statusOnly, f => f.Name == "EnginesList") is null);
        main.NavigateTo(4);
        var enginesPage = (Views.EnginesPage)main.PageHost.Content;
        await Snapshot(main, "engines");
        Check("engines_page_rendered", enginesPage.EnginesList.Items.Count == EngineSupportCatalog.All.Length
            && enginesPage.EnginesList.ActualHeight > 0);
        Check("engine_catalog_covers_engines", EngineSupportCatalog.All.Length == 12
            && !EngineSupportCatalog.All.Any(e => e.Engine == GameEngine.Unknown)
            && EngineSupportCatalog.All.TakeWhile(e => e.IsSupported).Count()
                == EngineSupportCatalog.All.Count(e => e.IsSupported));
        Check("engine_touhou_supported", EngineSupportCatalog.All.Single(e => e.Engine == GameEngine.Touhou).IsSupported);
        Check("engine_renpy_recognized_only", !EngineSupportCatalog.All.Single(e => e.Engine == GameEngine.RenPy).IsSupported);
        main.NavigateTo(1);
        var endpoints = (Views.EndpointsPage)main.PageHost.Content;
        Check("secondary_groups_collapsed", !endpoints.FreeAdvanced.IsExpanded && !endpoints.ByokAdvanced.IsExpanded
            && !endpoints.OfflineAdvanced.IsExpanded);
        Check("default_llm_cards_only_add", endpoints.LlmCards.ItemsSource.Cast<ViewModels.EndpointCard>().All(c => c.IsAddCard));
        Check("llm_panels_hosted_kimi_openai", endpoints.KimiPanel.DataContext is ViewModels.EndpointCard { Id: "LlmKimi" }
            && endpoints.KimiKeyBox.Password.Length == 0
            && endpoints.OpenAiPanel.DataContext is ViewModels.EndpointCard { Id: "LlmOpenAI" }
            && endpoints.OpenAiKeyBox.Password.Length == 0);
        endpoints.DeepSeekKeyBox.Password = "ui-fixture-key";
        endpoints.SaveDeepSeekButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("key_saved_and_auto_candidate", main.Settings.EndpointKeys.ContainsKey("LlmDeepSeek")
            && endpoints.GetAutoCandidates(EndpointCategory.Llm).Any(c => c.Id == "LlmDeepSeek"));
        endpoints.UseDeepSeekButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        Check("deepseek_primary_click_selects", main.Settings.Endpoint == "LlmDeepSeek"
            && endpoints.DeepSeekPanel.DataContext is ViewModels.EndpointCard { IsPrimary: true });
        foreach (var theme in Themes.ThemeCatalog.All)
        {
            main.OnThemeChanged(theme.Id);
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Check("deepseek_primary_highlight_" + theme.Id,
                Equals(endpoints.UseDeepSeekButton.Background, main.FindResource("AccentFillBrush"))
                && Equals(endpoints.UseDeepSeekButton.Foreground, main.FindResource("OnAccentBrush"))
                && Equals(endpoints.DeepSeekPanel.BorderBrush, main.FindResource("AccentBrush")));
        }
        main.OnThemeChanged("default");
        await Snapshot(main, "deepseek-primary");
        endpoints.FreeAdvanced.IsExpanded = true;
        endpoints.UpdateLayout();
        var bingPrimary = FindVisual<System.Windows.Controls.Button>(endpoints.FreeCards,
            b => b.Name == "PrimaryEndpointButton" && b.Tag is ViewModels.EndpointCard { Id: "BingTranslate" });
        Check("free_primary_button_available", bingPrimary is not null);
        bingPrimary?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        Check("switching_primary_clears_deepseek_highlight", main.Settings.Endpoint == "BingTranslate"
            && Equals(endpoints.UseDeepSeekButton.Background, main.FindResource("ControlBackgroundBrush"))
            && Equals(endpoints.DeepSeekPanel.BorderBrush, main.FindResource("BorderBrush"))
            && bingPrimary is not null && Equals(bingPrimary.Background, main.FindResource("AccentFillBrush")));
        endpoints.FreeAdvanced.IsExpanded = false;
        endpoints.DeleteDeepSeekButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("key_deleted_from_ui_and_candidates", !main.Settings.EndpointKeys.ContainsKey("LlmDeepSeek")
            && endpoints.DeepSeekKeyBox.Password.Length == 0
            && !endpoints.GetAutoCandidates(EndpointCategory.Llm).Any(c => c.Id == "LlmDeepSeek"));
        endpoints.KimiKeyBox.Password = "ui-fixture-kimi";
        endpoints.SaveKimiButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("kimi_key_saved_with_catalog_model", main.Settings.EndpointKeys.TryGetValue("LlmKimi", out var kimiKeys)
            && kimiKeys["ApiKey"] == "ui-fixture-kimi" && kimiKeys["Model"] == "moonshot-v1-8k"
            && endpoints.GetAutoCandidates(EndpointCategory.Llm).Any(c => c.Id == "LlmKimi"));
        endpoints.UseKimiButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        Check("kimi_primary_click_selects", main.Settings.Endpoint == "LlmKimi"
            && endpoints.KimiPanel.DataContext is ViewModels.EndpointCard { IsPrimary: true });
        endpoints.DeleteKimiButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("kimi_key_deleted_falls_back", !main.Settings.EndpointKeys.ContainsKey("LlmKimi")
            && main.Settings.Endpoint == "AutoFree");
        var agentDialog = new Views.AgentEndpointDialog();
        agentDialog.JsonText.Text = "{broken-json}";
        Check("agent_dialog_rejects_invalid", !agentDialog.TryImport() && agentDialog.Result is null && agentDialog.ErrorText.Text.Length > 0);
        agentDialog.JsonText.Text = "{\"name\":\"Fixture model\",\"baseUrl\":\"http://127.0.0.1:9/v1\",\"model\":\"fixture\",\"apiKey\":\"\"}";
        Check("agent_dialog_imports_valid", agentDialog.TryImport() && agentDialog.Result?.Group == "Llm");
        await Snapshot(agentDialog, "agent-model-config", 700, 600);
        agentDialog.Close();
        var legacySettings = new AppSettings { TargetLanguage = "zh-CN", Endpoint = "LlmKimi" };
        legacySettings.EndpointKeys["LlmKimi"] = new() { ["ApiKey"] = "legacy-fixture", ["Model"] = "fixture-model" };
        var legacyHost = new MainWindow(startServices: false, legacySettings);
        legacyHost.NavigateTo(1);
        var legacyPage = (Views.EndpointsPage)legacyHost.PageHost.Content;
        Check("configured_legacy_model_preserved", legacyPage.KimiPanel.DataContext is ViewModels.EndpointCard { Id: "LlmKimi", IsPrimary: true }
            && legacyPage.KimiKeyBox.Password == "legacy-fixture");
        legacyHost.Close();
        main.NavigateTo(0);
        main.NavigateTo(6);
        await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check("manual_reentry_stays", main.NavList.SelectedIndex == 6 && !page.AutoAdvanceEnabled);
        // 六套主题检查真实页面；仅合成游戏行，不扫描、不启停游戏、不访问端点。
        main.NavigateTo(0);
        var games = (Views.GamesPage)main.PageHost.Content;
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "franca-theme-" + Guid.NewGuid().ToString("N"));
        main.Settings.LastLaunchedAppId = 1207650;
        games.ReplaceGames(new[]
        {
            new GameInfo { Name = "Suzerain", TranslatedName = "宗主国", NameSource = "machine",
                Path = Path.Combine(fixtureRoot, "suzerain"), AppId = 1207650, Engine = GameEngine.UnityIL2CPP, IsReady = true },
            new GameInfo { Name = "Stardew Valley", TranslatedName = "星露谷物语", NameSource = "official",
                Path = Path.Combine(fixtureRoot, "stardew"), Engine = GameEngine.UnityMono },
            new GameInfo { Name = "BALL x PIT", Path = Path.Combine(fixtureRoot, "ball"), Engine = GameEngine.UnityIL2CPP },
            new GameInfo { Name = "Persona 5 Royal", Path = Path.Combine(fixtureRoot, "persona"), Engine = GameEngine.Unknown },
        });
        Check("recent_game_restored", games.RecentGameButton.Visibility == Visibility.Visible
            && games.RecentGameButton.Tag is GameInfo { AppId: 1207650 });
        games.GameGrid.SelectedIndex = 0;
        await Snapshot(main, "row-details-expanded");
        var selectedRow = (System.Windows.Controls.DataGridRow)games.GameGrid.ItemContainerGenerator.ContainerFromIndex(0);
        static T? FindVisual<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject
        {
            if (root is T match && (predicate is null || predicate(match))) return match;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                if (FindVisual<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i), predicate) is { } found) return found;
            return null;
        }
        static bool PreviewClick(UIElement target, System.Windows.Input.MouseButton button = System.Windows.Input.MouseButton.Left)
        {
            var args = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, button)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent };
            target.RaiseEvent(args);
            return args.Handled;
        }
        Check("row_details_initially_expanded", selectedRow.DetailsVisibility == Visibility.Visible);
        var actionButton = FindVisual<System.Windows.Controls.Primitives.ButtonBase>(selectedRow)!;
        PreviewClick(actionButton);
        Check("row_action_does_not_collapse", games.GameGrid.SelectedIndex == 0);
        var details = FindVisual<System.Windows.Controls.Primitives.DataGridDetailsPresenter>(selectedRow)!;
        PreviewClick(details);
        Check("row_details_content_does_not_collapse", games.GameGrid.SelectedIndex == 0);
        PreviewClick(selectedRow, System.Windows.Input.MouseButton.Right);
        Check("row_right_click_does_not_collapse", games.GameGrid.SelectedIndex == 0);
        Check("row_second_click_collapses", PreviewClick(selectedRow) && games.GameGrid.SelectedIndex == -1
            && selectedRow.DetailsVisibility == Visibility.Collapsed);
        Check("row_unselected_click_allows_default_selection", !PreviewClick(selectedRow));
        games.GameGrid.SelectedIndex = 1;
        games.GameGrid.UpdateLayout();
        var nextRow = (System.Windows.Controls.DataGridRow)games.GameGrid.ItemContainerGenerator.ContainerFromIndex(1);
        Check("row_switch_expands_new_selection", nextRow.DetailsVisibility == Visibility.Visible);
        Check("row_new_selection_can_collapse", PreviewClick(nextRow) && games.GameGrid.SelectedIndex == -1);
        await Snapshot(main, "row-details-collapsed");
        foreach (var theme in Themes.ThemeCatalog.All)
        {
            main.OnThemeChanged(theme.Id);
            main.NavigateTo(3);
            await Snapshot(main, "theme-" + theme.Id);
            main.NavigateTo(2);
            var settingsPage = (Views.SettingsPage)main.PageHost.Content;
            settingsPage.ApplyUiLanguage(); // 模拟已更新的主题选择，同时核对本地化重建后的选中值。
            await Snapshot(main, "theme-" + theme.Id + "-settings");
            Check("theme_selection_" + theme.Id, Equals(settingsPage.ThemeCombo.SelectedValue, theme.Id));
            main.NavigateTo(1);
            await Snapshot(main, "theme-" + theme.Id + "-endpoints");
            main.NavigateTo(0);
            games.GameGrid.SelectedIndex = 0;
            await Snapshot(main, "theme-" + theme.Id + "-games");
            await Snapshot(main, "theme-" + theme.Id + "-games-min", 900, 600);
            Check("library_actions_visible_min_" + theme.Id, games.GameGrid.Columns.Where(c => c.Visibility == Visibility.Visible)
                .Sum(c => c.ActualWidth) <= games.GameGrid.ActualWidth + 1);
            main.NavigateTo(1);
            await Snapshot(main, "theme-" + theme.Id + "-endpoints-min", 900, 600);
            main.NavigateTo(2);
            await Snapshot(main, "theme-" + theme.Id + "-settings-min", 900, 600);
        }
        main.OnThemeChanged("default");
        Check("theme_switch_roundtrip", main.Settings.Theme == "default"
            && ((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color
                == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Themes.ThemeCatalog.Find("default").Colors["Accent"]));
        main.Close();
        earlyExit.Close();
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static void RunProbeStartup(string? screenshotPath)
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tSTARTUP\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition)
                failures++;
        }

        var entries = AttributionCatalog.All;
        Check("catalog_count", entries.Length == 24);
        Check("catalog_complete", entries.All(item =>
            !string.IsNullOrWhiteSpace(item.Name)
            && !string.IsNullOrWhiteSpace(item.Role)
            && !string.IsNullOrWhiteSpace(item.Category)
            && !string.IsNullOrWhiteSpace(item.License)
            && Uri.TryCreate(item.Url, UriKind.Absolute, out var url)
            && url.Scheme == Uri.UriSchemeHttps
            && (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || url.Host.Equals("fonts.google.com", StringComparison.OrdinalIgnoreCase))));
        Check("catalog_unique", entries.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Length
            && entries.Select(item => item.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Length);
        Check("catalog_no_market_research", entries.All(item =>
            !item.Name.Contains("RenpyThief", StringComparison.OrdinalIgnoreCase)
            && !item.Name.Contains("OCR 竞品", StringComparison.OrdinalIgnoreCase)));
        Check("catalog_required", new[] { "BepInEx", "XUnity.AutoTranslator", ".NET/WPF" }
            .All(required => entries.Any(item => item.Name == required)));

        Check("source_default", new AppSettings().SourceLanguage == "en");
        var sources = AppSettings.GetSourceLanguages();
        Check("source_options", sources.Length == AppSettings.Languages.Length + 1
            && sources[0].Code == "auto"
            && sources.Skip(1).Select(item => item.Code).SequenceEqual(AppSettings.Languages.Select(item => item.Code)));
        var game = new GameInfo { Name = "Suzerain", Path = "", AppId = 1207650 };
        Check("cache_legacy_key", GameNameService.CacheKey(game, "en", "zh-CN") == "1207650|zh-CN");
        Check("cache_auto_key", GameNameService.CacheKey(game, "auto", "zh-CN") == "1207650|auto>zh-CN");
        Check("cache_explicit_key", GameNameService.CacheKey(game, "ja", "zh-CN") == "1207650|ja>zh-CN");
        Check("local_strings_complete", !UiStrings.MissingKeys().Any());
        Check("theme_catalog_complete", Themes.ThemeCatalog.All.Length == 6
            && Themes.ThemeCatalog.All.All(t => Themes.ThemeCatalog.ColorKeys.All(t.Colors.ContainsKey))
            && Themes.ThemeCatalog.Find("nonexistent").Id == "default");

        // 普通字号按 4.5:1 验证，涵盖正文、辅助文字、状态文字及选中/悬停底色。
        static double Luminance(string hex)
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            static double Linear(byte channel)
            {
                var value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        static double Contrast(string foreground, string background)
        {
            var a = Luminance(foreground);
            var b = Luminance(background);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
        foreach (var theme in Themes.ThemeCatalog.All)
        {
            var colors = theme.Colors;
            var pairs = from foreground in new[] { "TextPrimary", "TextSecondary", "Accent", "Ok", "Warn", "Fail" }
                        from background in new[] { "PageBackground", "CardBackground", "NavBackground", "ControlBackground",
                            "ControlHover", "NavSelected", "NavHover", "RowSelected", "RowAlternate", "ActiveRow" }
                        select (foreground, background);
            var invalid = pairs.Concat(new[] { ("OnAccent", "AccentFill"), ("OnFail", "Fail") })
                .Where(pair => Contrast(colors[pair.Item1], colors[pair.Item2]) < 4.5).ToArray();
            Check("theme_contrast_" + theme.Id, invalid.Length == 0);
            foreach (var pair in invalid)
                Console.WriteLine($"CONTRAST\t{theme.Id}\t{pair.Item1}/{pair.Item2}\t{Contrast(colors[pair.Item1], colors[pair.Item2]):F2}");
        }

        var splash = new Views.AcknowledgementsPage();
        splash.BeginStartupCountdown();
        Check("splash_items", splash.AttributionItems.ItemsSource.Cast<object>().Count() == entries.Length);
        Check("splash_local_text", splash.ThanksText == UiStrings.Get("splash.thanks"));
        var acknowledgements = new Views.AcknowledgementsPage();
        Check("manual_acknowledgements", splash.AutoAdvanceEnabled && !acknowledgements.AutoAdvanceEnabled
            && splash.SecondsRemaining == 5
            && acknowledgements.CountdownText.Visibility == Visibility.Collapsed
            && acknowledgements.AttributionItems.ItemsSource.Cast<object>().Count() == entries.Length);
        Check("project_license", AttributionCatalog.ProjectLicense == "MIT"
            && splash.ProjectLicenseText.Text.Contains("MIT")
            && File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LICENSE")).StartsWith("MIT License"));
        Check("license_payload", File.Exists(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md"))
            && Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "LICENSES"), "*.txt").Length >= 7);
        var licenses = new Views.LicenseWindow();
        Check("license_viewer", licenses.LicenseContent.Text.Contains("Copyright (c) 2018 Bepis")
            && licenses.LicenseContent.Text.Contains("GNU LESSER GENERAL PUBLIC LICENSE")
            && licenses.LicenseContent.Text.Contains("Copyright (c) 2026 Franca contributors"));
        licenses.Close();
        const int pageWidth = 960, pageHeight = 660;
        var surface = (FrameworkElement)splash;
        surface.Measure(new Size(pageWidth, pageHeight));
        surface.Arrange(new Rect(0, 0, pageWidth, pageHeight));
        surface.UpdateLayout();
        Check("splash_license_layout", splash.ProjectLicenseText.ActualHeight > 0
            && splash.LicenseScopeText.ActualHeight > 0
            && splash.UpstreamRiskText.ActualHeight > 0
            && splash.AttributionItems.ActualHeight > 400);
        static IEnumerable<System.Windows.Controls.TextBlock> TextBlocks(DependencyObject root)
        {
            if (root is System.Windows.Controls.TextBlock text)
                yield return text;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in TextBlocks(System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                    yield return child;
        }
        var clipped = new List<string>();
        foreach (var text in TextBlocks(surface))
        {
            var sample = new System.Windows.Controls.TextBlock
            {
                Text = text.Text,
                FontFamily = text.FontFamily,
                FontSize = text.FontSize,
                FontWeight = text.FontWeight,
                FontStyle = text.FontStyle,
                TextWrapping = text.TextWrapping,
                FlowDirection = text.FlowDirection,
            };
            sample.Measure(new Size(text.TextWrapping == TextWrapping.NoWrap
                ? double.PositiveInfinity : Math.Max(1, text.ActualWidth), double.PositiveInfinity));
            var bounds = text.TransformToAncestor(surface).TransformBounds(new Rect(text.RenderSize));
            if (sample.DesiredSize.Width > text.ActualWidth + 1
                || sample.DesiredSize.Height > text.ActualHeight + 1
                || bounds.Left < -1 || bounds.Top < -1
                || bounds.Right > surface.ActualWidth + 1 || bounds.Bottom > surface.ActualHeight + 1)
                clipped.Add(text.Text);
        }
        Check("splash_text_fits", clipped.Count == 0);
        foreach (var text in clipped)
            Console.WriteLine($"CLIPPED\t{UiStrings.Current}\t{text}");
        if (screenshotPath is not null)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                pageWidth, pageHeight, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(screenshotPath);
            encoder.Save(output);
        }
        splash.CancelStartupCountdown();
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }


}
