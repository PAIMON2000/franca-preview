using System.IO;
using System.Security.Cryptography;
using Franca.Services;
using Franca.Services.UePak;

namespace Franca.Diagnostics;

// Synthetic game directories only; never touches a real game.
internal static class UeInstallProbe
{
    public static async Task RunAsync()
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"RESULT\tUEINSTALL\t{name}\t{(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }
        var root = Path.Combine(Path.GetTempPath(), "franca-ue-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var game = Path.Combine(root, "game");
            var paks = Path.Combine(game, "Octopath_Traveler2", "Content", "Paks");
            Directory.CreateDirectory(paks);
            // original pak + prepared override, arbitrary but valid pak bytes
            var original = PakFileWriter.Write("../../../", 42, new List<PakFileWriter.Item>
            { new() { Path = "Game/Content/X.uasset", Data = [1, 2, 3] } });
            var prepared = PakFileWriter.Write("../../../", 42, new List<PakFileWriter.Item>
            { new() { Path = "Game/Content/Y.uasset", Data = [4, 5, 6, 7] } });
            var originalHash = Convert.ToHexString(SHA256.HashData(original));
            var originalPath = Path.Combine(paks, "Game-WindowsNoEditor.pak");
            await File.WriteAllBytesAsync(originalPath, original);
            var preparedPath = Path.Combine(root, "prepared.pak");
            await File.WriteAllBytesAsync(preparedPath, prepared);

            const string pakDir = "Octopath_Traveler2/Content/Paks";
            const string overrideName = "pakchunk99-WindowsNoEditor_P.pak";
            var service = new UePakInstallService(game);

            var first = service.Install(preparedPath, pakDir, overrideName, pakDir + "/Game-WindowsNoEditor.pak", originalHash);
            Check("install_committed", first.State == "installed"
                && File.Exists(Path.Combine(paks, overrideName))
                && service.InstalledJournals().Single(j => j.UePak).Status == "committed");
            Check("original_untouched", (await File.ReadAllBytesAsync(originalPath)).SequenceEqual(original));

            var again = service.Install(preparedPath, pakDir, overrideName, pakDir + "/Game-WindowsNoEditor.pak", originalHash);
            Check("install_idempotent", again.State == "already-installed");

            var un = service.Uninstall();
            Check("uninstall_removes_override", un.State == "uninstalled"
                && !File.Exists(Path.Combine(paks, overrideName)));
            Check("uninstall_keeps_original", (await File.ReadAllBytesAsync(originalPath)).SequenceEqual(original));
            Check("uninstall_not_installed", service.Uninstall().State == "not-installed");

            service.Install(preparedPath, pakDir, overrideName, pakDir + "/Game-WindowsNoEditor.pak", originalHash);
            File.Delete(Path.Combine(paks, overrideName));
            Check("repair_restores", service.Repair().State == "repaired"
                && File.ReadAllBytes(Path.Combine(paks, overrideName)).SequenceEqual(prepared));
            Check("repair_intact", service.Repair().State == "intact");

            // baseline mismatch: game updated under us
            await File.WriteAllBytesAsync(originalPath, [9, 9, 9, 9]);
            try
            {
                service.Install(preparedPath, pakDir, overrideName, pakDir + "/Game-WindowsNoEditor.pak", originalHash);
                Check("baseline_mismatch_refused", false);
            }
            catch (InvalidDataException) { Check("baseline_mismatch_refused", true); }

            // foreign override refusal
            await File.WriteAllBytesAsync(originalPath, original);
            await File.WriteAllBytesAsync(Path.Combine(paks, overrideName), [8, 8, 8]);
            try
            {
                service.Install(preparedPath, pakDir, overrideName, pakDir + "/Game-WindowsNoEditor.pak", originalHash);
                Check("foreign_override_refused", false);
            }
            catch (IOException) { Check("foreign_override_refused", true); }

            // external tamper: refuse uninstall (restore baseline first: remove foreign file, reinstall ours)
            File.Delete(Path.Combine(paks, overrideName));
            await File.WriteAllBytesAsync(Path.Combine(paks, overrideName), [7, 7]);
            try
            {
                service.Uninstall();
                Check("tampered_uninstall_refused", false);
            }
            catch (IOException) { Check("tampered_uninstall_refused", File.Exists(Path.Combine(paks, overrideName))); }

            // crash recovery: pending journal + half-written own override rolls back via shared Recovery
            var record = Path.Combine(game, ".franca", "installations", "crash" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(record);
            var halfWritten = new byte[] { 1, 1 };
            var halfHash = Convert.ToHexString(SHA256.HashData(halfWritten));
            File.WriteAllText(Path.Combine(record, "manifest.json"), $$"""
            { "Status": "pending", "UePak": true, "OriginalPak": "x", "OriginalSha256": "y", "PreparedSha256": "z",
              "Files": [ { "Path": "Octopath_Traveler2/Content/Paks/pakchunk99-WindowsNoEditor_P.pak",
                           "OriginalHash": null, "InstalledHash": "{{halfHash}}" } ], "Directories": [] }
            """);
            await File.WriteAllBytesAsync(Path.Combine(paks, overrideName), halfWritten);
            var probeService = new UePakInstallService(game);
            _ = probeService.Repair(); // triggers Recover() first; may then legitimately restore a committed generation
            Check("crash_recovery_rolls_back",
                System.Text.Json.JsonSerializer.Deserialize<UePakInstallService.Journal>(
                    File.ReadAllText(Path.Combine(record, "manifest.json")))!.Status == "rolled-back");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RESULT\tUEINSTALL\texception\t{ex}");
            failures++;
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"RESULT\tUEINSTALL\tfailures={failures}");
        if (failures > 0) Environment.ExitCode = 1;
    }
}
