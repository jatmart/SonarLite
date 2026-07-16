using System.Runtime.InteropServices;

namespace SonarLite.Interop;

/// <summary>
/// Per-application audio output routing via the undocumented AudioPolicyConfigFactory
/// (the same mechanism Windows Settings' per-app device picker and EarTrumpet use).
/// Routings persist in Windows across app and system restarts.
/// </summary>
public sealed class AppRoutingService
{
    // The activatable runtime class is AudioPolicyConfig; AudioPolicyConfigFactory is only the
    // name of its factory *interface*. Using the latter yields REGDB_E_CLASSNOTREG and silently
    // disables all per-app routing.
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private const string DevInterfaceAudioRender = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private object? _factory;
    private bool _tried;

    public bool IsAvailable => GetFactory() is not null;

    /// <summary>
    /// Route a process's playback to the given MMDevice ID, or back to system default when null.
    ///
    /// Routing SonarLite itself is refused outright. This override doesn't just move a process's
    /// audio -- it redefines what "the default device" *resolves to* inside that process, which is
    /// exactly how per-app routing works. Applied to ourselves it silently poisons every
    /// GetDefaultAudioEndpoint() call we make: SonarLite would see the cable as the system default
    /// forever, no matter what the real default is, and no matter how many times we set it. Our own
    /// render path never needed the override anyway (AudioEngine hands WasapiOut an explicit MMDevice),
    /// so a self-route is all cost and no benefit -- which is why this is a hard refusal at the
    /// chokepoint rather than a check in the one caller that happened to reach it.
    /// </summary>
    public bool SetRenderDevice(int processId, string? mmDeviceId)
    {
        if (processId == Environment.ProcessId) return false;
        return SetRenderDeviceCore(processId, mmDeviceId);
    }

    /// <summary>
    /// Drop any persisted routing override on SonarLite's own process. Returns true if one was
    /// actually there to remove.
    ///
    /// Windows persists these overrides per *app identity* (the executable), not per pid, so the
    /// refusal in <see cref="SetRenderDevice"/> only stops us writing a *new* self-route -- one
    /// written by an earlier build outlives the process that wrote it and every later pid inherits it,
    /// poisoning that process's view of the default device forever.
    ///
    /// Must be called once the process actually has an audio session, not before. Windows only
    /// associates a live pid with its persisted app identity when that pid has a session, so called
    /// any earlier this reads back "no override" and clears nothing -- while the override still
    /// materialises the moment the engine opens its render stream. The clear is issued
    /// unconditionally for the same reason: the read is only trustworthy after the fact, so gating the
    /// write on it reintroduces exactly that race.
    /// </summary>
    public bool ClearSelfRoute()
    {
        int me = Environment.ProcessId;
        bool had = GetRenderDevice(me) is not null;
        SetRenderDeviceCore(me, null);
        return had;
    }

    private bool SetRenderDeviceCore(int processId, string? mmDeviceId)
    {
        var factory = GetFactory();
        if (factory is null) return false;

        IntPtr hstr = IntPtr.Zero;
        try
        {
            if (mmDeviceId is not null)
            {
                var path = $@"\\?\SWD#MMDEVAPI#{mmDeviceId}#{DevInterfaceAudioRender}";
                Marshal.ThrowExceptionForHR(WindowsCreateString(path, path.Length, out hstr));
            }

            for (int role = 0; role <= 2; role++) // eConsole, eMultimedia, eCommunications
            {
                int hr = factory switch
                {
                    IAudioPolicyConfigWin11 w11 => w11.SetPersistedDefaultAudioEndpoint((uint)processId, 0, role, hstr),
                    IAudioPolicyConfigLegacy leg => leg.SetPersistedDefaultAudioEndpoint((uint)processId, 0, role, hstr),
                    _ => -1
                };
                Marshal.ThrowExceptionForHR(hr);
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hstr != IntPtr.Zero) WindowsDeleteString(hstr);
        }
    }

    /// <summary>Read a process's persisted playback routing target (device interface path), or null when default.</summary>
    public string? GetRenderDevice(int processId)
    {
        var factory = GetFactory();
        if (factory is null) return null;
        try
        {
            IntPtr h;
            int hr = factory switch
            {
                IAudioPolicyConfigWin11 w11 => w11.GetPersistedDefaultAudioEndpoint((uint)processId, 0, 1, out h),
                IAudioPolicyConfigLegacy leg => leg.GetPersistedDefaultAudioEndpoint((uint)processId, 0, 1, out h),
                _ => Fail(out h)
            };
            if (hr != 0 || h == IntPtr.Zero) return null;
            try
            {
                var ptr = WindowsGetStringRawBuffer(h, out var len);
                return ptr == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr, len);
            }
            finally { WindowsDeleteString(h); }
        }
        catch { return null; }

        static int Fail(out IntPtr h) { h = IntPtr.Zero; return -1; }
    }

    private object? GetFactory()
    {
        if (_tried) return _factory;
        _tried = true;
        try { RoInitialize(1); } catch { /* already initialized on this thread is fine */ }

        _factory = TryActivate(typeof(IAudioPolicyConfigWin11)) ?? TryActivate(typeof(IAudioPolicyConfigLegacy));
        return _factory;
    }

    private static object? TryActivate(Type interfaceType)
    {
        IntPtr hcls = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(ClassName, ClassName.Length, out hcls));
            var iid = interfaceType.GUID;
            if (RoGetActivationFactory(hcls, ref iid, out var p) != 0 || p == IntPtr.Zero) return null;
            try { return Marshal.GetTypedObjectForIUnknown(p, interfaceType); }
            finally { Marshal.Release(p); }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hcls != IntPtr.Zero) WindowsDeleteString(hcls);
        }
    }

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int len, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out int length);
}

// Vtable layout mirrors EarTrumpet's reverse-engineered interface. The first 3 stubs are
// IInspectable's methods (we declare IUnknown-based so the runtime doesn't need IInspectable
// support); the next 19 pad unrelated factory methods; then the three we actually call.
#pragma warning disable IDE1006
[ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigWin11
{
    void _insp1(); void _insp2(); void _insp3();
    void _s01(); void _s02(); void _s03(); void _s04(); void _s05(); void _s06(); void _s07();
    void _s08(); void _s09(); void _s10(); void _s11(); void _s12(); void _s13(); void _s14();
    void _s15(); void _s16(); void _s17(); void _s18(); void _s19();
    [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
    [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);
    [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
}

[ComImport, Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigLegacy
{
    void _insp1(); void _insp2(); void _insp3();
    void _s01(); void _s02(); void _s03(); void _s04(); void _s05(); void _s06(); void _s07();
    void _s08(); void _s09(); void _s10(); void _s11(); void _s12(); void _s13(); void _s14();
    void _s15(); void _s16(); void _s17(); void _s18(); void _s19();
    [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
    [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);
    [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
}
#pragma warning restore IDE1006
