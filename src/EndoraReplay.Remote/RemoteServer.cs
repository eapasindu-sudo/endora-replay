using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EndoraReplay.Core.Input;
using EndoraReplay.Core.Remote;
using EndoraReplay.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace EndoraReplay.Remote;

public sealed record RemoteCamera(int Id, string Name, string Status, bool Simulated);
public sealed record RemoteClip(long Id, int Source, string Tags, string Description, double Seconds, bool Armed);

/// <summary>State pushed to phones several times a second.</summary>
public sealed record RemoteState(
    string State,
    bool AtemConnected,
    int? Program,
    int? Preview,
    int ReplayInput,
    int SelectedCamera,
    double Speed,
    string Mode,
    double? MarkIn,
    double? MarkOut,
    double Position,
    double BufferSeconds,
    string? Armed,
    string? LastAlert,
    bool Override,
    bool AllowTransport,
    IReadOnlyList<RemoteCamera> Cameras,
    IReadOnlyList<string> Tags);

/// <param name="Action">An <see cref="OperatorActions"/> name, or "Jog", "SetTag", "ArmClip".</param>
public sealed record RemoteCommand(string Action, double? Value = null, string? Text = null, long? ClipId = null);

/// <summary>Implemented by the app: the remote only ever goes through the same operator command path as the keyboard.</summary>
public interface IRemoteHandler
{
    RemoteState GetState();
    IReadOnlyList<RemoteClip> GetClips();
    byte[]? GetPreviewJpeg(int? sourceId);

    /// <summary>Runs a command. Returns an error message, or null on success.</summary>
    Task<string?> ExecuteAsync(RemoteCommand command, string client);
}

public sealed record RemoteClientInfo(string Id, string Name, string Address, DateTime ConnectedUtc);

/// <summary>
/// Embedded web server for the phone remote (any iOS/Android browser, same network). Access needs the
/// one-session code carried in the QR link; regenerating the code drops every connected phone.
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    private static readonly HashSet<string> TransportActions =
    [
        OperatorActions.Replay, OperatorActions.Live, OperatorActions.Stop, OperatorActions.CancelAuto,
        OperatorActions.EmergencyOverride, "ArmClip",
    ];

    private readonly IRemoteHandler _handler;
    private readonly DiagnosticsService _diag;
    private readonly ConcurrentDictionary<string, (RemoteClientInfo Info, WebSocket Socket, CancellationTokenSource Cts)> _clients = new();
    private WebApplication? _app;
    private volatile string _code = "";
    private volatile bool _allowTransport = true;

    public RemoteServer(IRemoteHandler handler, DiagnosticsService diagnostics)
    {
        _handler = handler;
        _diag = diagnostics;
    }

    public int Port { get; private set; }
    public bool IsRunning => _app is not null;
    public IReadOnlyList<RemoteClientInfo> Clients => _clients.Values.Select(c => c.Info).ToList();
    public event Action? ClientsChanged;

    public string? Url => IsRunning ? $"http://{LanAddress()}:{Port}/r/{_code}" : null;

    public async Task StartAsync(int port, string accessCode, bool allowTransport)
    {
        await StopAsync().ConfigureAwait(false);
        _code = accessCode;
        _allowTransport = allowTransport;
        Port = port;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Any, port));
        builder.Services.AddRouting();
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });

        app.MapGet("/", () => Results.Text("Endora Replay remote. Scan the QR code on the Endora screen.", "text/plain"));
        app.MapGet("/r/{code}", (string code) => Authorized(code) ? Results.Content(Page(), "text/html; charset=utf-8") : Denied());
        app.MapGet("/api/state", (HttpContext ctx) => Authorized(ctx) ? Results.Json(_handler.GetState(), JsonOptions) : Denied());
        app.MapGet("/api/clips", (HttpContext ctx) => Authorized(ctx) ? Results.Json(_handler.GetClips(), JsonOptions) : Denied());
        app.MapGet("/api/preview.jpg", (HttpContext ctx, int? src) =>
        {
            if (!Authorized(ctx)) return Denied();
            var jpeg = _handler.GetPreviewJpeg(src);
            return jpeg is null ? Results.NoContent() : Results.Bytes(jpeg, "image/jpeg");
        });
        app.Map("/ws", HandleSocketAsync);
        // One URL per action for Bitfocus Companion (Generic HTTP), Stream Deck plugins and vMix scripts:
        // GET or POST /api/cmd/Replay?code=XXXX (optional &value=).
        app.MapMethods("/api/cmd/{action}", ["GET", "POST"], async (HttpContext ctx, string action, double? value, string? text) =>
        {
            if (!Authorized(ctx)) return Denied();
            var client = $"http:{ctx.Connection.RemoteIpAddress}";
            string? error;
            if (TransportActions.Contains(action) && !_allowTransport) error = "This remote may not take replays on or off air.";
            else error = await _handler.ExecuteAsync(new RemoteCommand(action, value, text), client).ConfigureAwait(false);
            return Results.Json(new { ok = error is null, action, error }, JsonOptions, statusCode: error is null ? 200 : 409);
        });

        await app.StartAsync().ConfigureAwait(false);
        _app = app;
        _diag.Info(LogCategory.Remote, $"Phone remote listening on {Url}");
    }

    public void SetAllowTransport(bool allow) => _allowTransport = allow;

    /// <summary>Switches to a new access code and disconnects all phones.</summary>
    public async Task ChangeCodeAsync(string code)
    {
        _code = code;
        foreach (var id in _clients.Keys) await DisconnectAsync(id).ConfigureAwait(false);
        _diag.Info(LogCategory.Remote, "Access code changed; all phones disconnected.");
    }

    public async Task DisconnectAsync(string clientId)
    {
        if (!_clients.TryRemove(clientId, out var c)) return;
        await c.Cts.CancelAsync().ConfigureAwait(false);
        try { c.Socket.Abort(); } catch (Exception) { }
        ClientsChanged?.Invoke();
    }

    /// <summary>QR code PNG for <see cref="Url"/>.</summary>
    public byte[]? QrPng(int pixelsPerModule = 10)
    {
        var url = Url;
        if (url is null) return null;
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private bool Authorized(string? code) => AccessCodeGenerator.Matches(_code, code);
    private bool Authorized(HttpContext ctx) => Authorized(ctx.Request.Query["code"].FirstOrDefault() ?? ctx.Request.Headers["X-Endora-Code"].FirstOrDefault());
    private static IResult Denied() => Results.Text("Access code is wrong or expired. Scan the QR code again.", "text/plain", statusCode: 403);

    private async Task HandleSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest || !Authorized(ctx))
        {
            ctx.Response.StatusCode = 403;
            return;
        }
        var name = ctx.Request.Query["name"].FirstOrDefault() ?? "Phone";
        name = name.Length > 40 ? name[..40] : name;
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var info = new RemoteClientInfo(id, name, ctx.Connection.RemoteIpAddress?.ToString() ?? "?", DateTime.UtcNow);
        _clients[id] = (info, socket, cts);
        ClientsChanged?.Invoke();
        _diag.Info(LogCategory.Remote, $"{name} connected from {info.Address}");

        var sendLock = new SemaphoreSlim(1, 1);
        async Task SendAsync(object msg)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
            await sendLock.WaitAsync(cts.Token).ConfigureAwait(false);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false); }
            finally { sendLock.Release(); }
        }

        var pusher = Task.Run(async () =>
        {
            string? last = null;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150));
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false) && socket.State == WebSocketState.Open)
            {
                var state = _handler.GetState() with { AllowTransport = _allowTransport };
                var json = JsonSerializer.Serialize(state, JsonOptions);
                if (json == last) continue;
                last = json;
                await SendAsync(new { type = "state", state }).ConfigureAwait(false);
            }
        }, cts.Token);

        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (!r.EndOfMessage) continue;
                RemoteCommand? cmd;
                try { cmd = JsonSerializer.Deserialize<RemoteCommand>(buffer.AsSpan(0, r.Count), JsonOptions); }
                catch (JsonException) { continue; }
                if (cmd is null) continue;

                string? error;
                if (TransportActions.Contains(cmd.Action) && !_allowTransport)
                    error = "This phone may mark, tag and pick angles only.";
                else
                    error = await _handler.ExecuteAsync(cmd, $"phone:{name}").ConfigureAwait(false);
                await SendAsync(new { type = "ack", action = cmd.Action, ok = error is null, error }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try { await pusher.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (WebSocketException) { }
            _clients.TryRemove(id, out _);
            ClientsChanged?.Invoke();
            _diag.Info(LogCategory.Remote, $"{name} disconnected");
        }
    }

    private static string? _page;

    private static string Page()
    {
        if (_page is not null) return _page;
        var asm = typeof(RemoteServer).Assembly;
        var res = asm.GetManifestResourceNames().First(n => n.EndsWith("index.html", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return _page = r.ReadToEnd();
    }

    /// <summary>Best guess at the LAN IPv4 address phones should use.</summary>
    public static string LanAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())
            .ToList();
        return candidates.FirstOrDefault(a => !a.StartsWith("169.254.", StringComparison.Ordinal)) ?? "127.0.0.1";
    }

    public async Task StopAsync()
    {
        foreach (var id in _clients.Keys) await DisconnectAsync(id).ConfigureAwait(false);
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
