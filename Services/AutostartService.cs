using Microsoft.Win32;

namespace SonarLite.Services;

/// <summary>
/// Autostart via the HKCU Run key, plus the one registry value that actually makes it fast after a
/// restart. Windows deliberately holds Run-key (and Startup-folder) launches back by ~10s after
/// logon via Explorer's serialized-startup delay so the desktop paints first -- that delay, not the
/// app's own init, is why SonarLite "took too long after a restart." StartupDelayInMSec=0 under the
/// per-user Explorer\Serialize key removes it, and it's all HKCU so it needs no elevation (a task in
/// the Scheduler's root folder is Access Denied for a standard user, and the app runs non-elevated
/// on purpose -- an elevated SonarLite can't see non-elevated apps' audio sessions). The matching
/// "ultra priority" half is the process raising its own priority class at startup; see App.OnStartup.
/// </summary>
internal static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SerializePath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
    private const string ValueName = "SonarLite";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            key.SetValue(ValueName, $"\"{exePath}\"");
            DisableStartupDelay();
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Explorer waits StartupDelayInMSec (default ~10s) after logon before launching Run/Startup
    /// items; forcing it to 0 for this user makes the launch immediate. Best-effort: if the write is
    /// somehow blocked, autostart still works, just with the default delay.
    /// </summary>
    private static void DisableStartupDelay()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SerializePath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(SerializePath);
            key.SetValue("StartupDelayInMSec", 0, RegistryValueKind.DWord);
        }
        catch { }
    }
}
