using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SonarLite.Interop;
using SonarLite.Models;
using SonarLite.Services;
using DragDropEffects = System.Windows.DragDropEffects;
using ListBox = System.Windows.Controls.ListBox;
using Brush = System.Windows.Media.Brush;
using DataObject = System.Windows.DataObject;

namespace SonarLite;

public partial class MainWindow : Window
{
    private readonly PrefsStore _prefs = new();
    private readonly AppRoutingService _routing = new();
    private readonly AudioEngine _engine = new();
    private readonly AudioSessionService _audio;
    private readonly HidChatMixListener _hid = new();
    private readonly DeviceSwitcherService _devices = new();
    private readonly EqualizerApoService _apo = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _meterTimer;
    private readonly Dictionary<SessionClass, BusChannel> _buses = new();

    private static readonly (string Label, int Hz)[] BandDefs =
    [
        ("31", 31), ("62", 62), ("125", 125), ("250", 250), ("500", 500),
        ("1k", 1000), ("2k", 2000), ("4k", 4000), ("8k", 8000), ("16k", 16000)
    ];

    private readonly Dictionary<SessionClass, List<EqBand>> _eqBands = new();
    private readonly Dictionary<SessionClass, string?> _eqPreset = new();
    private SessionClass _editingProfile = SessionClass.Game;
    private bool _applyingPreset;
    private bool _suppressPresetEvent;
    private bool _suppressDeviceEvents;
    private int _lastChatPercent = 100;
    private int _lastGamePercent = 100;

    private TrayIcon? _trayIcon;
    private System.Windows.Controls.ContextMenu? _trayMenu;
    private bool _isExiting;
    private System.Windows.Point _dragStart;
    private AppSession? _dragSession;
    private Brush? _colDefaultBrush;

    public MainWindow()
    {
        InitializeComponent();

        _audio = new AudioSessionService(_prefs, _routing);
        GameList.ItemsSource = CreateFilteredView(SessionClass.Game);
        ChatList.ItemsSource = CreateFilteredView(SessionClass.Chat);
        MediaList.ItemsSource = CreateFilteredView(SessionClass.Media);
        _colDefaultBrush = GameCol.BorderBrush;

        InitBuses();

        InitEqBands();
        EqPresetCombo.ItemsSource = EqPreset.All;
        EqEnabledCheck.IsChecked = _prefs.EqEnabled;
        EqProfileGame.IsChecked = true; // triggers EqProfile_Checked → syncs preset combo + band list

        // A curve left in Equalizer APO by an older build would stack on top of our own DSP.
        _apo.NeutralizeIfOurs();

        _audio.Refresh();   // discover the cable + current sessions before the engine starts
        StartEngine();

        AutostartCheck.IsChecked = AutostartService.IsEnabled();

        // Session state changes push through immediately (see AudioSessionService.SessionsChanged),
        // and AudioSessionService.RouteFor triggers its own targeted rescan the instant we retarget
        // a pid's device -- the actual case OnSessionCreated misses (retargeting isn't the same
        // event WASAPI fires for a user-driven default-device change). Nothing routine depends on
        // this timer at all: it exists solely to self-heal a session silently dropped by a swallowed
        // COM exception mid-rescan (RescanDevice/ProcessSession's own catch blocks), which is rare by
        // construction. Every tick's AudioSessionManager.RefreshSessions() call leaks a native handle
        // that nothing can free (see [[naudio-refreshsessions-handle-leak]]) -- confirmed by direct
        // measurement, this was the single largest contributor to SonarLite's idle handle growth,
        // dwarfing everything else combined. A safety net for a rare case should fire rarely, not
        // every 20s; matching the interval to how often it's actually needed is the fix, not a
        // shorter and shorter patch of the same unavoidable per-call cost.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _refreshTimer.Tick += (_, _) =>
        {
            _audio.Refresh();
            SyncEngineTaps();
            UpdateEqStatus();
            if (!_engine.IsRunning) StartEngine();
        };
        _refreshTimer.Start();

        _audio.SessionsChanged += () =>
        {
            _audio.ReapplyVolumes();// an app that just migrated hands its dial gain to the engine
            SyncEngineTaps();
            UpdateEqStatus();
        };

        // Driven by DeviceSwitcherService.DevicesChanged (Windows' own endpoint-notification push)
        // instead of a poll -- see that event's doc comment for why polling this was actually
        // SonarLite's main source of unbounded memory growth.
        _devices.DevicesChanged += () => RefreshDeviceLists();
        RefreshDeviceLists();

        // Meters are pure eye-candy on a COM read per session. 20fps was assumed "not costly" but
        // was never measured -- under forced software rendering (see App.OnStartup) every tick's
        // bitmap re-render is real native-heap churn, not a GPU-offloaded no-op, and that churn is
        // the leading suspect for private memory that ratchets up after UI interaction and never
        // comes back down even once idle. 10fps is still smooth for a level meter.
        _meterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _meterTimer.Tick += (_, _) =>
        {
            if (WindowState == WindowState.Minimized || !IsVisible) return;
            _audio.UpdatePeaks();
            foreach (var (cls, bus) in _buses) bus.Peak = _audio.BusPeak(cls);
        };
        _meterTimer.Start();

        _hid.ChatMixChanged += OnChatMixChanged;
        _hid.StatusReport += OnHidStatus;
        _hid.Start();

        // Image.Source="SonarLite.ico" picks the smallest embedded frame (16x16) and upscales it
        // to fit, which is what made the header icon look blurry -- extracting the "large" system
        // icon (32x32) and downscaling it to the header's 24px display size stays crisp instead.
        HeaderIcon.Source = NativeIcon.ToFrozenImageSourceAndDestroy(NativeIcon.Extract(Environment.ProcessPath!, small: false));

        SetupTrayIcon();
        ApplyDarkTitleBar();

        // Apps silenced onto the cable would stay silent if we vanished without unrouting them.
        // Startup already self-heals, but catch the orderly exits (logoff, shutdown) too.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeRevertRoutings();
        System.Windows.Application.Current.SessionEnding += (_, _) => SafeRevertRoutings();
    }

    private void SafeRevertRoutings()
    {
        try
        {
            _audio.TappedBuses.Clear();
            _audio.RevertAllRoutings();
        }
        catch
        {
            // Shutting down; nothing left to recover with.
        }
    }

    // --- Bus channel strips ---

    private void InitBuses()
    {
        var accents = new Dictionary<SessionClass, string>
        {
            [SessionClass.Game] = "GameBrush",
            [SessionClass.Chat] = "ChatBrush",
            [SessionClass.Media] = "MediaBrush"
        };
        var headers = new Dictionary<SessionClass, ContentControl>
        {
            [SessionClass.Game] = GameHeader,
            [SessionClass.Chat] = ChatHeader,
            [SessionClass.Media] = MediaHeader
        };

        foreach (SessionClass cls in SessionClasses.All)
        {
            var bus = new BusChannel
            {
                Class = cls,
                Name = cls.ToString().ToUpperInvariant(),
                Accent = (Brush)FindResource(accents[cls]),
                Volume = _prefs.GetBusVolume(cls),
                IsMuted = _prefs.GetBusMuted(cls)
            };
            bus.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BusChannel.Peak)) return; // meter tick, not user state
                _prefs.SaveBus(cls, bus.Volume, bus.IsMuted);
                ApplyBusGain(cls);
            };
            _buses[cls] = bus;
            headers[cls].Content = bus;
        }
    }

    /// <summary>Fader × mute × the dial's share for this bus, applied wherever that bus's audio lives.</summary>
    private void ApplyBusGain(SessionClass cls)
    {
        float dial = (cls == SessionClass.Chat ? _lastChatPercent : _lastGamePercent) / 100f;
        float gain = _buses[cls].EffectiveGain * dial;
        _engine.SetGain(cls, gain);       // for apps we're tapping
        _audio.SetBusFactor(cls, gain);   // for apps still playing direct
    }

    private void ApplyAllBusGains()
    {
        foreach (SessionClass cls in SessionClasses.All) ApplyBusGain(cls);
    }

    private ListCollectionView CreateFilteredView(SessionClass cls)
    {
        var view = new ListCollectionView(_audio.Sessions) { Filter = o => ((AppSession)o).Classification == cls };
        view.LiveFilteringProperties.Add(nameof(AppSession.Classification));
        view.IsLiveFiltering = true;
        return view;
    }

    // --- Audio engine ---

    /// <summary>
    /// Only silence apps onto the cable once the engine is actually mixing them back; otherwise a
    /// failed start would leave every app muted on a sink nobody is listening to.
    /// </summary>
    private void StartEngine()
    {
        if (!_audio.RoutingAvailable || !_engine.Start(_audio.ResolveRealDefaultDevice()))
        {
            _audio.TappedBuses.Clear();
            _audio.RevertAllRoutings();
            _audio.ReapplyVolumes();
            UpdateEqStatus();
            SyncPlaybackSelection();
            return;
        }

        ApplyEqTargets();
        ApplyAllBusGains();
        RefreshTappedBuses();
        UpdateEqStatus();
        // The engine picks its own render device; the dropdown has to follow it, not predict it.
        SyncPlaybackSelection();
        SyncDevicePresets();
    }

    /// <summary>
    /// Move any bus on a device-voiced preset onto the one matching the device we're actually
    /// rendering to. Runs from StartEngine, which is the single funnel for both launch and every
    /// device change (RestartEngine), so this is the one place that knows the output really moved.
    ///
    /// Only presets that declare a DeviceKind track the device -- a bus on Immersive, Flat or a
    /// hand-edited curve is left exactly as the user set it. Choosing Nova Pro or Pebble V2 is what
    /// opts a bus in, and from then on the pair follows the headset going on and off.
    /// </summary>
    private void SyncDevicePresets()
    {
        var name = _engine.RenderDeviceName;
        if (name is null) return;

        // Same classifier the routing layer already ranks devices with, so the preset can never
        // disagree with which device the engine actually chose.
        var kind = AudioSessionService.IsHeadset(name) ? DeviceKind.Headset : DeviceKind.Speakers;

        foreach (SessionClass cls in SessionClasses.All)
        {
            var target = EqPreset.ForDevice(_eqPreset.GetValueOrDefault(cls), kind);
            if (target is not null) ApplyPreset(cls, target);
        }
    }

    /// <summary>Load a preset's curve into a bus's bands and push it to the engine.</summary>
    private void ApplyPreset(SessionClass profile, EqPreset preset)
    {
        _applyingPreset = true;   // these are our writes, not the user hand-editing a band
        var bands = _eqBands[profile];
        for (int i = 0; i < bands.Count; i++)
            bands[i].GainDb = preset.Gains[i];
        _applyingPreset = false;

        _eqPreset[profile] = preset.Name;
        PersistAndApply(profile);

        if (profile == _editingProfile)
        {
            _suppressPresetEvent = true;
            EqPresetCombo.SelectedItem = preset;
            _suppressPresetEvent = false;
        }
    }

    /// <summary>A bus is only worth tapping when it actually shapes the sound; a flat curve would
    /// cost capture + mix latency for a no-op, so those apps stay direct on the headset.</summary>
    private bool BusNeedsTap(SessionClass cls) =>
        _engine.IsRunning
        && EqEnabledCheck.IsChecked == true
        && _eqBands[cls].Any(b => Math.Abs(b.GainDb) >= 0.01);

    private void RefreshTappedBuses()
    {
        var wanted = SessionClasses.All.Where(BusNeedsTap).ToHashSet();
        if (!wanted.SetEquals(_audio.TappedBuses))
        {
            _audio.TappedBuses.Clear();
            foreach (var cls in wanted) _audio.TappedBuses.Add(cls);
            _audio.RouteAll(); // request the move; the tap follows once the app actually migrates
        }
        SyncEngineTaps();
    }

    /// <summary>
    /// Tap exactly the apps that are actually sitting on the silent cable, whatever the bus's
    /// current curve. Routing is a request; a running stream only honours it when the app next
    /// reopens, so following the cable rather than the request is what keeps audio neither doubled
    /// (tapped while still audible) nor silent (untapped while still on the cable).
    /// </summary>
    private void SyncEngineTaps()
    {
        if (!_engine.IsRunning) return;
        var wantedByClass = SessionClasses.All.ToDictionary(
            cls => cls,
            cls => (IReadOnlyCollection<int>)_audio.PidsFor(cls).Where(_audio.PidsOnNullSink.Contains).ToHashSet());
        _engine.SyncAll(wantedByClass);
    }

    private void RestartEngine()
    {
        _engine.Stop();
        _audio.TappedBuses.Clear();
        StartEngine();
    }

    // --- EQ ---

    private void InitEqBands()
    {
        foreach (SessionClass profile in SessionClasses.All)
        {
            var saved = _prefs.GetEqGains(profile);
            var bands = new List<EqBand>();
            for (int i = 0; i < BandDefs.Length; i++)
            {
                var band = new EqBand(BandDefs[i].Label, BandDefs[i].Hz) { GainDb = saved?[i] ?? 0 };
                var captured = profile;
                band.PropertyChanged += (_, _) => OnEqBandChanged(captured);
                bands.Add(band);
            }
            _eqBands[profile] = bands;
            _eqPreset[profile] = _prefs.GetEqPreset(profile);
        }
        EqBandList.ItemsSource = _eqBands[_editingProfile];
    }

    private readonly Dictionary<SessionClass, DispatcherTimer> _eqWriteThrottle = new();
    private readonly HashSet<SessionClass> _eqWritePending = new();

    private void OnEqBandChanged(SessionClass profile)
    {
        if (_applyingPreset) return;
        _eqPreset[profile] = null; // manual tweak: no longer a named preset
        ThrottledPersistAndApply(profile);
    }

    /// <summary>
    /// Dragging a band slider fires a change on every tick; PersistAndApply does synchronous disk
    /// I/O (Equalizer APO's config file, prefs.json), so calling it unthrottled would block the UI
    /// thread dozens of times per drag gesture. Apply immediately, then cap follow-up writes to
    /// once per ~80ms with a trailing call so the final dragged value is never dropped.
    /// </summary>
    private void ThrottledPersistAndApply(SessionClass profile)
    {
        if (_eqWriteThrottle.TryGetValue(profile, out var timer) && timer.IsEnabled)
        {
            _eqWritePending.Add(profile);
            return;
        }

        PersistAndApply(profile);

        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_eqWritePending.Remove(profile)) PersistAndApply(profile);
            };
            _eqWriteThrottle[profile] = timer;
        }
        timer.Start();
    }

    private void PersistAndApply(SessionClass profile)
    {
        var enabled = EqEnabledCheck.IsChecked == true;
        var gains = _eqBands[profile].Select(b => b.GainDb).ToArray();
        var preset = _eqPreset.GetValueOrDefault(profile);
        _prefs.SaveEq(profile, gains, enabled, preset);
        _engine.SetEq(profile, gains, enabled, EqPreset.PreampFor(preset));
        RefreshTappedBuses(); // a curve going flat/non-flat moves the bus off/onto the tap path
        UpdateEqStatus();
    }

    private void ApplyEqTargets()
    {
        var enabled = EqEnabledCheck.IsChecked == true;
        foreach (SessionClass cls in SessionClasses.All)
            _engine.SetEq(cls, [.. _eqBands[cls].Select(b => b.GainDb)], enabled,
                EqPreset.PreampFor(_eqPreset.GetValueOrDefault(cls)));
    }

    private void UpdateEqStatus()
    {
        string text;
        if (!_routing.IsAvailable)
            text = "Per-app routing unavailable on this system — EQ inactive.";
        else if (_audio.NullSinkDeviceId is null)
            text = "VB-Cable not found. Install VB-Audio Virtual Cable to enable per-app EQ.";
        else if (!_engine.IsRunning)
            text = $"Audio engine inactive — {_engine.Status}";
        else if (_audio.TappedBuses.Count == 0)
            text = "Ready. Curves are flat, so audio plays direct with no added latency.";
        else
            text = $"EQ live on {string.Join(", ", _audio.TappedBuses.OrderBy(b => b))} — {_engine.TapCount} app(s) tapped.";

        if (EqStatusLabel.Text != text) EqStatusLabel.Text = text;
    }

    private void EqProfile_Checked(object sender, RoutedEventArgs e)
    {
        var profile = ReferenceEquals(sender, EqProfileChat) ? SessionClass.Chat
            : ReferenceEquals(sender, EqProfileMedia) ? SessionClass.Media
            : SessionClass.Game;

        foreach (var tb in new[] { EqProfileGame, EqProfileChat, EqProfileMedia })
            if (!ReferenceEquals(tb, sender)) tb.IsChecked = false;

        _editingProfile = profile;
        EqBandList.ItemsSource = _eqBands[profile];
        // Foreground is inherited, so this paints every band slider's fill in the channel's colour.
        EqBandList.Foreground = _buses[profile].Accent;

        _suppressPresetEvent = true;
        EqPresetCombo.SelectedItem = EqPreset.All.FirstOrDefault(p => p.Name == _eqPreset.GetValueOrDefault(profile));
        _suppressPresetEvent = false;
    }

    private void EqProfile_Click(object sender, RoutedEventArgs e)
    {
        // Behave like radio buttons: clicking the checked one shouldn't uncheck it.
        if (sender is System.Windows.Controls.Primitives.ToggleButton { IsChecked: false } tb)
            tb.IsChecked = true;
    }

    private void EqPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetEvent || EqPresetCombo.SelectedItem is not EqPreset preset) return;
        ApplyPreset(_editingProfile, preset);
    }

    private void EqEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        _prefs.EqEnabled = EqEnabledCheck.IsChecked == true;
        _prefs.Persist();
        ApplyEqTargets();
        RefreshTappedBuses();
        UpdateEqStatus();
    }

    // --- Devices ---

    private List<DeviceOption> _boundPlayback = new();
    private List<DeviceOption> _boundRecording = new();

    /// <summary>
    /// What the mix is actually coming out of right now. The engine renders to the device it
    /// resolved at Start() and never follows Windows' default afterward (see
    /// AudioEngine.RenderDeviceId), so Windows' default is only the answer while the engine is
    /// stopped -- reading it otherwise is what made the dropdown claim the headset while the
    /// speakers were the ones playing.
    /// </summary>
    private string? CurrentOutputId => _engine.RenderDeviceId ?? _devices.GetDefaultPlaybackId();

    /// <summary>
    /// Point the dropdown at whatever is actually playing, without re-enumerating endpoints -- that
    /// enumeration leaks a native handle per call (see [[naudio-mmdevicecollection-handle-leak]]),
    /// and the engine restarting doesn't change which devices exist, only which one we render to.
    /// </summary>
    private void SyncPlaybackSelection()
    {
        _suppressDeviceEvents = true;
        try { PlaybackCombo.SelectedItem = _boundPlayback.FirstOrDefault(d => d.Id == CurrentOutputId); }
        finally { _suppressDeviceEvents = false; }
    }

    private void RefreshDeviceLists()
    {
        // The cables are internal plumbing, not user-facing outputs.
        var rawPlayback = _devices.GetPlaybackDevices();

        _suppressDeviceEvents = true;
        try
        {
            var playback = rawPlayback
                .Where(d => !d.Name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => _audio.PreferenceRank(d.Id, d.Name))
                .ThenBy(d => d.Name)
                .ToList();

            // Rebinding ItemsSource tears down and rebuilds the dropdown's item containers; skip
            // it when the device set hasn't actually changed since last tick (the common case).
            if (!SameDevices(playback, _boundPlayback))
            {
                PlaybackCombo.ItemsSource = playback;
                _boundPlayback = playback;
            }
            PlaybackCombo.SelectedItem = _boundPlayback.FirstOrDefault(d => d.Id == CurrentOutputId);

            var recording = _devices.GetRecordingDevices()
                .Where(d => !d.Name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var defaultRecording = _devices.GetDefaultRecordingId();
            if (!SameDevices(recording, _boundRecording))
            {
                RecordingCombo.ItemsSource = recording;
                _boundRecording = recording;
            }
            RecordingCombo.SelectedItem = _boundRecording.FirstOrDefault(d => d.Id == defaultRecording);
        }
        finally
        {
            _suppressDeviceEvents = false;
        }

        // A device the user picked by hand is only honoured while it actually exists; once it's gone
        // (speakers on a dock that got unplugged) the preference ranking takes over again.
        if (_userPick is not null && _boundPlayback.All(d => d.Id != _userPick))
            _userPick = null;

        // Re-evaluated on every refresh, not just when the current device vanishes. The headset
        // powering on can't be handled from the HID report alone: Windows takes a moment to bring the
        // endpoint back, so at the instant the report arrives the Arctis often isn't in the device
        // list yet and there's nothing to switch to. The endpoint reappearing is itself a device
        // notification, so re-deciding here is what actually catches it. What keeps this from
        // stampeding over a manual pick (the original bug) is _userPick, not the absence of a check.
        SwitchToPreferredOutput();
    }

    private static bool SameDevices(List<DeviceOption> a, List<DeviceOption> b) =>
        a.Count == b.Count && a.Select(d => d.Id).SequenceEqual(b.Select(d => d.Id));

    /// <summary>
    /// The device the user picked by hand in the dropdown, and which therefore outranks the headset
    /// until they physically say otherwise. Cleared on a headset power transition (reaching for the
    /// power button is a newer, more explicit statement of intent than an earlier click) and when
    /// the device stops existing. Without this, the refresh raised by the pick itself immediately
    /// re-asserted the headset and cut the speakers dead -- the bug this whole change started from.
    /// </summary>
    private string? _userPick;

    /// <summary>
    /// The device the user most wants their audio to come out of, among the ones actually available:
    /// their explicit pick if they made one, otherwise AudioSessionService.PreferenceRank -- the same
    /// rule the engine's own render-target resolution uses, so "what the dropdown shows" and "where
    /// the mix goes" can't drift apart. Devices the user never picked in our UI are left alone.
    /// </summary>
    private DeviceOption? PreferredOutput()
    {
        var pick = _boundPlayback.FirstOrDefault(d => d.Id == _userPick && _audio.IsUsableOutput(d.Name));
        return pick ?? _boundPlayback
            .Where(d => _audio.IsUsableOutput(d.Name))
            .Select(d => (Device: d, Rank: _audio.PreferenceRank(d.Id, d.Name)))
            .Where(x => x.Rank != int.MaxValue)
            .OrderBy(x => x.Rank)
            .Select(x => x.Device)
            .FirstOrDefault();
    }

    private bool _switchPending;

    /// <summary>
    /// GG-style fallback: move output to the best available device and rebuild the engine on it.
    /// No-ops when the audio is already coming out of that device, so it's safe to call from any
    /// refresh -- and it has to be, because the events that should move the output (the headset's
    /// endpoint reappearing a second after it powers on) arrive as ordinary device notifications.
    /// </summary>
    private void SwitchToPreferredOutput()
    {
        // SetDefaultPlayback below raises OnDefaultDeviceChanged, which lands back here via
        // RefreshDeviceLists before the queued restart has moved the engine -- without this the
        // "still not where I want it" test would stay true and re-assert the default in a loop.
        if (_switchPending) return;

        // Compared against what's actually playing, not against _engine.RenderDeviceId directly: with
        // the engine stopped that's null forever, so re-asserting the default on every refresh would
        // never look satisfied and would loop. CurrentOutputId falls back to Windows' default, which
        // the SetDefaultPlayback below does move.
        var best = PreferredOutput();
        if (best is null || CurrentOutputId == best.Id) return;

        _switchPending = true;
        _devices.SetDefaultPlayback(best.Id);
        StatusLabel.Text = $"Switched output to {best.Name}.";
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _switchPending = false;
            RestartEngine();
        });
    }

    private void PlaybackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceEvents) return;
        if (PlaybackCombo.SelectedItem is not DeviceOption device) return;

        // WPF can deliver this event on a dispatcher-deferred pass after ItemsSource/SelectedItem
        // are reassigned -- by then RefreshDeviceLists' finally block has already reset
        // _suppressDeviceEvents to false, so the flag alone doesn't catch a re-sync that re-selects
        // the device already in use. Acting on it anyway would re-fire OnDefaultDeviceChanged, which
        // re-triggers RefreshDeviceLists via DevicesChanged -- a feedback loop that was measured
        // driving native handle growth at ~250/sec (see [[naudio-mmdevicecollection-handle-leak]]).
        // The test is against what's actually playing, not Windows' default: while the engine runs,
        // Windows' default is routinely our own cable, and comparing against that would make every
        // re-sync look like a real user pick.
        if (device.Id == CurrentOutputId) return;

        _userPick = device.Id;
        _prefs.PromotePlayback(device.Id);
        _devices.SetDefaultPlayback(device.Id);
        // The engine renders into the default device; rebuild it on the new one.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RestartEngine);
    }

    private void RecordingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceEvents) return;
        if (RecordingCombo.SelectedItem is DeviceOption device && device.Id != _devices.GetDefaultRecordingId())
            _devices.SetDefaultRecording(device.Id);
    }

    private void AutostartCheck_Changed(object sender, RoutedEventArgs e) =>
        AutostartService.SetEnabled(AutostartCheck.IsChecked == true);

    // --- ChatMix dial ---

    private void OnChatMixChanged(object? sender, ChatMixEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _lastChatPercent = e.ChatPercent;
            _lastGamePercent = e.GamePercent;
            // Both sides sit at 100 when the dial is untouched -- turning it only pulls one side
            // down rather than crossfading between them -- so centered means equal, and the bar
            // leans toward whichever side is still louder as the dial comes off center.
            ChatMixSlider.Value = 50 + (e.GamePercent - e.ChatPercent) / 2.0;
            StatusLabel.Text = "ChatMix dial connected — reading live.";
            // The dial balances Chat against everything on the game side of the mix. Tapped buses
            // take it at the engine's gain stage; untapped ones take it via session volume.
            ApplyAllBusGains();
        });
    }

    /// <summary>
    /// Base station pushes a power-change report on headset power on/off. Confirmed layout via
    /// live capture (report ID 0x07): [07][B5][..][..][flag], flag 0x08 = on, 0x04 = off. report[1]
    /// is the type byte, same framing the ChatMix parse uses. Detection was never the bug -- the
    /// auto-switch was (it didn't prefer the headset when it came back).
    ///
    /// This transition is the *only* thing that pulls audio back to the headset. Re-deciding that on
    /// every device refresh instead is what made a manual pick of the speakers impossible to keep.
    /// </summary>
    private void OnHidStatus(object? sender, byte[] report)
    {
        if (report.Length < 5 || report[1] != 0xB5) return;
        bool? online = report[4] switch { 0x08 => true, 0x04 => false, _ => null };
        if (online is null || online == _audio.HeadsetOnline) return;

        Dispatcher.Invoke(() =>
        {
            _audio.HeadsetOnline = online.Value;
            // Reaching for the power button overrules whatever they clicked earlier.
            _userPick = null;
            StatusLabel.Text = _audio.HeadsetOnline ? "Headset powered on." : "Headset powered off — falling back.";
            // Re-rank under the new power state and move the audio to match. When the headset is
            // coming back its endpoint may not have reappeared yet, in which case this does nothing
            // and the refresh triggered by the endpoint showing up is what performs the switch.
            RefreshDeviceLists();
        });
    }

    // --- Tile drag & drop ---

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragSession = null;
        // Don't start a drag from interactive elements inside the tile.
        var el = e.OriginalSource as DependencyObject;
        while (el is not null and not ListBoxItem)
        {
            if (el is Slider or System.Windows.Controls.Primitives.ButtonBase) return;
            el = VisualTreeHelper.GetParent(el);
        }
        _dragStart = e.GetPosition(null);
        _dragSession = FindAppSession(e.OriginalSource as DependencyObject);
    }

    private void Tile_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSession is null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var session = _dragSession;
        _dragSession = null;

        var container = FindContainer((ListBox)sender, session);
        if (container is not null) container.Opacity = 0.45;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(AppSession), session), DragDropEffects.Move);
        }
        finally
        {
            if (container is not null) container.Opacity = 1.0;
            ResetColumnHighlights();
        }
    }

    private void Column_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(AppSession)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Column_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(AppSession))) return;
        if (ColumnBorderFor(sender) is { } border)
        {
            border.BorderBrush = (Brush)FindResource("AccentBrush");
            border.BorderThickness = new Thickness(2);
        }
    }

    private void Column_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (ColumnBorderFor(sender) is { } border)
        {
            border.BorderBrush = _colDefaultBrush;
            border.BorderThickness = new Thickness(1);
        }
    }

    private void Column_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ResetColumnHighlights();
        if (e.Data.GetData(typeof(AppSession)) is not AppSession session) return;
        if (sender is not ListBox target) return;

        // Setting Classification alone is enough: the columns are live-filtered views keyed on it
        // (see CreateFilteredView), so the tile repositions itself, and AudioSessionService signals
        // SessionsChanged from its own PropertyChanged handler to move the engine tap. A Refresh()
        // call here used to be needed to force the tile to be rediscovered after being torn down
        // and rebuilt on every move -- that's gone now, and Refresh() costs two RefreshSessions()
        // calls (see [[naudio-refreshsessions-handle-leak]]) that this interaction no longer needs.
        session.Classification = target.Name switch
        {
            nameof(GameList) => SessionClass.Game,
            nameof(ChatList) => SessionClass.Chat,
            nameof(MediaList) => SessionClass.Media,
            _ => session.Classification
        };
    }

    private Border? ColumnBorderFor(object listBox) =>
        ReferenceEquals(listBox, GameList) ? GameCol
        : ReferenceEquals(listBox, ChatList) ? ChatCol
        : ReferenceEquals(listBox, MediaList) ? MediaCol
        : null;

    private void ResetColumnHighlights()
    {
        foreach (var col in new[] { GameCol, ChatCol, MediaCol })
        {
            col.BorderBrush = _colDefaultBrush;
            col.BorderThickness = new Thickness(1);
        }
    }

    private static ListBoxItem? FindContainer(ListBox list, AppSession session) =>
        list.ItemContainerGenerator.ContainerFromItem(session) as ListBoxItem;

    private static AppSession? FindAppSession(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: AppSession session })
                return session;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    // --- Tray / lifecycle ---

    private void SetupTrayIcon()
    {
        _trayMenu = new System.Windows.Controls.ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };
        var show = new System.Windows.Controls.MenuItem { Header = "Show" };
        show.Click += (_, _) => ShowFromTray();
        var exit = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exit.Click += (_, _) =>
        {
            _isExiting = true;
            Close();
        };
        _trayMenu.Items.Add(show);
        _trayMenu.Items.Add(exit);

        _trayIcon = new TrayIcon(this, NativeIcon.Extract(Environment.ProcessPath!), "SonarLite");
        _trayIcon.DoubleClicked += ShowFromTray;
        _trayIcon.RightClicked += () =>
        {
            _trayMenu.IsOpen = true;
        };
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RefreshDeviceLists(); // one cheap catch-up in case anything raced with the window reopening
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        Hide();
        ReclaimIdleMemory();
    }

    /// <summary>
    /// Parking in the tray is this app's real idle state -- the window is gone, the meter timer
    /// early-returns, and allocation drops to almost nothing, so the runtime would otherwise not run
    /// another GC for many minutes. That's precisely why memory "grows during use then stays flat":
    /// System.GC.ConserveMemory only decommits freed segments back to the OS *when a collection
    /// runs*, and at idle none do. Compacting once at the moment we go idle is what turns that flat
    /// plateau into an actual drop -- WPF's rendering/binding/drag scratch from the session just
    /// ended is genuinely dead, and the LOH compaction hands the largest buffers' pages straight
    /// back. This is a lifecycle reclamation point, not a GC.Collect() sprinkled to paper over a
    /// leak: it fires only on the tray transition, never on a timer or per-frame.
    /// </summary>
    private static void ReclaimIdleMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            _hid.Dispose();
            _refreshTimer.Stop();
            _meterTimer.Stop();
            // Never leave apps stranded on the silent cable with no engine mixing them back.
            SafeRevertRoutings();
            _engine.Dispose();
            _audio.Dispose();
            _devices.Dispose();
            _trayIcon?.Dispose();
            return;
        }

        // Closing the window minimizes to tray instead of exiting.
        e.Cancel = true;
        WindowState = WindowState.Minimized;
    }

    // --- Dark title bar (Win11 draws an inactive-window title bar white otherwise) ---

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            int useDark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
        }
        catch
        {
            // Older Windows without the attribute; title bar stays the OS default.
        }
    }
}
