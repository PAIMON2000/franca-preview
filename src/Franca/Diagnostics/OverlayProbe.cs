using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Franca.Services;
using Franca.Views;

namespace Franca.Diagnostics;

internal static class OverlayProbe
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    internal static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"RESULT\tOVERLAY\t{name}\t{(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }
        var root = Path.Combine(Path.GetTempPath(), "franca-overlay-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { IsTransient = true, TargetLanguage = "zh-CN", OverlayEnabled = true, TranslationMode = "quality" };
        UiStrings.Current = settings.TargetLanguage;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var relay = new RelayServer(() => settings.Snapshot(), new PackStore(Path.Combine(root, "packs")),
            async (text, _, _, _, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return ("测试译文", "fixture"); },
            new DictionaryService(Path.Combine(root, "dictionary")));
        var model = new OverlayStatusModel(() => settings, () => relay.IsRunning,
            () => new(relay.ServedCount, relay.CacheHits, relay.DictHits, relay.InFlight, relay.Failures));
        var window = new OverlayWindow(20000, 20000);
        try
        {
            Check("hidden_without_relay", !model.ShouldShow);
            await relay.EnsureStartedAsync();
            Check("visible_with_enabled_relay", model.ShouldShow);
            var before = GetForegroundWindow();
            window.SetLines(model.BuildLines());
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield();
            await Task.Delay(250);
            var handle = new WindowInteropHelper(window).Handle;
            Check("native_noactivate", (GetWindowLong(handle, -20) & 0x08000000) != 0);
            Check("native_clickthrough", (GetWindowLong(handle, -20) & 0x20) != 0);
            Check("native_topmost", (GetWindowLong(handle, -20) & 8) != 0);
            Check("show_does_not_change_foreground", GetForegroundWindow() == before);
            Check("showactivated_false", !window.ShowActivated);
            using var client = new HttpClient();
            var url = relay.UrlForGame(999998) + "?from=en&to=zh-CN&text=Overlay%20runtime%20test";
            var first = client.GetStringAsync(url);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("inflight_observed", relay.InFlight > 0);
            release.TrySetResult();
            await first;
            var initial = model.BuildLines();
            await client.GetStringAsync(url);
            var updated = model.BuildLines();
            window.SetLines(updated);
            window.UpdateLayout();
            Check("live_request_and_cache_counters", relay.ServedCount == 2 && relay.CacheHits == 1);
            Check("display_changes_from_live_counts", string.Join("|", initial) != string.Join("|", updated));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "overlay.png"))) encoder.Save(file);
            Check("actual_wpf_render", bitmap.PixelWidth > 100 && bitmap.PixelHeight > 40);
            settings.OverlayEnabled = false;
            Check("disabled_hides_model", !model.ShouldShow);
            relay.Stop();
            settings.OverlayEnabled = true;
            Check("relay_stop_hides_model", !model.ShouldShow);
            window.Hide();
            Check("window_hidden", !window.IsVisible);
        }
        finally
        {
            window.Close();
            relay.Stop();
            try { Directory.Delete(root, true); } catch { }
        }
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
