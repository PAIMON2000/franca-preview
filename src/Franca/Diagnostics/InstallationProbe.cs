using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Franca.Services;

namespace Franca.Diagnostics;

internal static class InstallationProbe
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "franca-install-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"RESULT\tINSTALLATION\t{name}\t{(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }
        (string game, string stage) Fixture(string name)
        {
            var game = Path.Combine(root, name, "game");
            var stage = Path.Combine(root, name, "stage");
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(game, "keep.txt"), "user");
            File.WriteAllText(Path.Combine(stage, "keep.txt"), "installed");
            Directory.CreateDirectory(Path.Combine(stage, "new"));
            File.WriteAllText(Path.Combine(stage, "new", "plugin.dll"), "plugin");
            return (game, stage);
        }
        void Log(string message) => Console.WriteLine(message);
        try
        {
            var (game, stage) = Fixture("success");
            InstallationTransaction.Install(game, stage, () => { }, Log);
            Check("commit_content", File.ReadAllText(Path.Combine(game, "keep.txt")) == "installed");
            var manifest = Directory.GetFiles(game, "manifest.json", SearchOption.AllDirectories).Single();
            Check("commit_manifest", File.ReadAllText(manifest).Contains("committed"));
            Check("original_backup", File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "backup", "keep.txt")) == "user");
            InstallationTransaction.Recover(game, Log);
            Check("committed_recovery_is_noop", File.Exists(Path.Combine(game, "new", "plugin.dll")));

            foreach (var kind in new[] { "write-error", "cancel", "validation-error" })
            {
                var f = Fixture(kind);
                using var cts = new CancellationTokenSource();
                try
                {
                    InstallationTransaction.Install(f.game, f.stage,
                        () => { if (kind == "validation-error") throw new IOException("Injected validation error"); }, Log, cts.Token,
                        i => { if (kind == "write-error") throw new IOException("Injected disk error"); if (kind == "cancel") cts.Cancel(); });
                    Check(kind + "_throws", false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { Check(kind + "_throws", true); }
                Check(kind + "_original_restored", File.ReadAllText(Path.Combine(f.game, "keep.txt")) == "user");
                Check(kind + "_new_file_removed", !File.Exists(Path.Combine(f.game, "new", "plugin.dll")));
                Check(kind + "_new_directory_removed", !Directory.Exists(Path.Combine(f.game, "new")));
                Check(kind + "_journal", File.ReadAllText(Directory.GetFiles(f.game, "manifest.json", SearchOption.AllDirectories).Single()).Contains("rolled-back"));
            }

            var conflict = Fixture("conflict");
            try
            {
                InstallationTransaction.Install(conflict.game, conflict.stage, () => { }, Log, afterWrite: _ =>
                {
                    File.WriteAllText(Path.Combine(conflict.game, "keep.txt"), "external edit");
                    throw new IOException("Injected concurrent edit");
                });
            }
            catch (AggregateException) { }
            Check("conflict_user_edit_preserved", File.ReadAllText(Path.Combine(conflict.game, "keep.txt")) == "external edit");
            Check("conflict_recovery_required", File.ReadAllText(Directory.GetFiles(conflict.game, "manifest.json", SearchOption.AllDirectories).Single()).Contains("recovery-required"));
            try { InstallationTransaction.Recover(conflict.game, Log); Check("conflict_recovery_blocks", false); }
            catch (AggregateException) { Check("conflict_recovery_blocks", true); }

            var interrupted = Fixture("interrupted");
            var record = Path.Combine(interrupted.game, ".franca", "installations", "simulated-crash");
            Directory.CreateDirectory(Path.Combine(record, "backup"));
            var original = Path.Combine(interrupted.game, "keep.txt");
            File.Copy(original, Path.Combine(record, "backup", "keep.txt"));
            var journal = new InstallationTransaction.InstallationJournal();
            journal.Files.Add(new() { Path = "keep.txt", OriginalHash = InstallationTransaction.Hash(original),
                InstalledHash = InstallationTransaction.Hash(Path.Combine(interrupted.stage, "keep.txt")) });
            File.WriteAllText(Path.Combine(record, "manifest.json"), JsonSerializer.Serialize(journal));
            File.Copy(Path.Combine(interrupted.stage, "keep.txt"), original, true);
            Check("pending_visible_to_health", InstallationTransaction.HasPending(interrupted.game));
            Check("pending_health_issue", TranslationHealthService.Inspect(interrupted.game,
                new AppSettings { TargetLanguage = "zh-CN" }, null).Issues.Any(i => i.Code == "installation-incomplete"));
            InstallationTransaction.Recover(interrupted.game, Log);
            Check("pending_crash_recovered", File.ReadAllText(original) == "user");
            Check("pending_journal_updated", File.ReadAllText(Path.Combine(record, "manifest.json")).Contains("rolled-back"));
            Check("recovery_clears_pending", !InstallationTransaction.HasPending(interrupted.game));
            using (InstallationTransaction.AcquireLock(game))
            {
                try { using var duplicate = InstallationTransaction.AcquireLock(game); Check("exclusive_game_lock", false); }
                catch (IOException) { Check("exclusive_game_lock", true); }
            }

            var archive = Path.Combine(root, "unsafe.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open())) writer.Write("escape");
            try { InstallationTransaction.Extract(archive, stage, default); Check("zip_traversal_rejected", false); }
            catch (InvalidDataException) { Check("zip_traversal_rejected", true); }
            Check("zip_did_not_escape", !File.Exists(Path.Combine(root, "success", "escape.txt")));
            File.WriteAllText(Path.Combine(root, "corrupt.zip"), "truncated");
            try { InstallationTransaction.Extract(Path.Combine(root, "corrupt.zip"), stage, default); Check("corrupt_zip_rejected", false); }
            catch (InvalidDataException) { Check("corrupt_zip_rejected", true); }
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { InstallationTransaction.Extract(archive, stage, canceled.Token); Check("staging_cancel", false); }
            catch (OperationCanceledException) { Check("staging_cancel", true); }

            var notices = Fixture("notices");
            File.WriteAllText(Path.Combine(notices.game, "changelog.txt"), "game release notes");
            var noticeArchive = Path.Combine(root, "notice.zip");
            using (var zip = ZipFile.Open(noticeArchive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("changelog.txt").Open())) writer.Write("BepInEx release notes");
            InstallationTransaction.Extract(noticeArchive, notices.stage, default);
            InstallationTransaction.Install(notices.game, notices.stage, () => { }, Log);
            Check("game_changelog_preserved", File.ReadAllText(Path.Combine(notices.game, "changelog.txt")) == "game release notes");
            Check("package_changelog_retained", File.ReadAllText(Path.Combine(notices.game, "BepInEx", "franca-package-notices", "changelog.txt")) == "BepInEx release notes");
        }
        finally { Directory.Delete(root, true); }
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
