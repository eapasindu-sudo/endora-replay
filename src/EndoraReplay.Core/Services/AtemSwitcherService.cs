using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Services;

/// <summary>
/// Owns the switcher connection: connects, reconnects after loss, and forwards Program/Preview/tally/transition
/// state. Switch commands are only sent while connected; on disconnect they fail fast instead of queueing.
/// </summary>
public sealed class AtemSwitcherService : IAsyncDisposable
{
    private readonly Func<AtemBackendKind, IAtemSwitcher> _factory;
    private readonly DiagnosticsService _diag;
    private IAtemSwitcher? _switcher;
    private AtemSettings _settings = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    public AtemSwitcherService(Func<AtemBackendKind, IAtemSwitcher> factory, DiagnosticsService diagnostics)
    {
        _factory = factory;
        _diag = diagnostics;
    }

    public AtemSnapshot Snapshot => _switcher?.Snapshot ?? AtemSnapshot.Disconnected;
    public bool IsConnected => Snapshot.Connected;
    public AtemBackendKind? Backend => _switcher?.Kind;
    public bool IsSimulated => _switcher?.IsSimulated ?? false;
    public IAtemSwitcher? Switcher => _switcher;

    /// <summary>Raised on any thread whenever the switcher state changes, including disconnects.</summary>
    public event Action<AtemSnapshot>? StateChanged;

    public async Task StartAsync(AtemSettings settings)
    {
        await StopAsync().ConfigureAwait(false);
        _settings = settings;
        _switcher = _factory(settings.Backend);
        _switcher.StateChanged += OnState;
        _switcher.ConnectionLost += OnLost;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ConnectLoopAsync(_cts.Token));
        if (_switcher.IsSimulated)
            _diag.Warn(LogCategory.Atem, "ATEM backend is SIMULATED. No switcher is being controlled.");
    }

    private void OnState(AtemSnapshot s) => StateChanged?.Invoke(s);

    private void OnLost(string reason)
    {
        _diag.Error(LogCategory.Atem, $"Connection lost: {reason}");
        StateChanged?.Invoke(AtemSnapshot.Disconnected);
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (_switcher is { } sw && !sw.Snapshot.Connected)
            {
                attempt++;
                try
                {
                    _diag.Info(LogCategory.Atem, $"Connecting to {_settings.Address} ({sw.Kind}), attempt {attempt}");
                    await sw.ConnectAsync(_settings.Address, _settings.MixEffectBlock, ct).ConfigureAwait(false);
                    if (sw.Snapshot.Connected)
                    {
                        attempt = 0;
                        _diag.Info(LogCategory.Atem, $"Connected: {sw.Snapshot.ModelName}, PGM {sw.Snapshot.ProgramInput}, PVW {sw.Snapshot.PreviewInput}");
                        StateChanged?.Invoke(sw.Snapshot);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    if (attempt == 1 || attempt % 10 == 0)
                        _diag.Warn(LogCategory.Atem, $"Connect failed: {e.Message}");
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.ReconnectSeconds)), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public Task SetPreviewAsync(int input) => RunAsync($"Preview → {input}", sw => sw.SetPreviewInputAsync(input));
    public Task CutAsync() => RunAsync("CUT", sw => sw.PerformCutAsync());

    public Task AutoAsync(AtemTransitionStyle style) => RunAsync($"AUTO ({style})", async sw =>
    {
        await sw.SetNextTransitionStyleAsync(style).ConfigureAwait(false);
        await sw.PerformAutoTransitionAsync().ConfigureAwait(false);
    });

    public Task ConfigureDownstreamKeyAsync(int keyer, int input, double clip, double gain) =>
        RunAsync($"DSK {keyer} luma key of input {input} (clip {clip:0}%, gain {gain:0}%)", sw => sw.ConfigureDownstreamKeyAsync(keyer, input, clip, gain));

    public Task SetDownstreamKeyOnAirAsync(int keyer, bool onAir) =>
        RunAsync($"DSK {keyer} {(onAir ? "on air" : "off air")}", sw => sw.SetDownstreamKeyOnAirAsync(keyer, onAir));

    public Task ConfigureUpstreamKeyAsync(int keyer, int input, double clip, double gain) =>
        RunAsync($"Upstream key {keyer} luma key of input {input} (clip {clip:0}%, gain {gain:0}%)", sw => sw.ConfigureUpstreamKeyAsync(keyer, input, clip, gain));

    public Task SetUpstreamKeyOnAirAsync(int keyer, bool onAir) =>
        RunAsync($"Upstream key {keyer} {(onAir ? "on air" : "off air")}", sw => sw.SetUpstreamKeyOnAirAsync(keyer, onAir));

    private async Task RunAsync(string what, Func<IAtemSwitcher, Task> command)
    {
        var sw = _switcher;
        if (sw is null || !sw.Snapshot.Connected)
            throw new InvalidOperationException($"{what} not sent: ATEM not connected.");
        await _commandLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await command(sw).ConfigureAwait(false);
            _diag.Info(LogCategory.Atem, what);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _cts.Dispose();
            _cts = null;
        }
        if (_switcher is not null)
        {
            _switcher.StateChanged -= OnState;
            _switcher.ConnectionLost -= OnLost;
            try { await _switcher.DisconnectAsync().ConfigureAwait(false); } catch (Exception) { }
            await _switcher.DisposeAsync().ConfigureAwait(false);
            _switcher = null;
            StateChanged?.Invoke(AtemSnapshot.Disconnected);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
