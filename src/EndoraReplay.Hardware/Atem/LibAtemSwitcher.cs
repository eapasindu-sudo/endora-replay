using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using LibAtem.Commands;
using LibAtem.Commands.DeviceProfile;
using LibAtem.Commands.DownstreamKey;
using LibAtem.Commands.MixEffects;
using LibAtem.Commands.MixEffects.Key;
using LibAtem.Commands.MixEffects.Transition;
using LibAtem.Commands.Settings;
using LibAtem.Common;
using LibAtem.Net;

namespace EndoraReplay.Hardware.Atem;

/// <summary>
/// ATEM control over Ethernet using the open-source LibAtem protocol implementation (LGPL-3.0).
/// This is NOT the official Blackmagic SDK; it is offered because it needs no SDK install and builds in CI.
/// HARDWARE PATH: validate against the actual ATEM Mini Extreme firmware before a live show.
/// </summary>
public sealed class LibAtemSwitcher : IAtemSwitcher
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private AtemClient? _client;
    private MixEffectBlockId _me = MixEffectBlockId.One;
    private AtemSnapshot _snapshot = AtemSnapshot.Disconnected;
    private TaskCompletionSource? _connected;
    private readonly Dictionary<int, (bool Program, bool Preview)> _tally = [];

    public AtemBackendKind Kind => AtemBackendKind.NetworkProtocol;
    public bool IsSimulated => false;
    public AtemSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public event Action<AtemSnapshot>? StateChanged;
    public event Action<string>? ConnectionLost;

    public async Task ConnectAsync(string address, int mixEffectBlock, CancellationToken cancellationToken)
    {
        await DisconnectAsync().ConfigureAwait(false);
        _me = (MixEffectBlockId)Math.Clamp(mixEffectBlock - 1, 0, 3);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AtemClient(address, false);
        lock (_gate)
        {
            _client = client;
            _connected = tcs;
            _tally.Clear();
            _snapshot = AtemSnapshot.Disconnected;
        }
        client.OnConnection += OnConnection;
        client.OnDisconnect += OnDisconnect;
        client.OnReceive += OnReceive;
        client.Connect();
        try
        {
            await tcs.Task.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw new TimeoutException($"No response from ATEM at {address}.");
        }
    }

    private void OnConnection(object sender)
    {
        if (!ReferenceEquals(sender, _client)) return;
        Update(s => s with { Connected = true });
        _connected?.TrySetResult();
    }

    private void OnDisconnect(object sender)
    {
        if (!ReferenceEquals(sender, _client)) return;
        var was = Snapshot.Connected;
        Update(_ => AtemSnapshot.Disconnected);
        if (was) ConnectionLost?.Invoke("ATEM stopped responding.");
    }

    private void OnReceive(object sender, IReadOnlyList<ICommand> commands)
    {
        if (!ReferenceEquals(sender, _client)) return;
        var changed = false;
        lock (_gate)
        {
            foreach (var cmd in commands)
            {
                switch (cmd)
                {
                    case ProgramInputGetCommand p when p.Index == _me:
                        _snapshot = _snapshot with { ProgramInput = (int)p.Source };
                        changed = true;
                        break;
                    case PreviewInputGetCommand p when p.Index == _me:
                        _snapshot = _snapshot with { PreviewInput = (int)p.Source };
                        changed = true;
                        break;
                    case TransitionPositionGetCommand t when t.Index == _me:
                        if (_snapshot.InTransition != t.InTransition)
                        {
                            _snapshot = _snapshot with { InTransition = t.InTransition };
                            changed = true;
                        }
                        break;
                    case TallyBySourceCommand t:
                        _tally.Clear();
                        foreach (var (src, state) in t.Tally) _tally[(int)src] = (state.Item1, state.Item2);
                        _snapshot = _snapshot with
                        {
                            ProgramTally = _tally.Where(kv => kv.Value.Program).Select(kv => kv.Key).ToHashSet(),
                            PreviewTally = _tally.Where(kv => kv.Value.Preview).Select(kv => kv.Key).ToHashSet(),
                        };
                        changed = true;
                        break;
                    case ProductIdentifierCommand pid:
                        _snapshot = _snapshot with { ModelName = pid.Name };
                        changed = true;
                        break;
                    case TopologyV8Command top:
                        _snapshot = _snapshot with { StingerCount = (int)top.Stingers };
                        break;
                    case TopologyCommand top:
                        _snapshot = _snapshot with { StingerCount = (int)top.Stingers };
                        break;
                    case InputPropertiesGetCommand ip when (int)ip.Id is >= 1 and <= 40:
                        var names = new Dictionary<int, string>(_snapshot.InputNames ?? new Dictionary<int, string>()) { [(int)ip.Id] = ip.LongName };
                        _snapshot = _snapshot with { InputNames = names };
                        changed = true;
                        break;
                    case DownstreamKeyStateGetV8Command d:
                        SetDsk((int)d.Index + 1, d.OnAir);
                        changed = true;
                        break;
                    case DownstreamKeyStateGetCommand d:
                        SetDsk((int)d.Index + 1, d.OnAir);
                        changed = true;
                        break;
                    case MixEffectKeyOnAirGetCommand k when k.MixEffectIndex == MixEffectBlockId.One:
                        var usk = new HashSet<int>(_snapshot.UpstreamKeysOnAir ?? new HashSet<int>());
                        if (k.OnAir) usk.Add((int)k.KeyerIndex + 1); else usk.Remove((int)k.KeyerIndex + 1);
                        _snapshot = _snapshot with { UpstreamKeysOnAir = usk };
                        changed = true;
                        break;
                    case VideoModeGetCommand vm:
                        _snapshot = _snapshot with { VideoMode = vm.VideoMode.ToString() };
                        changed = true;
                        break;
                }
            }
        }
        if (changed && Snapshot.Connected) StateChanged?.Invoke(Snapshot);
    }

    private void SetDsk(int keyer, bool onAir)
    {
        var on = new HashSet<int>(_snapshot.DownstreamKeysOnAir ?? new HashSet<int>());
        if (onAir) on.Add(keyer); else on.Remove(keyer);
        _snapshot = _snapshot with { DownstreamKeysOnAir = on };
    }

    public Task ConfigureDownstreamKeyAsync(int keyer, int input, double clipPercent, double gainPercent)
    {
        var id = (DownstreamKeyId)(keyer - 1);
        Send(new DownstreamKeyFillSourceSetCommand { Index = id, FillSource = (VideoSource)input });
        Send(new DownstreamKeyCutSourceSetCommand { Index = id, CutSource = (VideoSource)input });
        Send(new DownstreamKeyGeneralSetCommand
        {
            Index = id,
            Mask = DownstreamKeyGeneralSetCommand.MaskFlags.PreMultipliedKey | DownstreamKeyGeneralSetCommand.MaskFlags.Clip
                | DownstreamKeyGeneralSetCommand.MaskFlags.Gain | DownstreamKeyGeneralSetCommand.MaskFlags.Invert,
            PreMultipliedKey = false,
            Clip = clipPercent,
            Gain = gainPercent,
            Invert = false,
        });
        Send(new DownstreamKeyMaskSetCommand { Index = id, Mask = DownstreamKeyMaskSetCommand.MaskFlags.MaskEnabled, MaskEnabled = false });
        Send(new DownstreamKeyTieSetCommand { Index = id, Tie = false });
        return Task.CompletedTask;
    }

    public Task SetDownstreamKeyOnAirAsync(int keyer, bool onAir)
    {
        Send(new DownstreamKeyOnAirSetCommand { Index = (DownstreamKeyId)(keyer - 1), OnAir = onAir });
        return Task.CompletedTask;
    }

    public Task ConfigureUpstreamKeyAsync(int keyer, int input, double clipPercent, double gainPercent)
    {
        var me = MixEffectBlockId.One;
        var id = (UpstreamKeyId)(keyer - 1);
        Send(new MixEffectKeyTypeSetCommand
        {
            MixEffectIndex = me, KeyerIndex = id,
            Mask = MixEffectKeyTypeSetCommand.MaskFlags.KeyType | MixEffectKeyTypeSetCommand.MaskFlags.FlyEnabled,
            KeyType = MixEffectKeyType.Luma, FlyEnabled = false,
        });
        Send(new MixEffectKeyFillSourceSetCommand { MixEffectIndex = me, KeyerIndex = id, FillSource = (VideoSource)input });
        Send(new MixEffectKeyCutSourceSetCommand { MixEffectIndex = me, KeyerIndex = id, CutSource = (VideoSource)input });
        Send(new MixEffectKeyLumaSetCommand
        {
            MixEffectIndex = me, KeyerIndex = id,
            Mask = MixEffectKeyLumaSetCommand.MaskFlags.PreMultiplied | MixEffectKeyLumaSetCommand.MaskFlags.Clip
                | MixEffectKeyLumaSetCommand.MaskFlags.Gain | MixEffectKeyLumaSetCommand.MaskFlags.Invert,
            PreMultiplied = false, Clip = clipPercent, Gain = gainPercent, Invert = false,
        });
        Send(new MixEffectKeyMaskSetCommand { MixEffectIndex = me, KeyerIndex = id, Mask = MixEffectKeyMaskSetCommand.MaskFlags.MaskEnabled, MaskEnabled = false });
        return Task.CompletedTask;
    }

    public Task SetUpstreamKeyOnAirAsync(int keyer, bool onAir)
    {
        Send(new MixEffectKeyOnAirSetCommand { MixEffectIndex = MixEffectBlockId.One, KeyerIndex = (UpstreamKeyId)(keyer - 1), OnAir = onAir });
        return Task.CompletedTask;
    }

    public Task SetPreviewInputAsync(int input)
    {
        Send(new PreviewInputSetCommand { Index = _me, Source = (VideoSource)input });
        return Task.CompletedTask;
    }

    public Task SetNextTransitionStyleAsync(AtemTransitionStyle style)
    {
        if (style == AtemTransitionStyle.Stinger && Snapshot.StingerCount == 0)
            throw new NotSupportedException($"{Snapshot.ModelName} has no stinger transition. Use Endora stinger mode.");
        Send(new TransitionPropertiesSetCommand
        {
            Index = _me,
            Mask = TransitionPropertiesSetCommand.MaskFlags.NextStyle,
            NextStyle = style switch
            {
                AtemTransitionStyle.Mix => TransitionStyle.Mix,
                AtemTransitionStyle.Dip => TransitionStyle.Dip,
                AtemTransitionStyle.Wipe => TransitionStyle.Wipe,
                AtemTransitionStyle.Dve => TransitionStyle.DVE,
                AtemTransitionStyle.Stinger => TransitionStyle.Stinger,
                _ => TransitionStyle.Mix,
            },
        });
        return Task.CompletedTask;
    }

    public Task PerformAutoTransitionAsync()
    {
        Send(new MixEffectAutoCommand { Index = _me });
        return Task.CompletedTask;
    }

    public Task PerformCutAsync()
    {
        Send(new MixEffectCutCommand { Index = _me });
        return Task.CompletedTask;
    }

    private void Send(ICommand cmd)
    {
        var c = _client;
        if (c is null || !Snapshot.Connected) throw new InvalidOperationException("ATEM not connected.");
        c.SendCommand(cmd);
    }

    private void Update(Func<AtemSnapshot, AtemSnapshot> f)
    {
        AtemSnapshot s;
        lock (_gate) { _snapshot = f(_snapshot); s = _snapshot; }
        StateChanged?.Invoke(s);
    }

    public Task DisconnectAsync()
    {
        AtemClient? c;
        lock (_gate) { c = _client; _client = null; }
        if (c is not null)
        {
            c.OnConnection -= OnConnection;
            c.OnDisconnect -= OnDisconnect;
            c.OnReceive -= OnReceive;
            try { c.Dispose(); } catch (Exception) { }
        }
        lock (_gate) _snapshot = AtemSnapshot.Disconnected;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
