using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Franca.Services;

// All game writes are journalled before mutation. Backups and completed manifests are retained.
internal sealed class InstallationTransaction
{
    internal sealed class InstallationFileRecord
    {
        public string Path { get; set; } = "";
        public string? OriginalHash { get; set; }
        public string InstalledHash { get; set; } = "";
    }
    internal sealed class InstallationJournal
    {
        public string Status { get; set; } = "pending";
        public List<InstallationFileRecord> Files { get; set; } = new();
        public List<string> Directories { get; set; } = new();
    }

    private readonly string _game;
    private readonly string _record;
    private readonly InstallationJournal _journal;
    private string Manifest => System.IO.Path.Combine(_record, "manifest.json");

    private InstallationTransaction(string game, string record, InstallationJournal journal)
    { _game = System.IO.Path.GetFullPath(game); _record = record; _journal = journal; }

    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative))
            throw new InvalidDataException("Invalid installation path: " + relative);
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
        var result = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installation path escapes target: " + relative);
        if (relative.Split('\\', '/').Any(p => p.Contains(':') || p.TrimEnd(' ', '.') != p || p is "." or ".."))
            throw new InvalidDataException("Unsafe installation path: " + relative);
        for (var p = result; p is not null; p = System.IO.Path.GetDirectoryName(p))
        {
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation refuses reparse points: " + p);
            if (string.Equals(p, fullRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
        return result;
    }

    // Both archives are validated and staged before the first game-directory mutation.
    internal static void Extract(string archive, string stage, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count == 0) throw new InvalidDataException("Empty runtime archive.");
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (name.EndsWith(System.IO.Path.DirectorySeparatorChar))
            { SafePath(stage, name.TrimEnd(System.IO.Path.DirectorySeparatorChar)); continue; }
            if (name.StartsWith(".franca", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive may not write transaction records.");
            // BepInEx ships a generic root changelog; it must never replace the game's own.
            if (string.Equals(name, "changelog.txt", StringComparison.OrdinalIgnoreCase))
                name = System.IO.Path.Combine("BepInEx", "franca-package-notices", "changelog.txt");
            var target = SafePath(stage, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            input.CopyTo(output);
            if (output.Length != entry.Length) throw new InvalidDataException("Truncated archive entry.");
        }
    }

    internal static void Recover(string game, Action<string> log)
    {
        var records = SafePath(game, ".franca/installations");
        if (!Directory.Exists(records)) return;
        foreach (var directory in Directory.GetDirectories(records).Order())
        {
            var file = SafePath(game, System.IO.Path.GetRelativePath(game,
                System.IO.Path.Combine(directory, "manifest.json")));
            if (!File.Exists(file)) continue; // Backup preparation was interrupted before pending was published.
            var relative = System.IO.Path.GetRelativePath(game, file);
            SafePath(game, relative);
            var journal = JsonSerializer.Deserialize<InstallationJournal>(File.ReadAllText(file))
                ?? throw new InvalidDataException("Unreadable install manifest: " + file);
            if (journal.Status is "pending" or "recovery-required")
                new InstallationTransaction(game, System.IO.Path.GetDirectoryName(file)!, journal).Rollback(log);
            else if (journal.Status is not ("committed" or "rolled-back"))
                throw new InvalidDataException("Unknown installation state: " + file);
        }
    }

    internal static bool HasPending(string game)
    {
        try
        {
            var records = SafePath(game, ".franca/installations");
            if (!Directory.Exists(records)) return false;
            foreach (var directory in Directory.GetDirectories(records))
            {
                var file = SafePath(game, System.IO.Path.GetRelativePath(game,
                    System.IO.Path.Combine(directory, "manifest.json")));
                if (!File.Exists(file)) continue;
                var journal = JsonSerializer.Deserialize<InstallationJournal>(File.ReadAllText(file));
                if (journal is null || journal.Status is not ("committed" or "rolled-back")) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return true; }
    }

    internal static FileStream AcquireLock(string game)
    {
        var path = SafePath(game, ".franca/install.lock");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal static void Install(string game, string stage, Action validate, Action<string> log,
        CancellationToken ct = default, Action<int>? afterWrite = null)
    {
        Recover(game, log);
        var record = SafePath(game, ".franca/installations/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(record, "backup"));
        var tx = new InstallationTransaction(game, record, new InstallationJournal());
        // Snapshot every overwritten file before publishing the pending journal.
        foreach (var source in Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Order())
        {
            var relative = System.IO.Path.GetRelativePath(stage, source);
            if (relative.StartsWith(".franca", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Reserved installation path.");
            var target = SafePath(game, relative);
            var entry = new InstallationFileRecord { Path = relative, InstalledHash = Hash(source) };
            if (File.Exists(target))
            {
                entry.OriginalHash = Hash(target);
                var backup = SafePath(System.IO.Path.Combine(record, "backup"), relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!);
                File.Copy(target, backup);
            }
            tx._journal.Files.Add(entry);
            for (var dir = System.IO.Path.GetDirectoryName(target); dir != null && !Directory.Exists(dir);
                 dir = System.IO.Path.GetDirectoryName(dir))
            {
                var rel = System.IO.Path.GetRelativePath(game, dir);
                if (!tx._journal.Directories.Contains(rel)) tx._journal.Directories.Add(rel);
            }
        }
        tx.Save();
        log("安装事务备份与清单：" + record);
        try
        {
            var index = 0;
            foreach (var entry in tx._journal.Files)
            {
                ct.ThrowIfCancellationRequested();
                var target = SafePath(game, entry.Path);
                if ((File.Exists(target) ? Hash(target) : null) != entry.OriginalHash)
                    throw new IOException("File changed during installation: " + entry.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                var incoming = System.IO.Path.Combine(record, "incoming");
                File.Copy(SafePath(stage, entry.Path), incoming, true);
                File.Move(incoming, target, true);
                afterWrite?.Invoke(++index);
            }
            ct.ThrowIfCancellationRequested();
            validate();
            tx._journal.Status = "committed";
            tx.Save();
        }
        catch (Exception installationError)
        {
            try { tx.Rollback(log); }
            catch (Exception recoveryError)
            { throw new AggregateException("安装未完成，回滚遇到冲突；请保留事务备份。", installationError, recoveryError); }
            throw;
        }
    }

    private void Save()
    {
        File.WriteAllText(Manifest + ".tmp", JsonSerializer.Serialize(_journal, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(Manifest + ".tmp", Manifest, true);
    }

    private void Rollback(Action<string> log)
    {
        var errors = new List<Exception>();
        foreach (var entry in _journal.Files.AsEnumerable().Reverse())
        {
            try
            {
                var target = SafePath(_game, entry.Path);
                var current = File.Exists(target) ? Hash(target) : null;
                if (current == entry.OriginalHash) continue;
                if (current != entry.InstalledHash) throw new IOException("Rollback conflict: " + target);
                if (entry.OriginalHash is null) File.Delete(target);
                else
                {
                    var backup = SafePath(System.IO.Path.Combine(_record, "backup"), entry.Path);
                    if (Hash(backup) != entry.OriginalHash) throw new IOException("Backup checksum mismatch: " + backup);
                    var incoming = System.IO.Path.Combine(_record, "restore");
                    File.Copy(backup, incoming, true);
                    File.Move(incoming, target, true);
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        foreach (var relative in _journal.Directories.OrderByDescending(p => p.Length))
        {
            try
            {
                var dir = SafePath(_game, relative);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        _journal.Status = errors.Count == 0 ? "rolled-back" : "recovery-required";
        Save();
        log($"安装事务 {_journal.Status}：{_record}");
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
