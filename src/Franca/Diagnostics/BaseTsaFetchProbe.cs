using System.IO;
using System.Text.Json;
using Franca.Services;

namespace Franca.Diagnostics;

// 东方补丁数据下载烟测（--probe-base-tsa-fetch，需要联网，手动运行）。
// 在临时工作树里真实执行一次补丁数据准备：读清单 → 下载 → 逐个校验 SHA-256，
// 不接触真实游戏目录，也不改写程序自己的工作树。
internal static class BaseTsaFetchProbe
{
    public static Task RunAsync()
    {
        var failures = 0;
        void Check(string name, bool condition)
        {
            Console.WriteLine($"RESULT\tBASETSA\t{name}\t{(condition ? "PASS" : "FAIL")}");
            if (!condition) failures++;
        }

        var root = Path.Combine(Path.GetTempPath(), "franca-basetsa-fetch-" + Guid.NewGuid().ToString("N"));
        var workTree = Path.Combine(root, "work");
        Directory.CreateDirectory(workTree);
        try
        {
            var manifestPath = Path.Combine(AppContext.BaseDirectory,
                "ThirdParty", "thcrap-repos", "base_tsa.files.json");
            Check("manifest_present", File.Exists(manifestPath));
            var expected = 0;
            if (File.Exists(manifestPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    expected = files.GetArrayLength();
            }
            Check("manifest_has_files", expected > 200);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            TouhouThcrapService.EnsureBaseTsaForProbe(workTree);
            watch.Stop();

            var dir = Path.Combine(workTree, "repos", "nmlgc", "base_tsa");
            Check("versions_downloaded", File.Exists(Path.Combine(dir, "versions.js")));
            var actual = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
            Check("file_count_matches_manifest", expected > 0 && actual == expected);
            Console.WriteLine($"NOTE\tBASETSA\tdownloaded {actual} files in {watch.ElapsedMilliseconds} ms");

            // 二次调用必须幂等：已就位时直接跳过，不重复下载
            var again = System.Diagnostics.Stopwatch.StartNew();
            TouhouThcrapService.EnsureBaseTsaForProbe(workTree);
            again.Stop();
            Check("second_run_idempotent", again.ElapsedMilliseconds < Math.Max(200, watch.ElapsedMilliseconds / 2));
        }
        catch (Exception ex)
        {
            Check("download_without_exception", false);
            Console.WriteLine($"NOTE\tBASETSA\t{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine($"RESULT\tBASETSA\tfailures={failures}");
        return Task.CompletedTask;
    }
}
