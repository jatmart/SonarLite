using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SonarLite.Interop;
using SonarLite.Models;

namespace SonarLite.Services;

public sealed class AudioSessionService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly PrefsStore _prefs;
    private readonly AppRoutingService _routing;
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    private MMDevice? _defaultDevice;
    private MMDevice? _cableDevice;
    private AudioSessionManager? _defaultMgr;
    private AudioSessionManager? _cableMgr;

    /// <summary>(pid, deviceId) -> the control we registered on and the relay doing the registering,
    /// so a later Expired/prune can unregister the exact pair instead of leaking the COM callback.</summary>
    private readonly Dictionary<(int pid, string device), (AudioSessionControl control, SessionEventRelay relay)> _eventClients = new();

    /// <summary>pid -> device IDs currently holding a non-Expired session for it. Empty means gone.</summary>
    private readonly Dictionary<int, HashSet<string>> _liveDevices = new();

    /// <summary>MMDevice ID of the virtual cable used purely as a silent sink for captured apps.</summary>
    public string? NullSinkDeviceId { get; private set; }

    /// <summary>
    /// Buses whose apps we want silenced onto the cable, because their curve actually shapes the
    /// sound. A flat bus stays on the headset so it pays no capture/mix latency at all.
    /// </summary>
    public HashSet<SessionClass> TappedBuses { get; } = new();

    /// <summary>
    /// PIDs whose session on the cable is Active right now -- audio is actually flowing through it,
    /// not just present-but-idle. Routing only takes effect when an app opens its next stream, so a
    /// pid's cable session can hang around Inactive (not yet Expired) long after its audio has
    /// already moved elsewhere; requiring Active is what keeps this the true "who is silenced right
    /// now" signal. Tap exactly these: tapping a pid whose session isn't Active on the cable would
    /// double its audio (it's already audible somewhere else), and dropping the tap before a pid's
    /// cable session actually migrates away would silence audio that hasn't moved yet.
    /// </summary>
    public HashSet<int> PidsOnNullSink { get; } = new();

    /// <summary>
    /// Fires (already marshaled to the UI thread) the instant a session appears, disappears, or its
    /// Active/Inactive/Expired state actually changes -- pushed by WASAPI rather than discovered on
    /// the next poll, which is what keeps EQ-drag transitions from cutting out for a poll interval.
    /// </summary>
    public event Action? SessionsChanged;

    /// <summary>Never touch the audio engine's own device-graph process.</summary>
    private static readonly string[] SystemProcesses = ["audiodg"];

    /// <summary>Per-bus gain (fader × mute × ChatMix dial) for apps we are not tapping.</summary>
    private readonly Dictionary<SessionClass, float> _busFactors =
        SessionClasses.All.ToDictionary(c => c, _ => 1f);

    public ObservableCollection<AppSession> Sessions { get; } = new();

    public AudioSessionService(PrefsStore prefs, AppRoutingService routing)
    {
        _prefs = prefs;
        _routing = routing;
    }

    public bool RoutingAvailable => _routing.IsAvailable && NullSinkDeviceId is not null;

    /// <summary>
    /// Re-hooks a device only when its ID actually changed (headset swap, cable appearing late),
    /// but always re-walks the already-open session managers' Sessions lists. OnSessionCreated
    /// alone is not reliable for sessions that appear because we retargeted an app's per-process
    /// default device -- that is not the same event WASAPI fires for a user-driven default-device
    /// change -- so this rescan is the real discovery path for a newly-tapped app, not just a
    /// backstop. It is still far cheaper than the old design: no MMDevice/AudioSessionManager
    /// churn, just a SessionCollection walk plus idempotent per-session bookkeeping.
    /// </summary>
    public void Refresh()
    {
        NullSinkDeviceId ??= FindRenderId("CABLE Input");

        var realDefault = ResolveRealDefaultDevice();
        if (realDefault is null)
        {
            // No real (non-cable) device available at all right now; nothing sensible to hook.
        }
        else if (_defaultDevice is null || _defaultDevice.ID != realDefault.ID)
            RehookDevice(realDefault, isNullSink: false, slot: ref _defaultDevice, mgrSlot: ref _defaultMgr);
        else
            realDefault.Dispose();

        if (NullSinkDeviceId is not null && (_cableDevice is null || _cableDevice.ID != NullSinkDeviceId))
        {
            MMDevice cable;
            try { cable = _enumerator.GetDevice(NullSinkDeviceId); }
            catch { return; }
            RehookDevice(cable, isNullSink: true, slot: ref _cableDevice, mgrSlot: ref _cableMgr);
        }

        RescanBoth();

        // Safety net for a session that somehow never signals Expired (e.g. a hard process kill).
        // Checks every underlying pid, not just the primary, so a merged tile survives as long as
        // any of its processes (e.g. Discord's other pid) is still alive.
        for (int i = Sessions.Count - 1; i >= 0; i--)
        {
            var session = Sessions[i];
            bool anyAlive = false;
            foreach (var pid in session.AllProcessIds)
            {
                try { using var _ = Process.GetProcessById(pid); anyAlive = true; break; }
                catch { }
            }
            if (!anyAlive)
            {
                Sessions.RemoveAt(i);
                foreach (var pid in session.AllProcessIds)
                {
                    _liveDevices.Remove(pid);
                    PidsOnNullSink.Remove(pid);
                    // A hard-killed process never raises Expired, so HandleStateChanged never runs
                    // for it; without this, its COM event registration (and the RCW pair keeping it
                    // alive) would sit in _eventClients forever.
                    foreach (var key in _eventClients.Keys.Where(k => k.pid == pid).ToList())
                        if (_eventClients.Remove(key, out var reg))
                            try { reg.control.UnRegisterEventClient(reg.relay); } catch { }
                }
            }
        }
    }

    private void RehookDevice(MMDevice device, bool isNullSink, ref MMDevice? slot, ref AudioSessionManager? mgrSlot)
    {
        try { slot?.Dispose(); } catch { }
        slot = device;

        AudioSessionManager mgr;
        try { mgr = device.AudioSessionManager; }
        catch { mgrSlot = null; return; }

        mgr.OnSessionCreated += (_, _) =>
            _dispatcher.BeginInvoke(() => RescanDevice(device, mgr, isNullSink));

        // Refresh() always calls RescanBoth() right after hooking, so this device gets scanned
        // there. Scanning it again here too would be a second RefreshSessions() call for free --
        // and that call is the one proven to leak a native handle per invocation (see RescanBoth's
        // doc comment), so every avoidable duplicate call matters.
        mgrSlot = mgr;
    }

    /// <summary>
    /// Rescans whichever devices are currently hooked. Kept off a tight timer deliberately:
    /// AudioSessionManager.RefreshSessions() leaks a native handle per call (measured -- Process
    /// handle count climbs continuously and linearly with call rate even at idle, while the
    /// managed GC heap stays flat at a few MB, so this is not garbage a GC can reclaim). Calling
    /// it 5x/sec (two devices at a 400ms poll) was the actual driver behind SonarLite's working
    /// set climbing continuously. See [[naudio-refreshsessions-handle-leak]].
    /// </summary>
    private void RescanBoth()
    {
        if (_defaultDevice is not null && _defaultMgr is not null)
            RescanDevice(_defaultDevice, _defaultMgr, isNullSink: false);
        if (_cableDevice is not null && _cableMgr is not null)
            RescanDevice(_cableDevice, _cableMgr, isNullSink: true);
    }

    private bool _rescanQueued;
    private bool _rescanFollowupQueued;

    /// <summary>
    /// Retargeting a pid's device (see RouteFor) only takes effect on its next stream open, and
    /// that doesn't reliably raise OnSessionCreated -- so rather than lean on a tight perpetual
    /// poll to eventually notice, rescan right when we cause the retarget, plus one short
    /// follow-up once the app has had a moment to actually reopen its stream.
    ///
    /// Coalesced rather than fired once per call: RouteAll() calls this once per session in a
    /// tight loop, and a rescan always re-walks every current session regardless of what changed,
    /// so N calls in the same burst only ever need to produce one immediate + one follow-up scan.
    /// Firing all N was pure waste on top of the already-documented per-call handle cost of the
    /// RefreshSessions() each rescan makes.
    /// </summary>
    private void RescanSoon()
    {
        // Always deferred, never inline: RouteFor is called from inside EnsureTile's own scan
        // (both for a brand new session and a sibling merge), so an inline rescan would re-enter
        // RescanDevice/ProcessSession/EnsureTile from partway through the very call it's nested
        // in -- for a session not yet added to Sessions, that recursed as "still new" forever.
        if (!_rescanQueued)
        {
            _rescanQueued = true;
            _dispatcher.BeginInvoke(() => { _rescanQueued = false; RescanBoth(); });
        }
        if (!_rescanFollowupQueued)
        {
            _rescanFollowupQueued = true;
            _ = Task.Delay(750).ContinueWith(_ => _dispatcher.BeginInvoke(() =>
            {
                _rescanFollowupQueued = false;
                RescanBoth();
            }));
        }
    }

    private void RescanDevice(MMDevice device, AudioSessionManager mgr, bool isNullSink)
    {
        // IAudioSessionEnumerator is documented as a point-in-time snapshot that never updates
        // itself; NAudio's Sessions getter lazily caches one and only replaces it when this is
        // called. Skipping it (relying only on OnSessionCreated) missed sessions that appear from
        // retargeting a process's device rather than a genuinely new stream -- that doesn't
        // reliably raise OnSessionCreated the way a real new session does.
        try { mgr.RefreshSessions(); } catch { return; }

        NAudio.CoreAudioApi.SessionCollection collection;
        try { collection = mgr.Sessions; } catch { return; }

        for (int i = 0; i < collection.Count; i++)
        {
            try { ProcessSession(collection[i], device, isNullSink); }
            catch { /* session vanished mid-scan */ }
        }
    }

    private void ProcessSession(AudioSessionControl control, MMDevice device, bool isNullSink)
    {
        AudioSessionState state;
        try { state = control.State; } catch { return; }

        int pid;
        try { pid = (int)control.GetProcessID; } catch { return; }
        if (pid == 0 || pid == Environment.ProcessId) return;

        if (state == AudioSessionState.AudioSessionStateExpired)
        {
            HandleStateChanged(pid, device, isNullSink, state);
            return;
        }

        var key = (pid, device.ID);
        if (!_eventClients.ContainsKey(key))
        {
            var relay = new SessionEventRelay(pid, device, isNullSink, this);
            try
            {
                control.RegisterEventClient(relay);
                _eventClients[key] = (control, relay);
            }
            catch { /* registration failure just means we fall back to the Refresh() safety net */ }
        }

        if (!_liveDevices.TryGetValue(pid, out var set))
            _liveDevices[pid] = set = new HashSet<string>();
        set.Add(device.ID);

        if (isNullSink) SetCableActive(pid, state == AudioSessionState.AudioSessionStateActive);

        EnsureTile(pid, control, state);
    }

    private void EnsureTile(int pid, AudioSessionControl control, AudioSessionState state)
    {
        var existing = Sessions.FirstOrDefault(s => s.ProcessId == pid || s.AliasControls.ContainsKey(pid));
        if (existing is not null)
        {
            // Routing a pid to a different device opens a brand new COM session object there; the
            // tile's Control still points at the old (now stale) one unless rebound, which freezes
            // its meter and misdirects its volume writes forever. But every enumeration hands us a
            // fresh NAudio wrapper even for a session we already know about, so reference equality
            // is never true for it -- comparing that way rebound (and discarded) a perfectly live
            // control every single rescan. Compare actual session identity instead.
            bool isPrimarySlot = existing.ProcessId == pid;
            var currentControl = isPrimarySlot ? existing.Control : existing.AliasControls[pid];
            if (state == AudioSessionState.AudioSessionStateActive && IsDifferentSession(currentControl, control))
            {
                if (isPrimarySlot) existing.Control = control;
                else existing.AliasControls[pid] = control;
            }
            return;
        }

        Process process;
        try { process = Process.GetProcessById(pid); }
        catch { return; }
        using var _ = process; // GetProcessById opens a native handle that must be released

        if (SystemProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
            return;

        var name = process.ProcessName;

        // A second (or third) pid under the same process name -- e.g. a multi-process app like
        // Discord that opens more than one audio session -- merges into that tile instead of
        // showing a duplicate row for what the user sees as one app.
        var sibling = Sessions.FirstOrDefault(s => s.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (sibling is not null)
        {
            sibling.AliasControls[pid] = control;
            RouteFor(sibling);
            ApplySession(sibling);
            SessionsChanged?.Invoke();
            return;
        }

        var saved = _prefs.Resolve(name);
        var session = new AppSession
        {
            ProcessId = pid,
            ProcessName = name,
            DisplayName = ResolveDisplayName(process, name),
            Icon = TryGetIcon(process),
            Control = control,
            UserVolume = saved.Volume,
            IsMuted = saved.Muted,
            Classification = saved.Classification
        };
        session.PropertyChanged += (_, e) =>
        {
            // The meter ticks many times a second and is not user state; never persist it.
            if (e.PropertyName == nameof(AppSession.Peak)) return;

            _prefs.Save(session.ProcessName, session.Classification, session.UserVolume, session.IsMuted);
            if (e.PropertyName == nameof(AppSession.Classification))
            {
                // The tile itself never needs to be torn down for this: the columns are
                // live-filtered views over Sessions keyed on Classification (see
                // MainWindow.CreateFilteredView), so changing this property alone already moves
                // the tile to its new column. And Control is already rebound in place, above,
                // the moment a genuinely new COM session shows up for this pid on the retargeted
                // device -- comparing session identity, not reference equality. Destroying and
                // recreating the whole AppSession here (as this used to do) meant re-extracting
                // the app's icon from disk on every single bus move, which is real GDI/WIC churn,
                // not a free operation, for zero benefit over just leaving the tile alone.
                RouteFor(session);
                SessionsChanged?.Invoke(); // tell the engine to move this pid's tap now
                return;
            }
            ApplySession(session);
        };

        // Add before routing: RouteFor's rescan (deferred, but still keyed off current state)
        // needs to find this tile already present, not re-discover the same pid as "new" again.
        Sessions.Add(session);
        RouteFor(session);
        ApplySession(session);
        SessionsChanged?.Invoke();
    }

    private static bool IsDifferentSession(AudioSessionControl current, AudioSessionControl fresh)
    {
        string currentId;
        try { currentId = current.GetSessionInstanceIdentifier; }
        catch { return true; } // the stored one is dead; the fresh one is the only usable option
        try { return currentId != fresh.GetSessionInstanceIdentifier; }
        catch { return false; } // the fresh one is unusable; keep what we have
    }

    private void SetCableActive(int pid, bool active)
    {
        bool changed = active ? PidsOnNullSink.Add(pid) : PidsOnNullSink.Remove(pid);
        if (!changed) return;

        var session = Sessions.FirstOrDefault(s => s.ProcessId == pid || s.AliasControls.ContainsKey(pid));
        if (session is not null) ApplySession(session);
        SessionsChanged?.Invoke();
    }

    private void HandleStateChanged(int pid, MMDevice device, bool isNullSink, AudioSessionState state)
    {
        if (isNullSink) SetCableActive(pid, state == AudioSessionState.AudioSessionStateActive);

        if (state != AudioSessionState.AudioSessionStateExpired)
        {
            SessionsChanged?.Invoke();
            return;
        }

        var key = (pid, device.ID);
        if (_eventClients.Remove(key, out var reg))
            try { reg.control.UnRegisterEventClient(reg.relay); } catch { }

        if (_liveDevices.TryGetValue(pid, out var set))
        {
            set.Remove(device.ID);
            if (set.Count == 0)
            {
                _liveDevices.Remove(pid);
                var session = Sessions.FirstOrDefault(s => s.ProcessId == pid || s.AliasControls.ContainsKey(pid));
                if (session is not null)
                {
                    // An alias pid dying just drops that slot; the tile survives on its primary or
                    // any other alias. The primary pid dying only removes the whole tile once no
                    // other pid sharing it is still alive either.
                    bool wasAlias = session.AliasControls.Remove(pid);
                    if (!wasAlias && !session.AllProcessIds.Any(id => id != pid && _liveDevices.ContainsKey(id)))
                        Sessions.Remove(session);
                }
            }
        }

        SessionsChanged?.Invoke();
    }

    /// <summary>Relays WASAPI's per-session callbacks (arriving on a COM thread) back onto the UI
    /// thread. Held alive by AudioSessionService._eventClients for exactly as long as it stays
    /// registered with COM -- letting it go out of scope before unregistering would silently stop
    /// delivery.</summary>
    private sealed class SessionEventRelay(int pid, MMDevice device, bool isNullSink, AudioSessionService owner)
        : IAudioSessionEventsHandler
    {
        public void OnVolumeChanged(float volume, bool isMuted) { }
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }

        public void OnStateChanged(AudioSessionState state) =>
            owner._dispatcher.BeginInvoke(() => owner.HandleStateChanged(pid, device, isNullSink, state));

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) =>
            owner._dispatcher.BeginInvoke(() =>
                owner.HandleStateChanged(pid, device, isNullSink, AudioSessionState.AudioSessionStateExpired));
    }

    /// <summary>Lazy on purpose: its one caller filters further before materializing, and eagerly
    /// building a HashSet here just to throw it away would be a wasted allocation every call.</summary>
    public IEnumerable<int> PidsFor(SessionClass cls) =>
        Sessions.Where(s => s.Classification == cls).SelectMany(s => s.AllProcessIds);

    private string? FindRenderId(string prefix)
    {
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (d)
                {
                    try
                    {
                        if (d.FriendlyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return d.ID;
                    }
                    catch { /* name unavailable; skip */ }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Headset power state, as last reported by the base station over HID. A powered-off headset
    /// keeps its Windows endpoint alive (the base station stays on USB), so it will happily accept
    /// the entire mix and play it to nobody -- which is why every "is this device a usable output"
    /// question has to consult this rather than just the endpoint's Active state.
    ///
    /// Defaults false, not true: StartEngine's first render-device pick runs before HidChatMixListener
    /// has even started (MainWindow's constructor calls StartEngine well before _hid.Start()), so at
    /// that first pick this flag can only ever be a guess -- an unstarted HID thread hasn't reported
    /// anything yet, and a machine with no base station at all never will. Guessing "off" fails toward
    /// a real fallback device; guessing "on" fails toward silently rendering to dead air until the
    /// first HID status report arrives a moment later and corrects it.
    /// </summary>
    public bool HeadsetOnline { get; set; }

    public static bool IsHeadset(string name) => name.Contains("Arctis", StringComparison.OrdinalIgnoreCase);

    /// <summary>False for a headset that's powered off -- never a legal render target.</summary>
    public bool IsUsableOutput(string name) => HeadsetOnline || !IsHeadset(name);

    /// <summary>
    /// How much the user wants their audio to come out of this device; lower wins, int.MaxValue
    /// means "never picked in our UI, so leave it alone". The headset outranks everything when it's
    /// powered on, whatever the user last picked manually: powering it on has to pull audio back to
    /// it. This is the single definition of preferred-output order -- MainWindow's device picker and
    /// the engine's render target both resolve through it, so the dropdown cannot end up naming a
    /// different device from the one the mix is actually coming out of (the two used to rank
    /// candidates by separate, quietly diverging rules, and did exactly that).
    /// </summary>
    public int PreferenceRank(string id, string name)
    {
        int i = _prefs.PlaybackPriority.IndexOf(id);
        if (i < 0) return int.MaxValue;
        return IsHeadset(name) && HeadsetOnline ? -1 : i;
    }

    /// <summary>
    /// The one real playback device the app should treat as "the default" everywhere it matters --
    /// where AudioEngine renders the mixed output, and where this class watches for brand-new
    /// (not-yet-tapped) apps' sessions -- normally just Windows' own Role.Multimedia default,
    /// except when that's our own routing cable or a powered-off headset. A tapped app actively
    /// streaming into the cable can make Windows report the cable itself as "default"; the cable
    /// isn't a real output device, so rendering to it is silent output and watching it for sessions
    /// misses every genuinely new app. In either case the real answer is the best candidate by
    /// <see cref="PreferenceRank"/>. Caller owns the returned device.
    ///
    /// Note this answers "where is the audio going", not "where should it go" -- it deliberately
    /// keeps Windows' default when that's a usable device the user never ranked, rather than
    /// dragging output onto a ranked one behind their back. MainWindow owns that second question.
    /// </summary>
    public MMDevice? ResolveRealDefaultDevice()
    {
        MMDevice raw;
        try { raw = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { return null; }
        if (raw.ID != NullSinkDeviceId && IsUsableOutput(SafeName(raw))) return raw;
        raw.Dispose();

        List<MMDevice> candidates;
        try
        {
            candidates = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Where(d =>
                {
                    var name = SafeName(d);
                    return !name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase) && IsUsableOutput(name);
                })
                .ToList();
        }
        catch { return null; }

        var best = candidates.OrderBy(d => PreferenceRank(d.ID, SafeName(d))).FirstOrDefault();
        foreach (var d in candidates)
            if (d != best) d.Dispose();
        return best;
    }

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Apps on a tapped bus are sent to the cable so only our EQ'd mix reaches the headset.
    /// Everything else plays straight to the default device.
    /// </summary>
    private void RouteFor(AppSession session)
    {
        if (!_routing.IsAvailable) return;
        var tapped = RoutingAvailable && TappedBuses.Contains(session.Classification);
        var target = tapped ? NullSinkDeviceId : null;
        foreach (var pid in session.AllProcessIds)
            _routing.SetRenderDevice(pid, target);
        RescanSoon();
    }

    public void RouteAll()
    {
        foreach (var s in Sessions.ToList()) RouteFor(s);
    }

    /// <summary>Send every app back to the default device (on exit, or when capture stops).</summary>
    public void RevertAllRoutings()
    {
        if (!_routing.IsAvailable) return;
        foreach (var s in Sessions.ToList())
            foreach (var pid in s.AllProcessIds)
                _routing.SetRenderDevice(pid, null);
    }

    public void SetBusFactor(SessionClass cls, float factor)
    {
        _busFactors[cls] = factor;
        ReapplyVolumes();
    }

    /// <summary>Reapply session volumes after a bus gain changes or a tap comes and goes.</summary>
    public void ReapplyVolumes()
    {
        foreach (var s in Sessions) ApplySession(s);
    }

    /// <summary>Poll every session's meter. Cheap enough to drive the UI meters at ~20fps.</summary>
    public void UpdatePeaks()
    {
        foreach (var s in Sessions) s.Peak = s.ReadPeak();
    }

    public float BusPeak(SessionClass cls)
    {
        float peak = 0f;
        foreach (var s in Sessions)
            if (s.Classification == cls && s.Peak > peak) peak = s.Peak;
        return peak;
    }

    private void ApplySession(AppSession session)
    {
        // An app we're actually tapping gets its bus gain inside the engine, so the session itself
        // stays at the user's own level. Anything still playing direct takes the bus gain here.
        // Tap state is genuinely per-pid, so a merged tile's underlying sessions can each need a
        // different factor (e.g. one pid tapped, its sibling still direct).
        ApplyOne(session, session.ProcessId, session.Control);
        foreach (var (pid, control) in session.AliasControls)
            ApplyOne(session, pid, control);
    }

    private void ApplyOne(AppSession session, int pid, AudioSessionControl control)
    {
        float factor = PidsOnNullSink.Contains(pid) ? 1f : _busFactors[session.Classification];
        session.ApplyVolume(control, factor);
    }

    private static string ResolveDisplayName(Process process, string fallback)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
                return description;
        }
        catch
        {
            // Access denied (elevated/protected process); fall back to exe name.
        }
        return fallback;
    }

    private static ImageSource? TryGetIcon(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (path is null) return null;
            return NativeIcon.ToFrozenImageSourceAndDestroy(NativeIcon.Extract(path));
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { _defaultDevice?.Dispose(); } catch { }
        try { _cableDevice?.Dispose(); } catch { }
        _enumerator.Dispose();
    }
}
