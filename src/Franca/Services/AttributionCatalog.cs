namespace Franca.Services;

public sealed record AttributionEntry(
    string Name,
    string RoleKey,
    string Category,
    string Url,
    string License)
{
    public string Role => UiStrings.Get(RoleKey);
}

public static class AttributionCatalog
{
    public const string ProjectLicense = "MIT";
    public static readonly AttributionEntry[] All =
    {
        new("BepInEx", "credit.loader", "Runtime", "https://github.com/BepInEx/BepInEx", "LGPL-2.1"),
        new("UnityDoorstop", "credit.bootstrap", "Adapted source", "https://github.com/NeighTools/UnityDoorstop", "LGPL-2.1"),
        new("Il2CppInterop", "credit.interop", "Runtime", "https://github.com/BepInEx/Il2CppInterop", "LGPL-3.0"),
        new("XUnity.AutoTranslator", "credit.unity", "Runtime", "https://github.com/bbepis/XUnity.AutoTranslator", "MIT"),
        new("RapidOCR", "credit.research", "Research reference", "https://github.com/RapidAI/RapidOCR", "Apache-2.0"),
        new("PaddleOCR", "credit.research", "Research reference", "https://github.com/PaddlePaddle/PaddleOCR", "Apache-2.0"),
        new("manga-ocr", "credit.research", "Research reference", "https://github.com/kha-white/manga-ocr", "Apache-2.0"),
        new(".NET/WPF", "credit.desktop", "Runtime", "https://github.com/dotnet/wpf", "MIT"),

        new("Ren'Py SDK", "credit.research", "Research reference", "https://github.com/renpy/renpy", "MIT / LGPL"),
        new("unrpyc", "credit.research", "Research reference", "https://github.com/CensoredUsername/unrpyc", "MIT"),
        new("UE4SS", "credit.research", "Research reference", "https://github.com/UE4SS-RE/RE-UE4SS", "MIT"),
        new("UAssetGUI", "credit.research", "Research reference", "https://github.com/atenfyr/UAssetGUI", "MIT"),
        new("repak", "credit.research", "Research reference", "https://github.com/trumank/repak", "Apache-2.0"),
        new("GARbro", "credit.research", "Research reference", "https://github.com/morkt/GARbro", "MIT"),
        new("gdsdecomp", "credit.research", "Research reference", "https://github.com/GDRETools/gdsdecomp", "MIT"),
        new("UndertaleModTool", "credit.research", "Research reference", "https://github.com/UnderminersTeam/UndertaleModTool", "GPL-3.0"),

        new("Textractor", "credit.research", "Research reference", "https://github.com/Artikash/Textractor", "GPL-3.0"),
        new("LunaTranslator", "credit.research", "Research reference", "https://github.com/HIllya51/LunaTranslator", "GPL-3.0"),
        new("Harmony (0Harmony)", "credit.patching", "Runtime", "https://github.com/pardeike/Harmony", "MIT"),

        new("thtk", "credit.thtools", "Runtime", "https://github.com/thpatch/thtk", "BSD"),
        new("thcrap", "credit.thtools", "Runtime", "https://github.com/thpatch/thcrap", "Unlicense"),
        new("Noto Sans SC", "credit.thtools", "Runtime", "https://fonts.google.com/noto", "OFL-1.1"),

        new("qingjian（青简）", "credit.dictionary", "Design reference", "https://github.com/qingjian-team/qingjian", "GPL-3.0-or-later"),
        new("Real-time-translation-typing", "credit.failover", "Design reference", "https://github.com/sxzxs/Real-time-translation-typing", "No license"),
    };
}
