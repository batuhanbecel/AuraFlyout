using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuraFlyout.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Media.Control;
using GsmtcManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using GsmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace AuraFlyout.Media;

/// <summary>
/// Wraps Windows' Global System Media Transport Controls (the API behind the native media overlay).
/// Everything here is exposed as bindable properties on the UI thread.
/// </summary>
public sealed partial class MediaService : ObservableObject
{
    private GsmtcManager? _manager;
    private GsmtcSession? _session;
    // every session's change events, kept so they can be removed when the session ends
    private readonly Dictionary<GsmtcSession, Subscription> _hooked = [];

    private sealed record Subscription(
        Windows.Foundation.TypedEventHandler<GsmtcSession, PlaybackInfoChangedEventArgs> Playback,
        Windows.Foundation.TypedEventHandler<GsmtcSession, MediaPropertiesChangedEventArgs> Properties,
        Windows.Foundation.TypedEventHandler<GsmtcSession, TimelinePropertiesChangedEventArgs> Timeline);
    private string? _pinnedSessionId;
    private string _lastTrackKey = "";
    private int _refreshVersion;

    private TimeSpan _timelinePosition;
    private TimeSpan _seekTarget;
    private DateTimeOffset _seekAt;
    private DateTimeOffset _timelineUpdated;
    private double _playbackRate = 1;

    [ObservableProperty] public partial bool HasSession { get; private set; }
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Artist { get; private set; } = "";
    [ObservableProperty] public partial string Album { get; private set; } = "";
    [ObservableProperty] public partial ImageSource? Cover { get; private set; }
    [ObservableProperty] public partial string AppName { get; private set; } = "";
    [ObservableProperty] public partial string AppId { get; private set; } = "";
    [ObservableProperty] public partial ImageSource? AppIcon { get; private set; }
    [ObservableProperty] public partial bool IsPlaying { get; private set; }
    [ObservableProperty] public partial bool CanPlayPause { get; private set; }
    [ObservableProperty] public partial bool CanNext { get; private set; }
    [ObservableProperty] public partial bool CanPrevious { get; private set; }
    [ObservableProperty] public partial bool CanSeek { get; private set; }
    [ObservableProperty] public partial bool CanShuffle { get; private set; }
    [ObservableProperty] public partial bool CanRepeat { get; private set; }
    [ObservableProperty] public partial bool IsShuffle { get; private set; }
    /// <summary>0 none, 1 track, 2 list</summary>
    [ObservableProperty] public partial int RepeatMode { get; private set; }
    [ObservableProperty] public partial TimeSpan Duration { get; private set; }
    [ObservableProperty] public partial int SessionCount { get; private set; }

    /// <summary>Raised when the playing track changes within the same player.</summary>
    public event Action? TrackChanged;

    /// <summary>Raised whenever any media info changed (used by the taskbar widget).</summary>
    public event Action? Updated;

    public bool HasMultipleSessions => SessionCount > 1;

    partial void OnSessionCountChanged(int value) => OnPropertyChanged(nameof(HasMultipleSessions));

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GsmtcManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => OnUi(SelectSession);
            _manager.CurrentSessionChanged += (_, _) => OnUi(SelectSession);
            SettingsStore.Current.ExcludedApps.CollectionChanged += (_, _) => OnUi(SelectSession);
            SelectSession();
        }
        catch (Exception ex)
        {
            Log.Error("Media manager could not be created", ex);
        }
    }

    /// <summary>Current position, interpolated between the (sparse) timeline updates.</summary>
    public TimeSpan Position
    {
        get
        {
            var pos = _timelinePosition;
            if (IsPlaying && _timelineUpdated != default)
                pos += TimeSpan.FromTicks((long)((DateTimeOffset.Now - _timelineUpdated).Ticks * _playbackRate));
            if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
            return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
        }
    }

    public IReadOnlyList<GsmtcSession> Sessions =>
        _manager?.GetSessions().Where(s => !IsExcluded(s.SourceAppUserModelId)).ToList() ?? [];

    private static bool IsExcluded(string appId) =>
        SettingsStore.Current.ExcludedApps.Any(x => appId.Contains(x, StringComparison.OrdinalIgnoreCase));

    private void SelectSession()
    {
        if (_manager == null) return;

        var all = _manager.GetSessions();
        var candidates = all.Where(s => !IsExcluded(s.SourceAppUserModelId)).ToList();

        // subscribe to every session so we notice when another player starts
        foreach (var s in all)
        {
            if (_hooked.ContainsKey(s)) continue;
            var sub = new Subscription(
                (sender, args) => OnUi(() => OnPlaybackInfoChanged(sender)),
                (sender, args) => OnUi(() => { if (sender == _session) _ = RefreshPropertiesAsync(); }),
                (sender, args) => OnUi(() => { if (sender == _session) RefreshTimeline(); }));
            try
            {
                s.PlaybackInfoChanged += sub.Playback;
                s.MediaPropertiesChanged += sub.Properties;
                s.TimelinePropertiesChanged += sub.Timeline;
                _hooked[s] = sub;
            }
            catch (Exception ex)
            {
                Log.Warn("Media session events unavailable", ex);
            }
        }
        // A session that ended loses its handlers before it is let go. Left registered, the WinRT
        // event state of every ended session stayed behind; browsers open and close sessions all
        // the time (measured: about 15 MB more heap per thousand sessions, none with this).
        foreach (var gone in _hooked.Keys.Where(h => !all.Contains(h)).ToList())
            Unhook(gone);

        GsmtcSession? pick = null;
        if (_pinnedSessionId != null)
            pick = candidates.FirstOrDefault(s => s.SourceAppUserModelId == _pinnedSessionId);

        if (pick == null)
        {
            var current = _manager.GetCurrentSession();
            bool currentOk = current != null && candidates.Contains(current);
            var playing = candidates.FirstOrDefault(s => SafeStatus(s) == PlaybackStatus.Playing);

            if (currentOk && SafeStatus(current!) == PlaybackStatus.Playing) pick = current;
            else if (playing != null) pick = playing;
            else if (currentOk) pick = current;
            else pick = candidates.FirstOrDefault();
        }

        SessionCount = candidates.Count;

        if (pick != _session)
        {
            _session = pick;
            _lastTrackKey = "";
            Refresh();
        }
    }

    private void Unhook(GsmtcSession s)
    {
        if (!_hooked.Remove(s, out var sub)) return;
        try
        {
            s.PlaybackInfoChanged -= sub.Playback;
            s.MediaPropertiesChanged -= sub.Properties;
            s.TimelinePropertiesChanged -= sub.Timeline;
        }
        catch
        {
            // the player already went away: nothing left to remove
        }
    }

    private void OnPlaybackInfoChanged(GsmtcSession sender)
    {
        if (sender == _session) RefreshPlayback();

        // follow whichever player starts playing, unless the user pinned one
        if (_pinnedSessionId == null && SafeStatus(sender) == PlaybackStatus.Playing && sender != _session)
            SelectSession();
    }

    private static PlaybackStatus? SafeStatus(GsmtcSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus; }
        catch { return null; }
    }

    private void Refresh()
    {
        if (_session == null)
        {
            HasSession = false;
            Title = Artist = Album = AppName = AppId = "";
            Cover = null;
            AppIcon = null;
            IsPlaying = false;
            Duration = TimeSpan.Zero;
            Updated?.Invoke();
            return;
        }

        HasSession = true;
        AppId = _session.SourceAppUserModelId;
        var app = AppInfoResolver.Resolve(AppId);
        AppName = app.Name;
        AppIcon = app.Icon;
        if (app.Icon == null) _ = LoadIconAsync(AppId);

        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshPropertiesAsync();
    }

    private async Task LoadIconAsync(string appId)
    {
        var icon = await AppInfoResolver.ResolveIconAsync(appId);
        if (icon != null && AppId == appId) AppIcon = icon;
    }

    private void RefreshPlayback()
    {
        if (_session == null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            var c = info.Controls;
            IsPlaying = info.PlaybackStatus == PlaybackStatus.Playing;
            CanPlayPause = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
            CanNext = c.IsNextEnabled;
            CanPrevious = c.IsPreviousEnabled;
            CanShuffle = c.IsShuffleEnabled;
            CanRepeat = c.IsRepeatEnabled;
            IsShuffle = info.IsShuffleActive ?? false;
            RepeatMode = info.AutoRepeatMode switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.Track => 1,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => 2,
                _ => 0,
            };
            _playbackRate = info.PlaybackRate ?? 1;
            CanSeek = c.IsPlaybackPositionEnabled && Duration > TimeSpan.Zero;

            // re-anchor the interpolation so pausing does not jump
            _timelinePosition = Position;
            _timelineUpdated = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            Log.Warn("Playback info failed", ex);
        }
        Updated?.Invoke();
    }

    private void RefreshTimeline()
    {
        if (_session == null) return;
        try
        {
            var t = _session.GetTimelineProperties();
            Duration = t.EndTime - t.StartTime;
            var reported = t.Position - t.StartTime;
            var updated = t.LastUpdatedTime.Year > 2000 ? t.LastUpdatedTime : DateTimeOffset.Now;
            // right after a seek the player may still report where it was: keep the sought position
            bool stale = DateTimeOffset.Now - _seekAt < TimeSpan.FromSeconds(2) &&
                         Math.Abs((reported - _seekTarget).TotalSeconds) > 2.5;
            if (!stale)
            {
                _timelinePosition = reported;
                _timelineUpdated = updated;
            }
            CanSeek = _session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled && Duration > TimeSpan.Zero;
        }
        catch
        {
            Duration = TimeSpan.Zero;
        }
    }

    private async Task RefreshPropertiesAsync()
    {
        var session = _session;
        if (session == null) return;
        int version = ++_refreshVersion;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion || session != _session || props == null) return;

            string newTitle = props.Title ?? "";
            string newArtist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            string key = newTitle + "\u0001" + newArtist;

            Title = newTitle;
            Artist = newArtist;
            Album = props.AlbumTitle ?? "";

            ImageSource? cover = null;
            if (props.Thumbnail != null)
                cover = await LoadThumbnailAsync(props.Thumbnail);
            if (version != _refreshVersion || session != _session) return;
            Cover = cover;

            bool changed = _lastTrackKey != "" && key != _lastTrackKey && newTitle != "";
            if (newTitle != "") _lastTrackKey = key;
            RefreshTimeline();
            Updated?.Invoke();
            if (changed) TrackChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Media properties failed", ex);
        }
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var net = stream.AsStream();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            if (ms.Length == 0) return null;
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 300;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ---------- commands ----------

    public async void PlayPause() => await Try(s => s.TryTogglePlayPauseAsync().AsTask());
    public async void Next() => await Try(s => s.TrySkipNextAsync().AsTask());
    public async void Previous() => await Try(s => s.TrySkipPreviousAsync().AsTask());

    public async void ToggleShuffle() => await Try(s => s.TryChangeShuffleActiveAsync(!IsShuffle).AsTask());

    public async void CycleRepeat()
    {
        var next = RepeatMode switch
        {
            0 => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            2 => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
        };
        await Try(s => s.TryChangeAutoRepeatModeAsync(next).AsTask());
    }

    public async void Seek(TimeSpan position)
    {
        if (_session == null) return;
        try
        {
            // the bar shows the new position at once, not when the player has answered
            _seekTarget = position;
            _seekAt = DateTimeOffset.Now;
            _timelinePosition = position;
            _timelineUpdated = _seekAt;
            var t = _session.GetTimelineProperties();
            long ticks = (t.StartTime + position).Ticks;
            if (!await _session.TryChangePlaybackPositionAsync(ticks))
            {
                _seekAt = default;
                RefreshTimeline(); // refused: back to where it really is
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Seek failed", ex);
        }
    }

    /// <summary>Cycles to the next media session (when several players are open).</summary>
    public void SwitchSession()
    {
        var list = Sessions;
        if (list.Count < 2) return;
        int idx = _session == null ? -1 : list.ToList().IndexOf(_session);
        var next = list[(idx + 1) % list.Count];
        _pinnedSessionId = next.SourceAppUserModelId;
        SelectSession();
    }

    /// <summary>Brings the player window to the front.</summary>
    public void OpenPlayer() => AppInfoResolver.Activate(AppId);

    private async Task Try(Func<GsmtcSession, Task<bool>> action)
    {
        if (_session == null) return;
        try
        {
            await action(_session);
        }
        catch (Exception ex)
        {
            Log.Warn("Media command failed", ex);
        }
    }

    private static void OnUi(Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(action);
}
