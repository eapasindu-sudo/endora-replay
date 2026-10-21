using EndoraReplay.Core.Services;

namespace EndoraReplay.Core.Vmix;

/// <summary>
/// Writes Endora's state into Bitfocus Companion custom variables through Companion's HTTP API
/// (POST /api/custom-variable/&lt;name&gt;/value?value=...), so Stream Deck buttons can show it. Only changed values
/// are sent. Create the variables in Companion first (Variables → Custom variables).
/// </summary>
public sealed class CompanionVariables : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly DiagnosticsService _diag;
    private readonly Dictionary<string, string> _sent = [];
    private readonly SemaphoreSlim _busy = new(1, 1);
    private string _baseUrl = "";
    private bool _warned;

    public CompanionVariables(DiagnosticsService diagnostics) => _diag = diagnostics;

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            var v = (value ?? "").Trim().TrimEnd('/');
            if (v == _baseUrl) return;
            _baseUrl = v;
            lock (_sent) _sent.Clear();
            _warned = false;
        }
    }

    public static string Url(string baseUrl, string name, string value) =>
        $"{baseUrl.TrimEnd('/')}/api/custom-variable/{Uri.EscapeDataString(name)}/value?value={Uri.EscapeDataString(value)}";

    /// <summary>Sends the values that changed since the last call. Never throws.</summary>
    public async Task PushAsync(IReadOnlyDictionary<string, string> values)
    {
        var baseUrl = _baseUrl;
        if (baseUrl.Length == 0) return;
        if (!await _busy.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            foreach (var (name, value) in values)
            {
                lock (_sent)
                {
                    if (_sent.TryGetValue(name, out var last) && last == value) continue;
                }
                try
                {
                    using var r = await Http.PostAsync(Url(baseUrl, name, value), null).ConfigureAwait(false);
                    if (!r.IsSuccessStatusCode)
                    {
                        Warn($"Companion answered {(int)r.StatusCode} for {name}. Create the custom variable '{name}' in Companion.");
                        continue;
                    }
                    lock (_sent) _sent[name] = value;
                    _warned = false;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    Warn($"Companion at {baseUrl} not reachable: {e.Message}");
                    return;
                }
            }
        }
        finally
        {
            _busy.Release();
        }
    }

    private void Warn(string message)
    {
        if (_warned) return;
        _warned = true;
        _diag.Warn(LogCategory.Remote, message);
    }

    public void Dispose() => _busy.Dispose();
}
