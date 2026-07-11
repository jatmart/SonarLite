using HidSharp;

namespace SonarLite.Services;

public sealed class ChatMixEventArgs(int gamePercent, int chatPercent) : EventArgs
{
    public int GamePercent { get; } = gamePercent;
    public int ChatPercent { get; } = chatPercent;
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

    private CancellationTokenSource? _cts;
    private Thread? _loop;

    public event EventHandler<ChatMixEventArgs>? ChatMixChanged;

    /// <summary>Raised with the raw report whenever the base station sends a non-ChatMix status report.</summary>
    public event EventHandler<byte[]>? StatusReport;

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

            try
            {
                if (outDev is not null) { try { outStream = outDev.Open(); } catch { } }

                using var stream = dev.Open();
                // Widened from 500ms: also measured directly -- HidStream.Read() leaks a smaller
                // but still real ~0.9KB per call even when it only times out (the common case
                // while the dial sits untouched), and a shorter timeout just means calling that
                // leaking API more often for no functional benefit. 2s still notices a dial move
                // quickly; it only changes how soon a *timeout* retries.
                stream.ReadTimeout = 2000;
                SendEnableChatMix(outDev, outStream);
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
                outStream?.Dispose();
            }
        }
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
