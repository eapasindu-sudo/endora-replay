using System.Windows;
using System.Windows.Controls;

namespace EndoraReplay.App.Views;

/// <summary>Input tile layout: only the cameras in use are shown, and they grow when the controls are minimised.</summary>
public partial class MainWindow
{
    private int _layoutCount = -1;
    private bool _layoutMinimized;
    private int _tileCols = 8, _tileRows = 1;

    private void ApplyLayoutIfChanged()
    {
        var ui = _host.Settings.Ui;
        if (ui.CameraCount != _layoutCount || ui.ControlsMinimized != _layoutMinimized) ApplyLayout();
    }

    private void ApplyLayout()
    {
        var ui = _host.Settings.Ui;
        var count = Math.Clamp(ui.CameraCount, 1, 8);
        _layoutCount = ui.CameraCount;
        _layoutMinimized = ui.ControlsMinimized;

        FullControls.Visibility = _layoutMinimized ? Visibility.Collapsed : Visibility.Visible;
        CompactControls.Visibility = _layoutMinimized ? Visibility.Visible : Visibility.Collapsed;
        ControlsRow.Height = _layoutMinimized ? GridLength.Auto : new GridLength(140);
        // Minimised: the tiles share the height with the monitors; otherwise they keep one row under the controls.
        TilesRow.Height = _layoutMinimized ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ControlsToggle.Content = _layoutMinimized ? "▴ Show controls" : "▾ Minimise controls";

        (_tileCols, _tileRows) = _layoutMinimized && count > 4 ? ((count + 1) / 2, 2) : (count, 1);
        Tiles.Columns = _tileCols;
        Tiles.Rows = _tileRows;
        for (var i = 0; i < Tiles.Children.Count; i++)
        {
            var tile = (FrameworkElement)Tiles.Children[i];
            tile.Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
            var lastInRow = (i + 1) % _tileCols == 0 || i == count - 1;
            tile.Margin = new Thickness(0, 0, lastInRow ? 0 : 10, _tileRows > 1 && i < _tileCols ? 10 : 0);
        }
        SizeTiles();
    }

    private void OnTilesSizeChanged(object sender, SizeChangedEventArgs e) => SizeTiles();

    /// <summary>Gives each preview a 16:9 frame as large as the space allows.</summary>
    private void SizeTiles()
    {
        if (Tiles.ActualWidth <= 0) return;
        const double captionHeight = 22;
        var width = (Tiles.ActualWidth - 10 * (_tileCols - 1)) / _tileCols;
        var height = width * 9 / 16;
        if (_layoutMinimized && Tiles.ActualHeight > 0)
            height = Math.Min(height, (Tiles.ActualHeight - 10 * (_tileRows - 1)) / _tileRows - captionHeight);
        else
            height = Math.Min(height, 200);
        height = Math.Max(60, height);
        foreach (var t in _tiles)
            if (t is not null && Math.Abs(t.Frame.Height - height) > 0.5) t.Frame.Height = height;
    }

    private void OnToggleControls(object sender, RoutedEventArgs e)
    {
        _host.Settings.Ui.ControlsMinimized = !_host.Settings.Ui.ControlsMinimized;
        try { _host.Settings.Save(_host.SettingsPath); }
        catch (Exception ex) { _host.Diagnostics.Warn(Core.Services.LogCategory.App, $"Could not save the layout: {ex.Message}"); }
        ApplyLayout();
    }
}
