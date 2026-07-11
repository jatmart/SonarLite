using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SonarLite.Services;

public sealed record DeviceOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class DeviceSwitcherService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly NotificationRelay _relay;

    /// <summary>
    /// Fires (already marshaled to the UI thread) when a device is added/removed, its state
    /// changes, or the system default changes. Windows' own endpoint-notification API is a
    /// reliable push for these -- unlike the per-app routing retarget quirk that made polling
    /// necessary on the session side, there's no known gap here, so this replaces what used to be
    /// a 2s poll. That poll's MMDeviceEnumerator.EnumerateAudioEndPoints() call was the actual
    /// driver of SonarLite's working set climbing continuously: NAudio's MMDeviceCollection holds
    /// a native COM/handle resource with no Dispose to release it promptly, so a perpetual 0.5Hz
    /// poll leaked steadily forever. See [[naudio-mmdevicecollection-handle-leak]].
    ///
    /// Coalesced, not raw: a subscriber that reacts to every native notification re-enumerates
    /// devices (that same per-call handle leak) at whatever rate the notifications arrive --
    /// including a burst, or a sustained fight with another app over the default device (see
    /// EnforcePlaybackPriority's own backoff for that case). A single pending flag collapses an
    /// entire burst into one trailing refresh instead of one refresh per raw notification.
    /// </summary>
    public event Action? DevicesChanged;

    public DeviceSwitcherService()
    {
        _relay = new NotificationRelay(this);
        try { _enumerator.RegisterEndpointNotificationCallback(_relay); } catch { }
    }

    private bool _notifyPending;

    private void RaiseDevicesChanged()
    {
        if (_notifyPending) return;
        _notifyPending = true;
        _ = Task.Delay(300).ContinueWith(_ => _dispatcher.BeginInvoke(() =>
        {
            _notifyPending = false;
            DevicesChanged?.Invoke();
        }));
    }

    private sealed class NotificationRelay(DeviceSwitcherService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.RaiseDevicesChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => owner.RaiseDevicesChanged();
        public void OnDeviceRemoved(string deviceId) => owner.RaiseDevicesChanged();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => owner.RaiseDevicesChanged();
        // Fires for every minor property tweak (volume, format...); reacting to it would just
        // reintroduce a tight poll-like refresh rate under the covers.
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    public List<DeviceOption> GetPlaybackDevices() => EnumerateDevices(DataFlow.Render);

    public List<DeviceOption> GetRecordingDevices() => EnumerateDevices(DataFlow.Capture);

    private List<DeviceOption> EnumerateDevices(DataFlow flow)
    {
        var result = new List<DeviceOption>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (d) { result.Add(new DeviceOption(d.ID, SafeName(d))); }
        }
        return result;
    }

    public string? GetDefaultPlaybackId() => GetDefaultId(DataFlow.Render);

    public string? GetDefaultRecordingId() => GetDefaultId(DataFlow.Capture);

    private string? GetDefaultId(DataFlow flow)
    {
        try
        {
            using var d = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return d.ID;
        }
        catch { return null; }
    }

    public void SetDefaultPlayback(string deviceId) => PolicyConfig.SetDefaultDevice(deviceId);

    public void SetDefaultRecording(string deviceId) => PolicyConfig.SetDefaultDevice(deviceId);

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; }
        catch { return d.ID; }
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_relay); } catch { }
        _enumerator.Dispose();
    }
}
