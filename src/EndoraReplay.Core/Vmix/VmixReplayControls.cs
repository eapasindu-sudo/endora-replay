using System.Globalization;

namespace EndoraReplay.Core.Vmix;

/// <summary>
/// vMix Replay controls offered to the phone remote, Companion and keyboard: action name → vMix function.
/// Only these functions can be run from a remote; anything else is refused. Function names are from the vMix
/// Shortcut Function Reference (Replay section).
/// </summary>
public static class VmixReplayControls
{
    public const string Prefix = "vmix.";

    /// <summary>Starts the replay: from READY it runs the stinger and the ATEM and starts vMix playing at the cut; on air it resumes.</summary>
    public const string Play = "vmix.Play";

    /// <summary>Plays the last marked event, the same way as <see cref="Play"/>.</summary>
    public const string PlayLast = "vmix.PlayLast";

    /// <summary>Pauses the vMix replay. With auto return on, Endora goes back to live after the stop wait.</summary>
    public const string Stop = "vmix.Stop";

    public const string MarkIn = "vmix.MarkIn";
    public const string MarkOut = "vmix.MarkOut";
    public const string MarkCancel = "vmix.MarkCancel";

    /// <summary>New event from the last N seconds of live (value = seconds, default 8).</summary>
    public const string Last = "vmix.Last";

    /// <summary>Camera angle (value = 1-8).</summary>
    public const string Camera = "vmix.Camera";

    /// <summary>Jump by frames (value = -300..300).</summary>
    public const string Jog = "vmix.Jog";

    /// <summary>Playback speed (value = 0.05-1, 1 = 100 %).</summary>
    public const string Speed = "vmix.Speed";

    public const string Now = "vmix.Now";
    public const string NextEvent = "vmix.NextEvent";
    public const string PreviousEvent = "vmix.PreviousEvent";

    public static bool IsControl(string action) => action.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True for the actions that start a replay from READY.</summary>
    public static bool StartsReplay(string action) => action is Play or PlayLast;

    /// <summary>The vMix function for a play action: ReplayPlay or ReplayPlayLastEvent.</summary>
    public static string PlayFunction(string action) => action == PlayLast ? "ReplayPlayLastEvent" : "ReplayPlay";

    /// <summary>Maps an action to a vMix function and query. Returns an error for an unknown action or a bad value.</summary>
    public static string? TryMap(string action, double? value, out string function, out string? query)
    {
        function = "";
        query = null;
        static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        switch (action)
        {
            case Play: function = "ReplayPlay"; return null;
            case PlayLast: function = "ReplayPlayLastEvent"; return null;
            case Stop: function = "ReplayPause"; return null;
            case MarkIn: function = "ReplayMarkIn"; return null;
            case MarkOut: function = "ReplayMarkOut"; return null;
            case MarkCancel: function = "ReplayMarkCancel"; return null;
            case Now: function = "ReplayJumpToNow"; return null;
            case NextEvent: function = "ReplaySelectNextEvent"; return null;
            case PreviousEvent: function = "ReplaySelectPreviousEvent"; return null;
            case Last:
            {
                var s = value ?? 8;
                if (s is < 1 or > 120) return "Seconds must be 1-120.";
                function = "ReplayMarkInOutLive";
                query = "Value=" + Num(Math.Round(s));
                return null;
            }
            case Camera:
            {
                if (value is not { } c || c != Math.Floor(c) || c is < 1 or > 8) return "Camera must be 1-8.";
                function = "ReplayCamera" + Num(c);
                return null;
            }
            case Jog:
            {
                if (value is not { } f || f != Math.Floor(f) || f == 0 || Math.Abs(f) > 300) return "Jog must be -300..300 frames.";
                function = "ReplayJumpFrames";
                query = "Value=" + Num(f);
                return null;
            }
            case Speed:
            {
                if (value is not { } sp || sp is < 0.05 or > 1) return "Speed must be 5-100 %.";
                function = "ReplaySetSpeed";
                query = "Value=" + Num(sp);
                return null;
            }
            default:
                return $"{action} is not a vMix replay control.";
        }
    }
}
