using Franca.Models;

namespace Franca.Services;

// 引擎清单：安装能力决定支持状态；全部已支持引擎统一标「实验适配」，不写正式承诺。
public sealed record EngineSupportEntry(GameEngine Engine, string NoteKey)
{
    public string Name => Engine.Label();
    public bool IsSupported => Engine.IsInstallable() || Engine.IsUe() || Engine.IsRenPy();
    public string BadgeText => UiStrings.Get(IsSupported ? "engines.experimental" : "engines.recognized");
    public string Note => UiStrings.Get(NoteKey);
}

public static class EngineSupportCatalog
{
    // 已支持的在前；Unknown 不列入。仅写有真机证据的引擎为已支持，其余仅识别。
    public static readonly EngineSupportEntry[] All =
    {
        new(GameEngine.UnityIL2CPP, "engines.note.il2cpp"),
        new(GameEngine.UnityMono, "engines.note.mono"),
        new(GameEngine.Touhou, "engines.note.touhou"),
        new(GameEngine.Unreal, "engines.note.unreal"),
        new(GameEngine.RenPy, "engines.note.renpy"),
        new(GameEngine.RPGMaker, "engines.note.recognized"),
        new(GameEngine.Godot, "engines.note.recognized"),
        new(GameEngine.Clausewitz, "engines.note.recognized"),
        new(GameEngine.MonoGame, "engines.note.recognized"),
        new(GameEngine.GameMaker, "engines.note.recognized"),
        new(GameEngine.Source2, "engines.note.recognized"),
        new(GameEngine.REEngine, "engines.note.recognized"),
        new(GameEngine.MapleStory, "engines.note.online"),
    };
}
