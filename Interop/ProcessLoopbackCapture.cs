using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SonarLite.Interop;

/// <summary>
/// Captures the render streams of a single process (and its children) using the Windows
/// application-loopback API: ActivateAudioInterfaceAsync on the pseudo-device
/// "VAD\Process_Loopback" with AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK.
///
/// This is what lets SonarLite apply a distinct EQ curve per app bus without needing one
/// virtual cable per bus: we tap each app's audio directly by PID. Requires Windows 10
/// build 20348 or later.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";

    private const int ShareModeShared = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int StreamFlagsEventCallback = 0x00040000;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint StreamFlagsSrcDefaultQuality = 0x08000000;

    private const int BufferFlagsSilent = 0x2;
    private const short VtBlob = 65;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly int _processId;
    private readonly bool _excludeTree;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public WaveFormat WaveFormat { get; }

    public string? Error { get; private set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public ProcessLoopbackCapture(int processId, int sampleRate, int channels, bool excludeTree = false)
    {
        _processId = processId;
        _excludeTree = excludeTree;
        // AUTOCONVERTPCM lets us name any PCM format and have the engine convert the app's
        // stream for us; the loopback activation path does not support GetMixFormat.
        WaveFormat = new WaveFormat(sampleRate, 16, channels);
    }

    public void Start()
    {
        if (_thread is not null) return;
        // The default 1MB stack is sized for deep/recursive managed call graphs; this thread only
        // ever runs a flat WASAPI poll-and-copy loop plus a handful of COM marshaling frames, so a
        // much smaller reservation is the actually-correct size, not a trimmed-down compromise.
        // One of these exists per actively-tapped process, so this scales with real usage.
        _thread = new Thread(Run, maxStackSize: 256 * 1024) { IsBackground = true, Name = $"loopback-{_processId}" };
        // ActivateAudioInterfaceAsync invokes its completion handler on an MTA thread.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>
    /// Keeps re-establishing the tap until cancelled, rather than dying on the first failure.
    ///
    /// A single-shot activation is silently fatal here. Everything in this method runs on the
    /// capture's own thread, so a failure cannot reach AudioEngine.AddTapLocked's catch -- the tap
    /// stays registered in bus.Taps with its buffer wired into the mixer, producing nothing forever,
    /// and SyncAll only ever *adds* taps for pids it doesn't already hold, so the corpse is never
    /// replaced. The app sits routed to the silent cable, listed and "active", with no audio.
    ///
    /// Restarting the engine (any output-device switch) is precisely the case that provokes it: every
    /// capture is disposed and a fresh one is activated for the same pid a moment later, racing the
    /// OS's teardown of the stream we just closed. Retrying costs nothing when activation succeeds
    /// first time, and is the difference between a transient race and permanent silence when it
    /// doesn't.
    /// </summary>
    private void Run()
    {
        int failures = 0;
        while (!_cts.IsCancellationRequested)
        {
            if (RunSession()) failures = 0;   // reached the streaming loop; a later exit is a fresh problem
            if (_cts.IsCancellationRequested) break;

            failures++;
            // Ramp 250ms -> 2s. A pid that has genuinely gone away keeps failing until its tap is
            // removed, so this must not spin. Slept in slices against IsCancellationRequested rather
            // than waiting on _cts.Token.WaitHandle: Dispose can run while Activate() is still inside
            // its 5s wait, past the 2s join, and touching the token's handle after that throws
            // ObjectDisposedException -- on a background thread, which takes the process down.
            int backoffMs = Math.Min(2000, 250 * failures);
            for (int slept = 0; slept < backoffMs && !_cts.IsCancellationRequested; slept += 100)
                Thread.Sleep(100);
        }
    }

    /// <summary>Activate and stream until cancelled or the stream breaks. True if it got as far as
    /// actually streaming, false if activation itself failed.</summary>
    private bool RunSession()
    {
        IntPtr hEvent = IntPtr.Zero;
        IAudioClientLite? client = null;
        IAudioCaptureClientLite? capture = null;
        bool streamed = false;

        try
        {
            client = Activate();
            if (client is null) return false;

            uint flags = (uint)(StreamFlagsLoopback | StreamFlagsEventCallback)
                       | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality;

            Marshal.ThrowExceptionForHR(client.Initialize(ShareModeShared, (int)flags, 0, 0, WaveFormat, IntPtr.Zero));

            hEvent = CreateEventW(IntPtr.Zero, false, false, null);
            if (hEvent == IntPtr.Zero) throw new InvalidOperationException("CreateEvent failed.");
            Marshal.ThrowExceptionForHR(client.SetEventHandle(hEvent));

            var iid = IidAudioCaptureClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var captureObj));
            capture = (IAudioCaptureClientLite)captureObj;

            Marshal.ThrowExceptionForHR(client.Start());
            streamed = true;
            Error = null;

            var buffer = new byte[WaveFormat.AverageBytesPerSecond / 4];
            int blockAlign = WaveFormat.BlockAlign;

            while (!_cts.IsCancellationRequested)
            {
                WaitForSingleObject(hEvent, 200);

                while (!_cts.IsCancellationRequested)
                {
                    if (capture.GetNextPacketSize(out int packetFrames) != 0 || packetFrames == 0) break;
                    if (capture.GetBuffer(out var pData, out int frames, out int bufferFlags, out _, out _) != 0) break;

                    int bytes = frames * blockAlign;
                    if (bytes > 0)
                    {
                        if (buffer.Length < bytes) buffer = new byte[bytes];
                        if ((bufferFlags & BufferFlagsSilent) != 0 || pData == IntPtr.Zero)
                            Array.Clear(buffer, 0, bytes);
                        else
                            Marshal.Copy(pData, buffer, 0, bytes);

                        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                    }

                    capture.ReleaseBuffer(frames);
                }
            }

            try { client.Stop(); } catch { }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            if (capture is not null) Marshal.FinalReleaseComObject(capture);
            if (client is not null) Marshal.FinalReleaseComObject(client);
            if (hEvent != IntPtr.Zero) CloseHandle(hEvent);
        }

        return streamed;
    }

    private IAudioClientLite? Activate()
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1,                 // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)_processId,
            ProcessLoopbackMode = _excludeTree ? 1 : 0
        };

        IntPtr paramsPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<AudioClientActivationParams>());
        IntPtr propVariantPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var pv = new PropVariantBlob
            {
                Vt = VtBlob,
                CbSize = Marshal.SizeOf<AudioClientActivationParams>(),
                PBlobData = paramsPtr
            };
            Marshal.StructureToPtr(pv, propVariantPtr, false);

            var handler = new ActivationHandler();
            var iid = IidAudioClient;
            ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, ref iid, propVariantPtr, handler, out var op);
            Marshal.FinalReleaseComObject(op);

            if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
            {
                Error = "Loopback activation timed out.";
                return null;
            }
            if (handler.ActivateResult != 0 || handler.Client is null)
            {
                Error = $"Loopback activation failed (0x{handler.ActivateResult:X8}).";
                return null;
            }
            return handler.Client;
        }
        finally
        {
            Marshal.FreeCoTaskMem(propVariantPtr);
            Marshal.FreeCoTaskMem(paramsPtr);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _thread?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _thread = null;
        _cts.Dispose();
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Completed = new(false);
        public int ActivateResult = -1;
        public IAudioClientLite? Client;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out ActivateResult, out var iface);
                if (ActivateResult == 0 && iface is not null) Client = (IAudioClientLite)iface;
            }
            catch (Exception ex)
            {
                ActivateResult = ex.HResult;
            }
            finally
            {
                Completed.Set();
            }
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

/// <summary>
/// PROPVARIANT holding a VT_BLOB. Field offsets follow the 64-bit layout: the union begins at
/// offset 8, and BLOB's pointer member is 8-aligned within it.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PropVariantBlob
{
    [FieldOffset(0)] public short Vt;
    [FieldOffset(2)] public short Reserved1;
    [FieldOffset(4)] public short Reserved2;
    [FieldOffset(6)] public short Reserved3;
    [FieldOffset(8)] public int CbSize;
    [FieldOffset(16)] public IntPtr PBlobData;
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClientLite
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity,
                                 [In] WaveFormat pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out int bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out int padding);
    [PreserveSig] int IsFormatSupported(int shareMode, [In] WaveFormat pFormat, IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClientLite
{
    [PreserveSig] int GetBuffer(out IntPtr data, out int numFramesToRead, out int bufferFlags,
                                out long devicePosition, out long qpcPosition);
    [PreserveSig] int ReleaseBuffer(int numFramesRead);
    [PreserveSig] int GetNextPacketSize(out int numFramesInNextPacket);
}
