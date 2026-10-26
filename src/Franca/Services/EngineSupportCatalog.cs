using Franca.Models;

namespace Franca.Services;

// 引擎清单：安装能力决定支持状态；UE 另标实验适配，不改变正式启用门槛。
public sealed record EngineSupportEntry(GameEngine Engine, string NoteKey)
{
    public string Name => Engine.Label();
    public bool IsSupported => Engine.IsInstallable();
    public string BadgeText => UiStrings.Get(Engine == GameEngine.Unreal ? "engines.experimental"
        : IsSupported ? "engines.supported" : "engines.recognized");
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
        new(GameEngine.RenPy, "engines.note.recognized"),
        new(GameEngine.RPGMaker, "engines.note.recognized"),
        new(GameEngine.Godot, "engines.note.recognized"),
        new(GameEngine.Unreal, "engines.note.unreal"),
        new(GameEngine.Clausewitz, "engines.note.recognized"),
        new(GameEngine.MonoGame, "engines.note.recognized"),
        new(GameEngine.GameMaker, "engines.note.recognized"),
        new(GameEngine.Source2, "engines.note.recognized"),
        new(GameEngine.REEngine, "engines.note.recognized"),
    };
}
