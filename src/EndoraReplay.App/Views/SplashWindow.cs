using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EndoraReplay.App.Services;

namespace EndoraReplay.App.Views;

/// <summary>Loading screen with the Endora Replay branding, shown while the app starts.</summary>
public sealed class SplashWindow : Window
{
    private readonly TextBlock _status;

    public SplashWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = false;
        Width = 760;
        Height = 460;
        Title = "Endora Replay";
        Background = Brushes.Black;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/endora.ico"));

        var orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x00));
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/endora-branding.png")),
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        root.Children.Add(logo);

        var bar = new ProgressBar
        {
            IsIndeterminate = true, Height = 3, Margin = new Thickness(60, 8, 60, 14),
            Foreground = orange, Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), BorderThickness = new Thickness(0),
        };
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        var bottom = new DockPanel { Margin = new Thickness(60, 0, 60, 0) };
        var version = new TextBlock { Text = "v" + UpdateService.CurrentVersionText, Foreground = Brushes.Gray, FontSize = 12 };
        DockPanel.SetDock(version, Dock.Right);
        bottom.Children.Add(version);
        _status = new TextBlock { Text = "Starting…", Foreground = Brushes.White, FontSize = 13 };
        bottom.Children.Add(_status);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), BorderThickness = new Thickness(1), Child = root,
        };
    }

    public void SetStatus(string text) => _status.Text = text;
}
