using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Islet.Services;
using Islet.Ui;

namespace Islet;

public sealed record ToastData(string Glyph, Color GlyphColor, string Title, string Value, Color ValueColor,
                               int? Battery = null, bool Charging = false);

public partial class IslandWindow : Window
{
    enum View { Hidden, Idle, CompactMusic, Toast, Music, Home }
    const double StageW = 440;

    public Settings Settings { get; }
    public MediaService Media { get; }
    public AppsService Apps { get; } = new();
    public Tray? Tray { get; set; }

    readonly LyricsService _lyricsSvc = new();
    readonly BlurBackdrop _blur = new();
    readonly PowerService _power;
    readonly BluetoothService _bt;
    SystemWatch? _watch;
    IntPtr _hwnd;

    View _view = View.Idle;
    FrameworkElement? _panel;
    double _targetW = 126, _targetH = 32;
    bool _expanded, _hiddenFs;
    View _expandTarget = View.Home;

    // Auto-hide: the island stays hidden until the pointer touches the top edge of the screen.
    bool _revealed;
    int _edgeTicks;
    DateTime _outsideSince = DateTime.MaxValue;
    readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    ToastData? _toast;
    readonly Queue<ToastData> _toasts = new();
    readonly DispatcherTimer _toastTimer, _hoverTimer, _leaveTimer, _uiTick;

    readonly Storyboard _cmBars, _emBars;
    bool _cmBarsOn, _emBarsOn;
    readonly SolidColorBrush _accent = new(Colors.White);
    readonly RectangleGeometry _clip = new();
    readonly TranslateTransform _dotsT = new();
    int _dotCount;

    List<LyricLine>? _lyrics;
    string _lyricsKey = "";
    int _lyricIdx = -2;
    object? _lastArt;
    DateTime _pausedAt = DateTime.MinValue;
    bool _wasPlaying;

    static readonly SpringEase Spring = Frozen(new SpringEase { Damping = 0.66, Stiffness = 8.5 });
    static readonly SpringEase SoftSpring = Frozen(new SpringEase { Damping = 0.8, Stiffness = 8 });
    static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // Glass tiles: a lighter frosted chip with a faint rim
    static readonly SolidColorBrush TileBg = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
    static readonly SolidColorBrush TileHover = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    static readonly SolidColorBrush TileRim = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));

    public IslandWindow(Settings settings)
    {
        Settings = settings;
        InitializeComponent();
        new WindowInteropHelper(this).Owner = _blur.Handle; // owned windows always sit above their owner

        Media = new MediaService(Dispatcher);
        _power = new PowerService(Dispatcher);
        _bt = new BluetoothService(Dispatcher);

        Stage.Clip = _clip;
        Dots.RenderTransform = _dotsT;
        _cmBars = Bars.Build(CmBars, 4, 3, 2.5, 14, _accent);
        _emBars = Bars.Build(EmBars, 4, 3.5, 3, 20, _accent);
        EmFill.Background = _accent;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); NextToast(); };
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); if (Pill.IsMouseOver && !_expanded) Expand(PrimaryTarget()); };
        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); if (!Pill.IsMouseOver && _expanded) { _expanded = false; Recompute(); } };
        _uiTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTick.Tick += (_, _) => { if (_view == View.Music) TickMusic(); else if (_view == View.Home) RefreshHome(); };
        _edgeTimer.Tick += (_, _) => EdgeTick();

        Pill.SizeChanged += (_, _) => UpdateShape();
        Pill.DragOver += Pill_DragEnter;

        Media.Changed += OnMediaChanged;
        Apps.Changed += BuildApps;

        _power.PluggedIn += () =>
        {
            if (Settings.BatteryAlerts)
                ShowToast(new ToastData("", Colors.White, "Charging", _power.Percent + "%", Palette.Green, _power.Percent, true));
        };
        _power.Low += p =>
        {
            if (Settings.BatteryAlerts)
                ShowToast(new ToastData("", Colors.White, "Low Battery", p + "%", Palette.Red, p, false));
        };

        _bt.DeviceChanged += (name, connected) =>
        {
            if (!Settings.BluetoothAlerts) return;
            bool audio = System.Text.RegularExpressions.Regex.IsMatch(name,
                "buds|pods|headphone|headset|earbud|wh-|wf-|jbl|bose|beats|boat|airdopes|rockerz|nirvana|soundcore|speaker",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            ShowToast(new ToastData(audio ? "" : "", connected ? Palette.Blue : Palette.Gray,
                name, connected ? "Connected" : "Disconnected", Palette.Gray));
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd);
            ApplyGlass();
            _watch = new SystemWatch(_hwnd);
            _watch.PrivacyChanged += OnPrivacy;
            _watch.FullscreenChanged += fs => { if (_ignoreFs) return; _hiddenFs = fs; if (fs) _revealed = false; Recompute(); };
            _watch.Polled += () => { if (IsVisible) Native.EnsureTopmost(_hwnd); };
        };

        Loaded += async (_, _) =>
        {
            PositionWindow();
            UpdateShape();
            Apps.Load();
            BuildApps();
            ApplyAutoHide();
            _watch?.Start();
            _power.Start();
            _bt.Start();
            if (DebugOn) StartDebugHarness();
            try { await Media.InitAsync(); } catch { /* media controls unavailable */ }
            After(8, Compact); // drop start-up garbage
        };

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PositionWindow);
    }

    void PositionWindow()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    // ═══════════════════════════ auto-hide / top-edge reveal ═══════════════════════════

    void ApplyAutoHide()
    {
        _edgeTimer.IsEnabled = Settings.AutoHide;
        if (!Settings.AutoHide) _revealed = false;
        Recompute();
    }

    void EdgeTick()
    {
        if (_hiddenFs || _hwnd == IntPtr.Zero) return;
        var c = Native.Cursor();
        Point tl, br;
        try { tl = PointToScreen(new Point(0, 0)); br = PointToScreen(new Point(ActualWidth, ActualHeight)); }
        catch { return; }

        if (!_revealed)
        {
            // Pointer pushed against the top edge, roughly above the island (≈200 ms dwell avoids accidental pops)
            bool atEdge = c.Y <= tl.Y + 1 && c.X >= tl.X && c.X <= br.X;
            _edgeTicks = atEdge ? _edgeTicks + 1 : 0;
            if (_edgeTicks >= 2) { _edgeTicks = 0; Reveal(); }
            return;
        }

        // Revealed: conceal once the pointer has been away from the island for a moment
        bool inside;
        try
        {
            var p0 = Pill.PointToScreen(new Point(0, 0));
            var p1 = Pill.PointToScreen(new Point(Pill.ActualWidth, Pill.ActualHeight));
            const double pad = 28;
            inside = c.X >= p0.X - pad && c.X <= p1.X + pad && c.Y >= tl.Y && c.Y <= p1.Y + pad;
            // Still pressed against the reveal strip counts as "inside" (avoids open/close flicker at its ends)
            inside |= c.Y <= tl.Y + 1 && c.X >= tl.X && c.X <= br.X;
        }
        catch { inside = false; }

        if (inside) _outsideSince = DateTime.MaxValue;
        else if (_outsideSince == DateTime.MaxValue) _outsideSince = DateTime.Now;
        else if ((DateTime.Now - _outsideSince).TotalMilliseconds > 600) Conceal();
    }

    void Reveal()
    {
        _revealed = true;
        _outsideSince = DateTime.MaxValue;
        Expand(PrimaryTarget());
    }

    void Conceal()
    {
        _outsideSince = DateTime.MaxValue;
        if (!Settings.AutoHide) { _expanded = false; Recompute(); return; }
        _revealed = false;
        _expanded = false;
        Recompute();
    }

    // ═══════════════════════════ state machine ═══════════════════════════

    bool MusicActive => Media.Current is { } m && (m.IsPlaying || (DateTime.Now - _pausedAt).TotalSeconds < 10);

    View PrimaryTarget() => _view == View.CompactMusic || MusicActive ? View.Music : View.Home;

    void Expand(View target)
    {
        _expanded = true;
        _expandTarget = target;
        _hoverTimer.Stop();
        Recompute();
    }

    void Recompute()
    {
        View v;
        if (_hiddenFs) v = View.Hidden;
        else if (_toast != null) v = View.Toast;
        else if (Settings.AutoHide && !_revealed) v = View.Hidden;
        else if (_expanded) v = _expandTarget == View.Music && Media.Current == null ? View.Home : _expandTarget;
        else if (MusicActive) v = View.CompactMusic;
        else v = Settings.ShowIdlePill || Settings.AutoHide ? View.Idle : View.Hidden;
        Apply(v);
    }

    FrameworkElement? PanelFor(View v) => v switch
    {
        View.CompactMusic => PCompactMusic,
        View.Toast => PToast,
        View.Music => PMusic,
        View.Home => PHome,
        _ => null,
    };

    void Apply(View v)
    {
        var old = _panel;
        var neu = PanelFor(v);
        bool changed = v != _view;

        if (neu != null) neu.Visibility = Visibility.Visible;
        Populate(v);
        var (w, h) = SizeFor(v);

        if (changed)
        {
            var prev = _view;
            _view = v;
            _panel = neu;
            if (old != null && old != neu) FadeOut(old);
            if (neu != null && neu != old) FadeIn(neu);

            if (v == View.Hidden)
            {
                _blur.Hide();
                var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
                a.Completed += (_, _) => { if (_view == View.Hidden) Hide(); };
                Row.BeginAnimation(OpacityProperty, a);
            }
            else if (prev == View.Hidden || !IsVisible)
            {
                if (!IsVisible)
                {
                    // Drop in from a small pill at the very top, like the island waking up
                    Pill.BeginAnimation(WidthProperty, null);
                    Pill.BeginAnimation(HeightProperty, null);
                    Pill.Width = 90; Pill.Height = 22;
                    _targetW = 90; _targetH = 22;
                    Show();
                    if (_hwnd != IntPtr.Zero) Native.EnsureTopmost(_hwnd);
                }
                Row.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            }

            _uiTick.IsEnabled = v is View.Music or View.Home;
        }
        UpdateBars();
        AnimateSize(w, h);
    }

    (double w, double h) SizeFor(View v)
    {
        double extra = _dotCount > 0 ? 6 + 11 * _dotCount : 0;
        switch (v)
        {
            case View.Hidden: return (Math.Max(70, _targetW * 0.6), 22);
            case View.Idle: return (126 + extra, 32);
            case View.CompactMusic:
                PCompactMusic.Width = 236 + extra;
                CmBars.Margin = new Thickness(0, 0, 14 + extra, 0);
                return (236 + extra, 34);
            case View.Toast:
                TRight.Margin = new Thickness(0, 0, 14 + extra, 0);
                TLeft.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                TRight.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double tw = Math.Ceiling(Math.Max(240 + extra, TLeft.DesiredSize.Width + TRight.DesiredSize.Width + 34));
                tw = Math.Min(tw, StageW - 20);
                PToast.Width = tw;
                return (tw, 38);
            case View.Music: return (400, MeasureH(PMusic));
            case View.Home: return (360, MeasureH(PHome));
        }
        return (126, 32);
    }

    static double MeasureH(FrameworkElement p)
    {
        p.Measure(new Size(p.Width, double.PositiveInfinity));
        return Math.Ceiling(p.DesiredSize.Height);
    }

    void Populate(View v)
    {
        if (v == View.Music) { RefreshMusicUi(); TickMusic(); }
        else if (v == View.Home) RefreshHome();
    }

    // ═══════════════════════════ animation ═══════════════════════════

    void AnimateSize(double w, double h)
    {
        if (Math.Abs(w - _targetW) < 0.5 && Math.Abs(h - _targetH) < 0.5) return;
        bool growing = h > _targetH + 1 || w > _targetW + 1;
        _targetW = w; _targetH = h;
        var ease = growing ? Spring : SoftSpring;
        var d = TimeSpan.FromMilliseconds(growing ? 560 : 480);
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, d) { EasingFunction = ease });
    }

    void UpdateShape()
    {
        double w = Pill.ActualWidth, h = Pill.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Min(h / 2, 32);
        Pill.CornerRadius = new CornerRadius(r);

        // The stage sits inside the 1px glass rim
        double b = Pill.BorderThickness.Left, iw = Math.Max(0, w - 2 * b), ih = Math.Max(0, h - 2 * b), ir = Math.Max(0, r - b);
        _clip.Rect = new Rect((StageW - iw) / 2, 0, iw, ih);
        _clip.RadiusX = ir; _clip.RadiusY = ir;
        if (_dotCount > 0)
        {
            double dw = _dotCount * 11 - 5;
            _dotsT.X = (StageW - iw) / 2 + iw - 12 - dw;
            _dotsT.Y = h <= 42 ? (ih - 6) / 2 : 16;
        }

        PlaceBlur();
    }

    /// <summary>Keeps the blur backdrop exactly behind the pill (or hidden).</summary>
    void PlaceBlur()
    {
        if (!Settings.Glass || !IsVisible || _view == View.Hidden || _hwnd == IntPtr.Zero) { _blur.Hide(); return; }
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var p = Pill.PointToScreen(new Point(0, 0));
            double r = Pill.CornerRadius.TopLeft;
            _blur.Place((int)Math.Round(p.X), (int)Math.Round(p.Y),
                        (int)Math.Round(Pill.ActualWidth * dpi.DpiScaleX), (int)Math.Round(Pill.ActualHeight * dpi.DpiScaleY),
                        (int)Math.Round(r * dpi.DpiScaleX));
        }
        catch { _blur.Hide(); }
    }

    void ApplyGlass()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool on = Settings.Glass;
        Pill.Background = (Brush)FindResource(on ? "GlassFill" : "SolidFill");
        Pill.BorderBrush = on ? (Brush)FindResource("GlassRim") : Brushes.Transparent;
        Sheen.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateShape();
    }

    static void FadeIn(FrameworkElement p)
    {
        p.Visibility = Visibility.Visible;
        p.RenderTransformOrigin = new Point(0.5, 0);
        var st = new ScaleTransform(0.94, 0.94);
        p.RenderTransform = st;
        var begin = TimeSpan.FromMilliseconds(90);
        p.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = begin, EasingFunction = EaseOut });
        var s = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(480)) { BeginTime = begin, EasingFunction = SoftSpring };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, s);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, s);
    }

    void FadeOut(FrameworkElement p)
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        a.Completed += (_, _) => { if (p != _panel) p.Visibility = Visibility.Collapsed; };
        p.BeginAnimation(OpacityProperty, a);
    }

    void UpdateBars()
    {
        bool playing = Media.Current?.IsPlaying == true;
        SetBars(_cmBars, CmBars, ref _cmBarsOn, playing && _view == View.CompactMusic);
        SetBars(_emBars, EmBars, ref _emBarsOn, playing && _view == View.Music);
    }

    static void SetBars(Storyboard sb, FrameworkElement host, ref bool running, bool want)
    {
        if (want == running) return;
        running = want;
        if (want) sb.Begin(host, true); else sb.Stop(host);
    }

    // ═══════════════════════════ media + lyrics ═══════════════════════════

    void OnMediaChanged()
    {
        var m = Media.Current;
        if (m == null) { _wasPlaying = false; Recompute(); return; }

        if (_wasPlaying && !m.IsPlaying) { _pausedAt = DateTime.Now; After(10.3, Recompute); }
        _wasPlaying = m.IsPlaying;

        if (!ReferenceEquals(m.Art, _lastArt))
        {
            _lastArt = m.Art;
            Brush b = m.Art != null
                ? new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill }
                : new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
            CmArt.Background = b;
            EmArt.Background = b;
            _accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(m.Accent, TimeSpan.FromMilliseconds(450)));
        }

        string key = m.Artist + "\u0001" + m.Title;
        if (key != _lyricsKey)
        {
            _lyricsKey = key;
            _lyrics = null;
            _lyricIdx = -2;
            EmLyric.Text = "";
            if (Settings.Lyrics && m.Title.Length > 0) _ = LoadLyricsAsync(key);
        }

        RefreshMusicUi();
        Recompute();
    }

    async Task LoadLyricsAsync(string key)
    {
        await Task.Delay(700); // let duration/timeline arrive first
        var m = Media.Current;
        if (m == null || key != _lyricsKey) return;
        var lines = await _lyricsSvc.GetAsync(m.Artist, m.Title, m.Duration);
        if (key != _lyricsKey) return;
        _lyrics = lines;
        _lyricIdx = -2;
        RefreshMusicUi();
        TickMusic();
        Recompute();
        After(4, Compact); // track loaded: hand temporary buffers back to Windows
    }

    static void Compact()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    void RefreshMusicUi()
    {
        var m = Media.Current;
        if (m == null) return;
        EmTitle.Text = m.Title.Length > 0 ? m.Title : "Not playing";
        EmArtist.Text = m.Artist;
        BPlay.Content = m.IsPlaying ? "" : "";
        EmLyric.Visibility = Settings.Lyrics && _lyrics is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        EmProgressRow.Visibility = m.Duration.TotalSeconds > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    void TickMusic()
    {
        var m = Media.Current;
        if (m == null) return;
        var pos = m.CurrentPosition;
        var dur = m.Duration;
        if (dur.TotalSeconds > 1)
        {
            double r = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1);
            EmFill.Width = Math.Max(4, EmTrack.ActualWidth * r);
            EmElapsed.Text = Fmt(pos);
            EmRemain.Text = "-" + Fmt(dur - pos);
        }

        if (_lyrics is { Count: > 0 } l && Settings.Lyrics)
        {
            var p = pos + TimeSpan.FromMilliseconds(250);
            int idx = -1;
            for (int i = 0; i < l.Count && l[i].Time <= p; i++) idx = i;
            if (idx != _lyricIdx)
            {
                _lyricIdx = idx;
                string text = idx >= 0 ? l[idx].Text : "";
                SetLyric(string.IsNullOrWhiteSpace(text) ? "♪" : text);
            }
        }
    }

    void SetLyric(string text)
    {
        EmLyric.Text = text;
        EmLyric.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
        if (EmLyric.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = SoftSpring });
    }

    static string Fmt(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ═══════════════════════════ home + app shortcuts ═══════════════════════════

    public void ToggleHome()
    {
        if (_expanded && _view == View.Home) Conceal();
        else { _revealed = true; Expand(View.Home); }
    }

    void RefreshHome()
    {
        var now = DateTime.Now;
        HTime.Text = now.ToString("t", CultureInfo.CurrentCulture);
        HDate.Text = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
        if (_power.HasBattery)
        {
            HBatteryRow.Visibility = Visibility.Visible;
            HBattery.Text = _power.Percent + "%";
            HBatteryGlyph.Set(_power.Percent, _power.Charging);
        }
        else HBatteryRow.Visibility = Visibility.Collapsed;
    }

    void BuildApps()
    {
        AppsRow.Children.Clear();
        foreach (var app in Apps.Apps)
        {
            var a = app;
            var img = new Image { Source = Apps.Icon(a), Width = 22, Height = 22 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var scale = new ScaleTransform(1, 1);
            var tile = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Background = TileBg,
                BorderBrush = TileRim, BorderThickness = new Thickness(1), Child = img,
                Margin = new Thickness(AppsRow.Children.Count > 0 ? 8 : 0, 0, 0, 0), Cursor = Cursors.Hand,
                ToolTip = a.Name, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
            };
            tile.MouseEnter += (_, _) => tile.Background = TileHover;
            tile.MouseLeave += (_, _) => { tile.Background = TileBg; Bounce(scale, 1); };
            tile.MouseLeftButtonDown += (_, e) => { Bounce(scale, 0.88); e.Handled = true; };
            tile.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Bounce(scale, 1);
                AppsService.Launch(a);
                After(0.15, Conceal);
            };
            tile.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var item = new MenuItem { Header = $"Remove {a.Name}" };
                item.Click += (_, _) => Apps.Remove(a);
                new ContextMenu { Items = { item }, PlacementTarget = tile, IsOpen = true };
            };
            AppsRow.Children.Add(tile);
        }
        AppsHint.Visibility = Apps.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsRow.Visibility = Apps.Apps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_view == View.Home) { _targetW = -1; Recompute(); }
    }

    static void Bounce(ScaleTransform s, double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to < 1 ? 90 : 380)) { EasingFunction = to < 1 ? EaseOut : Spring };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void Pill_DragEnter(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        if (files && _view != View.Home) { _revealed = true; Expand(View.Home); }
    }

    void Pill_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var p in paths)
        {
            if (Apps.Apps.Count >= AppsService.Max)
            {
                ShowToast(new ToastData("", Palette.Orange, "Shortcuts full", $"{AppsService.Max} max", Palette.Gray));
                break;
            }
            if (!Apps.Add(p)) continue;
            ShowToast(new ToastData("", Palette.Blue, "Pinned " + Apps.Apps[^1].Name, "", Palette.Gray));
        }
        e.Handled = true;
    }

    // ═══════════════════════════ toasts / privacy ═══════════════════════════

    public void ShowToast(ToastData t)
    {
        _toasts.Enqueue(t);
        if (_toast == null) NextToast();
    }

    void NextToast()
    {
        if (_toasts.Count == 0) { _toast = null; Recompute(); return; }
        var t = _toast = _toasts.Dequeue();
        TIcon.Text = t.Glyph;
        TIcon.Foreground = new SolidColorBrush(t.GlyphColor);
        TIcon.Visibility = t.Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TTitle.Text = t.Title;
        TValue.Text = t.Value;
        TValue.Foreground = new SolidColorBrush(t.ValueColor);
        if (t.Battery is int p) { TBattery.Visibility = Visibility.Visible; TBattery.Set(p, t.Charging); }
        else TBattery.Visibility = Visibility.Collapsed;

        if (_view == View.Toast) // swapping one toast for another
            PToast.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(220)));
        Recompute();
        _toastTimer.Start();
    }

    void OnPrivacy(bool mic, bool cam)
    {
        Dots.Children.Clear();
        if (cam) Dots.Children.Add(Dot(Palette.Green));
        if (mic) Dots.Children.Add(Dot(Palette.Orange));
        _dotCount = Dots.Children.Count;
        _targetW = -1; // force a resize
        Recompute();
        UpdateShape();

        Ellipse Dot(Color c) => new()
        {
            Width = 6, Height = 6, Fill = new SolidColorBrush(c),
            Margin = new Thickness(Dots.Children.Count > 0 ? 5 : 0, 0, 0, 0),
        };
    }

    // ═══════════════════════════ input ═══════════════════════════

    void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _leaveTimer.Stop();
        if (!_expanded && Settings.HoverExpand && _view == View.CompactMusic) _hoverTimer.Start();
    }

    void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (_expanded && !Settings.AutoHide) _leaveTimer.Start(); // in auto-hide mode the edge watcher handles this
    }

    void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (_view == View.Toast) { _toastTimer.Stop(); NextToast(); return; }
        if (_expanded)
        {
            // In auto-hide mode, clicking an expanded music card flips to home (apps) and back
            if (Settings.AutoHide) Expand(_view == View.Music ? View.Home : MusicActive ? View.Music : View.Home);
            else { _expanded = false; Recompute(); }
        }
        else Expand(PrimaryTarget());
    }

    void Pill_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        Tray?.ShowMenu();
        e.Handled = true;
    }

    void Pill_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) { Media.TogglePlayPause(); e.Handled = true; }
    }

    void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Native.PressKey(e.Delta > 0 ? Native.VK_VOLUME_UP : Native.VK_VOLUME_DOWN);
        e.Handled = true;
    }

    void OnPrev(object s, RoutedEventArgs e) => Media.Previous();
    void OnNext(object s, RoutedEventArgs e) => Media.Next();
    void OnPlay(object s, RoutedEventArgs e) => Media.TogglePlayPause();

    public void OnSettingsChanged()
    {
        ApplyGlass();
        _lyricsKey = "";          // re-evaluate lyrics for the current track
        _edgeTimer.IsEnabled = Settings.AutoHide;
        if (!Settings.AutoHide) _revealed = false;
        if (Media.Current != null) OnMediaChanged();
        else Recompute();
    }

    void After(double seconds, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    // ═══════════════════════════ debug harness (ISLET_DEBUG=1 only) ═══════════════════════════

    static readonly bool DebugOn = Environment.GetEnvironmentVariable("ISLET_DEBUG") == "1";
    bool _ignoreFs;

    static void Dbg(string s)
    {
        if (!DebugOn) return;
        try { Directory.CreateDirectory(Settings.Dir); File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); }
        catch { }
    }

    /// <summary>Reads commands from cmd.txt and renders the island to snap-*.png (for automated testing).</summary>
    void StartDebugHarness()
    {
        string cmdPath = System.IO.Path.Combine(Settings.Dir, "cmd.txt");
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        t.Tick += (_, _) =>
        {
            if (!File.Exists(cmdPath)) return;
            string[] cmds;
            try { cmds = File.ReadAllLines(cmdPath); File.Delete(cmdPath); } catch { return; }
            foreach (var c in cmds)
            {
                var parts = c.Trim().Split(' ', 2);
                switch (parts[0])
                {
                    case "reveal": Reveal(); break;
                    case "ignorefs": _ignoreFs = true; _hiddenFs = false; Recompute(); break;
                    case "conceal": Conceal(); break;
                    case "home": _revealed = true; Expand(View.Home); break;
                    case "music": _revealed = true; Expand(View.Music); break;
                    case "collapse": _expanded = false; Recompute(); break;
                    case "toast": ShowToast(new ToastData("", Colors.White, "Charging", "60%", Palette.Green, 60, true)); break;
                    case "dots": OnPrivacy(true, true); break;
                    case "nodots": OnPrivacy(false, false); break;
                    case "snap": Snap(parts.Length > 1 ? parts[1] : "x"); break;
                    case "state":
                        Dbg($"{Native.RegionInfo(_hwnd)} glass={Settings.Glass} composition={_blur.UsingComposition}");
                        Dbg($"view={_view} visible={IsVisible} revealed={_revealed} expanded={_expanded} apps=[{string.Join(", ", Apps.Apps.ConvertAll(a => a.Name + " -> " + a.Target))}]");
                        break;
                }
            }
        };
        t.Start();
    }

    void Snap(string name)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var root = (FrameworkElement)Content;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)), null, new Rect(0, 0, ActualWidth, ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(root.TranslatePoint(new Point(), this), new Size(root.ActualWidth, root.ActualHeight)));
            }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(System.IO.Path.Combine(Settings.Dir, $"snap-{name}.png"));
            enc.Save(fs);
        }
        catch (Exception ex) { Dbg("snap failed " + ex.Message); }
    }
}
