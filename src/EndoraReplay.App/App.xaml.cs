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
        try
        {
            _host = await Task.Run(() => AppHost.StartAsync(AppSettings.DefaultSettingsPath));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Endora Replay could not start.\n\n{ex.Message}\n\nSettings file: {AppSettings.DefaultSettingsPath}",
                "Endora Replay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var main = new MainWindow(_host);
        MainWindow = main;
        main.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app (and the replay output) alive: log and continue rather than crash on air.
        _host?.Diagnostics.Error(Core.Services.LogCategory.App, $"Unhandled UI error: {e.Exception}");
        e.Handled = true;
    }
}
