using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
#if BMD_SDK
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BMDSwitcherAPI;
#endif

namespace EndoraReplay.Hardware.Atem;

/// <summary>
/// ATEM control through the official Blackmagic ATEM Switchers SDK (BMDSwitcherAPI COM).
/// Compiled only when lib\Interop.BMDSwitcherAPI.dll exists (see docs/BUILD.md); otherwise
/// <see cref="IsAvailable"/> is false and the UI says why.
/// All SDK calls run on one dedicated MTA thread; state is polled every 40 ms, so no SDK callbacks are needed.
/// UNTESTED ON HARDWARE: written against the SDK's documented interfaces; validate with docs/HARDWARE_VALIDATION.md.
/// </summary>
public sealed class BlackmagicSdkSwitcher : IAtemSwitcher
{
#if BMD_SDK
    public static bool IsAvailable => true;
    public static string UnavailableReason => "";
#else
    public static bool IsAvailable => false;
    public static string UnavailableReason =>
        "This build was made without the Blackmagic ATEM SDK interop (lib\\Interop.BMDSwitcherAPI.dll). Use Network protocol, or rebuild with the SDK (docs/BUILD.md).";
#endif

    private readonly object _gate = new();
    private AtemSnapshot _snapshot = AtemSnapshot.Disconnected;

    public AtemBackendKind Kind => AtemBackendKind.BlackmagicSdk;
    public bool IsSimulated => false;
    public AtemSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public event Action<AtemSnapshot>? StateChanged;
    public event Action<string>? ConnectionLost;

    private void Publish(AtemSnapshot s)
    {
        bool changed;
        lock (_gate)
        {
            changed = s != _snapshot && !(s.Connected == _snapshot.Connected && s.ProgramInput == _snapshot.ProgramInput &&
                s.PreviewInput == _snapshot.PreviewInput && s.InTransition == _snapshot.InTransition &&
                s.ProgramTally.SetEquals(_snapshot.ProgramTally) && s.PreviewTally.SetEquals(_snapshot.PreviewTally));
            _snapshot = s;
        }
        if (changed) StateChanged?.Invoke(s);
    }

#if BMD_SDK
    private readonly BlockingCollection<Action> _work = new();
    private Thread? _thread;
    private CancellationTokenSource? _pollCts;
    private IBMDSwitcher? _switcher;
    private IBMDSwitcherMixEffectBlock? _me;
    private readonly List<IBMDSwitcherInput> _inputs = [];

    private Task<T> Invoke<T>(Func<T> f)
    {
        EnsureThread();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    private Task Invoke(Action a) => Invoke(() => { a(); return true; });

    private void EnsureThread()
    {
        if (_thread is not null) return;
        _thread = new Thread(() =>
        {
            foreach (var w in _work.GetConsumingEnumerable()) w();
        }) { IsBackground = true, Name = "ATEM SDK" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public async Task ConnectAsync(string address, int mixEffectBlock, CancellationToken cancellationToken)
    {
        await DisconnectAsync().ConfigureAwait(false);
        await Invoke(() =>
        {
            IBMDSwitcherDiscovery discovery = new CBMDSwitcherDiscovery();
            _BMDSwitcherConnectToFailure failure = 0;
            try
            {
                discovery.ConnectTo(address, out _switcher, out failure);
            }
            catch (COMException e)
            {
                throw new IOException($"ATEM SDK could not connect to {address}: {failure} ({e.Message})", e);
            }

            _switcher.GetProductName(out var product);

            // Mix effect block N.
            var iid = typeof(IBMDSwitcherMixEffectBlockIterator).GUID;
            _switcher.CreateIterator(ref iid, out var itPtr);
            var meIt = (IBMDSwitcherMixEffectBlockIterator)Marshal.GetObjectForIUnknown(itPtr);
            for (var i = 0; i < mixEffectBlock; i++) meIt.Next(out _me);
            if (_me is null) throw new InvalidOperationException($"ATEM has no M/E {mixEffectBlock}.");

            // Inputs (for tally).
            _inputs.Clear();
            var inIid = typeof(IBMDSwitcherInputIterator).GUID;
            _switcher.CreateIterator(ref inIid, out var inPtr);
            var inIt = (IBMDSwitcherInputIterator)Marshal.GetObjectForIUnknown(inPtr);
            while (true)
            {
                inIt.Next(out var input);
                if (input is null) break;
                _inputs.Add(input);
            }

            Publish(ReadState(product));
        }).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        _pollCts = new CancellationTokenSource();
        _ = Task.Run(() => PollAsync(_pollCts.Token));
    }

    private AtemSnapshot ReadState(string? product = null)
    {
        _me!.GetProgramInput(out var program);
        _me.GetPreviewInput(out var preview);
        _me.GetInTransition(out var inTransition);
        var pgm = new HashSet<int>();
        var pvw = new HashSet<int>();
        foreach (var input in _inputs)
        {
            input.GetInputId(out var id);
            input.IsProgramTallied(out var p);
            input.IsPreviewTallied(out var v);
            if (p != 0) pgm.Add((int)id);
            if (v != 0) pvw.Add((int)id);
        }
        return new AtemSnapshot(true, product ?? Snapshot.ModelName, (int)program, (int)preview, inTransition != 0, pgm, pvw, null);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var s = await Invoke(() => ReadState()).ConfigureAwait(false);
                Publish(s);
            }
            catch (Exception e) when (e is COMException or InvalidCastException or NullReferenceException)
            {
                Publish(AtemSnapshot.Disconnected);
                ConnectionLost?.Invoke($"ATEM SDK: {e.Message}");
                return;
            }
        }
    }

    public Task SetPreviewInputAsync(int input) => Invoke(() => _me!.SetPreviewInput(input));

    public Task SetNextTransitionStyleAsync(AtemTransitionStyle style) => Invoke(() =>
    {
        var tp = (IBMDSwitcherTransitionParameters)_me!;
        tp.SetNextTransitionStyle(style switch
        {
            AtemTransitionStyle.Dip => _BMDSwitcherTransitionStyle.bmdSwitcherTransitionStyleDip,
            AtemTransitionStyle.Wipe => _BMDSwitcherTransitionStyle.bmdSwitcherTransitionStyleWipe,
            AtemTransitionStyle.Dve => _BMDSwitcherTransitionStyle.bmdSwitcherTransitionStyleDVE,
            AtemTransitionStyle.Stinger => _BMDSwitcherTransitionStyle.bmdSwitcherTransitionStyleStinger,
            _ => _BMDSwitcherTransitionStyle.bmdSwitcherTransitionStyleMix,
        });
    });

    public Task PerformAutoTransitionAsync() => Invoke(() => _me!.PerformAutoTransition());
    public Task PerformCutAsync() => Invoke(() => _me!.PerformCut());

    private IBMDSwitcherDownstreamKey DownstreamKey(int keyer)
    {
        var iid = typeof(IBMDSwitcherDownstreamKeyIterator).GUID;
        _switcher!.CreateIterator(ref iid, out var ptr);
        var it = (IBMDSwitcherDownstreamKeyIterator)Marshal.GetObjectForIUnknown(ptr);
        for (var i = 1; ; i++)
        {
            it.Next(out var dsk);
            if (dsk is null) throw new InvalidOperationException($"This switcher has no downstream keyer {keyer}.");
            if (i == keyer) return dsk;
        }
    }

    public Task ConfigureDownstreamKeyAsync(int keyer, int input, double clipPercent, double gainPercent) => Invoke(() =>
    {
        var dsk = DownstreamKey(keyer);
        dsk.SetInputFill(input);
        dsk.SetInputCut(input);
        dsk.SetPreMultiplied(0);
        dsk.SetClip(clipPercent / 100.0);
        dsk.SetGain(gainPercent / 100.0);
        dsk.SetInverse(0);
        dsk.SetMasked(0);
        dsk.SetTie(0);
    });

    public Task SetDownstreamKeyOnAirAsync(int keyer, bool onAir) => Invoke(() => DownstreamKey(keyer).SetOnAir(onAir ? 1 : 0));

    public async Task DisconnectAsync()
    {
        if (_pollCts is not null)
        {
            await _pollCts.CancelAsync().ConfigureAwait(false);
            _pollCts.Dispose();
            _pollCts = null;
        }
        if (_switcher is not null)
        {
            await Invoke(() =>
            {
                foreach (var i in _inputs) Marshal.ReleaseComObject(i);
                _inputs.Clear();
                if (_me is not null) Marshal.ReleaseComObject(_me);
                if (_switcher is not null) Marshal.ReleaseComObject(_switcher);
                _me = null;
                _switcher = null;
            }).ConfigureAwait(false);
        }
        Publish(AtemSnapshot.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _work.CompleteAdding();
    }
#else
    public Task ConnectAsync(string address, int mixEffectBlock, CancellationToken cancellationToken) =>
        throw new NotSupportedException(UnavailableReason);

    public Task SetPreviewInputAsync(int input) => throw new NotSupportedException(UnavailableReason);
    public Task SetNextTransitionStyleAsync(AtemTransitionStyle style) => throw new NotSupportedException(UnavailableReason);
    public Task PerformAutoTransitionAsync() => throw new NotSupportedException(UnavailableReason);
    public Task PerformCutAsync() => throw new NotSupportedException(UnavailableReason);
    public Task ConfigureDownstreamKeyAsync(int keyer, int input, double clipPercent, double gainPercent) => throw new NotSupportedException(UnavailableReason);
    public Task SetDownstreamKeyOnAirAsync(int keyer, bool onAir) => throw new NotSupportedException(UnavailableReason);

    public Task DisconnectAsync()
    {
        Publish(AtemSnapshot.Disconnected);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Keeps the event used in both build flavours.
    internal void RaiseLost(string reason) => ConnectionLost?.Invoke(reason);
#endif
}
