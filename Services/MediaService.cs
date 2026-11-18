using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using Windows.Media;
using Windows.Media.Control;

namespace NotchWin.Services;

public class MediaInfo
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Source { get; init; } = "";
    public bool IsPlaying { get; init; }
    public bool IsVideo { get; init; }
    public byte[] Thumbnail { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Reads "what's playing" from Windows itself (the same info as the volume flyout):
/// Spotify, YouTube / YouTube Music in any browser, VLC, Netflix app, etc.
/// </summary>
public class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager _mgr;
    private GlobalSystemMediaTransportControlsSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _lastKey;
    private byte[] _thumb;

    public MediaInfo Current { get; private set; }
    public event Action<MediaInfo> Changed;

    public async Task StartAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (s, e) => _ = RefreshAsync();
            _mgr.SessionsChanged += (s, e) => { WatchAllSessions(); _ = RefreshAsync(); };
            WatchAllSessions();
        }
        catch (Exception ex)
        {
            Log.Write("Media API unavailable: " + ex.Message);
            return;
        }

        // Events cover almost everything; a slow poll catches the odd app that doesn't raise them.
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await RefreshAsync();
                await Task.Delay(5000);
            }
        });
    }

    private List<GlobalSystemMediaTransportControlsSession> _watched = new();

    /// <summary>
    /// Listen to every player (Spotify, each browser, VLC...), not just the "current" one,
    /// so starting a YouTube video while Spotify is paused shows up instantly.
    /// </summary>
    private void WatchAllSessions()
    {
        try
        {
            foreach (var old in _watched)
            {
                try
                {
                    old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                }
                catch { }
            }
            _watched = _mgr.GetSessions().ToList();
            foreach (var s in _watched)
            {
                s.MediaPropertiesChanged += OnMediaPropertiesChanged;
                s.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }
        }
        catch (Exception ex) { Log.Write("Media sessions: " + ex.Message); }
    }

    /// <summary>Prefer whichever app is actually playing, not just the one Windows thinks is "current".</summary>
    private GlobalSystemMediaTransportControlsSession PickSession()
    {
        if (_mgr == null) return null;
        try
        {
            foreach (var s in _mgr.GetSessions())
            {
                var pb = s.GetPlaybackInfo();
                if (pb != null && pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    return s;
            }
            return _mgr.GetCurrentSession();
        }
        catch { return null; }
    }

    private async Task RefreshAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var s = PickSession();
            _session = s;

            if (s == null)
            {
                Publish(null);
                return;
            }

            var props = await s.TryGetMediaPropertiesAsync();
            var pb = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();

            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            if (string.IsNullOrWhiteSpace(title))
            {
                Publish(null);
                return;
            }

            string key = title + "|" + artist + "|" + s.SourceAppUserModelId;
            if (key != _lastKey)
            {
                _lastKey = key;
                _thumb = null;
                _site = null;
            }
            string source = FriendlySource(s.SourceAppUserModelId);
            if (IsBrowser(source))
            {
                // Browsers only say "Chrome"; the tab title tells us whether it's YouTube or YouTube Music.
                _site ??= SiteFromWindowTitles(title);
                if (_site != null) source = _site;
            }
            // Apps often send the cover art a moment after the title, so keep trying until we get it.
            if (_thumb == null && props?.Thumbnail != null)
            {
                try
                {
                    using var ras = await props.Thumbnail.OpenReadAsync();
                    using var stream = ras.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    if (ms.Length > 0) _thumb = ms.ToArray();
                }
                catch { /* some apps expose no art */ }
            }

            Publish(new MediaInfo
            {
                Title = title,
                Artist = artist,
                Source = source,
                IsPlaying = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                IsVideo = pb?.PlaybackType == MediaPlaybackType.Video,
                Thumbnail = _thumb,
                Position = tl?.Position ?? TimeSpan.Zero,
                Duration = tl != null ? tl.EndTime - tl.StartTime : TimeSpan.Zero,
            });
        }
        catch (Exception ex)
        {
            Log.Write("Media refresh: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => _ = RefreshAsync();

    private void Publish(MediaInfo info)
    {
        var old = Current;
        Current = info;
        bool changed = old == null || info == null
            ? !ReferenceEquals(old, info)
            : old.Title != info.Title || old.Artist != info.Artist || old.IsPlaying != info.IsPlaying
              || old.Source != info.Source || !ReferenceEquals(old.Thumbnail, info.Thumbnail)
              || Math.Abs((old.Position - info.Position).TotalSeconds) >= 1;
        if (changed) Changed?.Invoke(info);
    }

    private string _site;

    private static bool IsBrowser(string source) =>
        source is "Chrome" or "Edge" or "Firefox" or "Brave" or "Opera";

    private static readonly HashSet<string> BrowserExes = new(StringComparer.OrdinalIgnoreCase)
        { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe" };

    /// <summary>Looks at browser window titles ("Song - YouTube Music", "Video - YouTube") to name the site.</summary>
    private static string SiteFromWindowTitles(string mediaTitle)
    {
        try
        {
            var titles = Native.WindowTitlesOf(pid =>
            {
                string path = Native.GetProcessPath(pid);
                return path != null && BrowserExes.Contains(Path.GetFileName(path));
            });
            string probe = mediaTitle.Length > 25 ? mediaTitle[..25] : mediaTitle;
            // Prefer the window that's showing this exact song/video.
            var ordered = titles.OrderByDescending(t => t.Contains(probe, StringComparison.OrdinalIgnoreCase));
            foreach (var t in ordered)
            {
                if (t.Contains("YouTube Music", StringComparison.OrdinalIgnoreCase)) return "YouTube Music";
                if (t.Contains("- YouTube", StringComparison.OrdinalIgnoreCase)) return "YouTube";
                if (t.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify Web";
                if (t.Contains("Netflix", StringComparison.OrdinalIgnoreCase)) return "Netflix";
                if (t.Contains("Prime Video", StringComparison.OrdinalIgnoreCase)) return "Prime Video";
                if (t.Contains("JioHotstar", StringComparison.OrdinalIgnoreCase) || t.Contains("Hotstar", StringComparison.OrdinalIgnoreCase)) return "JioHotstar";
                if (t.Contains("Twitch", StringComparison.OrdinalIgnoreCase)) return "Twitch";
                if (t.Contains(probe, StringComparison.OrdinalIgnoreCase)) break; // right window, unknown site
            }
        }
        catch { }
        return null;
    }

    private static string FriendlySource(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        string a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("chrome")) return "Chrome";
        if (a.Contains("msedge")) return "Edge";
        if (a.Contains("firefox") || a.Contains("308046b0af4a39cb")) return "Firefox";
        if (a.Contains("brave")) return "Brave";
        if (a.Contains("opera")) return "Opera";
        if (a.Contains("vlc")) return "VLC";
        if (a.Contains("zunemusic") || a.Contains("media player")) return "Media Player";
        if (a.Contains("netflix")) return "Netflix";
        if (a.Contains("applemusic")) return "Apple Music";
        if (a.Contains("primevideo")) return "Prime Video";
        // e.g. "SomeApp.exe" or "Publisher.App_hash!App"
        string name = aumid.Split('!')[0];
        name = name.Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    // ----- Controls -----
    public async Task PlayPauseAsync() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async Task PlayAsync() { try { if (_session != null) await _session.TryPlayAsync(); } catch { } }
    public async Task PauseAsync() { try { if (_session != null) await _session.TryPauseAsync(); } catch { } }
    public async Task NextAsync() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async Task PreviousAsync() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }
}
