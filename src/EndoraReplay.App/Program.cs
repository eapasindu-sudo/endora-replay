using System.Runtime.InteropServices;
using Velopack;

namespace EndoraReplay.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles install/update/uninstall hooks from the Velopack installer.
        VelopackApp.Build().Run();

        // 1 ms timer resolution for frame-accurate playout pacing.
        _ = TimeBeginPeriod(1);
        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        finally
        {
            _ = TimeEndPeriod(1);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);
}
