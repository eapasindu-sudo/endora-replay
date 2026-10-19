using System.Windows;
using System.Windows.Threading;
using EndoraReplay.App.Services;
using EndoraReplay.App.Views;
using EndoraReplay.Core.Config;

namespace EndoraReplay.App;

public partial class App : Application
{
    private AppHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        AskCameraCount();
        var splash = new SplashWindow();
        splash.Show();
        var shownAt = DateTime.UtcNow;
        // Created on the UI thread so status updates land here.
        var progress = new Progress<string>(splash.SetStatus);
        try
        {
            _host = await Task.Run(() => AppHost.StartAsync(AppSettings.DefaultSettingsPath, progress));
        }
        catch (Exception ex)
        {
            splash.Close();
            MessageBox.Show($"Endora Replay could not start.\n\n{ex.Message}\n\nSettings file: {AppSettings.DefaultSettingsPath}",
                "Endora Replay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        splash.SetStatus("Opening the operator screen…");
        // Keep the loading screen up long enough to be seen, without delaying a slow start further.
        var minimum = TimeSpan.FromSeconds(1.5) - (DateTime.UtcNow - shownAt);
        if (minimum > TimeSpan.Zero) await Task.Delay(minimum);
        var main = new MainWindow(_host);
        MainWindow = main;
        main.Show();
        splash.Close();
    }

    /// <summary>Asks how many cameras are in use before anything starts recording, and saves the answer.</summary>
    private static void AskCameraCount()
    {
        try
        {
            var path = AppSettings.DefaultSettingsPath;
            var settings = AppSettings.Load(path);
            if (!settings.Ui.AskCameraCountOnStart) return;
            var w = new CameraCountWindow(settings.Ui.CameraCount, settings.Ui.AskCameraCountOnStart);
            if (w.ShowDialog() != true) return;
            settings.SetCameraCount(w.Count);
            settings.Ui.AskCameraCountOnStart = w.AskAgain;
            settings.Save(path);
        }
        catch (Exception)
        {
            // A broken settings file is reported by the normal start-up below.
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app (and the replay output) alive: log and continue rather than crash on air.
        _host?.Diagnostics.Error(Core.Services.LogCategory.App, $"Unhandled UI error: {e.Exception}");
        e.Handled = true;
    }
}
