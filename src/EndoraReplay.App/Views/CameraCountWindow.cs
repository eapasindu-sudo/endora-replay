using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EndoraReplay.App.Views;

/// <summary>Asked at start: how many cameras are in use today. Number keys 1-8 pick, Enter keeps the last choice.</summary>
public sealed class CameraCountWindow : Window
{
    public int Count { get; private set; }
    public bool AskAgain { get; private set; }

    /// <summary>The operator chose vMix replay (no cameras recorded by Endora).</summary>
    public bool UseVmix { get; private set; }

    public CameraCountWindow(int current, bool askAgain)
    {
        Count = current;
        AskAgain = askAgain;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Black;
        Title = "Endora Replay";
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/endora.ico"));

        var orange = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x00));
        var root = new StackPanel { Margin = new Thickness(36, 28, 36, 28) };
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/endora-branding.png")), Height = 150, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        root.Children.Add(logo);
        root.Children.Add(new TextBlock
        {
            Text = "How many cameras today?", Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 16),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        for (var n = 1; n <= 8; n++)
        {
            var count = n;
            var b = new Button
            {
                Content = n.ToString(), Width = 64, Height = 64, FontSize = 26, FontWeight = FontWeights.Black, Margin = new Thickness(5),
                Foreground = n == current ? Brushes.Black : Brushes.White,
                Background = n == current ? orange : new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
                BorderBrush = n == current ? orange : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                Cursor = Cursors.Hand,
            };
            b.Click += (_, _) => Pick(count);
            row.Children.Add(b);
        }
        root.Children.Add(row);
        var vmix = new Button
        {
            Content = "Replays from vMix instead (Endora runs the stinger and the ATEM)  ·  V", Height = 44, FontSize = 14, FontWeight = FontWeights.Bold,
            Margin = new Thickness(5, 14, 5, 0), Foreground = Brushes.White, Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), BorderBrush = orange,
        };
        vmix.Click += (_, _) => PickVmix();
        root.Children.Add(vmix);
        var ask = new CheckBox
        {
            Content = "Ask every time Endora starts", IsChecked = askAgain, Foreground = Brushes.Gray, FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0),
        };
        ask.Click += (_, _) => AskAgain = ask.IsChecked == true;
        root.Children.Add(ask);
        root.Children.Add(new TextBlock
        {
            Text = $"Press 1-8, or Enter to keep {current}. You can change it later in Settings → Capture inputs.",
            Foreground = Brushes.Gray, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0),
        });
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), BorderThickness = new Thickness(1), Child = root };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is >= Key.D1 and <= Key.D8) Pick(e.Key - Key.D0);
            else if (e.Key is >= Key.NumPad1 and <= Key.NumPad8) Pick(e.Key - Key.NumPad0);
            else if (e.Key == Key.V) PickVmix();
            else if (e.Key is Key.Enter or Key.Escape) Pick(current);
        };
    }

    private void PickVmix()
    {
        UseVmix = true;
        DialogResult = true;
    }

    private void Pick(int n)
    {
        Count = n;
        DialogResult = true;
    }
}
