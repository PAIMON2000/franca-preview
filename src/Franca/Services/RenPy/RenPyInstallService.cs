using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Franca.Services.RenPy;

// Ren'Py tl-directory installation on top of the journaled transaction
// pattern. Installation only ADDS game/tl/<lang>/ files; no original game
// file is ever overwritten (foreign-file refusal is inherited from
// InstallationTransaction staging). Manifests reuse the journal shape so the
// existing crash Recovery pass rolls back interrupted installs too.
// Callers must ensure the game is stopped before install/uninstall.
public sealed class RenPyInstallService
{
    public sealed class Journal
    {
        public string Status { get; set; } = "pending";
        public List<FileRecord> Files { get; set; } = new();
        public List<string> Directories { get; set; } = new();
        public string Engine { get; set; } = "";
        public string Language { get; set; } = "";
        public string SourceStringsSha256 { get; set; } = "";
    }

    public sealed class FileRecord
    {
        public string Path { get; set; } = "";
        public string? OriginalHash { get; set; }
        public string InstalledHash { get; set; } = "";
    }

    public sealed record Result(string State, string Record);

    private readonly string _game;
    private string Manifest(string record) => Path.Combine(record, "manifest.json");

    public RenPyInstallService(string gameDirectory)
    { _game = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private IEnumerable<(string Record, Journal Journal)> ReadJournals()
    {
        var root = Path.Combine(_game, ".franca", "installations");
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.GetDirectories(root).Order())
        {
            var file = Path.Combine(dir, "manifest.json");
            if (!File.Exists(file)) continue;
            Journal? journal;
            try { journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(file)); }
            catch (JsonException) { continue; }
            if (journal is null) continue;
                if (journal.Engine != "renpy"
                    && !(journal.Files.Count > 0
                        && journal.Files.All(f => f.Path.Replace('\\', '/').StartsWith("game/tl/", StringComparison.Ordinal)))) continue;
                yield return (dir, journal);
        }
    }

    public static bool IsInstalled(string gameDirectory) =>
        new RenPyInstallService(gameDirectory).ReadJournals().Any(t =>
            t.Journal.Status == "committed" && t.Journal.Files.Count > 0);

    // Install parameters derived from the prepared manifest; the live game
    // scripts are re-extracted and hashed so a game update is detected before
    // any write.
    public Result Install(string preparedManifestPath, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        InstallationTransaction.Recover(_game, log);
        var manifestPath = Path.GetFullPath(preparedManifestPath);
        var dir = Path.GetDirectoryName(manifestPath)!;
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.GetProperty("Status").GetString() != "PreparedNotInstalled")
            throw new InvalidDataException("Prepared manifest is not in PreparedNotInstalled state.");
        var expectedHash = root.GetProperty("SourceStringsSha256").GetString() ?? "";
        var language = root.GetProperty("Language").GetString() ?? "";

        var liveHash = RenPyStaticService.StringsHash(
            RenPyExtractService.ExtractStrings(RenPyExtractService.Scan(_game))
                .Select(s => s.Text).Where(t => t.Trim().Length > 0).Distinct(StringComparer.Ordinal));
        if (!string.Equals(liveHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Game scripts differ from the prepared manifest (game updated?); prepare again.");

        var stage = Path.Combine(dir, "stage");
        if (!Directory.Exists(stage)) throw new FileNotFoundException("Prepared stage directory missing.", stage);

        InstallationTransaction.Install(_game, stage, Validate, log, ct);
        // The transaction wrote its own journal; find the newest committed
        // record and verify it is exactly our tl overlay before stamping it.
        var record = Directory.GetDirectories(Path.Combine(_game, ".franca", "installations"))
            .Select(d => (Dir: d, File: Path.Combine(d, "manifest.json")))
            .Where(t => File.Exists(t.File))
            .OrderByDescending(t => File.GetLastWriteTimeUtc(t.File))
            .FirstOrDefault(t =>
            {
                try
                {
                    var j = JsonSerializer.Deserialize<Journal>(File.ReadAllText(t.File));
                    return j is { Status: "committed" }
                        && j.Files.Count > 0
                        && j.Files.All(f => f.Path.Replace('\\', '/').StartsWith("game/tl/" + language + "/"));
                }
                catch (JsonException) { return false; }
            }).Dir;
        if (record is null) throw new InvalidDataException("Install transaction record not found after commit.");
        // Stamp the journal with RenPy metadata for update detection and uninstall.
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(Manifest(record)))!;
        journal.Engine = "renpy";
        journal.Language = language;
        journal.SourceStringsSha256 = expectedHash;
        File.WriteAllText(Manifest(record), JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
        return new Result("installed", record);
    }

    private void Validate()
    {
        // The staged tree was hash-verified by the transaction; nothing else to
        // validate before commit for an additive overlay.
    }

    public Result Uninstall(Action<string>? log = null)
    {
        log ??= _ => { };
        var target = ReadJournals().FirstOrDefault(t => t.Journal.Status == "committed");
        if (target.Journal is null) return new Result("not-installed", "");
        Rollback(target.Record, target.Journal, log);
        return new Result("uninstalled", target.Record);
    }

    private void Rollback(string record, Journal journal, Action<string> log)
    {
        var errors = new List<Exception>();
        foreach (var entry in journal.Files.AsEnumerable().Reverse())
        {
            try
            {
                var target = InstallationTransaction.SafePath(_game, entry.Path);
                var current = File.Exists(target) ? HashFile(target) : null;
                if (string.Equals(current, entry.OriginalHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(current, entry.InstalledHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Rollback conflict: " + target);
                if (entry.OriginalHash is null) File.Delete(target);
                else
                {
                    var backup = InstallationTransaction.SafePath(Path.Combine(record, "backup"), entry.Path);
                    if (!string.Equals(HashFile(backup), entry.OriginalHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Backup checksum mismatch: " + backup);
                    File.Copy(backup, target, overwrite: true);
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        foreach (var relative in journal.Directories.OrderByDescending(p => p.Length))
        {
            try
            {
                var dir = InstallationTransaction.SafePath(_game, relative);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        journal.Status = errors.Count == 0 ? "rolled-back" : "recovery-required";
        File.WriteAllText(Manifest(record), JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
        log($"RenPy 安装事务 {journal.Status}：{record}");
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
