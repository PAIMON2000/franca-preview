using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Franca.Services.UePak;

namespace Franca.Services;

// Preparation only. Never installs a pak or writes into a game directory.
// Existing target locres slots supply namespace/key/source hashes; only values change.
public sealed partial class UeStaticService
{
    public sealed record Resource(string SourcePath, string TargetPath, int Entries, int TargetEntries,
        int KeyMismatches, int SourceHashMismatches);
    public sealed record Report(string OutputDirectory, int Resources, int Entries, int UniqueTexts,
        int Translated, int Missing, int Cached, bool Incomplete, int PreservedTargetOnlyEntries, int LiteralOnlyTexts);
    private readonly StaticTextTranslationService _translator;
    private readonly string _packsRoot;
    public UeStaticService(string packsRoot,
        Func<string, string, string, string, AppSettings, CancellationToken, Task<string?>>? provider = null)
    {
        _packsRoot = packsRoot;
        _translator = new StaticTextTranslationService(new PackStore(packsRoot), provider);
    }

    private static void RequireOutsideGame(string gameRoot, string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase) || gameRoot.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Preparation output and caches must be outside the game directory");
        RejectReparseAncestors(full);
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Game, output and cache directories cannot traverse a reparse point");
    }

    public static IReadOnlyList<Resource> Inspect(string pakPath, string sourceCulture = "en", string targetCulture = "zh-Hans")
    {
        using var pak = PakFileReader.Open(pakPath);
        return ReadResources(pak, sourceCulture, targetCulture);
    }

    private static List<Resource> ReadResources(PakFileReader pak, string sourceCulture, string targetCulture)
    {
        if (!PackStore.IsValidLanguage(sourceCulture) || !PackStore.IsValidLanguage(targetCulture) || sourceCulture == targetCulture)
            throw new ArgumentException("Invalid or identical culture slots");
        var marker = "/" + sourceCulture + "/";
        var result = new List<Resource>();
        foreach (var entry in pak.Entries.Where(e => e.Path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase)
                     && e.Path.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            var pos = entry.Path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var target = entry.Path[..pos] + "/" + targetCulture + "/" + entry.Path[(pos + marker.Length)..];
            if (pak.Find(target) is null) continue;
            var resource = LocResFile.Parse(pak.Extract(entry));
            if (resource.Version == 3 && resource.Namespaces.Any(n =>
                    n.Hash != CityHash64.StringHash32(n.Name) || n.Entries.Any(e => e.KeyHash != CityHash64.StringHash32(e.Key))))
                throw new InvalidDataException("locres v3 text key hash mismatch: " + entry.Path);
            if (pak.PathHashIndex.Count > 0 && pak.FindByPathHash(Fnv64.HashPath(entry.Path, pak.PathHashSeed))?.Path != entry.Path)
                throw new InvalidDataException("pak path hash mismatch: " + entry.Path);
            var targetResource = LocResFile.Parse(pak.Extract(pak.Find(target)!));
            var sourceKeys = resource.Namespaces.SelectMany(n => n.Entries.Select(e => (Key: (n.Name, e.Key), e.SourceStringHash)))
                .ToDictionary(e => e.Key, e => e.SourceStringHash);
            var targetKeys = targetResource.Namespaces.SelectMany(n => n.Entries.Select(e => (Key: (n.Name, e.Key), e.SourceStringHash)))
                .ToDictionary(e => e.Key, e => e.SourceStringHash);
            result.Add(new Resource(entry.Path, target, sourceKeys.Count, targetKeys.Count,
                sourceKeys.Keys.Except(targetKeys.Keys).Count() + targetKeys.Keys.Except(sourceKeys.Keys).Count(),
                sourceKeys.Count(e => targetKeys.TryGetValue(e.Key, out var h) && h != e.Value)));
        }
        return result;
    }

    public async Task<Report> PrepareAsync(string gameDirectory, string pakPath, int appId, string outputRoot,
        IReadOnlyCollection<string> selectedSourcePaths, AppSettings settings,
        IProgress<StaticTextTranslationService.StaticTranslationProgress>? progress, CancellationToken ct,
        bool freeOnly = true, string targetCulture = "zh-Hans")
    {
        var gameRoot = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        RejectReparseAncestors(gameRoot);
        var output = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        RequireOutsideGame(gameRoot, output);
        RequireOutsideGame(gameRoot, _packsRoot);
        if (!Path.GetFullPath(pakPath).StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source pak must belong to the explicitly selected game directory");
        if (appId <= 0 || settings.SourceLanguage != "en" || settings.TargetLanguage is not ("zh-CN" or "ja"))
            throw new NotSupportedException("Experimental preparation currently supports English -> zh-CN or ja");
        if (selectedSourcePaths.Count == 0) throw new ArgumentException("Select at least one resource");
        var snapshot = settings.Snapshot();
        using var pak = PakFileReader.Open(pakPath);
        var resources = ReadResources(pak, "en", targetCulture);
        var selected = resources.Where(r => selectedSourcePaths.Contains(r.SourcePath, StringComparer.Ordinal)).ToArray();
        if (selected.Length != selectedSourcePaths.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("An explicitly selected resource is absent or has no target slot");
        var documents = selected.Select(r => (Resource: r, Source: LocResFile.Parse(pak.Extract(pak.Find(r.SourcePath)!)),
            Target: LocResFile.Parse(pak.Extract(pak.Find(r.TargetPath)!)))).ToArray();
        // Match keys and source hashes before any provider requests; refuse changed source revisions.
        foreach (var (_, source, target) in documents)
        {
            var targetKeys = target.Namespaces.SelectMany(n => n.Entries.Select(e => (n.Name, e.Key, e.SourceStringHash))).ToHashSet();
            var sourceKeys = source.Namespaces.SelectMany(n => n.Entries.Select(e => (n.Name, e.Key, e.SourceStringHash))).ToHashSet();
            if (!sourceKeys.IsSubsetOf(targetKeys)) throw new InvalidDataException("Source keys are missing or source hashes differ in target locres");
        }
        var texts = documents.SelectMany(d => d.Source.Namespaces).SelectMany(n => n.Entries).Select(e => e.Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        var result = await _translator.TranslateAsync(appId, texts, snapshot, freeOnly, progress, ct);
        ct.ThrowIfCancellationRequested();
        var items = new List<PakFileWriter.Item>();
        foreach (var (resource, source, target) in documents)
        {
            var sourceValues = source.ToDictionary();
            foreach (var ns in target.Namespaces)
                foreach (var entry in ns.Entries)
                {
                    if (!sourceValues.TryGetValue(ns.Name, out var sourceEntries)
                        || !sourceEntries.TryGetValue(entry.Key, out var text)) continue; // target-only native entries
                    entry.Value = result.Translations.GetValueOrDefault(text) ?? text;
                }
            // UE 4.26.2 rejected the compressed overlay despite UE5 UnrealPak verification.
            // Localization-only overlays are small; use uncompressed payloads until legacy block decoding is verified.
            items.Add(new PakFileWriter.Item { Path = resource.TargetPath, Data = target.Serialize(), Compress = false });
        }
        var bytes = PakFileWriter.Write(pak.MountPoint, pak.PathHashSeed, items);
        using (var check = PakFileReader.FromBytes(bytes))
            foreach (var item in items)
                if (!check.Extract(check.Find(item.Path)!).AsSpan().SequenceEqual(item.Data))
                    throw new InvalidDataException("Prepared pak verification failed");
        Directory.CreateDirectory(output);
        var generation = Guid.NewGuid().ToString("N");
        var published = Path.Combine(output, "prepared-" + generation);
        var staging = published;
        Directory.CreateDirectory(staging);
        var committed = false;
        var report = new Report(published, items.Count, selected.Sum(r => r.Entries), texts.Length,
            result.Translations.Keys.Count(t => t.Length <= TranslationTextSegmenter.MaxTextLength && TranslationTextSegmenter.Split(t).Any(p => p.NeedsTranslation)),
            result.Missing, result.Cached, result.Missing > 0,
            selected.Sum(r => r.TargetEntries - r.Entries),
            result.Translations.Keys.Count(t => !TranslationTextSegmenter.Split(t).Any(p => p.NeedsTranslation)));
        try
        {
            var name = "pakchunk99-WindowsNoEditor_P.pak";
            await File.WriteAllBytesAsync(Path.Combine(staging, name), bytes, ct);
            var manifest = new { Status = "PreparedNotInstalled", Report = report, AppId = appId,
                SourcePak = Path.GetFullPath(pakPath), SourceLength = new FileInfo(pakPath).Length,
                SourceIndexSha1 = pak.IndexSha1,
                PreparedSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                SourceLanguage = snapshot.SourceLanguage, TargetLanguage = snapshot.TargetLanguage,
                TargetCultureSlot = targetCulture,
                Resources = selected, CreatedUtc = DateTimeOffset.UtcNow,
                EnabledProviders = EndpointCatalog.All.Where(e => !snapshot.DisabledEndpoints.Contains(e.Id)
                    && (!freeOnly || e.Category == EndpointCategory.Free)).Select(e => e.Id).ToArray(),
                UntranslatedValues = "English source; not cached as successful translations",
                TargetOnlyValues = "Original target entries without an English counterpart are preserved, not translated or cached" };
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.tmp"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested();
            // A generation is published only when the completion manifest appears.
            File.Move(Path.Combine(staging, "manifest.tmp"), Path.Combine(staging, "manifest.json"));
            committed = true;
        }
        finally { if (!committed && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        return report;
    }
}
