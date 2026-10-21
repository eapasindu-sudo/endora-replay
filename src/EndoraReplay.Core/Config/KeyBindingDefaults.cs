namespace EndoraReplay.Core.Input;

/// <summary>Operator actions that can be bound to keys, the phone remote and shortcut keyboards.</summary>
public static class OperatorActions
{
    public const string Replay = "Replay";
    public const string Live = "Live";
    public const string Stop = "Stop";
    public const string CancelAuto = "CancelAuto";
    public const string EmergencyOverride = "EmergencyOverride";
    public const string MarkIn = "MarkIn";
    public const string MarkOut = "MarkOut";
    public const string SaveClip = "SaveClip";
    public const string Tag = "Tag";
    public const string Queue = "Queue";
    public const string NextAngle = "NextAngle";
    public const string PreviousAngle = "PreviousAngle";
    public const string LiveMode = "LiveMode";
    public const string JogMode = "JogMode";
    public const string FrameBack = "FrameBack";
    public const string FrameForward = "FrameForward";
    public const string ShuttleBack = "ShuttleBack";
    public const string ShuttleForward = "ShuttleForward";
    public const string Speed100 = "Speed100";
    public const string Speed50 = "Speed50";
    public const string Speed25 = "Speed25";
    public const string ClearError = "ClearError";
    public const string Scoreboard = "Scoreboard";
    public const string SplitScreen = "SplitScreen";
    public const string ZoomIn = "ZoomIn";
    public const string ZoomOut = "ZoomOut";
    public const string ZoomReset = "ZoomReset";
    public const string PanLeft = "PanLeft";
    public const string PanRight = "PanRight";
    public const string PanUp = "PanUp";
    public const string PanDown = "PanDown";
    public const string BallNext = "BallNext";
    public const string BallUndo = "BallUndo";
    public static string Camera(int n) => $"Camera{n}";
}

public static class KeyBindingDefaults
{
    /// <summary>Action → gesture string in WPF KeyGesture syntax (e.g. "Shift+Left").</summary>
    public static readonly IReadOnlyDictionary<string, string> All = Build();

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>
        {
            [OperatorActions.Replay] = "Space",
            [OperatorActions.Live] = "Enter",
            [OperatorActions.Stop] = "Escape",
            [OperatorActions.CancelAuto] = "Back",
            [OperatorActions.EmergencyOverride] = "F12",
            [OperatorActions.MarkIn] = "I",
            [OperatorActions.MarkOut] = "O",
            [OperatorActions.SaveClip] = "S",
            [OperatorActions.Tag] = "T",
            [OperatorActions.Queue] = "Q",
            [OperatorActions.NextAngle] = "Tab",
            [OperatorActions.PreviousAngle] = "Shift+Tab",
            [OperatorActions.LiveMode] = "L",
            [OperatorActions.JogMode] = "J",
            [OperatorActions.FrameBack] = "Left",
            [OperatorActions.FrameForward] = "Right",
            [OperatorActions.ShuttleBack] = "Shift+Left",
            [OperatorActions.ShuttleForward] = "Shift+Right",
            [OperatorActions.Speed100] = "F1",
            [OperatorActions.Speed50] = "F2",
            [OperatorActions.Speed25] = "F3",
            [OperatorActions.ClearError] = "F9",
            [OperatorActions.Scoreboard] = "F5",
            [OperatorActions.SplitScreen] = "F6",
            [OperatorActions.ZoomIn] = "OemPlus",
            [OperatorActions.ZoomOut] = "OemMinus",
            [OperatorActions.ZoomReset] = "D0",
            [OperatorActions.PanLeft] = "Ctrl+Left",
            [OperatorActions.PanRight] = "Ctrl+Right",
            [OperatorActions.PanUp] = "Ctrl+Up",
            [OperatorActions.PanDown] = "Ctrl+Down",
            [OperatorActions.BallNext] = "B",
            [OperatorActions.BallUndo] = "Shift+B",
        };
        for (var i = 1; i <= 8; i++) d[OperatorActions.Camera(i)] = $"D{i}";
        return d;
    }

    /// <summary>Finds actions that share the same gesture (case-insensitive).</summary>
    public static IReadOnlyList<(string Gesture, string[] Actions)> FindConflicts(IReadOnlyDictionary<string, string> bindings) =>
        bindings.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .GroupBy(kv => kv.Value.Replace(" ", "").ToUpperInvariant())
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, g.Select(kv => kv.Key).OrderBy(x => x).ToArray()))
            .ToList();
}
