using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SonarLite.Interop;

/// <summary>
/// Native Shell_NotifyIcon wrapper. SonarLite used to pull in the entire WinForms framework
/// (System.Windows.Forms.dll, System.Windows.Forms.Primitives.dll, System.Drawing.Common.dll,
/// WindowsFormsIntegration.dll -- ~18MB of loaded module weight, plus WinForms' own native
/// message-pump/GDI+ init) just for one tray icon and its right-click menu. Both are a handful of
/// P/Invoke calls; the popup menu itself is a plain WPF ContextMenu, since WPF is already loaded
/// for the main window regardless. Left as a tray-only concern -- see NativeIcon for the matching
/// System.Drawing.Icon replacement used for app tile icons.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x8000 + 1; // WM_APP + 1
    private const uint NIM_ADD = 0, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA data);

    private NOTIFYICONDATA _data;
    private readonly HwndSource _source;
    private readonly IntPtr _hIcon;
    private bool _added;

    public event Action? Clicked;
    public event Action? RightClicked;

    /// <summary>Takes ownership of <paramref name="hIcon"/> and destroys it on Dispose.</summary>
    public TrayIcon(Window owner, IntPtr hIcon, string tip)
    {
        _hIcon = hIcon;
        var hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("No HwndSource for window handle.");
        _source.AddHook(WndProc);

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = tip
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            switch ((int)lParam)
            {
                // A single left click surfaces the window. A double click arrives as UP then DBLCLK;
                // routing both to the same handler means the second half of a double click is a
                // harmless idempotent re-show rather than a dead gesture.
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK: Clicked?.Invoke(); break;
                case WM_RBUTTONUP: RightClicked?.Invoke(); break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added) Shell_NotifyIcon(NIM_DELETE, ref _data);
        _source.RemoveHook(WndProc);
        NativeIcon.Destroy(_hIcon);
    }
}
