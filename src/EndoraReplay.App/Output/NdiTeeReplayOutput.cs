using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Hardware.Ndi;
using EndoraReplay.Hardware.Video;

namespace EndoraReplay.App.Output;

/// <summary>
/// Passes everything to the main replay output and also sends the same pictures as an NDI source.
/// The REPLAY badge is drawn by the display window only, so it is not in the NDI picture.
/// If NDI cannot start, the main output keeps working and the reason is logged.
/// </summary>
public sealed class NdiTeeReplayOutput(IReplayOutput inner, OutputSettings settings, DiagnosticsService diagnostics) : IReplayOutput
{
    private NdiSender? _ndi;
    private BgraImage? _holding;

    public string Name => _ndi is null ? inner.Name : $"{inner.Name} + NDI \"{_ndi.Name}\"";
    public bool IsSimulated => inner.IsSimulated;
    public OutputReadiness Readiness => inner.Readiness;
    public string ReadinessMessage => inner.ReadinessMessage;
    public EncodedFrame? LastFrame => inner.LastFrame;
    public bool ReplayBadgeVisible => inner.ReplayBadgeVisible;

    public event Action<OutputReadiness, string>? ReadinessChanged
    {
        add => inner.ReadinessChanged += value;
        remove => inner.ReadinessChanged -= value;
    }

    public async Task OpenAsync(VideoFormat format, CancellationToken cancellationToken)
    {
        await inner.OpenAsync(format, cancellationToken).ConfigureAwait(false);
        try
        {
            _holding = HoldingFrame.Render(settings.Holding, settings.HoldingImageFile, format);
            _ndi = new NdiSender(string.IsNullOrWhiteSpace(settings.NdiOutputName) ? "Endora Replay" : settings.NdiOutputName.Trim(), format);
            _ndi.SendImage(_holding);
            diagnostics.Info(LogCategory.Output, $"NDI output \"{_ndi.Name}\" started (untested hardware path).");
        }
        catch (Exception e)
        {
            _ndi = null;
            diagnostics.Error(LogCategory.Output, $"NDI output could not start: {e.Message}");
        }
    }

    public ModeValidationResult ValidateMode(VideoFormat format) => inner.ValidateMode(format);

    public void Present(EncodedFrame frame)
    {
        inner.Present(frame);
        _ndi?.Send(frame);
    }

    public void ShowHolding()
    {
        inner.ShowHolding();
        if (_holding is not null) _ndi?.SendImage(_holding);
    }

    public void SetReplayBadge(bool visible) => inner.SetReplayBadge(visible);

    public async ValueTask DisposeAsync()
    {
        _ndi?.Dispose();
        _ndi = null;
        await inner.DisposeAsync().ConfigureAwait(false);
    }
}
