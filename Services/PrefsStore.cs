using System.IO;
using System.Text.Json;
using SonarLite.Models;

namespace SonarLite.Services;

public sealed class PrefsStore
{
    private static readonly string DirPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonarLite");
    private static readonly string FilePath = Path.Combine(DirPath, "prefs.json");

    private static readonly string[] DefaultChatProcesses =
    [
        "discord", "discordptb", "discordcanary", "teams", "ms-teams",
        "slack", "zoom", "skype", "telegram", "signal", "webex", "vesktop"
    ];

    private Dictionary<string, SessionPrefs> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, double[]> EqGains { get; set; } = new();       // keyed by SessionClass name
    public Dictionary<string, string> EqPresets { get; set; } = new();       // profile -> preset name
    public bool EqEnabled { get; set; } = true;
    public List<string> PlaybackPriority { get; set; } = new();
    public List<string> RecordingPriority { get; set; } = new();
    public Dictionary<string, float> BusVolumes { get; set; } = new();       // bus -> fader 0..1
    public Dictionary<string, bool> BusMuted { get; set; } = new();          // bus -> muted

    public void SaveBus(SessionClass cls, float volume, bool muted)
    {
        BusVolumes[cls.ToString()] = volume;
        BusMuted[cls.ToString()] = muted;
        Persist();
    }

    public float GetBusVolume(SessionClass cls) =>
        BusVolumes.TryGetValue(cls.ToString(), out var v) ? Math.Clamp(v, 0f, 1f) : 1f;

    public bool GetBusMuted(SessionClass cls) => BusMuted.GetValueOrDefault(cls.ToString());

    public PrefsStore() => Load();

    /// <summary>Record a manual device pick as the new top fallback priority.</summary>
    public void PromotePlayback(string deviceId)
    {
        PlaybackPriority.Remove(deviceId);
        PlaybackPriority.Insert(0, deviceId);
        Persist();
    }

    /// <summary>Recording twin of <see cref="PromotePlayback"/>: a hand-picked microphone becomes the
    /// new top fallback the mic drops to when the headset (which always outranks it while online)
    /// powers off.</summary>
    public void PromoteRecording(string deviceId)
    {
        RecordingPriority.Remove(deviceId);
        RecordingPriority.Insert(0, deviceId);
        Persist();
    }

    public SessionPrefs Resolve(string processName)
    {
        if (_sessions.TryGetValue(processName, out var prefs))
            return prefs;

        var bare = Path.GetFileNameWithoutExtension(processName);
        var defaultClass = DefaultChatProcesses.Contains(bare, StringComparer.OrdinalIgnoreCase)
            ? SessionClass.Chat
            : SessionClass.Media;

        return new SessionPrefs { Classification = defaultClass, Volume = 1f, Muted = false };
    }

    public void Save(string processName, SessionClass classification, float volume, bool muted)
    {
        _sessions[processName] = new SessionPrefs { Classification = classification, Volume = volume, Muted = muted };
        Persist();
    }

    public void SaveEq(SessionClass profile, double[] gains, bool enabled, string? presetName)
    {
        EqGains[profile.ToString()] = gains;
        EqEnabled = enabled;
        if (presetName is not null) EqPresets[profile.ToString()] = presetName;
        else EqPresets.Remove(profile.ToString());
        Persist();
    }

    public double[]? GetEqGains(SessionClass profile) =>
        EqGains.TryGetValue(profile.ToString(), out var g) && g.Length == 10 ? g : null;

    public string? GetEqPreset(SessionClass profile) =>
        EqPresets.GetValueOrDefault(profile.ToString());

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<StoredData>(File.ReadAllText(FilePath));
                if (data is not null)
                {
                    _sessions = new Dictionary<string, SessionPrefs>(data.Sessions, StringComparer.OrdinalIgnoreCase);
                    EqGains = data.EqGains;
                    EqPresets = data.EqPresets;
                    EqEnabled = data.EqEnabled;
                    PlaybackPriority = data.PlaybackPriority;
                    RecordingPriority = data.RecordingPriority;
                    BusVolumes = data.BusVolumes;
                    BusMuted = data.BusMuted;
                }
            }
        }
        catch
        {
            // Corrupt or missing config: start fresh rather than crash the app.
        }
    }

    // Every setting flows through Persist(): app + bus faders, mutes, EQ curves, device promotions.
    // The backing collections are mutated only on the UI thread, so the JSON snapshot is taken there
    // (race-free) the instant something changes; only the disk write is deferred and coalesced. A
    // slider drag calls this dozens of times a second, and writing the whole file synchronously on the
    // UI thread on every one of those ticks -- which is what this used to do -- was real per-frame disk
    // I/O. Now the write lands at most once per debounce window however fast the values change, and it
    // never runs on the UI thread. The EQ band-drag path had its own throttle bolted on for exactly
    // this reason; the volume/mute faders had none and hammered the disk every tick. Coalescing here,
    // at the one choke point they all share, fixes every caller at once instead of per-caller patches.
    private readonly object _snapshotGate = new();   // guards _pendingJson + the timer; UI thread holds it only momentarily
    private readonly object _fileGate = new();        // serializes the actual write; never taken on the UI Persist() path
    private System.Threading.Timer? _flushTimer;
    private string? _pendingJson;

    public void Persist()
    {
        string json;
        try { json = JsonSerializer.Serialize(Snapshot()); }
        catch { return; }   // a value that won't serialize must not take the app down

        lock (_snapshotGate)
        {
            _pendingJson = json;
            _flushTimer ??= new System.Threading.Timer(_ => FlushPending());
            _flushTimer.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Write any pending change to disk right now, synchronously. Called on shutdown so a tweak
    /// made inside the debounce window still survives the process exiting.</summary>
    public void Flush()
    {
        lock (_snapshotGate) _flushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        FlushPending();
    }

    private void FlushPending()
    {
        string? json;
        lock (_snapshotGate) { json = _pendingJson; _pendingJson = null; }
        if (json is null) return;   // nothing changed since the last write
        lock (_fileGate)
        {
            try { Directory.CreateDirectory(DirPath); File.WriteAllText(FilePath, json); }
            catch { /* best-effort; losing prefs across restarts is non-fatal */ }
        }
    }

    // Built and serialized on the UI thread so it reads the live collections while nothing else mutates
    // them; the deferred write only ever touches the resulting immutable string, never these fields.
    private StoredData Snapshot() => new()
    {
        Sessions = _sessions,
        EqGains = EqGains,
        EqPresets = EqPresets,
        EqEnabled = EqEnabled,
        PlaybackPriority = PlaybackPriority,
        RecordingPriority = RecordingPriority,
        BusVolumes = BusVolumes,
        BusMuted = BusMuted
    };

    private sealed class StoredData
    {
        public Dictionary<string, SessionPrefs> Sessions { get; set; } = new();
        public Dictionary<string, double[]> EqGains { get; set; } = new();
        public Dictionary<string, string> EqPresets { get; set; } = new();
        public bool EqEnabled { get; set; } = true;
        public List<string> PlaybackPriority { get; set; } = new();
        public List<string> RecordingPriority { get; set; } = new();
        public Dictionary<string, float> BusVolumes { get; set; } = new();
        public Dictionary<string, bool> BusMuted { get; set; } = new();
    }
}
