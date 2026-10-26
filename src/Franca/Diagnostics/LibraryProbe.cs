using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Franca.Models;
using Franca.Services;
using Franca.Views;

namespace Franca.Diagnostics;

internal static class LibraryProbe
{
    public static async Task RunAsync(string? screenshotDirectory)
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tLIBRARY\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition) failures++;
        }
        var root = Path.Combine(AppContext.BaseDirectory, "library-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var playerSettingsPath = Path.Combine(AppContext.BaseDirectory, "franca-settings.json");
        var playerSettingsBefore = File.Exists(playerSettingsPath) ? File.ReadAllBytes(playerSettingsPath) : null;
        var settings = new AppSettings { TargetLanguage = "zh-CN" };
        var host = new MainWindow(startServices: false, settings);
        host.NavigateTo(0);
        var page = (GamesPage)host.PageHost.Content;
        GameInfo Sample(string name, GameEngine engine, bool? official, string translated = "") => new()
        {
            Name = name, Path = Path.Combine(root, name), Engine = engine,
            HasOfficialSupport = official, OfficialSupportLanguage = "zh-CN", TranslatedName = translated,
        };
        var unity = Sample("Zulu Unity", GameEngine.UnityMono, false, "星河物语");
        var other = Sample("Bravo Other", GameEngine.RenPy, false);
        var official = Sample("Alpha Official", GameEngine.UnityIL2CPP, true);
        var unknown = Sample("Delta Unknown", GameEngine.Unknown, null);
        page.ReplaceGames(new[] { official, unknown, unity, other });
        GameInfo[] Visible() => page.GameGrid.Items.Cast<GameInfo>().ToArray();
        Check("priority_default_on", settings.PrioritizeTranslation && page.PriorityToggle.IsChecked == true);
        Check("need_translation_unity_first", Visible().SequenceEqual(new[] { unity, other, unknown, official }));
        Check("unknown_and_official_stay_visible", Visible().Length == 4);
        page.PriorityToggle.IsChecked = false;
        Check("alphabetical_when_disabled", Visible().SequenceEqual(new[] { official, other, unknown, unity }));
        Check("priority_setting_serializes", !JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.PrioritizeTranslation);
        Check("transient_flag_not_serialized", settings.IsTransient && !JsonSerializer.Serialize(settings).Contains("IsTransient"));
        var savedSettings = new AppSettings
        {
            SourceLanguage = "ja", TargetLanguage = "fr", PrioritizeTranslation = false,
            EndpointKeys = new() { ["fixture"] = new() { ["ApiKey"] = "sentinel" } },
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(savedSettings))!;
        Check("other_settings_preserved", restored.SourceLanguage == "ja" && restored.TargetLanguage == "fr"
            && restored.EndpointKeys["fixture"]["ApiKey"] == "sentinel");
        page.GameSearchBox.Text = "zULu";
        Check("search_case_insensitive", Visible().SequenceEqual(new[] { unity }));
        page.GameSearchBox.Text = "星河";
        Check("search_translated_name", Visible().SequenceEqual(new[] { unity }));
        page.GameSearchBox.Text = "not-a-game";
        Check("search_empty_result", Visible().Length == 0 && page.StatsLabel.Text.Contains("0"));
        page.ClearSearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("clear_search_restores_all", Visible().Length == 4);
        Check("search_accents_and_tokens", GameLibraryOrder.Matches(Sample("Café World", GameEngine.Unknown, null), "cafe world"));
        Check("search_app_id", GameLibraryOrder.Matches(new GameInfo { Name = "Game", Path = root, AppId = 12345 }, "234"));
        Check("target_language_does_not_reuse_old_support", GameLibraryOrder.Priority(official, "ja") == 0);
        Check("old_settings_default_on", JsonSerializer.Deserialize<AppSettings>("{}")!.PrioritizeTranslation);
        page.PriorityToggle.IsChecked = true;
        official.HasOfficialSupport = false;
        page.RefreshAllRows();
        Check("priority_reacts_to_support_update", Visible().SequenceEqual(new[] { official, unity, other, unknown }));

        byte[] AppInfoFixture(int version, bool corruptRecord = false)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var keys = new[] { "appinfo", "common", "name", "type", "config", "installdir", "ignored" };
            void Text(string value) { writer.Write(System.Text.Encoding.UTF8.GetBytes(value)); writer.Write((byte)0); }
            void Key(byte kind, string key)
            {
                writer.Write(kind);
                if (version == 41) writer.Write(Array.IndexOf(keys, key)); else Text(key);
            }
            writer.Write(0x07564400u | (uint)version);
            writer.Write(1);
            if (version == 41) writer.Write(0L);
            foreach (var (id, type) in new[] { (501, "Application"), (502, "Game"), (503, "Tool") })
            {
                writer.Write(id);
                var sizeOffset = stream.Position;
                writer.Write(0);
                var start = stream.Position;
                writer.Write(new byte[version >= 40 ? 60 : 40]);
                Key(0, "appinfo");
                Key(0, "common");
                Key(1, "name"); Text("Unlisted Product");
                Key(1, "type"); Text(type);
                Key(2, "ignored"); writer.Write(42);
                if (corruptRecord && id == 501) writer.Write((byte)255);
                writer.Write((byte)8);
                Key(0, "config");
                Key(1, "installdir"); Text("Product" + id);
                writer.Write((byte)8); writer.Write((byte)8); writer.Write((byte)8);
                var end = stream.Position;
                stream.Position = sizeOffset;
                writer.Write((uint)(end - start));
                stream.Position = end;
            }
            writer.Write(0);
            if (version == 41)
            {
                var tableOffset = stream.Position;
                writer.Write(keys.Length);
                foreach (var key in keys) Text(key);
                stream.Position = 8;
                writer.Write(tableOffset);
            }
            return stream.ToArray();
        }
        foreach (var version in new[] { 39, 40, 41 })
        {
            var metadata = SteamAppInfoReader.Parse(AppInfoFixture(version));
            Check("steam_appinfo_v" + version, metadata.Count == 3 && metadata[501].Type == "Application"
                && metadata[502].Type == "Game" && metadata[503].InstallDir == "Product503");
            var damaged = SteamAppInfoReader.Parse(AppInfoFixture(version, corruptRecord: true));
            Check("steam_appinfo_bad_record_isolated_v" + version, !damaged.ContainsKey(501)
                && damaged.ContainsKey(502) && damaged.ContainsKey(503));
        }
        Check("steam_appinfo_truncated_header_safe", SteamAppInfoReader.Parse(new byte[] { 0x29, 0x44 }).Count == 0);
        Check("steam_appinfo_truncated_table_safe", SteamAppInfoReader.Parse(AppInfoFixture(41)[..20]).Count == 0);
        Check("steam_appinfo_unknown_version_safe", SteamAppInfoReader.Parse(AppInfoFixture(42)).Count == 0);
        Check("steam_appinfo_missing_cache_safe", SteamAppInfoReader.Read(root).Count == 0);
        var steamSoftware = new GameInfo { Name = "Unlisted Software", Path = root, SteamAppType = "Application" };
        Check("steam_type_filters_unlisted_software", !GameLibraryOrder.IsVisible(steamSoftware, true)
            && GameLibraryOrder.IsVisible(steamSoftware, false));
        Check("steam_game_type_overrides_name_fallback", !GameLibraryOrder.IsNonGameSoftware(
            new GameInfo { Name = "Blender", Path = root, SteamAppType = "Game" }));
        Check("steam_demo_mod_unknown_retained", new[] { "Demo", "Mod", "FutureType", "" }.All(type =>
            GameLibraryOrder.IsVisible(new GameInfo { Name = "Unlisted", Path = root, SteamAppType = type }, true)));
        Check("steam_non_game_types_hidden", new[] { "Tool", "Config", "DLC", "Music", "Video" }.All(type =>
            !GameLibraryOrder.IsVisible(new GameInfo { Name = "Unlisted", Path = root, SteamAppType = type }, true)));
        var directoryMetadata = SteamAppInfoReader.Parse(AppInfoFixture(41));
        Check("steam_directory_metadata_exact_only", SteamScanner.FindDirectoryType(directoryMetadata.Values, "product501") == "Application"
            && SteamScanner.FindDirectoryType(directoryMetadata.Values, "Product") == "");
        Check("steam_directory_conflicting_types_unknown", SteamScanner.FindDirectoryType(new[]
        {
            new SteamAppInfoReader.SteamAppMetadata("a", "Game", "Same"), new SteamAppInfoReader.SteamAppMetadata("b", "Tool", "Same"),
        }, "Same") == "");
        var helperDirectory = Path.Combine(root, "Glacier Events");
        Directory.CreateDirectory(helperDirectory);
        File.WriteAllBytes(Path.Combine(helperDirectory, "EAAntiCheat.GameServiceLauncher.exe"), Array.Empty<byte>());
        Check("glacier_anticheat_only_identified", SteamScanner.IsAuxiliaryOnlyDirectory(helperDirectory));
        var helper = new GameInfo { Name = "Glacier Events", Path = helperDirectory, IsAuxiliarySoftware = true,
            AppId = 777, IsReady = true, SteamAppType = "Beta" };
        Check("glacier_hidden_and_reversible", !GameLibraryOrder.IsVisible(helper, true) && GameLibraryOrder.IsVisible(helper, false));
        File.WriteAllBytes(Path.Combine(helperDirectory, "RealGame.exe"), Array.Empty<byte>());
        Check("game_bundling_anticheat_retained", !SteamScanner.IsAuxiliaryOnlyDirectory(helperDirectory));

        var blender = Sample("BLENDER", GameEngine.Unknown, null);
        var eaPlay = Sample("EA PLAY", GameEngine.Unknown, null);
        var fallbackSoftware = new GameInfo { Name = "Directory fallback", Path = Path.Combine(root, "EA Desktop") };
        var similarGame = Sample("Blender Adventure", GameEngine.Unknown, null);
        settings.LastLaunchedAppId = helper.AppId;
        page.ReplaceGames(new[] { unity, unknown, blender, eaPlay, fallbackSoftware, similarGame, steamSoftware, helper });
        host.NavigateTo(2);
        var settingsPage = (SettingsPage)host.PageHost.Content;
        // Exercise the loaded XAML control and its Click handler, not just the host method.
        // Show off-screen so WPF runs Loaded without taking focus or starting background services.
        host.ShowActivated = false;
        host.ShowInTaskbar = false;
        host.WindowStartupLocation = WindowStartupLocation.Manual;
        host.Left = -20000;
        host.Top = -20000;
        host.Show();
        var settingsSurface = (FrameworkElement)host.Content;
        settingsSurface.Measure(new Size(1180, 720));
        settingsSurface.Arrange(new Rect(0, 0, 1180, 720));
        settingsSurface.UpdateLayout();
        await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        settingsSurface.UpdateLayout();
        Check("hide_software_settings_control_visible", settingsPage.IsLoaded
            && settingsPage.HideNonGameSoftwareCheck.IsVisible
            && settingsPage.HideNonGameSoftwareCheck.ActualHeight > 0
            && Equals(settingsPage.HideNonGameSoftwareCheck.Content, UiStrings.Get("settings.hidesoftware")));
        if (screenshotDirectory is not null)
        {
            Directory.CreateDirectory(screenshotDirectory);
            var bitmap = new RenderTargetBitmap(1180, 720, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(settingsSurface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(screenshotDirectory, "settings-software-filter.png"));
            encoder.Save(output);
        }
        void ClickSoftwareFilter(bool value)
        {
            settingsPage.HideNonGameSoftwareCheck.IsChecked = value;
            settingsPage.HideNonGameSoftwareCheck.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        }
        Check("hide_software_default_checked", settings.HideNonGameSoftware
            && settingsPage.HideNonGameSoftwareCheck.IsChecked == true
            && JsonSerializer.Deserialize<AppSettings>("{}")!.HideNonGameSoftware);
        Check("hide_software_known_names_and_directory", Visible().Length == 3
            && Visible().Contains(unknown) && Visible().Contains(similarGame));
        Check("hide_software_stats_exclude_software", page.StatsLabel.Text == UiStrings.Format("games.count", 3));
        Check("hidden_helper_not_recent_shortcut", page.RecentGameButton.Visibility == Visibility.Collapsed);
        page.GameSearchBox.Text = "blender";
        Check("search_does_not_reveal_hidden_software", Visible().SequenceEqual(new[] { similarGame }));
        ClickSoftwareFilter(false);
        Check("uncheck_immediately_restores_software", !settings.HideNonGameSoftware
            && Visible().Length == 2 && Visible().Contains(blender));
        Check("uncheck_restores_recent_shortcut", page.RecentGameButton.Visibility == Visibility.Visible
            && ReferenceEquals(page.RecentGameButton.Tag, helper));
        Check("hide_software_false_roundtrip", !JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.HideNonGameSoftware);
        page.GameSearchBox.Clear();
        Check("uncheck_restores_all_entries", Visible().Length == 8);
        ClickSoftwareFilter(true);
        Check("recheck_immediately_hides_software", settings.HideNonGameSoftware
            && Visible().Length == 3 && page.GamesSnapshot.Count == 8);
        settings.LastLaunchedAppId = null;
        page.ReplaceGames(new[] { official, unknown, unity, other });
        host.NavigateTo(0);

        var steam = Path.Combine(root, "steam");
        var cache = Path.Combine(steam, "appcache", "librarycache");
        Directory.CreateDirectory(Path.Combine(cache, "42"));
        void WritePng(string path, int width, int height)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)180, width * height * 4).ToArray(), width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path);
            encoder.Save(output);
        }
        WritePng(Path.Combine(cache, "42", new string('a', 40) + ".png"), 32, 32);
        WritePng(Path.Combine(cache, "43_icon.png"), 32, 32);
        File.WriteAllText(Path.Combine(cache, "42", "icon.png"), "invalid image");
        var icon = SteamIconService.Load(steam, 42);
        Check("nested_hash_icon_and_corrupt_fallback", icon is BitmapSource { PixelWidth: 32, PixelHeight: 32, IsFrozen: true });
        Check("legacy_icon_layout", SteamIconService.Load(steam, 43) is not null);
        Check("missing_icon_safe", SteamIconService.Load(steam, 44) is null && SteamIconService.Load(steam, null) is null);
        WritePng(Path.Combine(cache, "45_icon.png"), 64, 32);
        Check("reject_non_square_artwork", SteamIconService.Load(steam, 45) is null);

        using var handler = new SteamFixtureHandler();
        using var http = new HttpClient(handler);
        var names = new GameNameService(Path.Combine(root, "names.json"), http);
        var endpointKeys = new Dictionary<string, Dictionary<string, string>>();
        async Task Resolve(GameInfo game, string target) => await names.ResolveAsync(
            new[] { game }, target, "Disabled", endpointKeys, _ => { }, _ => { }, CancellationToken.None);
        var english = new GameInfo { AppId = 100, Name = "English Adventure", Path = root };
        await Resolve(english, "en");
        Check("english_name_still_checks_language_support", english.HasOfficialSupport == false && english.OfficialSupportLanguage == "en");
        await Resolve(english, "en");
        Check("original_support_cached_and_english_request_reused", handler.Requests[100] == 1 && english.HasOfficialSupport == false);
        var chinese = new GameInfo { AppId = 101, Name = "中文游戏", Path = root };
        await Resolve(chinese, "zh-CN");
        Check("native_name_still_checks_language_support", chinese.HasOfficialSupport == true);
        var localized = new GameInfo { AppId = 102, Name = "Foreign Adventure", Path = root };
        await Resolve(localized, "zh-CN");
        Check("localized_store_title_not_language_support", localized.NameSource == "official" && localized.HasOfficialSupport == false);
        var unavailable = new GameInfo { AppId = 103, Name = "Unknown Adventure", Path = root };
        await Resolve(unavailable, "en");
        Check("unavailable_support_stays_unknown", unavailable.HasOfficialSupport is null);
        // 音频图例回归：去标签后 <br> 图例不能粘在最后一个语言 token 上，否则单语言/队尾语言被误判无官中
        var single = new GameInfo { AppId = 105, Name = "Single Language Game", Path = root };
        await Resolve(single, "zh-CN");
        Check("audio_legend_not_glued_single_language", single.HasOfficialSupport == true);
        var last = new GameInfo { AppId = 106, Name = "Last Language Game", Path = root };
        await Resolve(last, "zh-CN");
        Check("audio_legend_not_glued_last_language", last.HasOfficialSupport == true);
        var legacy = new GameInfo { AppId = 104, Name = "Legacy Adventure", Path = root };
        var legacyPath = Path.Combine(root, "legacy-names.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new Dictionary<string, GameNameService.CacheEntry>
        {
            [GameNameService.CacheKey(legacy, "en", "zh-CN")] = new()
            {
                Name = "旧商店译名", Source = "official", Version = 7, OfficialSupport = true,
                SupportFetchedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
        }));
        var legacyNames = new GameNameService(legacyPath, http);
        await legacyNames.ResolveAsync(new[] { legacy }, "zh-CN", "Disabled", endpointKeys,
            _ => { }, _ => { }, CancellationToken.None);
        Check("legacy_inferred_support_not_reused_on_failure", legacy.HasOfficialSupport is null);

        var traditional = new GameInfo { AppId = 107, Name = "Traditional Only", Path = root, Engine = GameEngine.Unknown };
        await Resolve(traditional, "zh-CN");
        var title = traditional.TranslatedName;
        Check("secondary_primary_still_unsupported", traditional.HasOfficialSupport == false);
        async Task Secondary(GameNameService service, GameInfo game, string language) =>
            await service.ResolveSecondarySupportAsync(new[] { game }, language, (item, support) =>
            {
                item.HasSecondaryOfficialSupport = support;
                item.SecondaryOfficialSupportLanguage = language;
            }, CancellationToken.None);
        var before = handler.Requests[107];
        await Secondary(names, traditional, "zh-TW");
        Check("secondary_traditional_supported", traditional.HasSecondaryOfficialSupport == true);
        Check("secondary_reuses_primary_metadata", handler.Requests[107] == before);
        Check("secondary_does_not_translate_or_change_target", traditional.TranslatedName == title
            && traditional.OfficialSupportLanguage == "zh-CN" && traditional.HasOfficialSupport == false);
        Check("secondary_priority_includes_supported", GameLibraryOrder.Priority(traditional, "zh-CN", "zh-TW") == 2);
        Check("secondary_priority_not_reused_after_change", GameLibraryOrder.Priority(traditional, "zh-CN", "ja") == 1);
        Check("secondary_status_shows_language", traditional.AvailableOfficialSupportLabel.Contains(AppSettings.FindLanguage("zh-TW").Display));
        var persistedNames = new GameNameService(Path.Combine(root, "names.json"), http);
        await Secondary(persistedNames, traditional, "zh-TW");
        Check("secondary_metadata_persists", handler.Requests[107] == before);
        names.InvalidateOfficialSupport();
        await Secondary(names, traditional, "zh-TW");
        Check("secondary_refresh_invalidates_metadata", handler.Requests[107] == before + 1);
        await Secondary(names, unavailable, "zh-TW");
        Check("secondary_lookup_failure_stays_unknown", unavailable.HasSecondaryOfficialSupport is null);
        await Secondary(names, traditional, "ja");
        Check("secondary_unsupported_does_not_claim_support", traditional.HasSecondaryOfficialSupport == false);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var called = false;
            try { await names.ResolveSecondarySupportAsync(new[] { traditional }, "zh-TW", (_, _) => called = true, canceled.Token); }
            catch (OperationCanceledException) { }
            Check("secondary_canceled_does_not_publish", !called);
        }
        Check("secondary_defaults_unset", JsonSerializer.Deserialize<AppSettings>("{}")!.SecondaryLanguage == "");
        settings.SecondaryLanguage = "zh-TW";
        Check("secondary_setting_snapshot", settings.Snapshot().SecondaryLanguage == "zh-TW");
        Check("secondary_choices_optional_no_auto", AppSettings.GetSecondaryLanguages().First().Code == ""
            && !AppSettings.GetSecondaryLanguages().Any(option => option.Code == "auto"));
        page.SyncSecondaryLanguageCombo("zh-TW");
        Check("secondary_combo_sync", (string?)page.SecondaryLanguageCombo.SelectedValue == "zh-TW");
        settings.SecondaryLanguage = "";
        page.SyncSecondaryLanguageCombo("");
        Check("secondary_combo_can_clear", (string?)page.SecondaryLanguageCombo.SelectedValue == "");
        Check("launch_game_label_explicit", UiStrings.Get("games.play") == "启动游戏");

        // 真实点击刷新所用的失效链路：缓存不变时复用，手动刷新后必须重新取 Steam 证据。
        var cachedRow = new GameInfo { AppId = 102, Name = localized.Name, Path = root };
        names.ApplyCachedNames(new[] { cachedRow }, "zh-CN", "en");
        Check("cached_title_visible_before_network", cachedRow.TranslatedName == localized.TranslatedName
            && cachedRow.HasOfficialSupport == false);
        var erasedPath = Path.Combine(root, "erased-support.json");
        File.WriteAllText(erasedPath, JsonSerializer.Serialize(new Dictionary<string, GameNameService.CacheEntry>
        {
            [GameNameService.CacheKey(chinese, "en", "zh-CN")] = new()
                { Name = chinese.Name, Source = "original", Version = 8 },
            ["support|101"] = new()
                { Source = "support-evidence", Version = 8, SupportedLanguages = "English, Simplified Chinese" },
        }));
        var erasedNames = new GameNameService(erasedPath, http);
        var restoredRow = new GameInfo { AppId = 101, Name = chinese.Name, Path = root };
        erasedNames.ApplyCachedNames(new[] { restoredRow }, "zh-CN", "en");
        Check("cached_evidence_recovers_previously_erased_support", restoredRow.HasOfficialSupport == true);
        names.InvalidateOfficialSupport();
        handler.Unavailable = true;
        await names.ResolveOfficialSupportAsync(new[] { chinese }, "zh-CN",
            (game, support) => game.HasOfficialSupport = support, CancellationToken.None);
        Check("official_refresh_failure_preserves_known_support", chinese.HasOfficialSupport == true);
        await Secondary(names, traditional, "zh-TW");
        Check("secondary_refresh_failure_preserves_evidence", traditional.HasSecondaryOfficialSupport == true);
        await names.ResolveOfficialSupportAsync(new[] { cachedRow }, "zh-CN",
            (_, _) => { }, CancellationToken.None);
        Check("official_only_refresh_preserves_title", cachedRow.TranslatedName == localized.TranslatedName
            && cachedRow.NameSource == localized.NameSource);
        handler.Unavailable = false;
        var priorTitle = chinese.TranslatedName;
        var priorNameSource = chinese.NameSource;
        var priorRequests = handler.Requests[101];
        handler.SetLanguages(101, "English, Traditional Chinese");
        names.InvalidateOfficialSupport();
        await Resolve(chinese, "zh-CN");
        Check("manual_refresh_requeries_primary_metadata", handler.Requests[101] > priorRequests
            && chinese.HasOfficialSupport == false);
        await Secondary(names, chinese, "zh-TW");
        Check("manual_refresh_updates_secondary_from_same_evidence", chinese.HasSecondaryOfficialSupport == true);
        Check("manual_refresh_preserves_cached_title", chinese.TranslatedName == priorTitle && chinese.NameSource == priorNameSource);
        handler.SetLanguages(101, "English, Simplified Chinese");
        names.InvalidateOfficialSupport();
        await Resolve(chinese, "zh-CN");
        await Secondary(names, chinese, "zh-TW");
        Check("manual_refresh_can_add_and_remove_support", chinese.HasOfficialSupport == true
            && chinese.HasSecondaryOfficialSupport == false);

        var installed = SteamScanner.ScanGames();
        Console.WriteLine($"LOCAL\tSTEAM\tgames={installed.Count}\ticons={installed.Count(game => game.Icon is not null)}");
        Console.WriteLine($"LOCAL\tSTEAM_TYPES\tknown={installed.Count(g => g.SteamAppType.Length > 0)}\thidden={installed.Count(GameLibraryOrder.IsNonGameSoftware)}");
        foreach (var entry in installed.Where(g => GameLibraryOrder.IsNonGameSoftware(g)
            || g.Name.Contains("Glacier", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"LOCAL\tCLASSIFICATION\t{entry.Name}\ttype={entry.SteamAppType}\thelper={entry.IsAuxiliarySoftware}\thidden={GameLibraryOrder.IsNonGameSoftware(entry)}");
        if (installed.Count > 0) page.ReplaceGames(installed);
        async Task Snapshot(string lang)
        {
            if (screenshotDirectory is null) return;
            Directory.CreateDirectory(screenshotDirectory);
            var surface = (FrameworkElement)host.Content;
            surface.Measure(new Size(1180, 720));
            surface.Arrange(new Rect(0, 0, 1180, 720));
            surface.UpdateLayout();
            await host.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            surface.UpdateLayout();
            Check("column_widths_" + lang, page.ColName.ActualWidth > 200
                && page.ColEngine.Visibility == Visibility.Collapsed && page.ColTranslated.Visibility == Visibility.Collapsed);
            var bitmap = new RenderTargetBitmap(1180, 720, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(screenshotDirectory, "library-" + lang + ".png"));
            encoder.Save(output);
        }
        if (screenshotDirectory is not null)
        {
            var officialUnsupported = Sample("Official non-Unity", GameEngine.Unreal, true);
            officialUnsupported.OfficialSupportLabel = UiStrings.Format("game.officialsupport", AppSettings.FindLanguage("zh-CN").Display);
            page.ReplaceGames(new[] { unity, other, unknown, officialUnsupported });
            await Snapshot("actions-zh-CN");
            page.ReplaceGames(installed.Count > 0 ? installed : new[] { official, unknown, unity, other });
        }
        foreach (var language in AppSettings.Languages)
        {
            settings.TargetLanguage = language.Code;
            UiStrings.Current = language.Code;
            foreach (NavItem item in host.NavList.Items) item.RefreshLabel();
            page.SyncLanguageCombo(language.Code);
            page.ApplyUiLanguage();
            page.RefreshAllRows();
            await Snapshot(language.Code);
            Check("localized_controls_" + language.Code, !UiStrings.MissingKeys().Any()
                && !page.PriorityLabel.Text.StartsWith("games.")
                && !page.SearchHintText.Text.StartsWith("games."));
        }
        host.Close();
        Check("player_settings_unchanged", playerSettingsBefore is null
            ? !File.Exists(playerSettingsPath)
            : File.Exists(playerSettingsPath) && playerSettingsBefore.SequenceEqual(File.ReadAllBytes(playerSettingsPath)));
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private sealed class SteamFixtureHandler : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public Dictionary<int, int> Requests { get; } = new();
        // 105/106 复刻 Steam 真实响应：音频图例紧跟 <br> 粘在最后一个语言之后（拖拖拉拉小菲镇案例）
        public void SetLanguages(int id, string languages) => LanguageLists[id] = languages;
        private readonly Dictionary<int, string> LanguageLists = new()
        {
            [101] = "English, Simplified Chinese",
            [107] = "English, Traditional Chinese",
            [105] = "Simplified Chinese<strong>*</strong><br><strong>*</strong>languages with full audio support",
            [106] = "Japanese, Simplified Chinese<strong>*</strong><br><strong>*</strong>languages with full audio support",
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = int.Parse(System.Text.RegularExpressions.Regex.Match(request.RequestUri!.Query, "appids=(\\d+)").Groups[1].Value);
            Requests[id] = Requests.GetValueOrDefault(id) + 1;
            var chinese = request.RequestUri.Query.Contains("l=schinese");
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [id.ToString()] = new { success = !Unavailable && id is not (103 or 104), data = new
                {
                    name = chinese ? "中文商店名称" : "English Adventure",
                    supported_languages = LanguageLists.GetValueOrDefault(id, "Japanese"),
                } },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
