using System.Net;
using System.Net.Sockets;
using System.Text;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Input;
using EndoraReplay.Core.Services;
using EndoraReplay.Core.Simulation;
using EndoraReplay.Core.Vmix;

namespace EndoraReplay.Core.Tests;

public class VmixProtocolTests
{
    [Fact]
    public void Tally_line_gives_program_and_preview()
    {
        var m = Assert.IsType<VmixTallyMessage>(VmixProtocol.ParseLine("TALLY OK 0121"));
        Assert.Equal(new HashSet<int> { 2, 4 }, m.Program);
        Assert.Equal(new HashSet<int> { 3 }, m.Preview);
    }

    [Fact]
    public void Activator_line_is_parsed()
    {
        var m = Assert.IsType<VmixActivatorMessage>(VmixProtocol.ParseLine("ACTS OK InputPlaying 5 1"));
        Assert.Equal("InputPlaying", m.Name);
        Assert.Equal(5, m.Input);
        Assert.True(m.IsOn);
        Assert.False(Assert.IsType<VmixActivatorMessage>(VmixProtocol.ParseLine("ACTS OK InputPlaying 5 0")).IsOn);
    }

    [Fact]
    public void Function_replies_are_ok_or_error()
    {
        Assert.True(Assert.IsType<VmixReplyMessage>(VmixProtocol.ParseLine("FUNCTION OK Stinger1")).Ok);
        var er = Assert.IsType<VmixReplyMessage>(VmixProtocol.ParseLine("FUNCTION ER Input not found"));
        Assert.False(er.Ok);
        Assert.Equal("Input not found", er.Text);
    }

    [Fact]
    public void Stream_parser_handles_split_data_and_length_headers()
    {
        const string xml = "<vmix><version>27.0.0.1</version></vmix>";
        var bytes = Encoding.UTF8.GetBytes($"TALLY OK 01\r\nXML {Encoding.UTF8.GetByteCount(xml) + 2}\r\n{xml}\r\nFUNCTION OK Cut\r\n");
        var p = new VmixStreamParser();
        var all = new List<VmixMessage>();
        // Feed in awkward pieces, as TCP may deliver them.
        for (var i = 0; i < bytes.Length; i += 7) all.AddRange(p.Feed(bytes.AsSpan(i, Math.Min(7, bytes.Length - i))));
        Assert.Equal(3, all.Count);
        Assert.IsType<VmixTallyMessage>(all[0]);
        Assert.Equal(xml, Assert.IsType<VmixDataMessage>(all[1]).Data);
        Assert.Equal("Cut", Assert.IsType<VmixReplyMessage>(all[2]).Text);
    }

    [Fact]
    public void State_xml_lists_inputs_and_inputs_resolve_by_number_title_or_type()
    {
        var (inputs, active, preview, version) = VmixProtocol.ParseState(FakeVmixServer.Xml(running: true));
        Assert.Equal("27.0.0.49", version);
        Assert.Equal(1, active);
        Assert.Equal(2, preview);
        Assert.Equal(3, inputs.Count);
        Assert.True(inputs.Single(i => i.Number == 3).IsRunning);
        Assert.Equal(3, VmixProtocol.ResolveInput("", inputs, "Replay"));
        Assert.Equal(1, VmixProtocol.ResolveInput("black", inputs));
        Assert.Equal(2, VmixProtocol.ResolveInput("2", inputs));
        Assert.Null(VmixProtocol.ResolveInput("Nope", inputs));
    }

    [Fact]
    public void Function_lines_are_formatted()
    {
        Assert.Equal("FUNCTION Stinger1 Input=3\r\n", VmixProtocol.FunctionLine("Stinger1", VmixProtocol.InputQuery(3)));
        Assert.Equal(("SetRate", "Input=3&Value=0.5"), VmixProtocol.SplitFunction(" SetRate Input=3&Value=0.5 "));
        Assert.Throws<ArgumentException>(() => VmixProtocol.FunctionLine("two words", null));
    }

    [Fact]
    public void Companion_url_is_escaped()
    {
        Assert.Equal("http://pc:8000/api/custom-variable/endora_state/value?value=REPLAY%20ON%20AIR",
            CompanionVariables.Url("http://pc:8000/", "endora_state", "REPLAY ON AIR"));
    }
}

/// <summary>A tiny stand-in for the vMix TCP API, enough for the client.</summary>
internal sealed class FakeVmixServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private NetworkStream? _stream;
    public readonly List<string> Received = [];

    public FakeVmixServer()
    {
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static string Xml(bool running) =>
        "<vmix><version>27.0.0.49</version><inputs>" +
        "<input key=\"k1\" number=\"1\" type=\"Colour\" title=\"Black\" shortTitle=\"Black\" state=\"Paused\"></input>" +
        "<input key=\"k2\" number=\"2\" type=\"Capture\" title=\"ATEM\" shortTitle=\"ATEM\" state=\"Running\"></input>" +
        $"<input key=\"k3\" number=\"3\" type=\"Replay\" title=\"Replay A\" shortTitle=\"Replay A\" state=\"{(running ? "Running" : "Paused")}\"></input>" +
        "</inputs><active>1</active><preview>2</preview></vmix>";

    private async Task ServeAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
        _stream = client.GetStream();
        using var reader = new StreamReader(_stream, Encoding.UTF8);
        while (!_cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(_cts.Token);
            if (line is null) return;
            lock (Received) Received.Add(line);
            if (line.StartsWith("SUBSCRIBE ", StringComparison.Ordinal)) await Send($"SUBSCRIBE OK {line[10..]}\r\n");
            else if (line == "TALLY") await Send("TALLY OK 100\r\n");
            else if (line == "XML")
            {
                var xml = Xml(false);
                await Send($"XML {Encoding.UTF8.GetByteCount(xml) + 2}\r\n{xml}\r\n");
            }
            else if (line.StartsWith("XMLTEXT ", StringComparison.Ordinal)) await Send("XMLTEXT OK Paused\r\n");
            else if (line.StartsWith("FUNCTION Bad", StringComparison.Ordinal)) await Send("FUNCTION ER Unknown function\r\n");
            else if (line.StartsWith("FUNCTION ", StringComparison.Ordinal))
            {
                // An event in between, as vMix may send one at any time.
                await Send("ACTS OK InputPlaying 3 1\r\n");
                await Send($"FUNCTION OK {line.Split(' ')[1]}\r\n");
            }
        }
    }

    public Task Send(string text) => _stream!.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch (Exception) { }
    }
}

public class VmixClientTests
{
    [Fact]
    public async Task Connects_subscribes_reads_inputs_and_runs_functions()
    {
        await using var server = new FakeVmixServer();
        await using var diag = new DiagnosticsService(null, () => 0);
        await using var client = new VmixClient(diag);
        await client.StartAsync("127.0.0.1", server.Port, 1);

        Assert.True(await Wait.For(() => client.Snapshot.Connected));
        Assert.Equal(3, client.Snapshot.Inputs.Count);
        Assert.True(client.Snapshot.OnProgram(1));
        lock (server.Received) Assert.Contains("SUBSCRIBE ACTS", server.Received);

        Assert.Null(await client.FunctionAsync("Stinger1", "Input=3"));
        Assert.True(await Wait.For(() => client.Snapshot.IsPlaying(3)));
        Assert.Contains("Unknown function", await client.FunctionAsync("BadOne", null));
        Assert.False(await client.IsInputRunningAsync(3));

        await server.Send("TALLY OK 001\r\n");
        Assert.True(await Wait.For(() => client.Snapshot.OnProgram(3)));
    }
}

internal static class Wait
{
    public static async Task<bool> For(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(10);
        }
        return cond();
    }
}

/// <summary>vMix as the automation sees it: inputs 1 Black, 2 ATEM feed, 3 Replay. Functions change Program like vMix.</summary>
internal sealed class FakeVmix : IVmix
{
    private readonly object _gate = new();
    private VmixSnapshot _s;
    public readonly List<string> Calls = [];
    public volatile bool Running;

    public FakeVmix()
    {
        var (inputs, _, _, _) = VmixProtocol.ParseState(FakeVmixServer.Xml(false));
        _s = new VmixSnapshot(true, "27", new HashSet<int> { 1 }, new HashSet<int> { 2 }, new HashSet<int>(), inputs);
    }

    public VmixSnapshot Snapshot { get { lock (_gate) return _s; } }
    public event Action<VmixSnapshot>? Changed;

    public void SetProgram(int input)
    {
        lock (_gate) _s = _s with { Program = new HashSet<int> { input } };
        Changed?.Invoke(Snapshot);
    }

    public string[] CallsNow() { lock (Calls) return [.. Calls]; }

    public Task<string?> FunctionAsync(string function, string? query, CancellationToken cancellationToken = default)
    {
        lock (Calls) Calls.Add(query is null ? function : $"{function} {query}");
        if (query is not null && query.StartsWith("Input=", StringComparison.Ordinal) && int.TryParse(query[6..], out var n)
            && function is "CutDirect" or "Stinger1" or "Fade")
            SetProgram(n);
        return Task.FromResult<string?>(null);
    }

    public Task<bool?> IsInputRunningAsync(int input, CancellationToken cancellationToken = default) =>
        Task.FromResult<bool?>(input == 3 && Running);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class VmixBridgeTests
{
    private const int VmixAtemInput = 8;

    private static AppSettings Settings(VmixStingerMode mode)
    {
        var s = new AppSettings { Engine = ReplayEngine.Vmix };
        s.Atem.ReplayInput = VmixAtemInput;
        s.Vmix.Mode = mode;
        s.Vmix.BlackInput = "Black";
        s.Vmix.CutPointMs = 40;
        s.Vmix.StingerLengthMs = 80;
        s.Vmix.BlackLeadMs = 10;
        s.Vmix.StopGraceMs = 60;
        return s;
    }

    private static async Task<(SimulatedAtemSwitcher Sim, AtemSwitcherService Atem)> Atem(DiagnosticsService diag)
    {
        var sim = new SimulatedAtemSwitcher { AutoDuration = TimeSpan.FromMilliseconds(30) };
        var atem = new AtemSwitcherService(_ => sim, diag);
        await atem.StartAsync(new AtemSettings { Backend = Model.AtemBackendKind.Simulated });
        Assert.True(await Wait.For(() => atem.IsConnected));
        return (sim, atem);
    }

    [Fact]
    public async Task Vmix_replay_playing_runs_keyed_stinger_in_and_back_to_live_when_it_stops()
    {
        await using var diag = new DiagnosticsService(null, () => 0);
        var (sim, atem) = await Atem(diag);
        await using var _ = atem;
        var vmix = new FakeVmix();
        var settings = Settings(VmixStingerMode.Keyed);
        await using var bridge = new VmixBridge(vmix, atem, () => settings, diag);
        bridge.Start();
        Assert.Equal(1, sim.Snapshot.ProgramInput);

        // The operator presses play on the vMix replay.
        await Task.Delay(300);
        vmix.Running = true;

        Assert.True(await Wait.For(() => bridge.State == BridgeState.OnAir));
        Assert.Equal(VmixAtemInput, sim.Snapshot.ProgramInput);
        Assert.Contains("Stinger1 Input=3", vmix.CallsNow());
        Assert.Equal(VmixAtemInput, sim.DownstreamKeyInputs[1]);
        Assert.False(sim.Snapshot.IsDownstreamKeyOnAir(1));

        // Replay ends in vMix.
        vmix.Running = false;
        Assert.True(await Wait.For(() => bridge.State == BridgeState.Idle && !sim.Snapshot.IsDownstreamKeyOnAir(1)));
        Assert.Equal(1, sim.Snapshot.ProgramInput);
        Assert.Equal("Stinger1 Input=1", vmix.CallsNow()[^1]);
        Assert.True(vmix.Snapshot.OnProgram(1));
    }

    [Fact]
    public async Task Replay_button_with_cover_cut_and_live_button()
    {
        await using var diag = new DiagnosticsService(null, () => 0);
        var (sim, atem) = await Atem(diag);
        await using var _ = atem;
        var vmix = new FakeVmix();
        var settings = Settings(VmixStingerMode.CoverCut);
        settings.Vmix.AutoReturnWhenReplayStops = false;
        await using var bridge = new VmixBridge(vmix, atem, () => settings, diag);
        bridge.Start();
        sim.PanelProgram(4);

        Assert.Null(bridge.Execute(OperatorActions.Replay, "test"));
        Assert.NotNull(bridge.Execute(OperatorActions.Replay, "test"));
        Assert.True(await Wait.For(() => bridge.State == BridgeState.OnAir));
        Assert.Equal(VmixAtemInput, sim.Snapshot.ProgramInput);
        Assert.Equal(new[] { "Stinger1 Input=3" }, vmix.CallsNow());

        Assert.Null(bridge.Execute(OperatorActions.Live, "test"));
        Assert.True(await Wait.For(() => bridge.State == BridgeState.Idle));
        Assert.Equal(4, sim.Snapshot.ProgramInput);
        Assert.Equal("Stinger1 Input=1", vmix.CallsNow()[^1]);
    }

    [Fact]
    public async Task Atem_follows_when_vmix_puts_the_replay_on_program_by_itself()
    {
        await using var diag = new DiagnosticsService(null, () => 0);
        var (sim, atem) = await Atem(diag);
        await using var _ = atem;
        var vmix = new FakeVmix();
        var settings = Settings(VmixStingerMode.CoverCut);
        settings.Vmix.StartWhenReplayPlays = false;
        await using var bridge = new VmixBridge(vmix, atem, () => settings, diag);
        bridge.Start();

        vmix.SetProgram(3);
        Assert.True(await Wait.For(() => bridge.State == BridgeState.OnAir && sim.Snapshot.ProgramInput == VmixAtemInput));
        Assert.Empty(vmix.CallsNow());

        vmix.SetProgram(1);
        Assert.True(await Wait.For(() => bridge.State == BridgeState.Idle && sim.Snapshot.ProgramInput == 1));
    }

    [Fact]
    public async Task Emergency_override_blocks_everything()
    {
        await using var diag = new DiagnosticsService(null, () => 0);
        var (sim, atem) = await Atem(diag);
        await using var _ = atem;
        var vmix = new FakeVmix();
        var settings = Settings(VmixStingerMode.Keyed);
        await using var bridge = new VmixBridge(vmix, atem, () => settings, diag);
        bridge.Start();

        Assert.Null(bridge.Execute(OperatorActions.EmergencyOverride));
        Assert.Equal(BridgeState.Override, bridge.State);
        Assert.NotNull(bridge.Execute(OperatorActions.Replay));
        vmix.Running = true;
        await Task.Delay(400);
        Assert.Equal(1, sim.Snapshot.ProgramInput);
        Assert.Empty(vmix.CallsNow());
    }

    [Fact]
    public void Keyed_mode_needs_a_black_input()
    {
        var s = new VmixSettings { Mode = VmixStingerMode.Keyed, BlackInput = "" };
        Assert.Contains(s.Validate(), p => p.Contains("black", StringComparison.OrdinalIgnoreCase));
        s.BlackInput = "1";
        Assert.Empty(s.Validate());
    }
}
