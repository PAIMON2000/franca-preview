namespace Franca.Models;

public enum GameEngine
{
    UnityIL2CPP,
    UnityMono,
    RenPy,
    RPGMaker,
    Godot,
    Unreal,
    Clausewitz,
    MonoGame,
    GameMaker,
    Source2,
    REEngine,
    Touhou,
    Unknown
}

public static class GameEngineExtensions
{
    public static string Label(this GameEngine engine) => engine switch
    {
        GameEngine.UnityIL2CPP => "Unity IL2CPP",
        GameEngine.UnityMono => "Unity Mono",
        GameEngine.RenPy => "Ren'Py",
        GameEngine.RPGMaker => "RPG Maker",
        GameEngine.Godot => "Godot",
        GameEngine.Unreal => "Unreal Engine",
        GameEngine.Clausewitz => "Clausewitz",
        GameEngine.MonoGame => "MonoGame",
        GameEngine.GameMaker => "GameMaker",
        GameEngine.Source2 => "Source 2",
        GameEngine.REEngine => "RE Engine",
        GameEngine.Touhou => "Touhou (ZUN Engine, thcrap)",
        _ => Services.UiStrings.Get("engine.unknown")
    };

    public static bool IsUnity(this GameEngine engine) =>
        engine is GameEngine.UnityIL2CPP or GameEngine.UnityMono;

    // 可安装翻译的引擎：Unity 注入 + 东方自研引擎 thcrap 运行时路线（TouhouThcrapService）
    public static bool IsInstallable(this GameEngine engine) =>
        engine.IsUnity() || engine is GameEngine.Touhou;
}

public class GameInfo : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void RaiseChanged(string name) => PropertyChanged?.Invoke(this,
        new System.ComponentModel.PropertyChangedEventArgs(name));

    public required string Name { get; init; }
    public required string Path { get; init; }
    public int? AppId { get; init; }
    public string SteamAppType { get; init; } = "";
    public bool IsAuxiliarySoftware { get; init; }
    public System.Windows.Media.ImageSource? Icon { get; init; }
    public GameEngine Engine { get; set; }
    public bool IsReady { get; set; }
    public string TranslationHealthText { get; set; } = "";
    public string TranslationHealthDetail { get; set; } = "";
    public bool NeedsTranslationRepair { get; set; }

    // 静态安装行内进度（仅安装进行中非空）：百分比 0–100 与 "（已处理/总字节）" 文本
    public double? InstallPercent { get; private set; }
    public string InstallProgressText { get; private set; } = "";
    public string? InstallError { get; private set; }
    public bool HasInstallError => !string.IsNullOrEmpty(InstallError);
    public bool IsInstalling => InstallPercent is not null;
    public void SetInstallProgress(double? percent, string byteText)
    {
        InstallPercent = percent;
        InstallProgressText = percent is null ? "" : $"{percent:F2}%{byteText}";
        RaiseChanged(nameof(InstallPercent));
        RaiseChanged(nameof(InstallProgressText));
        RaiseChanged(nameof(IsInstalling));
    }
    public void SetInstallError(string? error)
    {
        InstallError = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
        RaiseChanged(nameof(InstallError));
        RaiseChanged(nameof(HasInstallError));
    }
    public bool IsSupported => Engine.IsInstallable();
    public string LibraryTitle => string.IsNullOrWhiteSpace(TranslatedName) ? Name : TranslatedName;
    public string LibrarySubtitle => LibraryTitle == Name ? EngineLabel : Name;
    public string EnableTranslationLabel => Services.UiStrings.Get("game.enable");
    public string PrimaryActionLabel => IsToggling ? Services.UiStrings.Get("ui.working")
        : IsActive ? LaunchLabel : EnableTranslationLabel;
    public bool CanPrimaryAction => IsActive ? CanLaunch : CanToggle;
    public string LibraryStatusText => IsToggling ? Services.UiStrings.Get("ui.working")
        : NeedsTranslationRepair ? Services.UiStrings.Get("health.problem")
        : IsDisabledByUser ? Services.UiStrings.Get("game.disabled")
        : IsActive ? Services.UiStrings.Get("ui.configured")
        : !string.IsNullOrEmpty(AvailableOfficialSupportLabel) ? AvailableOfficialSupportLabel
        : !IsSupported ? Services.UiStrings.Get("ui.unsupported") : Services.UiStrings.Get("game.notready");
    public System.Windows.Media.Brush LibraryStatusColor => (System.Windows.Media.Brush)
        System.Windows.Application.Current.FindResource(NeedsTranslationRepair ? "WarnBrush" : IsActive ? "OkBrush" : "TextSecondaryBrush");
    public bool CanRepairTranslationConfig => Engine.IsInstallable() && IsReady && !IsToggling;
    public string RepairConfigLabel => Services.UiStrings.Get("health.repair");
    public string RepairConfigHint => Services.UiStrings.Get("health.hint");

    // 卸载（整体还原）在可安装引擎且存在 Franca 产物时可用；不识别是否损坏
    public bool CanUninstall => Engine.IsInstallable() && (IsReady
        || (Engine == GameEngine.Touhou && (Services.TouhouThcrapService.HasArtifacts(Path)
            || Services.TouhouStaticService.HasArtifacts(Path)))
        || (Engine.IsUnity() && Services.TranslationInstaller.HasInstalledArtifacts(Path)));
    public string UninstallLabel => Services.UiStrings.Get("uninstall.button");
    public string UninstallHint => Services.UiStrings.Get("uninstall.hint");

    public string TranslatedName { get; set; } = "";
    public string NameSource { get; set; } = ""; // official / machine / failed / en

    // 官方目标语言支持（null=未知），标签由 GameNameService 按当前目标语言填入
    public bool? HasOfficialSupport { get; set; }
    public string OfficialSupportLanguage { get; set; } = "";
    public string OfficialSupportLabel { get; set; } = "";
    public bool? HasSecondaryOfficialSupport { get; set; }
    public string SecondaryOfficialSupportLanguage { get; set; } = "";
    public string AvailableOfficialSupportLabel => !string.IsNullOrEmpty(OfficialSupportLabel) ? OfficialSupportLabel
        : HasSecondaryOfficialSupport == true && !string.IsNullOrEmpty(SecondaryOfficialSupportLanguage)
            ? Services.UiStrings.Format("game.officialsupport", Services.AppSettings.FindLanguage(SecondaryOfficialSupportLanguage).Display) : "";

    // 可逆停用：winhttp.dll.frdisabled（Unity 注入）、<名>.dat.frpatched（旧东方静态补丁）
    // 或 thcrap 路线 exe 原位为还原的原版（存根让位）视为停用
    public bool IsDisabledByUser => System.IO.File.Exists(System.IO.Path.Combine(Path, "winhttp.dll.frdisabled"))
        || (Engine == GameEngine.Touhou && (Services.TouhouThcrapService.IsDisabled(Path)
            || Services.TouhouStaticService.IsDisabled(Path)));
    public bool IsActive => IsReady && !IsDisabledByUser;
    public bool IsToggling { get; set; }

    public string EngineLabel => Engine.Label();

    public string TranslatedDisplay => NameSource switch
    {
        "official" => Services.UiStrings.Format("name.official", TranslatedName),
        "machine" => Services.UiStrings.Format("name.machine", TranslatedName),
        "resolving" => Services.UiStrings.Get("name.resolving"),
        "en" => TranslatedName,
        "original" => TranslatedName,
        "failed" => "-",
        _ => ""
    };

    // 官方语言标注最优先（有官中的游戏这条信息最有价值）；其次非可安装引擎占位、停用/就绪/未启用
    public string StatusText => !string.IsNullOrEmpty(AvailableOfficialSupportLabel) ? AvailableOfficialSupportLabel
        : !Engine.IsInstallable() ? "—"
        : IsDisabledByUser ? Services.UiStrings.Get("game.disabled")
        : Services.UiStrings.Get(IsReady ? "game.ready" : "game.notready");

    public System.Windows.Media.Brush StatusColor
    {
        get
        {
            if (!string.IsNullOrEmpty(AvailableOfficialSupportLabel))
                return (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentBrush");
            if (IsDisabledByUser)
                return (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("WarnBrush");
            if (IsActive)
                return (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("OkBrush");
            return (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextPrimaryBrush");
        }
    }

    public bool CanEnable => Engine.IsInstallable() && !IsReady;

    // 开关可用性：仅可安装引擎且不在切换过程中
    public bool CanToggle => Engine.IsInstallable() && !IsToggling;

    // 经 Steam 启动：仅翻译已启用且有 appId 的 Steam 游戏
    public bool CanLaunch => IsActive && AppId is > 0 && !IsToggling;
    public string LaunchLabel => Services.UiStrings.Get("games.play");
    public string LaunchHint => Services.UiStrings.Get("games.play.hint");

    public string ActionText => !Engine.IsInstallable()
        ? Services.UiStrings.Get("ui.unsupported")
        : Services.UiStrings.Get(IsReady ? "game.ready" : "game.enable");
}
