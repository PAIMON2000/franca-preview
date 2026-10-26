using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Franca.Services.UePak;

namespace Franca.Services;

public sealed partial class UeStaticService
{
    private sealed record TableDocument(Resource Resource, UeTextDataTable Source, UeTextDataTable Target);

    private static List<TableDocument> ReadTables(PakFileReader pak, string targetCulture)
    {
        var suffix = targetCulture switch { "zh-Hans" => "ZH_CN", "zh-Hant" => "ZH_TW", "ja" => "JA", _ => throw new ArgumentException("Unsupported table culture slot") };
        var documents = new List<TableDocument>();
        // Text/Database contains reference/metadata tables with other schemas; do not treat them as text.
        foreach (var entry in pak.Entries.Where(e => e.Path.EndsWith("EN.uasset", StringComparison.Ordinal)
                     && e.Path.Contains("/GameText/Database/", StringComparison.Ordinal)))
        {
            var targetPath = entry.Path[..^"EN.uasset".Length] + suffix + ".uasset";
            if (pak.Find(targetPath) is null) continue;
            UeTextDataTable Read(string path)
            {
                var export = pak.Find(Path.ChangeExtension(path, ".uexp")) ?? throw new InvalidDataException("Missing table export: " + path);
                return UeTextDataTable.Parse(pak.Extract(pak.Find(path)!), pak.Extract(export));
            }
            var source = Read(entry.Path); var target = Read(targetPath);
            var sourceKeys = source.Rows.Select(r => (r.Key, r.Id)).ToHashSet();
            var targetKeys = target.Rows.Select(r => (r.Key, r.Id)).ToHashSet();
            var mismatch = sourceKeys.Except(targetKeys).Count() + targetKeys.Except(sourceKeys).Count();
            if (mismatch > 0) throw new InvalidDataException("Table row identities differ: " + entry.Path);
            var targets = target.Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
            if (source.Rows.Any(r => r.Text.Length > 0 && targets[r.Key].TextKey is { } key && key != r.TextKey))
                throw new InvalidDataException("Source and target text keys differ: " + targetPath);
            documents.Add(new TableDocument(new Resource(entry.Path, targetPath, source.Rows.Count, target.Rows.Count, mismatch, 0), source, target));
        }
        return documents;
    }

    public static IReadOnlyList<Resource> InspectTables(string pakPath, string targetCulture = "zh-Hans")
    {
        using var pak = PakFileReader.Open(pakPath);
        return ReadTables(pak, targetCulture).Select(d => d.Resource).ToArray();
    }

    public async Task<Report> PrepareTablesAsync(string gameDirectory, string pakPath, int appId, string outputRoot,
        IReadOnlyCollection<string> selectedSourcePaths, AppSettings settings,
        IProgress<StaticTextTranslationService.StaticTranslationProgress>? progress, CancellationToken ct,
        string targetCulture = "zh-Hans")
    {
        var game = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        RejectReparseAncestors(game); RequireOutsideGame(game, outputRoot); RequireOutsideGame(game, _packsRoot);
        if (!Path.GetFullPath(pakPath).StartsWith(game, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Source pak is outside selected game");
        if (appId <= 0 || settings.SourceLanguage != "en" || settings.TargetLanguage is not ("ja" or "zh-CN")) throw new ArgumentException("Invalid table translation identity");
        using var pak = PakFileReader.Open(pakPath);
        var selected = ReadTables(pak, targetCulture).Where(d => selectedSourcePaths.Contains(d.Resource.SourcePath, StringComparer.Ordinal)).ToArray();
        if (selected.Length == 0 || selected.Length != selectedSourcePaths.Distinct(StringComparer.Ordinal).Count()) throw new ArgumentException("Selected tables not found");
        var snapshot = settings.Snapshot();
        var texts = selected.SelectMany(d => d.Source.Rows).Where(r => r.Text.Length > 0).Select(r => r.Text).Distinct(StringComparer.Ordinal).ToArray();
        var result = await _translator.TranslateAsync(appId, texts, snapshot, true, progress, ct);
        var items = new List<PakFileWriter.Item>();
        foreach (var document in selected)
        {
            var replacements = document.Source.Rows.Where(r => r.Text.Length > 0).ToDictionary(r => r.Key,
                r => result.Translations.GetValueOrDefault(r.Text) ?? r.Text, StringComparer.Ordinal);
            var targetNamespaces = document.Target.Rows.Where(r => r.Namespace is not null).Select(r => r.Namespace!).Distinct(StringComparer.Ordinal).ToArray();
            var targetRows = document.Target.Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
            var identities = new Dictionary<string, (string Namespace, string TextKey)>(StringComparer.Ordinal);
            foreach (var row in document.Source.Rows.Where(r => r.Text.Length > 0))
            {
                if (targetRows[row.Key].TextStart >= 0) continue;
                if (targetNamespaces.Length != 1 || row.TextKey is null) throw new InvalidDataException("Cannot establish empty target text identity");
                identities.Add(row.Key, (targetNamespaces[0], row.TextKey));
            }
            var (header, body) = document.Target.Rewrite(replacements, identities);
            items.Add(new() { Path = document.Resource.TargetPath, Data = header });
            items.Add(new() { Path = Path.ChangeExtension(document.Resource.TargetPath, ".uexp"), Data = body });
        }
        var bytes = PakFileWriter.Write(pak.MountPoint, pak.PathHashSeed, items);
        using (var check = PakFileReader.FromBytes(bytes))
            foreach (var item in items)
                if (!check.Extract(check.Find(item.Path)!).SequenceEqual(item.Data)) throw new InvalidDataException("Table pak roundtrip failed");
        ct.ThrowIfCancellationRequested();
        var generation = Path.Combine(Path.GetFullPath(outputRoot), "prepared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(generation);
        var report = new Report(generation, selected.Length, selected.Sum(d => d.Source.Rows.Count), texts.Length,
            result.Translations.Keys.Count(t => TranslationTextSegmenter.Split(t).Any(p => p.NeedsTranslation)),
            result.Missing, result.Cached, result.Missing > 0, 0,
            result.Translations.Keys.Count(t => !TranslationTextSegmenter.Split(t).Any(p => p.NeedsTranslation)));
        var committed = false;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(generation, "pakchunk99-WindowsNoEditor_P.pak"), bytes, ct);
            var manifest = new { Status = "PreparedNotInstalled", Format = "CookedUE4TextDataTable", Report = report,
                AppId = appId, SourcePak = Path.GetFullPath(pakPath), SourceIndexSha1 = pak.IndexSha1,
                PreparedSha256 = Convert.ToHexString(SHA256.HashData(bytes)), SourceLanguage = "en", TargetLanguage = snapshot.TargetLanguage,
                TargetCultureSlot = targetCulture, Resources = selected.Select(d => d.Resource), CreatedUtc = DateTimeOffset.UtcNow,
                EmptySourceValues = "Original target value preserved", UntranslatedValues = "English source; incomplete publication is never installable" };
            await File.WriteAllTextAsync(Path.Combine(generation, "manifest.tmp"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(Path.Combine(generation, "manifest.tmp"), Path.Combine(generation, "manifest.json")); committed = true;
        }
        finally { if (!committed) Directory.Delete(generation, true); }
        return report;
    }
}
