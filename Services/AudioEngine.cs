using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SonarLite.Interop;
using SonarLite.Models;

namespace SonarLite.Services;

/// <summary>
/// The heart of SonarLite. Each app classified into a bus (Game / Chat / Media) is routed to a
/// virtual cable so its own output is inaudible, and its audio is tapped directly by process ID
/// using the Windows application-loopback API. Every bus gets its own EQ curve and its own
/// ChatMix gain stage, and all three are mixed into a single stream rendered to the headset.
///
/// This is what makes three genuinely independent EQ profiles possible with just one virtual
/// cable, and why no Equalizer APO is involved: an APO on the headset endpoint would re-apply
/// one bus's curve to every other bus's audio on the way out.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private sealed class Bus
    {
        public required MixingSampleProvider Mixer;
        public required EqChain Eq;
        public required VolumeSampleProvider Volume;
        public readonly Dictionary<int, Tap> Taps = new();
    }

    private sealed record Tap(ProcessLoopbackCapture Capture, BufferedWaveProvider Buffer, ISampleProvider Sample);

    private readonly object _sync = new();
    private readonly Dictionary<SessionClass, Bus> _buses = new();
    private readonly Dictionary<SessionClass, double[]> _gains = new();
    private readonly Dictionary<SessionClass, bool> _eqEnabled = new();
    private readonly Dictionary<SessionClass, double> _preamp = new();
    private readonly Dictionary<SessionClass, float> _busGain = new();

    private WasapiOut? _output;
    private MMDevice? _renderDevice;
    private WaveFormat? _format;

    /// <summary>
    /// Master attenuation for the whole mix, driven by the volume of whichever endpoint Windows is
    /// showing the user (see <see cref="EndpointVolumeMirror"/>). Held as a field rather than only on
    /// the live provider so it survives Stop/Start -- the engine rebuilds on every device change, and
    /// a volume that silently reset to full on each rebuild would be its own bug.
    ///
    /// This has to be a gain stage inside the mix, not a write to the render endpoint's volume. The
    /// Arctis Nova Pro reports HardwareSupport = Volume, Mute and handles its level on the device,
    /// so it *silently ignores* MasterVolumeLevelScalar writes -- verified: writing 0.42 read back
    /// 1.000, no exception. Any design that mirrors volume by writing the output endpoint works on a
    /// Realtek and does nothing at all on the headset the user actually wears. Attenuating the
    /// samples we already own works everywhere and cannot be refused.
    /// </summary>
    private float _masterGain = 1f;
    private bool _masterMute;
    private VolumeSampleProvider? _masterVolume;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Not started";

    /// <summary>The device this engine is actually rendering to right now, or null if not running.
    /// This is a snapshot taken at Start() -- Windows' own notion of "default device" can keep
    /// changing afterward (e.g. another app fighting for the same headset) without this engine's
    /// actual output moving at all, since nothing here follows that live. Callers deciding whether
    /// a restart is worth the audible dropout should compare against this, not against a fresh
    /// GetDefaultAudioEndpoint() read.</summary>
    public string? RenderDeviceId => _renderDevice?.ID;

    /// <summary>Friendly name of the device from <see cref="RenderDeviceId"/>, cached at Start().
    /// Read from the MMDevice each time, this would be a COM call per access -- and callers want it
    /// on the UI thread, where the device list isn't necessarily populated yet (RefreshDeviceLists
    /// runs after the first StartEngine), so resolving the name by id through that list would come
    /// up empty at launch.</summary>
    public string? RenderDeviceName { get; private set; }

    public AudioEngine()
    {
        foreach (SessionClass cls in SessionClasses.All)
        {
            _gains[cls] = new double[10];
            _eqEnabled[cls] = true;
            _preamp[cls] = 0;
            _busGain[cls] = 1f;
        }
    }

    /// <summary>
    /// Set the master gain from a linear amplitude (1.0 = unity), plus mute. The caller converts
    /// Windows' volume scalar through the endpoint's own dB taper before calling, so the result
    /// tracks the loudness curve the volume UI implies rather than a raw linear fraction -- 50% on
    /// the slider is about -18dB, not half amplitude, and using the scalar directly would make the
    /// top of the range feel almost flat and the bottom fall off a cliff.
    /// </summary>
    public void SetMasterVolume(float amplitude, bool mute)
    {
        lock (_sync)
        {
            _masterGain = Math.Clamp(amplitude, 0f, 1f);
            _masterMute = mute;
            if (_masterVolume is not null) _masterVolume.Volume = _masterMute ? 0f : _masterGain;
        }
    }

    /// <summary>
    /// Renders to <paramref name="headset"/> exactly as given -- the caller (MainWindow, via
    /// AudioSessionService.ResolveRealDefaultDevice()) is responsible for resolving the real
    /// output device. This used to call GetDefaultAudioEndpoint() itself, which could return
    /// SonarLite's own routing cable as "default" -- rendering the whole mix into the cable is silent
    /// output, since nothing is listening to it as real speakers. That was long blamed on "apps tapped
    /// onto the cable make Windows report it that way", which was wrong: the real cause was SonarLite
    /// holding a per-app routing override on *itself*, which by design redefines what "default"
    /// resolves to inside this process. Fixed at the source (AppRoutingService.ClearSelfRoute /
    /// SetRenderDevice), so the raw default is trustworthy again -- but taking the device from the
    /// caller is still the right shape, since only the caller knows about manual picks and headset
    /// power. Takes ownership of <paramref name="headset"/> either way (disposed on failure, or held
    /// for the engine's lifetime).
    /// </summary>
    public bool Start(MMDevice? headset)
    {
        Stop();
        if (headset is null) { Status = "No default playback device."; return false; }
        lock (_sync)
        {
            try
            {
                // Match the endpoint's mix rate so nothing has to resample on the way out.
                int rate = headset.AudioClient.MixFormat.SampleRate;
                _format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);

                var master = new MixingSampleProvider(_format) { ReadFully = true };
                foreach (SessionClass cls in SessionClasses.All)
                {
                    var mixer = new MixingSampleProvider(_format) { ReadFully = true };
                    var eq = new EqChain(mixer);
                    eq.SetGains(_gains[cls], _eqEnabled[cls], _preamp[cls]);
                    var volume = new VolumeSampleProvider(eq) { Volume = _busGain[cls] };
                    master.AddMixerInput(volume);
                    _buses[cls] = new Bus { Mixer = mixer, Eq = eq, Volume = volume };
                }

                // Master gain sits after the bus mix and before the limiter, so turning the volume
                // down actually reduces what the limiter has to work on rather than being clamped
                // back up by it.
                _masterVolume = new VolumeSampleProvider(master) { Volume = _masterMute ? 0f : _masterGain };

                _output = new WasapiOut(headset, AudioClientShareMode.Shared, true, 50);
                _output.Init(new SafetyLimiter(_masterVolume));
                _output.Play();

                _renderDevice = headset;
                try { RenderDeviceName = headset.FriendlyName; } catch { RenderDeviceName = null; }
                IsRunning = true;
                Status = "Running";
                return true;
            }
            catch (Exception ex)
            {
                Status = $"Failed: {ex.Message}";
                try { headset.Dispose(); } catch { }
                StopLocked();
                return false;
            }
        }
    }

    /// <summary>
    /// Attach/detach per-process taps so every bus captures exactly its entry in <paramref
    /// name="wantedByClass"/>. Takes the state of all three buses at once, not one class at a time
    /// -- syncing bus-by-bus can't tell a cross-bus move from a plain removal, since by the time
    /// the destination bus's turn came, the source bus's own removal step (processed first) would
    /// already have torn the tap down. Deciding removals and moves together in one locked pass
    /// means a PID that's still wanted anywhere is always re-parented, never torn down and
    /// reactivated, regardless of which bus happens to be examined first.
    /// </summary>
    public void SyncAll(IReadOnlyDictionary<SessionClass, IReadOnlyCollection<int>> wantedByClass)
    {
        lock (_sync)
        {
            if (!IsRunning || _format is null) return;

            foreach (var (cls, bus) in _buses)
            {
                var wanted = wantedByClass.TryGetValue(cls, out var set) ? set : [];
                foreach (var pid in bus.Taps.Keys.Where(p => !wanted.Contains(p)).ToList())
                {
                    bool wantedElsewhere = wantedByClass.Any(kv => kv.Key != cls && kv.Value.Contains(pid));
                    if (!wantedElsewhere) RemoveTapLocked(bus, pid);
                }
            }

            foreach (var (cls, bus) in _buses)
            {
                var wanted = wantedByClass.TryGetValue(cls, out var set) ? set : [];
                foreach (var pid in wanted)
                {
                    if (bus.Taps.ContainsKey(pid)) continue;
                    // A PID belongs to exactly one bus. The capture taps the process system-wide
                    // and doesn't care which mixer its output feeds, so a bus move is just
                    // re-parenting the existing tap -- never tear down and reactivate a COM
                    // capture that's already live for this pid. (Reactivating here used to race
                    // the OS's teardown of the just-closed stream and could go silent forever with
                    // no error to catch.)
                    var stolenFrom = _buses.Values.FirstOrDefault(b => b != bus && b.Taps.ContainsKey(pid));
                    if (stolenFrom is not null)
                        MoveTapLocked(stolenFrom, bus, pid);
                    else
                        AddTapLocked(bus, pid);
                }
            }
        }
    }

    private void AddTapLocked(Bus bus, int pid)
    {
        var capture = new ProcessLoopbackCapture(pid, _format!.SampleRate, 2);
        try
        {
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(500)
            };
            capture.DataAvailable += (_, e) =>
            {
                // Bound mix-back latency if render ever falls behind capture.
                if (buffer.BufferedDuration.TotalMilliseconds > 200) buffer.ClearBuffer();
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            var sample = buffer.ToSampleProvider();

            bus.Mixer.AddMixerInput(sample);
            bus.Taps[pid] = new Tap(capture, buffer, sample);
            capture.Start();
        }
        catch
        {
            // Activation failed before the tap was registered in bus.Taps -- dispose it
            // here or it's never reached by RemoveTapLocked and its capture thread/CTS
            // leaks until finalization.
            capture.Dispose();
        }
    }

    private static void MoveTapLocked(Bus from, Bus to, int pid)
    {
        if (!from.Taps.Remove(pid, out var tap)) return;
        try { from.Mixer.RemoveMixerInput(tap.Sample); } catch { }
        to.Mixer.AddMixerInput(tap.Sample);
        to.Taps[pid] = tap;
    }

    private static void RemoveTapLocked(Bus bus, int pid)
    {
        if (!bus.Taps.Remove(pid, out var tap)) return;
        try { bus.Mixer.RemoveMixerInput(tap.Sample); } catch { }
        tap.Capture.Dispose();
    }

    public void SetEq(SessionClass cls, double[] gains, bool enabled, double preampDb = 0)
    {
        lock (_sync)
        {
            _gains[cls] = gains;
            _eqEnabled[cls] = enabled;
            _preamp[cls] = preampDb;
            if (_buses.TryGetValue(cls, out var bus)) bus.Eq.SetGains(gains, enabled, preampDb);
        }
    }

    public void SetGain(SessionClass cls, float gain)
    {
        lock (_sync)
        {
            _busGain[cls] = gain;
            if (_buses.TryGetValue(cls, out var bus)) bus.Volume.Volume = gain;
        }
    }

    public void Stop()
    {
        lock (_sync) StopLocked();
    }

    private void StopLocked()
    {
        IsRunning = false;
        foreach (var bus in _buses.Values)
            foreach (var pid in bus.Taps.Keys.ToList())
                RemoveTapLocked(bus, pid);
        _buses.Clear();

        try { _output?.Stop(); } catch { }
        _output?.Dispose();
        _output = null;

        try { _renderDevice?.Dispose(); } catch { }
        _renderDevice = null;
        RenderDeviceName = null;
        _format = null;
    }

    public void Dispose() => Stop();
}
