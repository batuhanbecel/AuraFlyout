using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AuraFlyout.Core;
using AuraFlyout.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AuraFlyout.Audio;

/// <summary>A playback device for the output picker.</summary>
public sealed record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>
/// One application in the volume mixer. An application can own several audio sessions (browsers
/// open one per tab / process): the row drives all of them together, like Windows' own mixer.
/// </summary>
public sealed partial class AudioSessionItem : ObservableObject
{
    private readonly List<AudioSessionControl> _controls;
    private bool _syncing;

    public AudioSessionItem(AudioSessionControl control, string name, ImageSource? icon, uint pid)
    {
        _controls = [control];
        Name = name;
        Icon = icon;
        ProcessId = pid;
        Sync();
    }

    /// <summary>Adds another session of the same application (it takes the row's current level).</summary>
    internal void Add(AudioSessionControl control) => _controls.Add(control);

    private AudioSessionControl Main => _controls[0];

    public uint ProcessId { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon != null;

    [ObservableProperty] public partial double Volume { get; set; }
    [ObservableProperty] public partial bool IsMuted { get; set; }
    [ObservableProperty] public partial bool IsActive { get; private set; }
    [ObservableProperty] public partial double Peak { get; private set; }

    partial void OnVolumeChanged(double value)
    {
        if (_syncing) return;
        float level = (float)Math.Clamp(value / 100.0, 0, 1);
        foreach (var c in _controls)
        {
            try { c.SimpleAudioVolume.Volume = level; }
            catch { /* session went away */ }
        }
        if (IsMuted && value > 0) IsMuted = false;
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_syncing) return;
        foreach (var c in _controls)
        {
            try { c.SimpleAudioVolume.Mute = value; }
            catch { /* session went away */ }
        }
    }

    public void Sync()
    {
        _syncing = true;
        try
        {
            Volume = Math.Round(Main.SimpleAudioVolume.Volume * 100);
            IsMuted = Main.SimpleAudioVolume.Mute;
        }
        catch
        {
            // ignore
        }
        finally
        {
            _syncing = false;
        }
    }

    public void UpdatePeak()
    {
        double peak = 0;
        bool active = false;
        foreach (var c in _controls)
        {
            try
            {
                float p = c.AudioMeterInformation.MasterPeakValue;
                peak = Math.Max(peak, p);
                active |= c.State == AudioSessionState.AudioSessionStateActive && p > 0.0005;
            }
            catch
            {
                // session went away
            }
        }
        Peak = peak;
        IsActive = active;
    }
}

/// <summary>Master volume, mute and per-app sessions of the default output device.</summary>
public sealed partial class AudioService : ObservableObject, IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private bool _syncing;
    private readonly DispatcherTimer _peakTimer;

    [ObservableProperty] public partial double Volume { get; set; }
    [ObservableProperty] public partial bool IsMuted { get; set; }
    [ObservableProperty] public partial string DeviceName { get; private set; } = "";

    public ObservableCollection<AudioSessionItem> Sessions { get; } = [];

    /// <summary>Raised when the system volume or mute changed (from any source).</summary>
    public event Action? VolumeChanged;

    /// <summary>Raised when the default output device changed.</summary>
    public event Action? DeviceChanged;

    public AudioService()
    {
        _peakTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var s in Sessions) s.UpdatePeak();
        }, Dispatcher.CurrentDispatcher);

        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            Log.Warn("Endpoint notifications unavailable", ex);
        }
        BindDefaultDevice();
    }

    public MMDevice? Device => _device;

    private void BindDefaultDevice()
    {
        try
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _device.Dispose();
            }
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            DeviceName = _device.FriendlyName;
            SyncFromDevice();
        }
        catch (Exception ex)
        {
            _device = null;
            DeviceName = "";
            Log.Warn("No default output device", ex);
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            SyncFromDevice();
            VolumeChanged?.Invoke();
        });

    public void SyncFromDevice()
    {
        if (_device == null) return;
        _syncing = true;
        try
        {
            Volume = Math.Round(_device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            IsMuted = _device.AudioEndpointVolume.Mute;
        }
        catch
        {
            // device lost
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnVolumeChanged(double value)
    {
        if (_syncing || _device == null) return;
        try
        {
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(value / 100.0, 0, 1);
            if (IsMuted && value > 0) IsMuted = false;
        }
        catch (Exception ex)
        {
            Log.Warn("Set volume failed", ex);
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_syncing || _device == null) return;
        try { _device.AudioEndpointVolume.Mute = value; }
        catch (Exception ex) { Log.Warn("Set mute failed", ex); }
    }

    public void Step(int delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    // ---------- output device (Windows quick settings › "Select a sound output") ----------

    /// <summary>The active playback devices, the default one marked.</summary>
    public List<AudioOutput> GetOutputs()
    {
        var list = new List<AudioOutput>();
        try
        {
            string? current = _device?.ID;
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioOutput(d.ID, d.FriendlyName, d.ID == current));
                d.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Output devices could not be listed", ex);
        }
        return list.OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Makes a device the default for every role, the way the Windows quick settings do (through the
    /// system's policy-config object; Windows then notifies every app, us included).
    /// </summary>
    public bool SetDefaultOutput(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            for (int role = 0; role <= 2; role++) // console, multimedia, communications
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Default output could not be changed", ex);
            return false;
        }
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    /// <summary>IPolicyConfig (Windows 7+): only SetDefaultEndpoint is used, the slots before it keep the vtable order.</summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    public void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>Re-reads the application sessions of the default device.</summary>
    public void RefreshSessions()
    {
        Sessions.Clear();
        if (_device == null) return;

        try
        {
            var manager = _device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            var rows = new Dictionary<string, AudioSessionItem>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                uint pid = s.GetProcessID;
                // our own session (the visualizer's loopback capture) is not an app the user plays
                if (pid == Environment.ProcessId) continue;
                string name;
                ImageSource? icon = null;

                if (s.IsSystemSoundsSession)
                {
                    name = L10n.Get("Mixer_SystemSounds");
                }
                else
                {
                    string? path = Native.GetProcessPath(pid);
                    name = !string.IsNullOrWhiteSpace(s.DisplayName) && !s.DisplayName.StartsWith('@') ? s.DisplayName : FriendlyName(pid, path);
                    if (path != null) icon = AppInfoResolver.IconFromExe(path);
                }

                // one row per application (browsers create many sessions), driving all of them
                if (rows.TryGetValue(name, out var row))
                {
                    row.Add(s);
                    continue;
                }
                row = new AudioSessionItem(s, name, icon, pid);
                rows[name] = row;
                Sessions.Add(row);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Session enumeration failed", ex);
        }
    }

    public void SetPeakMonitoring(bool on)
    {
        if (on) _peakTimer.Start();
        else _peakTimer.Stop();
    }

    private static string FriendlyName(uint pid, string? path)
    {
        try
        {
            if (path != null)
            {
                string desc = FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length < 40) return desc;
                return Path.GetFileNameWithoutExtension(path);
            }
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    // ---------- IMMNotificationClient ----------
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role != Role.Multimedia) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            BindDefaultDevice();
            DeviceChanged?.Invoke();
        });
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { /* ignore */ }
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
