using System.Reflection;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Services;
using Velopack;
using Velopack.Sources;

namespace EndoraReplay.App.Services;

public enum UpdateStatus
{
    NotInstalled,
    UpToDate,
    Checking,
    Downloading,
    ReadyToInstall,
    Failed,
}

/// <summary>
/// Automatic updates from GitHub Releases via Velopack. Updates download in the background but are only
/// applied when the operator presses "Restart &amp; install", never while recording or on air.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateSettings _settings;
    private readonly DiagnosticsService _diag;
    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public UpdateService(UpdateSettings settings, DiagnosticsService diagnostics)
    {
        _settings = settings;
        _diag = diagnostics;
        _manager = new UpdateManager(new GithubSource(settings.RepositoryUrl, null, settings.IncludePrereleases));
    }

    public static string CurrentVersionText =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev";

    public UpdateStatus Status { get; private set; } = UpdateStatus.UpToDate;
    public string? AvailableVersion => _pending?.TargetFullRelease.Version.ToString();
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public bool IsInstalled => _manager.IsInstalled;

    public event Action? Changed;

    /// <summary>Checks GitHub and downloads a newer version in the background.</summary>
    public async Task CheckAndDownloadAsync()
    {
        if (!_manager.IsInstalled)
        {
            Status = UpdateStatus.NotInstalled;
            Changed?.Invoke();
            return;
        }
        if (Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.ReadyToInstall) return;
        try
        {
            Status = UpdateStatus.Checking;
            Changed?.Invoke();
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                Status = UpdateStatus.UpToDate;
                Changed?.Invoke();
                return;
            }
            _diag.Info(LogCategory.Update, $"Update {info.TargetFullRelease.Version} available; downloading");
            Status = UpdateStatus.Downloading;
            Changed?.Invoke();
            await _manager.DownloadUpdatesAsync(info, p => { Progress = p; Changed?.Invoke(); }).ConfigureAwait(false);
            _pending = info;
            Status = UpdateStatus.ReadyToInstall;
            _diag.Info(LogCategory.Update, $"Update {info.TargetFullRelease.Version} downloaded; installs on restart");
        }
        catch (Exception e)
        {
            Status = UpdateStatus.Failed;
            Error = e.Message;
            _diag.Warn(LogCategory.Update, $"Update check failed: {e.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>Runs periodic checks while the app is open.</summary>
    public async Task RunBackgroundAsync(CancellationToken ct)
    {
        if (!_settings.CheckAutomatically) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await CheckAndDownloadAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Applies the downloaded update and restarts. Caller must ensure nothing is on air.</summary>
    public void ApplyAndRestart()
    {
        if (_pending is null) return;
        _diag.Info(LogCategory.Update, $"Installing {_pending.TargetFullRelease.Version} and restarting");
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}
