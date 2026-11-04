using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Franca.Services;

// UE override-pak installation on top of the journaled transaction pattern.
// The game's original pak is never written: installation adds/updates ONE
// override pak beside it; uninstall deletes only hash-matched own files.
// Manifests reuse the InstallationTransaction journal shape so the existing
// crash Recovery pass rolls back interrupted UE installs too.
// Callers must ensure the game is stopped before install/uninstall/repair.
public sealed class UePakInstallService
{
    public sealed class Journal
    {
        public string Status { get; set; } = "pending";
        public List<FileRecord> Files { get; set; } = new();
        public List<string> Directories { get; set; } = new();
        public bool UePak { get; set; }
        public string OriginalPak { get; set; } = "";
        public string OriginalSha256 { get; set; } = "";
        public string PreparedSha256 { get; set; } = "";
    }
    public sealed class FileRecord
    {
        public string Path { get; set; } = "";
        public string? OriginalHash { get; set; }
        public string InstalledHash { get; set; } = "";
    }
    public sealed record Result(string State, string Record, string OverridePath);

    private readonly string _game;
    private string Manifest(string record) => Path.Combine(record, "manifest.json");

    public UePakInstallService(string gameDirectory)
    { _game = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string Safe(string relative) => InstallationTransaction.SafePath(_game, relative);

    private IEnumerable<(string Record, Journal Journal)> ReadJournals()
    {
        var root = Path.Combine(_game, ".franca", "installations");
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.GetDirectories(root).Order())
        {
            var file = Path.Combine(dir, "manifest.json");
            if (!File.Exists(file)) continue;
            var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(file));
            if (journal is not null) yield return (dir, journal);
        }
    }

    public IReadOnlyList<Journal> InstalledJournals() => ReadJournals().Select(t => t.Journal).ToList();

    // 安装参数全部由准备产物清单推导，调用方只提供游戏目录与清单路径：
    // 原 pak 相对路径/基线哈希/索引 SHA1/长度逐项核对后才允许写入，防止张冠李戴。
    public sealed record PreparedPlan(string PreparedPakPath, string PakDirectoryRelative,
        string OverrideFileName, string OriginalPakRelative, string OriginalSha256);

    public static PreparedPlan ResolvePrepared(string gameDirectory, string preparedManifestPath, int? appId = null)
    {
        var game = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var manifestPath = Path.GetFullPath(preparedManifestPath);
        var dir = Path.GetDirectoryName(manifestPath)!;
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.GetProperty("Status").GetString() != "PreparedNotInstalled")
            throw new InvalidDataException("Prepared manifest is not in PreparedNotInstalled state.");
        if (appId is > 0 && root.GetProperty("AppId").GetInt32() != appId.Value)
            throw new InvalidDataException("Prepared manifest belongs to a different appId.");
        var sourcePak = root.GetProperty("SourcePak").GetString() ?? "";
        if (!Path.GetFullPath(sourcePak).StartsWith(game, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prepared manifest source pak is outside the selected game directory.");
        var originalRelative = Path.GetRelativePath(game, Path.GetFullPath(sourcePak)).Replace('\\', '/');
        var original = Path.Combine(game, originalRelative);
        if (!File.Exists(original))
            throw new FileNotFoundException("Original pak recorded in the prepared manifest is missing.", originalRelative);
        if (new FileInfo(original).Length != root.GetProperty("SourceLength").GetInt64())
            throw new InvalidDataException("Original pak size differs from the prepared manifest (game updated?); prepare again.");
        using (var pak = UePak.PakFileReader.Open(original))
            if (!pak.IndexSha1Valid || pak.IndexSha1 != root.GetProperty("SourceIndexSha1").GetString())
                throw new InvalidDataException("Original pak index SHA1 differs from the prepared manifest (game updated?); prepare again.");
        var prepared = Directory.GetFiles(dir, "*.pak");
        if (prepared.Length != 1)
            throw new FileNotFoundException("Expected exactly one prepared pak beside the manifest.", dir);
        var expectedPrepared = root.GetProperty("PreparedSha256").GetString() ?? "";
        var preparedPath = prepared[0];
        if (HashFile(preparedPath) != expectedPrepared)
            throw new InvalidDataException("Prepared pak hash differs from the manifest; the output directory is inconsistent.");
        var baseline = HashFile(original);
        var pakDirRelative = Path.GetDirectoryName(originalRelative)!.Replace('\\', '/');
        return new PreparedPlan(preparedPath, pakDirRelative, Path.GetFileName(preparedPath), originalRelative, baseline);
    }

    public static bool IsInstalled(string gameDirectory) =>
        new UePakInstallService(gameDirectory).InstalledJournals().Any(j =>
            j.UePak && j.Status == "committed" && j.Files.Count > 0);

    public Result Install(string preparedPakPath, string pakDirectoryRelative, string overrideFileName,
        string originalPakRelative, string expectedOriginalSha256, Action<string>? log = null,
        CancellationToken ct = default)
    {
        InstallationTransaction.Recover(_game, log ?? (_ => { }));
        var preparedHash = HashFile(preparedPakPath);
        var original = Safe(originalPakRelative);
        if (!File.Exists(original) || HashFile(original) != expectedOriginalSha256)
            throw new InvalidDataException("Game pak baseline mismatch (updated game or wrong directory); refusing to install.");
        var targetRelative = pakDirectoryRelative.TrimEnd('/', '\\') + "/" + overrideFileName;
        var target = Safe(targetRelative);
        string? originalHash = File.Exists(target) ? HashFile(target) : null;
        if (originalHash == preparedHash)
            return new Result("already-installed", "", targetRelative); // idempotent same generation
        if (originalHash is not null && !IsOwnOverride(originalHash))
            throw new IOException("Refusing to overwrite foreign file: " + targetRelative);

        var record = Path.Combine(_game, ".franca", "installations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(record, "backup"));
        var journal = new Journal
        {
            Status = "pending", UePak = true,
            OriginalPak = originalPakRelative, OriginalSha256 = expectedOriginalSha256,
            PreparedSha256 = preparedHash,
            Files = { new FileRecord { Path = targetRelative, OriginalHash = originalHash, InstalledHash = preparedHash } },
        };
        // backup/ holds the previous override for rollback; prepared copy enables repair.
        if (originalHash is not null) File.Copy(target, Path.Combine(record, "backup", overrideFileName));
        File.Copy(preparedPakPath, Path.Combine(record, "prepared.pak"));
        Save(record, journal);
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var incoming = Path.Combine(record, "incoming");
            File.Copy(preparedPakPath, incoming, true);
            File.Move(incoming, target, true);
            if (HashFile(target) != preparedHash || HashFile(original) != expectedOriginalSha256)
                throw new IOException("UE pak verification failed after write.");
            using (var check = UePak.PakFileReader.Open(target)) { }
            journal.Status = "committed";
            Save(record, journal);
            return new Result(originalHash is null ? "installed" : "updated", record, targetRelative);
        }
        catch (Exception installError)
        {
            try { Rollback(record, journal); }
            catch (Exception recoveryError)
            { throw new AggregateException("安装未完成，回滚遇到冲突；请保留事务备份。", installError, recoveryError); }
            throw;
        }
    }

    public Result Uninstall(Action<string>? log = null)
    {
        InstallationTransaction.Recover(_game, log ?? (_ => { }));
        Journal? current = null; string? currentRecord = null;
        foreach (var (record, journal) in ReadJournals())
            if (journal.UePak && journal.Status is "committed" && journal.Files.Count > 0)
            { current = journal; currentRecord = record; break; }
        if (current is null) return new Result("not-installed", "", "");
        var entry = current.Files[0];
        var target = Safe(entry.Path);
        if (File.Exists(target))
        {
            var hash = HashFile(target);
            if (hash != entry.InstalledHash)
                throw new IOException("Override pak was modified outside Franca; refusing to remove foreign content: " + entry.Path);
            File.Delete(target);
        }
        current.Status = "rolled-back";
        Save(currentRecord!, current);
        return new Result("uninstalled", currentRecord!, entry.Path);
    }

    public Result Repair(Action<string>? log = null)
    {
        InstallationTransaction.Recover(_game, log ?? (_ => { }));
        foreach (var (record, journal) in ReadJournals())
        {
            if (!journal.UePak || journal.Status is not ("committed" or "recovery-required") || journal.Files.Count == 0)
                continue;
            var entry = journal.Files[0];
            var target = Safe(entry.Path);
            if (File.Exists(target) && HashFile(target) == entry.InstalledHash)
                return new Result("intact", record, entry.Path);
            var backup = Path.Combine(record, "prepared.pak");
            if (!File.Exists(backup) || HashFile(backup) != entry.InstalledHash)
                throw new IOException("No verified prepared copy to repair from; reinstall the prepared pak.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var incoming = Path.Combine(record, "restore");
            File.Copy(backup, incoming, true);
            File.Move(incoming, target, true);
            if (HashFile(target) != entry.InstalledHash) throw new IOException("Repair verification failed.");
            journal.Status = "committed";
            Save(record, journal);
            return new Result("repaired", record, entry.Path);
        }
        return new Result("not-installed", "", "");
    }

    private bool IsOwnOverride(string hash)
        => ReadJournals().Any(t => t.Journal.UePak && t.Journal.Files.Any(f =>
            f.InstalledHash == hash && t.Journal.Status is "committed" or "rolled-back" or "pending"));

    private void Rollback(string record, Journal journal)
    {
        var entry = journal.Files[0];
        var target = Safe(entry.Path);
        var current = File.Exists(target) ? HashFile(target) : null;
        if (current == entry.InstalledHash)
        {
            if (entry.OriginalHash is null) File.Delete(target);
            else
            {
                var backup = Path.Combine(record, "backup", Path.GetFileName(entry.Path));
                if (HashFile(backup) != entry.OriginalHash) throw new IOException("Backup checksum mismatch.");
                File.Copy(backup, target, true);
            }
        }
        journal.Status = "rolled-back";
        Save(record, journal);
    }

    private void Save(string record, Journal journal)
    {
        Directory.CreateDirectory(record);
        File.WriteAllText(Manifest(record) + ".tmp", JsonSerializer.Serialize(journal,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(Manifest(record) + ".tmp", Manifest(record), true);
    }
}
