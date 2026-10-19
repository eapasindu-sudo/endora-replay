using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EndoraReplay.App.Services;

namespace EndoraReplay.App.Views;

/// <summary>CPU, GPU, RAM and network meters in the header.</summary>
public partial class MainWindow
{
    private readonly SystemMonitor _monitor = new();
    private SystemLoad? _shownLoad;
    private readonly List<(TextBlock Value, Border Fill)> _meters = [];

    private void BuildMeters()
    {
        foreach (var label in new[] { "CPU", "GPU", "RAM", "NET" })
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(label == "CPU" ? 0 : 12, 0, 0, 0) };
            sp.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            var track = new Border { Width = 34, Height = 6, CornerRadius = new CornerRadius(3), Background = (Brush)FindResource("Line2"), VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border { Width = 0, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("Green") };
            track.Child = fill;
            if (label != "NET") sp.Children.Add(track);
            var value = new TextBlock { Text = "—", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), MinWidth = label == "NET" ? 96 : 32, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(value);
            Meters.Children.Add(sp);
            _meters.Add((value, fill));
        }
        Closed += (_, _) => _monitor.Dispose();
    }

    private void RefreshMeters()
    {
        var l = _monitor.Latest;
        if (l is null || ReferenceEquals(l, _shownLoad)) return;
        _shownLoad = l;
        Set(0, l.CpuPercent, $"{l.CpuPercent:0}%", "CPU in use");
        Set(1, l.GpuPercent ?? 0, l.GpuPercent is { } g ? $"{g:0}%" : "n/a", l.GpuPercent is null ? "GPU counters not available on this PC" : "Busiest GPU engine");
        Set(2, l.RamPercent, $"{l.RamPercent:0}%", $"{l.RamUsedGb:0.0} of {l.RamTotalGb:0.0} GB in use");
        _meters[3].Value.Text = $"↓{Mbps(l.NetInMbps)} ↑{Mbps(l.NetOutMbps)}";
        _meters[3].Value.ToolTip = "Network traffic in and out, all active adapters";
    }

    private static string Mbps(double v) => v >= 100 ? $"{v:0}M" : v >= 1 ? $"{v:0.0}M" : $"{v * 1000:0}k";

    private void Set(int i, double pct, string text, string tip)
    {
        var (value, fill) = _meters[i];
        value.Text = text;
        value.ToolTip = tip;
        fill.Width = 34 * Math.Clamp(pct, 0, 100) / 100;
        // Amber from 70 %, red from 90 %: the point where replay playout may start to drop frames.
        var key = pct >= 90 ? "Red" : pct >= 70 ? "Amber" : "Green";
        fill.Background = (Brush)FindResource(key);
        value.Foreground = pct >= 90 ? (Brush)FindResource("Red") : (Brush)FindResource("Text");
    }
}
