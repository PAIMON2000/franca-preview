using System.Collections;
using System.Globalization;
using Franca.Models;

namespace Franca.Services;

public sealed class GameLibraryOrder(bool prioritizeTranslation, string targetLanguage, string secondaryLanguage = "") : IComparer
{
    // Fallback when Steam has no decisive type: an unknown engine is never evidence
    // of a non-game. Directory names also cover entries without appmanifest files.
    private static readonly HashSet<string> SoftwareNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "eaplay", "eadesktop", "eaapp", "blender", "wallpaperengine",
        "steamworksshared", "steamworkssharedcommonredistributables",
        "steamworkscommonredistributables", "steamvr", "steamlinuxruntime",
        "proton", "sourcefilmmaker", "obsstudio", "losslessscaling",
    };

    public static bool IsNonGameSoftware(GameInfo game)
    {
        // Explicit Steam game/demo/mod evidence wins over fallback name heuristics.
        switch (game.SteamAppType.ToLowerInvariant())
        {
            case "game": case "demo": case "mod": return false;
            case "application": case "tool": case "config": case "dlc":
            case "music": case "video": case "series": case "episode":
            case "hardware": case "advertising": case "guide": case "driver": return true;
        }
        if (game.IsAuxiliarySoftware) return true;
        static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());
        return SoftwareNames.Contains(Normalize(game.Name))
            || SoftwareNames.Contains(Normalize(System.IO.Path.GetFileName(game.Path.TrimEnd('\\', '/'))));
    }

    public static bool IsVisible(GameInfo game, bool hideNonGameSoftware) =>
        !hideNonGameSoftware || !IsNonGameSoftware(game);

    public static int Priority(GameInfo game, string targetLanguage, string secondaryLanguage = "") =>
        (game.OfficialSupportLanguage == targetLanguage && game.HasOfficialSupport == true)
            || (!string.IsNullOrEmpty(secondaryLanguage) && game.SecondaryOfficialSupportLanguage == secondaryLanguage
                && game.HasSecondaryOfficialSupport == true)
            ? 2 : game.Engine.IsUnity() ? 0 : 1;

    public int Compare(object? x, object? y)
    {
        if (x is not GameInfo left || y is not GameInfo right) return 0;
        var priority = prioritizeTranslation ? Priority(left, targetLanguage, secondaryLanguage).CompareTo(Priority(right, targetLanguage, secondaryLanguage)) : 0;
        if (priority != 0) return priority;
        var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return name != 0 ? name : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    }

    public static bool Matches(GameInfo game, string query)
    {
        var names = new[] { game.Name, game.TranslatedName, game.AppId?.ToString() ?? "" };
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(token =>
            names.Any(name => CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                name, token, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0));
    }
}
