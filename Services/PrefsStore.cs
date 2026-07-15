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

    public bool StartWithWindows { get; set; }
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
                    StartWithWindows = data.StartWithWindows;
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

    public void Persist()
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            var data = new StoredData
            {
                Sessions = _sessions,
                StartWithWindows = StartWithWindows,
                EqGains = EqGains,
                EqPresets = EqPresets,
                EqEnabled = EqEnabled,
                PlaybackPriority = PlaybackPriority,
                RecordingPriority = RecordingPriority,
                BusVolumes = BusVolumes,
                BusMuted = BusMuted
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
        }
        catch
        {
            // Best-effort persistence; losing prefs across restarts is non-fatal.
        }
    }

    private sealed class StoredData
    {
        public Dictionary<string, SessionPrefs> Sessions { get; set; } = new();
        public bool StartWithWindows { get; set; }
        public Dictionary<string, double[]> EqGains { get; set; } = new();
        public Dictionary<string, string> EqPresets { get; set; } = new();
        public bool EqEnabled { get; set; } = true;
        public List<string> PlaybackPriority { get; set; } = new();
        public List<string> RecordingPriority { get; set; } = new();
        public Dictionary<string, float> BusVolumes { get; set; } = new();
        public Dictionary<string, bool> BusMuted { get; set; } = new();
    }
}
