using NAudio.CoreAudioApi;

namespace SonarLite.Services;

/// <summary>
/// Bridges the volume of the endpoint Windows *shows* onto the mix that is actually audible.
///
/// With the routing cable held as Windows' default (see MainWindow.EnforceDefaultPlayback), the
/// volume keys, the tray slider and every app's volume UI all act on CABLE Input -- and none of it
/// was audible. The mix leaves through a WasapiOut on the headset (AudioEngine.Start), while the
/// cable's endpoint volume is applied at the cable's own endpoint mix, which nothing ever plays
/// back. The taps are process loopbacks taken from each app's render stream, upstream of that mix,
/// so they never see it either. Turning the cable down changed precisely nothing.
///
/// That is the missing half of "the cable is always the default". Windows will not bridge these two
/// endpoints for us -- there is no such thing as a default-device volume that follows a stream to
/// wherever it is really rendered -- so this class is the bridge.
///
/// It reports the level to a callback instead of writing the render endpoint's volume, and that is
/// not indirection for its own sake: the Arctis Nova Pro reports HardwareSupport = Volume, Mute and
/// keeps its level on the device, so it silently ignores MasterVolumeLevelScalar writes (verified:
/// writing 0.42 read back 1.000, with no exception to catch). Mirroring endpoint-to-endpoint works
/// on a Realtek and does nothing whatsoever on the headset -- so the level goes to
/// AudioEngine.SetMasterVolume, which attenuates samples we already own and cannot be refused.
/// </summary>
public sealed class EndpointVolumeMirror : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    /// <summary>Receives (linear amplitude, mute). Fixed for the mirror's lifetime so that
    /// <see cref="Retarget"/> can cheaply no-op on an unchanged source -- taking the callback per
    /// call would compare a fresh lambda every time and re-register the COM notification on every
    /// device refresh.</summary>
    private readonly Action<float, bool> _apply;

    private MMDevice? _source;
    private AudioEndpointVolumeNotificationDelegate? _handler;

    public EndpointVolumeMirror(Action<float, bool> apply) => _apply = apply;

    /// <summary>
    /// Point the mirror at the endpoint whose volume the user is really adjusting. Safe to call on
    /// every engine start and every device refresh: it no-ops when the source is unchanged.
    /// </summary>
    public void Retarget(string? sourceId)
    {
        if (sourceId is null) { Detach(); return; }
        if (_source?.ID == sourceId) return;

        Detach();
        try { _source = _enumerator.GetDevice(sourceId); }
        catch { Detach(); return; }

        _handler = data => Push(data.Muted);
        try { _source.AudioEndpointVolume.OnVolumeNotification += _handler; }
        catch { Detach(); return; }

        // Adopt the current level immediately. The callback only fires on *changes*, so without this
        // the mix keeps whatever gain it had until the user next touches the volume -- at launch that
        // is exactly "I turned it down, restarted, and it came back loud".
        Push(null);
    }

    /// <summary>
    /// Reads the level as dB and converts to linear amplitude rather than using the notification's
    /// 0..1 scalar directly. The scalar is a position on Windows' volume taper, not a gain: on this
    /// cable 0.75 is -4.4dB (amp 0.61) and 0.25 is -21dB (amp 0.089). Feeding the scalar in as gain
    /// would make the slider feel dead across its top half and collapse at the bottom.
    /// </summary>
    private void Push(bool? muted)
    {
        var device = _source;
        if (device is null) return;
        try
        {
            var vol = device.AudioEndpointVolume;
            float db = vol.MasterVolumeLevel;
            // -96dB is this endpoint's floor and means silence, not a very small number.
            float amp = db <= vol.VolumeRange.MinDecibels ? 0f : (float)Math.Pow(10, db / 20.0);
            _apply(Math.Clamp(amp, 0f, 1f), muted ?? vol.Mute);
        }
        catch { /* endpoint went away; the next Retarget re-establishes it */ }
    }

    private void Detach()
    {
        if (_source is not null && _handler is not null)
            try { _source.AudioEndpointVolume.OnVolumeNotification -= _handler; } catch { }
        _handler = null;
        try { _source?.Dispose(); } catch { }
        _source = null;
    }

    public void Dispose()
    {
        Detach();
        _enumerator.Dispose();
    }
}
