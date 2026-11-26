using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Islet.Ui;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Islet.Services;

public sealed class MediaInfo
{
    public string AppId = "";
    public string Title = "", Artist = "", Album = "";
    public BitmapSource? Art;
    public Color Accent = Colors.White;
    public bool IsPlaying;
    public TimeSpan Position, Duration;
    public DateTimeOffset PositionUpdated = DateTimeOffset.Now;

    public TimeSpan CurrentPosition
    {
        get
        {
            var p = IsPlaying ? Position + (DateTimeOffset.Now - PositionUpdated) : Position;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }
}

/// <summary>
/// Reads "now playing" from Windows' system media controls (Spotify, browsers, Apple Music, VLC...).
/// Fully event driven: no polling.
/// </summary>
public sealed class MediaService
{
    readonly Dispatcher _d;
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly List<GlobalSystemMediaTransportControlsSession> _watched = new();
    int _propsVersion;

    public MediaInfo? Current { get; private set; }
    public event Action? Changed;

    public MediaService(Dispatcher d) { _d = d; }

    public async Task InitAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.SessionsChanged += (_, _) => _d.InvokeAsync(Rewire);
        _mgr.CurrentSessionChanged += (_, _) => _d.InvokeAsync(Pick);
        Rewire();
    }

    void Rewire()
    {
        if (_mgr == null) return;
        foreach (var s in _watched) { try { s.PlaybackInfoChanged -= OnAnyPlayback; } catch { } }
        _watched.Clear();
        try
        {
            foreach (var s in _mgr.GetSessions()) { s.PlaybackInfoChanged += OnAnyPlayback; _watched.Add(s); }
        }
        catch { }
        Pick();
    }

    void OnAnyPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _d.InvokeAsync(Pick);

    static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    void Pick()
    {
        if (_mgr == null) return;
        GlobalSystemMediaTransportControlsSession? best = null;
        try
        {
            var sessions = _mgr.GetSessions().ToList();
            var cur = _session != null ? sessions.FirstOrDefault(s => s.SourceAppUserModelId == _session.SourceAppUserModelId) : null;
            if (cur != null && IsPlaying(cur)) best = cur;                // stay on what's playing
            best ??= sessions.FirstOrDefault(IsPlaying);                  // else anything that's playing
            best ??= cur;                                                 // else keep the paused one
            best ??= _mgr.GetCurrentSession();
        }
        catch { }
        Attach(best);
    }

    void Attach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s != null && _session != null && s.SourceAppUserModelId == _session.SourceAppUserModelId && Current != null)
        {
            _session = s;
            RefreshPlayback(); RefreshTimeline();
            _ = RefreshPropsAsync();
            Changed?.Invoke();
            return;
        }

        if (_session != null)
        {
            try { _session.MediaPropertiesChanged -= OnProps; _session.TimelinePropertiesChanged -= OnTimeline; } catch { }
        }
        _session = s;
        if (s == null) { Current = null; Changed?.Invoke(); return; }

        s.MediaPropertiesChanged += OnProps;
        s.TimelinePropertiesChanged += OnTimeline;
        Current = new MediaInfo { AppId = s.SourceAppUserModelId ?? "" };
        RefreshPlayback(); RefreshTimeline();
        _ = RefreshPropsAsync();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _d.InvokeAsync(() => _ = RefreshPropsAsync());

    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _d.InvokeAsync(() =>
    {
        var before = Current?.Duration;
        RefreshTimeline();
        if (Current != null && Current.Duration != before) Changed?.Invoke();
    });

    void RefreshPlayback()
    {
        var s = _session; var c = Current;
        if (s == null || c == null) return;
        bool playing = IsPlaying(s);
        if (c.IsPlaying != playing)
        {
            c.Position = c.CurrentPosition;
            c.PositionUpdated = DateTimeOffset.Now;
            c.IsPlaying = playing;
        }
    }

    void RefreshTimeline()
    {
        var s = _session; var c = Current;
        if (s == null || c == null) return;
        try
        {
            var t = s.GetTimelineProperties();
            var dur = t.EndTime - t.StartTime;
            c.Duration = dur > TimeSpan.Zero ? dur : TimeSpan.Zero;
            c.Position = t.Position;
            var upd = t.LastUpdatedTime;
            if (upd.Year < 2000 || upd > DateTimeOffset.Now.AddMinutes(1)) upd = DateTimeOffset.Now;
            c.PositionUpdated = upd;
        }
        catch { }
    }

    async Task RefreshPropsAsync()
    {
        var s = _session;
        if (s == null) return;
        int ver = ++_propsVersion;
        GlobalSystemMediaTransportControlsSessionMediaProperties? p;
        try { p = await s.TryGetMediaPropertiesAsync(); } catch { return; }
        var c = Current;
        if (ver != _propsVersion || p == null || c == null) return;

        string title = p.Title ?? "", artist = string.IsNullOrEmpty(p.Artist) ? p.AlbumArtist ?? "" : p.Artist;
        bool trackChanged = title != c.Title || artist != c.Artist;
        c.Title = title; c.Artist = artist; c.Album = p.AlbumTitle ?? "";
        if (trackChanged) c.Art = null;

        if (c.Art == null && p.Thumbnail != null)
        {
            BitmapSource? art = null;
            try { art = await LoadArt(p.Thumbnail); } catch { }
            if (ver != _propsVersion) return;
            c.Art = art;
            c.Accent = art != null ? Palette.Accent(art) : Colors.White;
        }
        RefreshPlayback();
        Changed?.Invoke();
    }

    static async Task<BitmapSource?> LoadArt(IRandomAccessStreamReference r)
    {
        using var ras = await r.OpenReadAsync();
        using var s = ras.AsStreamForRead();
        var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        if (ms.Length == 0) return null;
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 112; // small decode = small memory
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public void TogglePlayPause() { try { _ = _session?.TryTogglePlayPauseAsync(); } catch { } }
    public void Next() { try { _ = _session?.TrySkipNextAsync(); } catch { } }
    public void Previous() { try { _ = _session?.TrySkipPreviousAsync(); } catch { } }
}
