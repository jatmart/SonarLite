using System.IO;
using System.Windows;

namespace SonarLite;

public partial class App : System.Windows.Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonarLite", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "Ultra priority startup": raise our own process priority so, when we launch into the logon
        // crowd, the OS schedules our init ahead of the rest rather than time-slicing us behind it.
        // A standard user is allowed to raise its own class up to High (only Realtime needs admin),
        // so this needs no elevation. High -- not Realtime -- because Realtime can starve audio/input
        // system threads; High is the aggressive-but-safe tier and suits a low-latency audio app.
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High; }
        catch { /* priority is a nice-to-have; never fail startup over it */ }

        // Sliders, flat-colored panels and text -- nothing here benefits from GPU compositing, but
        // WPF's default hardware tier still stands up a full Direct3D device (and pulls in the GPU
        // vendor's shader compiler DLLs, tens of MB of driver-side allocations) the instant any
        // visual is rendered. Confirmed by direct A/B measurement, not just theory: cold-start
        // private memory was 98MB with hardware rendering vs 52MB with software -- the driver's
        // private footprint (device state, swapchain buffers, shader cache) is real, not just
        // shared/reusable pages. Software rendering is the correct choice for a memory-conscious
        // background utility with no graphically demanding content, not a workaround.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // A tray app that dies silently is impossible to diagnose after the fact.
        DispatcherUnhandledException += (_, args) => Log("Dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => Log("Task", args.Exception);
    }

    private static void Log(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"\n===== {DateTime.Now:u} [{source}]\n{ex}\n");
        }
        catch
        {
            // Nothing useful left to do if even the crash log can't be written.
        }
    }
}
