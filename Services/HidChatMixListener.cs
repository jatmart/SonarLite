using HidSharp;

namespace SonarLite.Services;

public sealed class ChatMixEventArgs(int gamePercent, int chatPercent) : EventArgs
{
    public int GamePercent { get; } = gamePercent;
    public int ChatPercent { get; } = chatPercent;
}

/// <summary>
/// <paramref name="IsInitial"/> marks the first reading after launch -- the state the headset was
/// already in, as opposed to the user reaching for the power button. Only the latter is an
/// expression of intent strong enough to override a device the user picked by hand.
/// </summary>
public sealed class HeadsetPowerEventArgs(bool online, bool isInitial) : EventArgs
{
    public bool Online { get; } = online;
    public bool IsInitial { get; } = isInitial;
}

/// <summary>
/// Reads the ChatMix dial position from a SteelSeries Arctis Nova Pro base station.
/// Protocol reverse-engineered against firmware 0130: MI_04&amp;COL02, report ID 0x07,
/// byte layout [07][45][game 0-100][chat 0-100].
/// </summary>
public sealed class HidChatMixListener : IDisposable
{
    private const int VendorId = 0x1038;
    private const byte InReportId = 0x07;
    private const byte ChatMixParam = 0x45;
    private const byte OutReportId = 0x06;
    private const byte EnableChatMixParam = 0x49;
    private const byte StatusParam = 0xB0;
    private const int HeadsetStateByte = 6;
    // Byte 6 of the 0xB0 status reply carries the power flag. Firmware reports 0x08 when the headset
    // is on -- the same 0x08=on flag the pushed 0xB5 report uses at report[4] (see OnHidStatus).
    // An earlier capture recorded 0x02, so accept that too. 0x08 is tested as a bit, not an exact
    // value: this byte has already changed meaning across firmware once, and an exact-match
    // whitelist turns any unlisted on-state (a composite like 0x0C while charging or with ANC
    // toggled) into a persistent false "off" -- which cleared the user's pick, moved the mix to the
    // speakers mid-session, and re-confirmed itself on every 2s poll with no way back while the
    // headset was audibly on. Observed off values (0x00, 0x04) have the 0x08 bit clear.
    private static bool IsConnectedFlag(byte b) => (b & 0x08) != 0 || b == 0x02;

    private CancellationTokenSource? _cts;
    private Thread? _loop;

    public event EventHandler<ChatMixEventArgs>? ChatMixChanged;

    /// <summary>Raised with the raw report whenever the base station sends a non-ChatMix status report.</summary>
    public event EventHandler<byte[]>? StatusReport;

    /// <summary>
    /// True once the base station's dial is actually being read, false while it's absent. The read
    /// loop retries a missing device forever and quietly, so without this nothing upstream can tell
    /// "dial sitting at centre" from "no dial here at all" -- and those need to look different: the
    /// UI has no honest reading to show, and the last dial position must not stay applied to the
    /// mix after the hardware that set it is gone.
    /// </summary>
    public event EventHandler<bool>? DialConnectedChanged;

    /// <summary>Headset power state, from the solicited 0xB0 status reply. Unlike the pushed 0xB5
    /// report this is answerable on demand, so it is known at startup rather than only after the
    /// user next touches the power button.</summary>
    public event EventHandler<HeadsetPowerEventArgs>? HeadsetOnlineChanged;

    private bool? _headsetOnline;
    private bool _dialConnected;

    // UTC ticks of the last valid 0xB0 reply, written by the status-reader thread and read by the
    // main loop. Any valid reply counts, including "off" -- silence here means the col01 pipe is
    // broken, not that the headset is off, and those must not be conflated: a broken pipe freezes
    // _headsetOnline at whatever was last seen, potentially stranding the mix on the wrong device
    // for the rest of the session.
    private long _lastStatusReplyTicks;

    // Probes go out every 2s, so this allows ~7 consecutive unanswered probes before the col01
    // status pipe is presumed dead and both collections are reconnected.
    private static readonly TimeSpan StatusSilenceLimit = TimeSpan.FromSeconds(15);

    private void SetDialConnected(bool connected)
    {
        if (_dialConnected == connected) return;
        _dialConnected = connected;
        DialConnectedChanged?.Invoke(this, connected);
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        // This loop blocks on HID reads for the app's entire lifetime -- Task.Run would park it on
        // a shared ThreadPool worker permanently, forcing the pool to grow a replacement for any
        // other async work that needs one. A dedicated thread is the correct tool for a genuinely
        // never-returning loop, and a small stack is enough for report polling.
        _loop = new Thread(() => RunLoop(_cts.Token), maxStackSize: 256 * 1024) { IsBackground = true, Name = "hid-chatmix" };
        _loop.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _loop = null;
    }

    private void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var dev = FindDevice("col02");
            if (dev is null)
            {
                // Headset/base station not present; back off and retry.
                SetDialConnected(false);
                Thread.Sleep(2000);
                continue;
            }

            // Opened once per connection and reused for the life of this loop, mirroring the
            // input stream below -- measured directly (isolated repro, 3000 iterations, zero
            // other code running): opening+writing+disposing a fresh HidStream per probe leaks
            // ~2.5KB of native memory every single call with no GC reclaim, and this used to run
            // every 2 seconds forever. That dwarfed every other source found in this app,
            // including the AudioSessionManager.RefreshSessions() cost documented on the audio
            // side. Reuse eliminates the open/dispose churn -- the actual leak site -- entirely.
            HidStream? outStream = null;
            var outDev = FindDevice("col01");

            Thread? statusReader = null;
            try
            {
                if (outDev is not null) { try { outStream = outDev.Open(); } catch { } }

                if (outStream is not null)
                {
                    Interlocked.Exchange(ref _lastStatusReplyTicks, DateTime.UtcNow.Ticks);
                    statusReader = new Thread(() => ReadStatusLoop(outStream, token), maxStackSize: 128 * 1024)
                    { IsBackground = true, Name = "hid-status" };
                    statusReader.Start();
                }

                using var stream = dev.Open();
                // Widened from 500ms: also measured directly -- HidStream.Read() leaks a smaller
                // but still real ~0.9KB per call even when it only times out (the common case
                // while the dial sits untouched), and a shorter timeout just means calling that
                // leaking API more often for no functional benefit. 2s still notices a dial move
                // quickly; it only changes how soon a *timeout* retries.
                stream.ReadTimeout = 2000;
                SendEnableChatMix(outDev, outStream);
                SetDialConnected(true);
                var buf = new byte[dev.GetMaxInputReportLength() + 1];
                var lastKeepAlive = DateTime.UtcNow;
                var lastProbe = DateTime.MinValue;

                while (!token.IsCancellationRequested)
                {
                    // Poll headset status (battery/connection) alongside the dial stream.
                    if (DateTime.UtcNow - lastProbe > TimeSpan.FromSeconds(2))
                    {
                        SendCommand(outDev, outStream, 0xB0);
                        lastProbe = DateTime.UtcNow;

                        // The status reader is the only live source of the headset's power state,
                        // and it can die (any non-timeout read error) or go silent (writes failing
                        // into a broken col01 stream, swallowed by SendCommand) while this dial
                        // stream stays perfectly healthy -- in which case nothing else here would
                        // ever notice, and _headsetOnline stays frozen for the rest of the session.
                        // Tear the connection down instead; the outer loop re-opens both
                        // collections and finally's _headsetOnline = null makes the next reading
                        // an initial one rather than a fake power transition.
                        if (statusReader is not null &&
                            (!statusReader.IsAlive ||
                             DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastStatusReplyTicks), DateTimeKind.Utc) > StatusSilenceLimit))
                        {
                            Thread.Sleep(1000);   // keep a persistently-broken col01 from becoming a tight reconnect spin
                            break;
                        }
                    }

                    int n;
                    try { n = stream.Read(buf); }
                    catch (TimeoutException)
                    {
                        // Base station may drop ChatMix mode if not periodically reasserted.
                        if (DateTime.UtcNow - lastKeepAlive > TimeSpan.FromSeconds(30))
                        {
                            SendEnableChatMix(outDev, outStream);
                            lastKeepAlive = DateTime.UtcNow;
                        }
                        continue;
                    }

                    if (n >= 4 && buf[0] == InReportId && buf[1] == ChatMixParam)
                    {
                        ChatMixChanged?.Invoke(this, new ChatMixEventArgs(buf[2], buf[3]));
                    }
                    else if (n >= 2)
                    {
                        StatusReport?.Invoke(this, buf[..n]);
                    }
                }
            }
            catch
            {
                // Device unplugged or opened elsewhere mid-stream; retry after a pause.
                Thread.Sleep(2000);
            }
            finally
            {
                SetDialConnected(false);
                // Next connection re-reports whatever it finds, as an initial reading rather than a
                // transition -- the base station going away is not the user pressing power.
                _headsetOnline = null;
                outStream?.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads the base station's replies to the periodic 0xB0 status probe, which is the only way to
    /// learn the headset's power state without waiting for it to change.
    ///
    /// The replies come back on col01 -- the interface we *write* commands to -- not col02, which is
    /// the only one the main loop reads. That mismatch is why the power state used to be unknowable
    /// at startup: the 0xB5 report is pushed on a power *transition* only, so a headset that was
    /// already on when SonarLite launched announced nothing, HeadsetOnline stayed false, and the mix
    /// went to the speakers until the headset was power-cycled. The probe was already being sent all
    /// along; nobody was listening to the answer.
    ///
    /// Frame captured live across a power cycle (report 0x06, param 0xB0):
    ///   on:  06 B0 00 00 01 00 [02] 08 08 00 02 0A 04 00 08 08 ...
    ///   off: 06 B0 00 00 01 00 [00] 08 08 00 02 0A 04 00 04 01 ...
    /// Byte 6 is the headset's connection state. Bytes 14/15 move with it (08 08 on, 04 01 off),
    /// matching the 0x08/0x04 semantics of the pushed 0xB5 report, but byte 6 is the clean flag.
    /// </summary>
    private void ReadStatusLoop(HidStream stream, CancellationToken token)
    {
        try
        {
            stream.ReadTimeout = 1000;
            // Sized from the observed 64-byte reply, NOT from col01's GetMaxInputReportLength():
            // col01 is the *output* collection, so it reports a tiny (often zero) input length, and
            // reading a 64-byte frame into a buffer cut to that throws -- which silently killed this
            // loop and took the headset's power state with it.
            var buf = new byte[Math.Max(64, stream.Device.GetMaxInputReportLength())];

            while (!token.IsCancellationRequested)
            {
                int n;
                try { n = stream.Read(buf); }
                catch (TimeoutException) { continue; }
                catch { break; }   // stream torn down under us; the outer loop re-establishes it

                if (n <= HeadsetStateByte || buf[0] != OutReportId || buf[1] != StatusParam) continue;
                Interlocked.Exchange(ref _lastStatusReplyTicks, DateTime.UtcNow.Ticks);
                SetHeadsetOnline(IsConnectedFlag(buf[HeadsetStateByte]));
            }
        }
        catch { /* the outer loop owns recovery */ }
    }

    private void SetHeadsetOnline(bool online)
    {
        if (_headsetOnline == online) return;
        bool first = _headsetOnline is null;
        _headsetOnline = online;
        HeadsetOnlineChanged?.Invoke(this, new HeadsetPowerEventArgs(online, first));
    }

    private static HidDevice? FindDevice(string collection) =>
        DeviceList.Local.GetHidDevices(vendorID: VendorId)
            .FirstOrDefault(d => d.DevicePath.Contains("mi_04", StringComparison.OrdinalIgnoreCase)
                               && d.DevicePath.Contains(collection, StringComparison.OrdinalIgnoreCase));

    private static void SendCommand(HidDevice? outDev, HidStream? outStream, byte param)
    {
        if (outDev is null || outStream is null) return;
        try
        {
            var report = new byte[outDev.GetMaxOutputReportLength()];
            report[0] = OutReportId;
            report[1] = param;
            outStream.Write(report);
        }
        catch { /* probe is best-effort */ }
    }

    private static void SendEnableChatMix(HidDevice? outDev, HidStream? outStream)
    {
        if (outDev is null || outStream is null) return;
        try
        {
            var report = new byte[outDev.GetMaxOutputReportLength()];
            report[0] = OutReportId;
            report[1] = EnableChatMixParam;
            report[2] = 0x01; // enable
            outStream.Write(report);
        }
        catch
        {
            // Best-effort; the read loop will retry on its own cadence.
        }
    }

    public void Dispose() => Stop();
}
