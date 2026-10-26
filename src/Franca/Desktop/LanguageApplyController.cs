using System.Diagnostics;
using System.Windows.Threading;
using Franca.Services;

namespace Franca.Desktop;

// One window-owned countdown, shared by both language selectors. All calls are on the dispatcher.
internal sealed class LanguageApplyController : IDisposable
{
    private readonly Func<CancellationToken, Task<GameTranslationService.LanguageApplyResult>> _apply;
    private readonly Func<double> _clock;
    private readonly DispatcherTimer _timer;
    private readonly bool _automatic;
    private CancellationTokenSource? _active;
    private double _elapsed, _last;
    private int _version;
    private bool _disposed;
    public bool Pending { get; private set; }
    public bool Paused { get; private set; }
    public bool Applying => _active is not null;
    public double Progress => Math.Clamp(_elapsed / 5, 0, 1);
    public int SecondsRemaining => (int)Math.Ceiling(Math.Max(0, 5 - _elapsed));
    public GameTranslationService.LanguageApplyResult? Result { get; private set; }
    public string? Error { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public event Action? Changed;

    public LanguageApplyController(Func<CancellationToken, Task<GameTranslationService.LanguageApplyResult>> apply,
        bool startTimer = true, Func<double>? clock = null)
    {
        _apply = apply;
        _automatic = startTimer;
        _clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        _last = _clock();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
    }

    public void Schedule()
    {
        if (_disposed) return;
        _version++;
        _active?.Cancel();
        Pending = true;
        Paused = false;
        _elapsed = 0;
        _last = _clock();
        Result = null;
        Error = null;
        if (_automatic) _timer.Start();
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (_disposed) return;
        if (!Pending) { if (!Applying) Schedule(); return; }
        AdvanceClock();
        Paused = !Paused;
        Changed?.Invoke();
    }

    public void Skip()
    {
        if (_disposed) return;
        if (!Pending) { if (Applying) return; Schedule(); }
        _elapsed = 5;
        Paused = false;
        Tick();
    }

    private void AdvanceClock()
    {
        var now = _clock();
        if (Pending && !Paused) _elapsed = Math.Min(5, _elapsed + Math.Max(0, now - _last));
        _last = now;
    }

    private void OnTick(object? sender, EventArgs e) => Tick();
    internal void Tick()
    {
        if (_disposed) return;
        AdvanceClock();
        if (Pending && !Paused && _elapsed >= 5 && !Applying)
        {
            Pending = false;
            var cts = new CancellationTokenSource();
            _active = cts;
            Completion = ApplyAsync(_version, cts);
        }
        if (!Pending && !Applying) _timer.Stop();
        Changed?.Invoke();
    }

    private async Task ApplyAsync(int version, CancellationTokenSource cts)
    {
        try
        {
            var result = await _apply(cts.Token);
            if (!_disposed && version == _version) Result = result;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && version == _version) Error = ex.Message;
            AppLog.Write($"Language configuration apply failed: {ex.Message}");
        }
        finally
        {
            _active = null;
            cts.Dispose();
            if (!_disposed) Tick();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _active?.Cancel();
    }
}
