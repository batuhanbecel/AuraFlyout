using System.Windows;
using AuraFlyout.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AuraFlyout.Audio;

/// <summary>
/// Mute state of the default communications microphone (the one calls and voice chat use), switched
/// like Windows' own microphone mute: on the device itself, so every app hears silence.
/// </summary>
public sealed partial class MicrophoneService : ObservableObject, IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;

    [ObservableProperty] public partial bool IsMuted { get; private set; }
    [ObservableProperty] public partial bool HasMicrophone { get; private set; }
    [ObservableProperty] public partial string DeviceName { get; private set; } = "";

    /// <summary>Raised when the mute state changed, from AuraFlyout or any other app (UI thread).</summary>
    public event Action? Changed;

    public MicrophoneService()
    {
        try { _enumerator.RegisterEndpointNotificationCallback(this); }
        catch (Exception ex) { Log.Warn("Microphone notifications unavailable", ex); }
        Bind();
    }

    private void Bind()
    {
        try
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnNotification;
                _device.Dispose();
            }
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _device.AudioEndpointVolume.OnVolumeNotification += OnNotification;
            DeviceName = _device.FriendlyName;
            HasMicrophone = true;
            IsMuted = _device.AudioEndpointVolume.Mute;
        }
        catch
        {
            _device = null;
            HasMicrophone = false;
            DeviceName = "";
            IsMuted = false;
        }
        Changed?.Invoke();
    }

    private void OnNotification(AudioVolumeNotificationData data) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (IsMuted == data.Muted) return;
            IsMuted = data.Muted;
            Changed?.Invoke();
        });

    /// <summary>Mutes / unmutes; returns the new state (true = muted).</summary>
    public bool Toggle()
    {
        if (_device == null) Bind();
        if (_device == null) return false;
        try
        {
            bool muted = !_device.AudioEndpointVolume.Mute;
            _device.AudioEndpointVolume.Mute = muted;
            IsMuted = muted;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Microphone mute failed", ex);
        }
        return IsMuted;
    }

    // ---- IMMNotificationClient: a new default microphone ----

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture && role == Role.Communications) Application.Current?.Dispatcher.BeginInvoke(Bind);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Application.Current?.Dispatcher.BeginInvoke(Bind);
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { /* ignore */ }
        _device?.Dispose();
    }
}
