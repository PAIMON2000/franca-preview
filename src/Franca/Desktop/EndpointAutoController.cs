using System.Windows.Threading;
using Franca.Services;

namespace Franca.Desktop;

// WPF scheduling stays at the desktop boundary; selection itself has no window dependency.
internal sealed class EndpointAutoController : IDisposable
{
    private readonly EndpointAutoService _service;
    private readonly Func<EndpointCategory, List<(string Id, string Host)>> _candidates;
    private readonly Func<string, CancellationToken, Task<TestOutcome>> _probe;
    private readonly Action<string, long> _selected;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? _cts;
    private EndpointCategory _group;
    private bool _busy;

    public EndpointAutoController(EndpointAutoService service,
        Func<EndpointCategory, List<(string Id, string Host)>> candidates,
        Func<string, CancellationToken, Task<TestOutcome>> probe, Action<string, long> selected)
    {
        (_service, _candidates, _probe, _selected) = (service, candidates, probe, selected);
        _timer.Tick += OnTick;
    }

    public void Start(EndpointCategory group)
    {
        Stop();
        _group = group;
        _cts = new CancellationTokenSource();
        _timer.Start();
        _ = TickAsync();
    }

    public void Stop()
    {
        _timer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _service.Reset();
    }

    private async void OnTick(object? sender, EventArgs e) => await TickAsync();

    private async Task TickAsync()
    {
        if (_busy || _cts is null) return;
        var ct = _cts.Token;
        _busy = true;
        try
        {
            var candidates = _candidates(_group);
            if (candidates.Count == 0)
            {
                AppLog.Write(UiStrings.Get("log.auto.none"));
                return;
            }
            var result = await _service.SelectAsync(candidates, _probe, ct);
            if (!ct.IsCancellationRequested && result is { } pick)
                _selected(pick.Id, pick.LatencyMs);
        }
        catch (OperationCanceledException) { }
        catch { /* A failed round must not stop future rounds. */ }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTick;
    }
}
