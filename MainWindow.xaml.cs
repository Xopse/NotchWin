using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NotchWin.Services;

namespace NotchWin;

public partial class MainWindow : Window
{
    // ---------- sizes of the three states ----------
    private const double ActivityWidth = 440, ExpandedWidth = 700, ExpandedHeight = 266;
    private const int HotkeyId = 0x4E57;

    private enum Mode { Compact, Activity, Expanded }

    private class Activity
    {
        public string Glyph = "\uE946";
        public Color Accent = Color.FromRgb(0x0A, 0x84, 0xFF);
        public string Title = "";
        public string Subtitle = "";
        public double Seconds = 5;
        public bool Sticky;
        /// <summary>Runs when the pop-up finishes (used to "drop" notifications into the bell).</summary>
        public Action OnEnd;
        /// <summary>Runs when you click the pop-up (e.g. "see what's new").</summary>
        public Action OnClick;
        public string Tag;
    }

    private AppConfig _cfg;
    private MediaService _media;
    private SystemStatsService _stats;
    private WeatherService _weather;
    private GameWatcher _games;
    private StreamService _stream;
    private NotificationService _notifications;
    private VoiceAssistant _voice;
    private CommandRouter _router;

    private IntPtr _hwnd;
    private Mode _mode = Mode.Compact;
    private bool _hovering, _hidden, _wasStreaming, _wasRecording;
    private Activity _activity;
    private DateTime _activityUntil;
    private readonly Queue<Activity> _queue = new();

    private MediaInfo _mediaInfo;
    private DateTime _mediaStamp;
    private byte[] _shownThumb;
    private string _lastTrack;
    private bool _eqRunning, _liveAnimRunning;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _hoverIn = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _hoverOut = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _trim = new() { Interval = TimeSpan.FromSeconds(8) };
    private int _tickCount;
    private SettingsWindow _settings;
    private Theme _theme = Theme.All[0];
    private LinearGradientBrush _bgBrush;   // kept so colour changes can animate (Vibe theme)

    // Position / dragging
    private bool _mouseDown, _dragging;
    private Native.POINT _dragStartCursor;
    private bool _clickThrough;
    private readonly DispatcherTimer _hitTest = new() { Interval = TimeSpan.FromMilliseconds(60) };

    // Notifications that went into the bell
    private readonly List<NotificationItem> _notifs = new();
    private int _unread;
    private bool _showNotifs;

    private double CompactHeight => Math.Clamp(_cfg.CompactHeight, 24, 60);

    public MainWindow()
    {
        InitializeComponent();
        _cfg = AppConfig.Load();
        ApplyTheme(_cfg.Theme);
        _hoverIn.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _cfg.HoverDelayMs));
        Pill.Width = _cfg.CompactWidth;
        Pill.Height = CompactHeight;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SystemEvents.DisplaySettingsChanged += (s, e) => Dispatcher.BeginInvoke(PositionWindow);

        _hoverIn.Tick += (s, e) => { _hoverIn.Stop(); _hovering = true; UpdateMode(); };
        _hoverOut.Tick += (s, e) => { _hoverOut.Stop(); _hovering = false; UpdateMode(); };
        _tick.Tick += (s, e) => OnTick();
        BigArt.MouseLeftButtonUp += (s, e) =>
        {
            if (_mediaInfo != null) return;
            e.Handled = true;
            CommandRouter.OpenMusicApp(_cfg); // nothing playing: the empty cover opens your music app
        };
        _hitTest.Tick += (s, e) => UpdateClickThrough();
        // Give unused memory back to Windows a few seconds after the notch closes.
        _trim.Tick += (s, e) => { _trim.Stop(); if (_mode != Mode.Expanded) Native.TrimMemory(); };
    }

    // =====================================================================
    //  Startup
    // =====================================================================

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Hidden from Alt+Tab and never steals focus from whatever you're doing.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        // Ctrl+Alt+Space = talk to the assistant
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        if (!Native.RegisterHotKey(_hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_SPACE))
            Log.Write("Ctrl+Alt+Space is already used by another app.");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            if (_voice != null) _voice.ListenNow();
            else Push(new Activity { Tag = "voice", Glyph = "\uE720", Accent = Color.FromRgb(0x64, 0xD2, 0xFF), Seconds = 4,
                                     Title = "Voice assistant is off", Subtitle = "Turn it on in Settings → Voice & AI." });
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        ApplyStartup();
        BuildShortcuts();
        BuildContextMenu();
        ApplyMode(force: true);

        _stats = new SystemStatsService();
        _stats.Start();

        _weather = new WeatherService(_cfg);
        _weather.Start();

        _games = new GameWatcher(_cfg);
        _games.GameLaunched += name => Dispatcher.BeginInvoke(() => Push(new Activity
        {
            Glyph = "\uE7FC", Accent = Color.FromRgb(0xBF, 0x5A, 0xF2),
            Title = "Launching " + name, Subtitle = "Have fun! The notch will hide while you play.", Seconds = 5,
        }));
        _games.Start();

        _stream = new StreamService(_cfg);
        _stream.Start();

        _notifications = new NotificationService(_cfg);
        _notifications.Received += n => Dispatcher.BeginInvoke(() => OnNotification(n));
        _notifications.Start();

        _media = new MediaService();
        _media.Changed += info => Dispatcher.BeginInvoke(() => OnMediaChanged(info));
        await _media.StartAsync();

        _router = new CommandRouter(_cfg, _media, _weather);
        if (!string.Equals(_cfg.VoiceMode, "Off", StringComparison.OrdinalIgnoreCase)) StartVoice();

        OnTick();
        _tick.Start();
        _hitTest.Start();
        _trim.Interval = TimeSpan.FromSeconds(30); // first trim once everything has started
        _trim.Start();

        // Auto-update: first check a minute after start, then every 6 hours.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            while (true)
            {
                if (!string.Equals(_cfg.UpdateMode, "off", StringComparison.OrdinalIgnoreCase) && Updater.IsInstalledCopy)
                {
                    try { await Dispatcher.InvokeAsync(() => CheckForUpdatesAsync(manual: false)).Task.Unwrap(); }
                    catch (Exception ex) { Log.Write("Update: " + ex.Message); }
                }
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });
    }

    // =====================================================================
    //  Updates
    // =====================================================================

    private bool _updating;

    private Updater.Release _availableUpdate;
    private string _announcedTag;
    private UpdateWindow _updateWindow;

    /// <summary>
    /// Checks GitHub for a newer release. Depending on Settings it either tells you about it (pop-up + bell,
    /// with the changelog) or installs it by itself. Returns a short status for Settings.
    /// </summary>
    public async Task<string> CheckForUpdatesAsync(bool manual)
    {
        if (_updating) return "An update is already being installed.";
        var (release, problem) = await Updater.GetLatestAsync(_cfg.UpdateRepo);
        if (release == null) return problem;
        if (release.Version <= Updater.Current) return $"You're up to date (version {Updater.Current}).";
        if (!Updater.IsInstalledCopy)
            return $"Version {release.Version} is available. This copy isn't the installed one, so install it with the installer.";

        _availableUpdate = release;
        if (manual)
        {
            ShowUpdateWindow();
            return $"Version {release.Tag} is available.";
        }
        if (string.Equals(_cfg.SkippedVersion, release.Tag, StringComparison.OrdinalIgnoreCase)) return "Skipped.";
        if (string.Equals(_cfg.UpdateMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            await InstallUpdateAsync(release, waitForGames: true);
            return $"Installing {release.Tag}...";
        }

        // "Ask" mode: tell everyone once, with the changelog one click away.
        if (_announcedTag != release.Tag)
        {
            _announcedTag = release.Tag;
            string firstLine = (release.Notes ?? "").Replace("\r", "").Split('\n')
                .Select(l => l.Trim().TrimStart('#', '-', '*', ' ').Trim())
                .FirstOrDefault(l => l.Length > 0) ?? "";
            if (firstLine.Length > 70) firstLine = firstLine[..70] + "...";

            Push(new Activity
            {
                Tag = "update", Glyph = "\uE896", Accent = Color.FromRgb(0x30, 0xD1, 0x58), Seconds = 10,
                Title = $"NotchWin {release.Tag} is out",
                Subtitle = (firstLine.Length > 0 ? firstLine + "  ·  " : "") + "Click to see what's new",
                OnClick = ShowUpdateWindow,
            });
            // Also leave it in the bell, so you can find it later.
            _notifs.Insert(0, new NotificationItem
            {
                App = "NotchWin", Title = $"Update {release.Tag} available", Body = firstLine.Length > 0 ? firstLine : "Click to see what's new",
                Handler = "notchwin:update",
            });
            if (_showNotifs) RenderNotifList();
        }
        return $"Version {release.Tag} is available.";
    }

    private void ShowUpdateWindow()
    {
        if (_availableUpdate == null) return;
        if (_updateWindow != null) { _updateWindow.Activate(); return; }
        var release = _availableUpdate;
        _updateWindow = new UpdateWindow(release, Updater.Current,
            install: () => InstallUpdateAsync(release, waitForGames: false),
            skip: () => { _cfg.SkippedVersion = release.Tag; _cfg.Save(); });
        _updateWindow.Closed += (s, e) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    private async Task InstallUpdateAsync(Updater.Release release, bool waitForGames)
    {
        if (_updating) return;
        _updating = true;
        var green = Color.FromRgb(0x30, 0xD1, 0x58);
        try
        {
            Push(new Activity { Tag = "update", Sticky = true, Glyph = "\uE896", Accent = green,
                Title = $"Updating to {release.Tag}", Subtitle = "Downloading..." });
            var (appDir, pkgRoot) = await Updater.DownloadAsync(release);

            // Don't restart in the middle of a game.
            while (_hidden && waitForGames) await Task.Delay(TimeSpan.FromSeconds(30));

            Push(new Activity { Tag = "update", Sticky = true, Glyph = "\uE777", Accent = green,
                Title = $"Installing {release.Tag}", Subtitle = "NotchWin will be back in a few seconds." });
            await Task.Delay(2500);
            Updater.ApplyAndRestart(appDir, pkgRoot, release.Version);
            _updateWindow?.Close();
            _settings?.Close();
            Close();
        }
        catch (Exception ex)
        {
            _updating = false;
            Log.Write("Update failed: " + ex);
            Push(new Activity { Tag = "update", Glyph = "\uE7BA", Accent = Color.FromRgb(0xFF, 0x9F, 0x0A), Seconds = 6,
                Title = "Update failed", Subtitle = ex.Message });
            throw;
        }
    }

    // =====================================================================
    //  Position. Drag it anywhere:
    //   • drop it on the top or bottom edge of a screen → docks there as a notch
    //   • drop it on the left or right edge → docks there and turns vertical
    //   • drop it anywhere else → floats as a pill
    //  Whatever it opens (pop-ups, the dashboard) always grows towards the free space, so nothing gets cut off.
    // =====================================================================

    private string _dock = "top";        // top, bottom, left, right, float
    private bool Vertical => !_dragging && (_dock == "left" || _dock == "right");
    private double _ax, _ay;             // where the small pill's centre is (screen units)
    private Rect _mon, _work;            // the monitor it's on, and that monitor minus the taskbar
    private bool _growUp;                // floating in the lower half → opens upwards
    private double _lastCompactW, _lastCompactH;

    private void PositionWindow()
    {
        _dock = (_cfg.Dock ?? "top").ToLowerInvariant();
        if (_dock is not ("top" or "bottom" or "left" or "right" or "float")) _dock = "top";
        double cx = _cfg.PosX >= 0 ? _cfg.PosX : SystemParameters.PrimaryScreenWidth / 2;
        double cy = _cfg.PosY > 0 ? _cfg.PosY : SystemParameters.PrimaryScreenHeight / 2;
        PlaceAt(cx, cy, instant: false);
    }

    /// <summary>Puts the small pill's centre at (cx, cy), snapped to the docked edge, and moves the window around it.</summary>
    private void PlaceAt(double cx, double cy, bool instant)
    {
        (_mon, _work) = MonitorAt(cx, cy);
        var (cw, ch) = CompactSize();

        switch (_dock)
        {
            case "top": cy = _mon.Top + ch / 2; break;
            case "bottom": cy = _work.Bottom - ch / 2; break;
            case "left": cx = _work.Left + cw / 2; break;
            case "right": cx = _work.Right - cw / 2; break;
        }
        cx = Math.Clamp(cx, _mon.Left + cw / 2, Math.Max(_mon.Left + cw / 2, _mon.Right - cw / 2));
        cy = Math.Clamp(cy, _mon.Top + ch / 2, Math.Max(_mon.Top + ch / 2, _mon.Bottom - ch / 2));
        _ax = cx;
        _ay = cy;
        _growUp = _dock == "bottom" || (_dock == "float" && cy > (_mon.Top + _mon.Bottom) / 2);

        double w = Width, h = Height, left, top;
        switch (_dock)
        {
            case "left": left = _work.Left; top = cy - h / 2; break;
            case "right": left = _work.Right - w; top = cy - h / 2; break;
            case "top": left = cx - w / 2; top = _mon.Top; break;
            case "bottom": left = cx - w / 2; top = _work.Bottom - h; break;
            default: left = cx - w / 2; top = _growUp ? cy + ch / 2 + 24 - h : cy - ch / 2 - 12; break;
        }
        // Keep the whole window on this screen, so everything it opens stays visible.
        Left = Math.Clamp(left, _mon.Left, Math.Max(_mon.Left, _mon.Right - w));
        Top = Math.Clamp(top, _mon.Top, Math.Max(_mon.Top, _mon.Bottom - h));
        ApplyMode(force: true, instant: instant);
    }

    /// <summary>Where the pill sits inside its (bigger, invisible) window for a given size.</summary>
    private Thickness MarginFor(double w, double h)
    {
        double W = Width, H = Height;
        double x = _ax - Left, y = _ay - Top; // anchor in window coordinates
        var (_, ch) = CompactSize();
        double l, t;
        switch (_dock)
        {
            case "left": l = 0; t = y - h / 2; break;
            case "right": l = W - w; t = y - h / 2; break;
            case "top": l = x - w / 2; t = y - ch / 2; break;
            case "bottom": l = x - w / 2; t = y + ch / 2 - h; break;
            default:
                l = x - w / 2;
                t = _growUp ? y + ch / 2 - h : y - ch / 2;
                break;
        }
        return new Thickness(Math.Clamp(l, 0, Math.Max(0, W - w)), Math.Clamp(t, 0, Math.Max(0, H - h)), 0, 0);
    }

    /// <summary>The monitor (and its area without the taskbar) under a point, in WPF units.</summary>
    private (Rect mon, Rect work) MonitorAt(double xDip, double yDip)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var pt = new Native.POINT { X = (int)(xDip * dpi.DpiScaleX), Y = (int)(yDip * dpi.DpiScaleY) };
            var handle = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(handle, ref mi))
            {
                Rect R(Native.RECT r) => new(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY,
                                             (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
                return (R(mi.rcMonitor), R(mi.rcWork));
            }
        }
        catch { }
        var all = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        return (all, SystemParameters.WorkArea);
    }

    /// <summary>Size of the small pill: wide when horizontal, tall when docked to a side.</summary>
    private (double w, double h) CompactSize() =>
        Vertical ? (CompactHeight + 4, VerticalLengthNow()) : (CompactWidthNow(), CompactHeight);

    private double VerticalLengthNow()
    {
        double len = 118;
        if (_cfg.ShowWeatherInPill && _weather?.Current != null) len += 34;
        if (_stream != null && (_stream.IsStreaming || _stream.IsRecording || _stream.MicInUse || _stream.CameraInUse)) len += 18;
        if (_unread > 0) len += 26;
        return len;
    }

    private void Pill_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDown = true;
        _dragging = false;
        Native.GetCursorPos(out _dragStartCursor);
        // Don't capture the mouse yet: a plain click must still reach the notification cards etc.
    }

    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // The button was released somewhere we didn't see (e.g. on a card that handled the click).
            _mouseDown = false;
            if (_dragging) { _dragging = false; Pill.ReleaseMouseCapture(); PositionWindow(); }
            return;
        }
        Native.GetCursorPos(out var cur);
        int dx = cur.X - _dragStartCursor.X, dy = cur.Y - _dragStartCursor.Y;
        if (!_dragging)
        {
            if (Math.Abs(dx) < 6 && Math.Abs(dy) < 6) return;
            _dragging = true;
            _hoverIn.Stop();
            _hoverOut.Stop();
            _hovering = false;
            _dock = "float";      // carried around as a small floating pill
            Pill.CaptureMouse();  // follow the mouse even if it outruns the pill
            UpdateMode();
        }

        // The pill follows the cursor.
        var dpi = VisualTreeHelper.GetDpi(this);
        PlaceAt(cur.X / dpi.DpiScaleX, cur.Y / dpi.DpiScaleY, instant: true);
    }

    private void Pill_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseDown) return;
        _mouseDown = false;
        if (Pill.IsMouseCaptured) Pill.ReleaseMouseCapture();

        if (_dragging)
        {
            _dragging = false;
            // Close to an edge? Dock there. Otherwise stay floating where it was dropped.
            var (cw, ch) = (CompactWidthNow(), CompactHeight);
            var gaps = new (string dock, double gap)[]
            {
                ("top", _ay - ch / 2 - _mon.Top),
                ("bottom", _work.Bottom - (_ay + ch / 2)),
                ("left", _ax - cw / 2 - _work.Left),
                ("right", _work.Right - (_ax + cw / 2)),
            };
            var nearest = gaps.OrderBy(g => g.gap).First();
            _cfg.Dock = nearest.gap < 40 ? nearest.dock : "float";
            _cfg.PosX = Math.Round(_ax);
            _cfg.PosY = Math.Round(_ay);
            _cfg.Save();
            PositionWindow();
            return;
        }

        // A simple click: dismiss a pop-up, or open the notifications if the bell is showing.
        if (_mode == Mode.Activity && _activity != null)
        {
            var onClick = _activity.OnClick;
            NextActivity();
            onClick?.Invoke();
        }
        else if (_mode == Mode.Compact && _unread > 0) OpenNotifications();
    }

    private void ResetPosition()
    {
        _cfg.Dock = "top";
        _cfg.PosX = -1;
        _cfg.PosY = 0;
        _cfg.Save();
        PositionWindow();
    }

    /// <summary>
    /// The window is bigger than the notch (so it has room to grow). Make every part of it except the
    /// notch itself completely click-through, so the browser tabs or buttons under it keep working.
    /// </summary>
    private void UpdateClickThrough()
    {
        bool through;
        if (_hidden) through = true;
        else if (_mouseDown || Pill.ContextMenu?.IsOpen == true) through = false;
        else
        {
            try
            {
                Native.GetCursorPos(out var p);
                var tl = Pill.PointToScreen(new Point(0, 0));
                var br = Pill.PointToScreen(new Point(Pill.ActualWidth, Pill.ActualHeight));
                through = !(p.X >= tl.X - 1 && p.X <= br.X + 1 && p.Y >= tl.Y - 1 && p.Y <= br.Y + 1);
            }
            catch { through = false; }
        }
        if (through == _clickThrough) return;
        _clickThrough = through;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex = through ? ex | Native.WS_EX_TRANSPARENT : ex & ~(long)Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Rounded on the open sides, flat against the screen edge it's docked to.</summary>
    private CornerRadius Corners(double r) => _dock switch
    {
        "top" => new CornerRadius(0, 0, r, r),
        "bottom" => new CornerRadius(r, r, 0, 0),
        "left" => new CornerRadius(0, r, r, 0),
        "right" => new CornerRadius(r, 0, 0, r),
        _ => new CornerRadius(r),
    };

    private void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (_cfg.RunOnStartup) key.SetValue("NotchWin", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("NotchWin", false);
        }
        catch (Exception ex) { Log.Write("Startup registration: " + ex.Message); }

        if (_cfg.RunOnStartup && StartupDisabledInTaskManager())
        {
            Dispatcher.BeginInvoke(() => Push(new Activity
            {
                Glyph = "\uE7BA", Accent = Color.FromRgb(0xFF, 0x9F, 0x0A), Seconds = 12,
                Title = "Windows is blocking NotchWin at startup",
                Subtitle = "Open Task Manager → Startup apps, right-click NotchWin → Enable.",
            }), DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Task Manager's "Startup apps" switch is stored separately; first byte 3 means "Disabled".</summary>
    private static bool StartupDisabledInTaskManager()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return k?.GetValue("NotchWin") is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
        }
        catch { return false; }
    }

    // =====================================================================
    //  Voice assistant
    // =====================================================================

    private void StartVoice()
    {
        _voice = new VoiceAssistant(_cfg);
        _voice.StatusChanged += (state, detail) => Dispatcher.BeginInvoke(() => OnVoiceStatus(state, detail));
        _voice.CommandHeard += cmd => _ = Task.Run(() => RunCommandAsync(cmd));
        _voice.Start();
    }

    private bool _voiceWasDownloading;

    private void OnVoiceStatus(VoiceAssistant.State state, string detail)
    {
        var cyan = Color.FromRgb(0x64, 0xD2, 0xFF);
        switch (state)
        {
            case VoiceAssistant.State.Downloading:
                _voiceWasDownloading = true;
                Push(new Activity { Tag = "voice", Sticky = true, Glyph = "\uE896", Accent = cyan,
                    Title = "Setting up the voice assistant", Subtitle = detail ?? "Downloading..." });
                break;

            case VoiceAssistant.State.Listening:
                Push(new Activity { Tag = "voice", Sticky = true, Glyph = "\uE720", Accent = cyan,
                    Title = "Listening...",
                    Subtitle = !string.IsNullOrEmpty(detail) ? detail
                        : _voice != null && _voice.UsesCloudSpeech
                        ? "Speak naturally. I'll stop listening when you pause."
                        : "Try \"play lofi beats\", \"next song\", \"open discord\"" });
                break;

            case VoiceAssistant.State.Thinking:
                Push(new Activity { Tag = "voice", Sticky = true, Glyph = "\uE720", Accent = cyan,
                    Title = string.IsNullOrEmpty(detail) ? "Got it..." : $"“{detail}”",
                    Subtitle = string.IsNullOrEmpty(detail) ? "Working out what you said" : "On it..." });
                break;

            case VoiceAssistant.State.Idle:
                if (_activity?.Tag == "voice" && _activity.Sticky) RemoveTagged("voice");
                if (_voiceWasDownloading)
                {
                    _voiceWasDownloading = false;
                    string wake = _cfg.WakeWords.FirstOrDefault() ?? "computer";
                    Push(new Activity { Tag = "voice", Glyph = "\uE720", Accent = cyan, Seconds = 5,
                        Title = "Voice assistant ready", Subtitle = $"Say \"{wake}\" or press Ctrl+Alt+Space." });
                }
                break;

            case VoiceAssistant.State.Off:
                if (!string.IsNullOrEmpty(detail))
                    Push(new Activity { Tag = "voice", Glyph = "\uE7BA", Accent = Color.FromRgb(0xFF, 0x9F, 0x0A),
                        Seconds = 8, Title = "Voice assistant is off", Subtitle = detail });
                break;
        }
    }

    private async Task RunCommandAsync(string cmd)
    {
        string reply;
        try { reply = await _router.HandleAsync(cmd); }
        catch (Exception ex) { Log.Write("Command: " + ex); reply = "Something went wrong."; }

        await Dispatcher.InvokeAsync(() => Push(new Activity
        {
            Tag = "voice", Glyph = "\uE720", Accent = Color.FromRgb(0x64, 0xD2, 0xFF),
            Title = $"“{cmd}”", Subtitle = reply,
            Seconds = Math.Clamp(3 + reply.Length / 14.0, 4, 14),
        }));
        _voice?.Speak(reply);
        _voice?.Done();
        // We asked a question ("Did you mean Riot Client?"): listen for the answer, no wake word needed.
        if (_router.ExpectsAnswer)
            _voice?.ListenAfterSpeaking(reply.StartsWith("Did you mean") ? reply + " Say yes or no." : reply);
    }

    // =====================================================================
    //  The 2x-a-second refresh
    // =====================================================================

    private void OnTick()
    {
        _tickCount++;
        var now = DateTime.Now;

        // Hide for games / fullscreen.
        bool hide = _games != null && _games.ShouldHide(_hwnd);
        if (hide != _hidden) SetHidden(hide);

        // Keep on top of other "always on top" windows.
        if (_tickCount % 4 == 0)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        // Activity timeout.
        if (_activity != null && now > _activityUntil) NextActivity();

        if (_stats != null) _stats.Active = _mode == Mode.Expanded && !_hidden;

        UpdateIndicators();
        UpdateCompact(now);

        if (_mode == Mode.Expanded) RefreshExpanded(now);
    }

    private void RefreshExpanded(DateTime now)
    {
        BigTime.Text = now.ToString("h:mm tt");
        DateText.Text = now.ToString("dddd, d MMM");
        UpdateStats();
        UpdateWeather();
        UpdateProgress();
    }

    private void SetHidden(bool hide)
    {
        _hidden = hide;
        Root.IsHitTestVisible = !hide;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(hide ? 0 : 1, TimeSpan.FromMilliseconds(hide ? 150 : 300)));
        if (hide)
        {
            _hovering = false;
            UpdateMode();
        }
    }

    private void UpdateCompact(DateTime now)
    {
        var m = _mediaInfo;
        bool showMedia = _cfg.ShowSongInPill && m != null && m.IsPlaying;
        string temp = _cfg.ShowWeatherInPill && _weather?.Current != null ? $"  ·  {_weather.Current.Icon} {Math.Round(_weather.Current.Temp)}°" : "";
        CompactText.Text = showMedia ? m.Title : now.ToString("h:mm tt") + temp;
        CompactArt.Visibility = showMedia && m.Thumbnail != null ? Visibility.Visible : Visibility.Collapsed;
        CompactIcon.Visibility = CompactArt.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        CompactIcon.Text = showMedia ? (m.IsVideo ? "\uE714" : "\uE8D6") : "\uE823";
        // Animations cost a little CPU, so only run them while you can actually see them.
        SetEqualizer(showMedia && _cfg.ShowEqualizer && _mode == Mode.Compact && !_hidden && !Vertical);

        // Vertical pill (docked to a side): time stacked, art on top, weather and dots below.
        VHour.Text = now.ToString("%h");
        VMinute.Text = now.ToString("mm");
        VAmPm.Text = now.ToString("tt");
        VArt.Visibility = CompactArt.Visibility;
        VIcon.Visibility = CompactIcon.Visibility;
        VIcon.Text = CompactIcon.Text;
        VWeather.Text = _cfg.ShowWeatherInPill && _weather?.Current != null ? $"{_weather.Current.Icon}\n{Math.Round(_weather.Current.Temp)}°" : "";
        VWeather.Visibility = VWeather.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_mode == Mode.Compact && !_dragging)
        {
            var (w, h) = CompactSize();
            if (Math.Abs(w - _lastCompactW) > 0.5 || Math.Abs(h - _lastCompactH) > 0.5) AnimateSize(w, h);
        }
    }

    private double CompactWidthNow()
    {
        double w = _cfg.CompactWidth;
        if (_cfg.ShowSongInPill && _mediaInfo != null && _mediaInfo.IsPlaying) w = Math.Max(w, 300);
        if (_stream != null && (_stream.IsStreaming || _stream.IsRecording || _stream.MicInUse || _stream.CameraInUse)) w += 20;
        if (_unread > 0) w += 24;
        return w;
    }

    private void UpdateIndicators()
    {
        if (_stream == null) return;
        bool live = _stream.IsStreaming, rec = _stream.IsRecording;

        LiveDot.Visibility = live || rec ? Visibility.Visible : Visibility.Collapsed;
        bool pulse = (live || rec) && !_hidden;
        if (pulse != _liveAnimRunning)
        {
            _liveAnimRunning = pulse;
            AnimationTimeline anim = null;
            if (pulse)
            {
                anim = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(anim, 20); // a slow pulse doesn't need 60 fps
            }
            LiveDot.BeginAnimation(OpacityProperty, anim);
        }
        LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        RecBadge.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Visibility = _stream.MicInUse ? Visibility.Visible : Visibility.Collapsed;
        CamDot.Visibility = _stream.CameraInUse ? Visibility.Visible : Visibility.Collapsed;
        VLive.Visibility = LiveDot.Visibility;
        VMic.Visibility = MicDot.Visibility;
        VCam.Visibility = CamDot.Visibility;
        MicBadge.Visibility = _stream.MicInUse ? Visibility.Visible : Visibility.Collapsed;
        MicBadgeText.Text = "Mic · " + _stream.MicApp;

        var red = Color.FromRgb(0xFF, 0x3B, 0x30);
        if (live && !_wasStreaming) Push(new Activity { Glyph = "\uE714", Accent = red, Title = "You're live", Subtitle = "Streaming from OBS", Seconds = 4 });
        if (!live && _wasStreaming) Push(new Activity { Glyph = "\uE714", Accent = red, Title = "Stream ended", Subtitle = "Nice stream!", Seconds = 4 });
        if (rec && !_wasRecording) Push(new Activity { Glyph = "\uE7C8", Accent = red, Title = "Recording", Subtitle = "OBS is recording", Seconds = 3 });
        _wasStreaming = live;
        _wasRecording = rec;
    }

    private void UpdateStats()
    {
        var s = _stats?.Current;
        if (s == null) return;
        SetBar(CpuBar, CpuTrack, s.Cpu, _theme.Accent1);
        CpuText.Text = $"{s.Cpu:0}%";
        SetBar(RamBar, RamTrack, s.Ram, _theme.Accent2);
        RamText.Text = $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB";
        if (s.Gpu >= 0)
        {
            SetBar(GpuBar, GpuTrack, s.Gpu, _theme.Accent3);
            GpuText.Text = s.GpuTemp != null ? $"{s.Gpu:0}% · {s.GpuTemp:0}°" : $"{s.Gpu:0}%";
        }
        else GpuText.Text = s.GpuTemp != null ? $"{s.GpuTemp:0}°C" : "n/a";

        NetIcon.Text = !s.Online ? "\uE7BA" : s.NetworkName == "Wi-Fi" ? "\uE701" : "\uE839";
        NetSpeed.Text = $"↓ {SystemStatsService.FormatSpeed(s.DownBytesPerSec)}   ↑ {SystemStatsService.FormatSpeed(s.UpBytesPerSec)}";
        NetInfo.Text = !s.Online ? "Offline" : s.PingMs >= 0 ? $"{s.NetworkName} · {s.PingMs} ms ping" : s.NetworkName;
    }

    private static void SetBar(System.Windows.Controls.Border bar, FrameworkElement track, double pct, Color baseColor)
    {
        double w = Math.Max(0, track.ActualWidth * Math.Clamp(pct, 0, 100) / 100.0);
        bar.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(400)));
        // Turn orange/red when things get busy.
        var c = pct >= 90 ? Color.FromRgb(0xFF, 0x45, 0x3A) : pct >= 75 ? Color.FromRgb(0xFF, 0x9F, 0x0A) : baseColor;
        bar.Background = new SolidColorBrush(c);
    }

    private void UpdateWeather()
    {
        var w = _weather?.Current;
        if (w == null)
        {
            WeatherTemp.Text = "";
            WeatherDesc.Text = "Getting weather...";
            return;
        }
        WeatherIcon.Text = w.Icon;
        WeatherTemp.Text = $"{Math.Round(w.Temp)}{w.Unit}  {w.City}";
        WeatherDesc.Text = $"{w.Description} · feels {Math.Round(w.FeelsLike)}° · {w.Humidity}% humidity";
    }

    // =====================================================================
    //  Media
    // =====================================================================

    private void OnMediaChanged(MediaInfo info)
    {
        var prev = _mediaInfo;
        _mediaInfo = info;
        _mediaStamp = DateTime.Now;

        if (info == null)
        {
            MediaSource.Text = "";
            MediaTitle.Text = "Nothing playing";
            MediaArtist.Text = "Play something on Spotify, YouTube...";
            BigArt.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Track");
            BigArtPlaceholder.Visibility = Visibility.Visible;
            PlayButton.Content = "\uE768";
            _shownThumb = null;
            UpdateCompact(DateTime.Now);
            return;
        }

        MediaSource.Text = (info.IsVideo || info.Source == "YouTube" ? "Watching · " : "Now playing · ") + info.Source;
        MediaTitle.Text = info.Title;
        MediaArtist.Text = info.Artist;
        PlayButton.Content = info.IsPlaying ? "\uE769" : "\uE768";

        if (!ReferenceEquals(info.Thumbnail, _shownThumb))
        {
            _shownThumb = info.Thumbnail;
            var img = ToImage(info.Thumbnail);
            // Videos get a wide 16:9 thumbnail, songs a square cover.
            bool wide = img != null && img.PixelHeight > 0 && img.PixelWidth / (double)img.PixelHeight > 1.3;
            BigArt.Width = wide ? 160 : 100;
            BigArt.Height = wide ? 90 : 100;
            if (_theme.Id == "vibe") ApplyVibe(img);
            if (img != null)
            {
                BigArt.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                CompactArt.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                VArt.Background = CompactArt.Background;
                BigArtPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                BigArt.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Track");
                BigArtPlaceholder.Visibility = Visibility.Visible;
            }
        }

        // Song changed while you weren't looking: a quick "now playing" pop.
        string track = info.Title + "|" + info.Artist;
        if (_cfg.ShowSongPopups && info.IsPlaying && prev != null && _lastTrack != null && track != _lastTrack && _mode != Mode.Expanded && !_hidden)
        {
            Push(new Activity
            {
                Tag = "media", Glyph = info.IsVideo ? "\uE714" : "\uE8D6", Accent = Color.FromRgb(0xFF, 0x37, 0x5F),
                Title = info.Title, Subtitle = string.IsNullOrEmpty(info.Artist) ? info.Source : $"{info.Artist} · {info.Source}", Seconds = 4,
            });
        }
        _lastTrack = track;
        UpdateCompact(DateTime.Now);
    }

    private void UpdateProgress()
    {
        var m = _mediaInfo;
        if (m == null || m.Duration.TotalSeconds < 1) { MediaProgress.Width = 0; return; }
        var pos = m.Position + (m.IsPlaying ? DateTime.Now - _mediaStamp : TimeSpan.Zero);
        double frac = Math.Clamp(pos.TotalSeconds / m.Duration.TotalSeconds, 0, 1);
        MediaProgress.Width = ProgressTrack.ActualWidth * frac;
    }

    private static BitmapImage ToImage(byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var bi = new BitmapImage();
            using var ms = new MemoryStream(data);
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 200;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private void SetEqualizer(bool on)
    {
        Equalizer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on == _eqRunning) return;
        _eqRunning = on;
        var bars = new[] { Bar1, Bar2, Bar3 };
        var durations = new[] { 420, 300, 520 };
        for (int i = 0; i < bars.Length; i++)
        {
            var st = (ScaleTransform)bars[i].RenderTransform;
            if (on)
            {
                var anim = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(durations[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Timeline.SetDesiredFrameRate(anim, 24);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
            else
            {
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                st.ScaleY = 0.35;
            }
        }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.PlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    // =====================================================================
    //  Activities (the little pop-ups)
    // =====================================================================

    private void Push(Activity a)
    {
        // Same kind of activity already showing (e.g. voice states)? Replace it in place.
        if (a.Tag != null && _activity?.Tag == a.Tag)
        {
            Begin(a);
            return;
        }
        if (a.Tag != null)
        {
            var keep = _queue.Where(q => q.Tag != a.Tag).ToList();
            _queue.Clear();
            foreach (var q in keep) _queue.Enqueue(q);
        }

        // Voice always jumps the queue.
        if (_activity == null || a.Tag == "voice") Begin(a);
        else if (_queue.Count < 4) _queue.Enqueue(a);
    }

    private void Begin(Activity a)
    {
        _activity = a;
        _activityUntil = a.Sticky ? DateTime.MaxValue : DateTime.Now.AddSeconds(a.Seconds);
        ActivityIcon.Text = a.Glyph;
        ActivityIconBg.Background = new SolidColorBrush(a.Accent);
        ActivityTitle.Text = a.Title;
        ActivitySubtitle.Text = a.Subtitle ?? "";
        ActivitySubtitle.Visibility = string.IsNullOrEmpty(a.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        UpdateMode(force: _mode == Mode.Activity);
    }

    private void NextActivity()
    {
        var ended = _activity;
        _activity = null;
        try { ended?.OnEnd?.Invoke(); } catch { }
        if (_queue.Count > 0) Begin(_queue.Dequeue());
        else UpdateMode();
    }

    private void RemoveTagged(string tag)
    {
        var keep = _queue.Where(q => q.Tag != tag).ToList();
        _queue.Clear();
        foreach (var q in keep) _queue.Enqueue(q);
        if (_activity?.Tag == tag) NextActivity();
    }

    private void Activity_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_activity != null) NextActivity();
    }

    // =====================================================================
    //  Themes
    // =====================================================================

    /// <summary>Restyles the whole notch instantly. Also used by Settings for live preview.</summary>
    public void ApplyTheme(string id)
    {
        _theme = Theme.Find(id);
        foreach (var kv in _theme.MakeBrushes()) Resources[kv.Key] = kv.Value;

        _bgBrush = _theme.MakeBackground();
        Resources["NotchBg"] = _bgBrush;
        Pill.BorderThickness = new Thickness(_theme.BorderThickness);

        Shadow.Visibility = _theme.Shadow ? Visibility.Visible : Visibility.Hidden;
        if (Shadow.Effect is System.Windows.Media.Effects.DropShadowEffect fx)
        {
            fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, null); // stop any Vibe fade
            fx.Color = _theme.Glow;
            fx.Opacity = _theme.GlowOpacity;
            fx.BlurRadius = _theme.GlowOpacity > 0.7 ? 26 : 22;
            fx.ShadowDepth = _theme.GlowOpacity > 0.7 ? 0 : 4; // glows sit evenly around the edge
        }

        if (_theme.Id == "vibe" && _shownThumb != null) ApplyVibe(ToImage(_shownThumb));
        if (_mode == Mode.Expanded) UpdateStats();
    }

    /// <summary>Vibe theme: fade the notch into the colours of the current cover art.</summary>
    private void ApplyVibe(BitmapSource img)
    {
        var baseTheme = Theme.Find("vibe");
        Color top = baseTheme.Bg1, bottom = baseTheme.Bg2, accent = baseTheme.Accent3;
        var dom = img != null ? Theme.DominantColor(img) : null;
        if (dom != null)
        {
            top = Theme.Shade(dom.Value, 0.06);
            bottom = Theme.Shade(dom.Value, 0.32);
            accent = Theme.Shade(dom.Value, 0.95);
        }

        var dur = TimeSpan.FromMilliseconds(900);
        if (_bgBrush != null && !_bgBrush.IsFrozen && _bgBrush.GradientStops.Count == 2)
        {
            _bgBrush.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(top, dur));
            _bgBrush.GradientStops[1].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(bottom, dur));
        }
        Resources["Eq"] = new SolidColorBrush(accent);
        if (Shadow.Effect is System.Windows.Media.Effects.DropShadowEffect fx)
            fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, new ColorAnimation(accent, dur));
    }

    // =====================================================================
    //  Notifications: a short pop-up, then it tucks into the bell
    // =====================================================================

    private void OnNotification(NotificationItem n)
    {
        _notifs.Insert(0, n);
        if (_notifs.Count > 30) _notifs.RemoveAt(_notifs.Count - 1);
        if (_showNotifs) RenderNotifList();

        if (_hidden)
        {
            // Gaming: no pop-up at all, it just waits in the bell.
            _unread++;
            UpdateBell();
            return;
        }

        // Watching a video / fullscreen app: keep the pop-up really short.
        bool watching = (_mediaInfo != null && _mediaInfo.IsPlaying && (_mediaInfo.IsVideo || _mediaInfo.Source == "YouTube"))
                        || GameWatcher.IsForegroundFullscreen(_hwnd);
        Push(new Activity
        {
            Glyph = "\uE8BD", Accent = Color.FromRgb(0x0A, 0x84, 0xFF),
            Title = $"{n.App} · {n.Title}", Subtitle = n.Body,
            Seconds = Math.Max(0.8, watching ? _cfg.NotificationPopupSecondsWhileWatching : _cfg.NotificationPopupSeconds),
            OnEnd = () =>
            {
                _unread++;
                UpdateBell();
                BounceBell();
            },
        });
    }

    private void UpdateBell()
    {
        string count = _unread > 9 ? "9+" : _unread.ToString();
        BellPanel.Visibility = _unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        BellCount.Text = count;
        VBell.Visibility = BellPanel.Visibility;
        VBellCount.Text = count;
        HeaderBadge.Visibility = _unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderBadgeCount.Text = count;
        if (_mode == Mode.Compact) { var (w, h) = CompactSize(); AnimateSize(w, h); }
    }

    /// <summary>A little "ding" wiggle as the notification lands in the bell.</summary>
    private void BounceBell()
    {
        var grow = new DoubleAnimation(1, 1.45, TimeSpan.FromMilliseconds(140))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            BeginTime = TimeSpan.FromMilliseconds(250), // after the pill has shrunk back
        };
        BellScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        BellScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void OpenNotifications()
    {
        ShowNotifPage(true);
        _hoverIn.Stop();
        _hovering = true; // stays open until the mouse leaves
        UpdateMode();
    }

    private void ShowNotifPage(bool show)
    {
        _showNotifs = show;
        NotifPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MainPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ShortcutsBar.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            _unread = 0;
            UpdateBell();
            RenderNotifList();
        }
    }

    private void Bell_Click(object sender, RoutedEventArgs e) => ShowNotifPage(!_showNotifs);

    private void ClearNotifs_Click(object sender, RoutedEventArgs e)
    {
        _notifs.Clear();
        _unread = 0;
        UpdateBell();
        RenderNotifList();
    }

    private void RenderNotifList()
    {
        NotifList.Children.Clear();
        if (_notifs.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "You're all caught up.",
                Margin = new Thickness(0, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            NotifList.Children.Add(empty);
            return;
        }

        foreach (var n in _notifs)
        {
            var item = n;
            var head = new DockPanel();
            var app = new TextBlock { Text = item.App, FontSize = 11 };
            app.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            var time = new TextBlock { Text = Ago(item.Time), FontSize = 11 };
            time.SetResourceReference(TextBlock.ForegroundProperty, "FgFaint");
            DockPanel.SetDock(time, Dock.Right);
            head.Children.Add(time);
            head.Children.Add(app);

            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            if (!string.IsNullOrEmpty(item.Body))
            {
                var bodyText = new TextBlock { Text = item.Body, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis };
                bodyText.SetResourceReference(TextBlock.ForegroundProperty, "FgSoft");
                body.Children.Add(bodyText);
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = body,
                Cursor = Cursors.Hand,
                ToolTip = "Open " + item.App,
            };
            card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "TileBg");
            card.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenNotificationApp(item); };
            NotifList.Children.Add(card);
        }
    }

    /// <summary>Clicking a notification opens the app that sent it (works for Store apps like WhatsApp and most others).</summary>
    private void OpenNotificationApp(NotificationItem n)
    {
        if (n.Handler == "notchwin:update") { ShowUpdateWindow(); return; }
        if (string.IsNullOrWhiteSpace(n.Handler)) return;
        CommandRouter.Open("explorer.exe", $"shell:AppsFolder\\{n.Handler}");
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h";
        return t.ToString("d MMM");
    }

    // =====================================================================
    //  Shape-shifting
    // =====================================================================

    private void UpdateMode(bool force = false)
    {
        Mode target = _hidden || _dragging ? Mode.Compact
                    : _hovering ? Mode.Expanded
                    : _activity != null ? Mode.Activity
                    : Mode.Compact;
        if (target == _mode && !force) return;
        if (target != Mode.Expanded && _showNotifs) ShowNotifPage(false);
        _mode = target;
        ApplyMode(force);
    }

    private void ApplyMode(bool force = false, bool instant = false)
    {
        switch (_mode)
        {
            case Mode.Compact:
                ShowView(Vertical ? CompactViewV : CompactView);
                var (cw, ch) = CompactSize();
                Pill.CornerRadius = Corners(Math.Min(cw, ch) / 2);
                _trim.Interval = TimeSpan.FromSeconds(8);
                _trim.Stop();
                _trim.Start();
                AnimateSize(cw, ch, instant);
                break;

            case Mode.Activity:
                ShowView(ActivityView);
                bool tall = (_activity?.Subtitle?.Length ?? 0) > 52;
                Pill.CornerRadius = Corners(26);
                AnimateSize(ActivityWidth, tall ? 96 : 66, instant);
                break;

            case Mode.Expanded:
                RefreshExpanded(DateTime.Now); // fresh numbers before it opens
                ShowView(ExpandedView);
                Pill.CornerRadius = Corners(32);
                AnimateSize(ExpandedWidth, ExpandedHeight, instant);
                break;
        }
    }

    private void ShowView(FrameworkElement view)
    {
        foreach (var v in new FrameworkElement[] { CompactView, CompactViewV, ActivityView, ExpandedView })
        {
            if (ReferenceEquals(v, view))
            {
                if (v.Visibility == Visibility.Visible && v.Opacity > 0.99) continue;
                v.Visibility = Visibility.Visible;
                v.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                {
                    BeginTime = TimeSpan.FromMilliseconds(110),
                });
            }
            else
            {
                v.BeginAnimation(OpacityProperty, null);
                v.Opacity = 0;
                v.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Grows/shrinks the pill. It also slides inside its window, so it always opens towards free space.</summary>
    private void AnimateSize(double w, double h, bool instant = false)
    {
        if (_mode == Mode.Compact) { _lastCompactW = w; _lastCompactH = h; }
        var margin = MarginFor(w, h);
        if (instant)
        {
            Pill.BeginAnimation(WidthProperty, null);
            Pill.BeginAnimation(HeightProperty, null);
            Pill.BeginAnimation(MarginProperty, null);
            Pill.Width = w;
            Pill.Height = h;
            Pill.Margin = margin;
            return;
        }
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };
        var dur = TimeSpan.FromMilliseconds(420);
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
        // The position moves with exactly the same timing, so the docked edge / anchor stays put.
        Pill.BeginAnimation(MarginProperty, new ThicknessAnimation(margin, dur) { EasingFunction = ease });
    }

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverOut.Stop();
        if (!_hovering) _hoverIn.Start();
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverIn.Stop();
        if (Pill.ContextMenu?.IsOpen == true) return;
        if (_hovering) _hoverOut.Start();
    }

    // =====================================================================
    //  Shortcuts & menu
    // =====================================================================

    private void BuildShortcuts()
    {
        ShortcutsBar.Items.Clear();
        foreach (var s in _cfg.Shortcuts)
        {
            var item = s;
            if (CommandRouter.IsMusicTile(item))
                ShortcutsBar.Items.Add(MakeTile(item.Icon, item.Name, () => CommandRouter.OpenMusicApp(_cfg)));
            else
                ShortcutsBar.Items.Add(MakeTile(item.Icon, item.Name, () => CommandRouter.Open(item.Target, item.Args)));
        }
        ShortcutsBar.Items.Add(MakeTile("E720", "Ask", () => _voice?.ListenNow()));
        ShortcutsBar.Items.Add(MakeTile("E713", "Notch", OpenSettings));
    }

    private Button MakeTile(string icon, string label, Action onClick)
    {
        bool isGlyph = Regex.IsMatch(icon ?? "", "^[0-9A-Fa-f]{4,5}$");
        var iconText = new TextBlock
        {
            Text = isGlyph ? char.ConvertFromUtf32(Convert.ToInt32(icon, 16)) : (icon ?? ""),
            FontFamily = isGlyph ? (FontFamily)FindResource("Icons") : new FontFamily("Segoe UI Emoji"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 62,
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "FgSoft");
        var stack = new StackPanel();
        stack.Children.Add(iconText);
        stack.Children.Add(labelText);

        var btn = new Button { Style = (Style)FindResource("Tile"), Content = stack, ToolTip = label };
        btn.Click += (s, e) => onClick();
        return btn;
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var settings = new MenuItem { Header = "Settings" };
        settings.Click += (s, e) => OpenSettings();
        menu.Items.Add(settings);

        var ask = new MenuItem { Header = "Ask assistant   (Ctrl+Alt+Space)" };
        ask.Click += (s, e) => _voice?.ListenNow();
        menu.Items.Add(ask);

        // "Treat <the app you're in> as a game" — for games the auto-detection misses.
        var markGame = new MenuItem();
        menu.Items.Add(markGame);
        (uint pid, string path) fgApp = (0, null);
        // Remember which app was in front at the moment you right-clicked (before the menu takes over).
        Pill.PreviewMouseRightButtonDown += (s, e) => fgApp = GameWatcher.ForegroundApp();
        menu.Opened += (s, e) =>
        {
            bool usable = fgApp.path != null && !fgApp.path.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase);
            markGame.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
            if (usable) markGame.Header = $"Treat \u201C{Path.GetFileName(fgApp.path)}\u201D as a game (hide for it)";
        };
        markGame.Click += (s, e) =>
        {
            if (fgApp.path == null || _games == null) return;
            _games.MarkAsGame(fgApp.pid, fgApp.path);
            Push(new Activity { Glyph = "\uE7FC", Accent = Color.FromRgb(0xBF, 0x5A, 0xF2), Seconds = 4,
                Title = "Got it", Subtitle = $"The notch will hide while {Path.GetFileNameWithoutExtension(fgApp.path)} is in front." });
        };

        menu.Items.Add(new Separator());

        var resetPos = new MenuItem { Header = "Move back to top centre" };
        resetPos.Click += (s, e) => ResetPosition();
        menu.Items.Add(resetPos);

        var restart = new MenuItem { Header = "Restart" };
        restart.Click += (s, e) => Restart();
        menu.Items.Add(restart);

        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (s, e) => Close();
        menu.Items.Add(quit);

        menu.Closed += (s, e) => { if (!Pill.IsMouseOver) _hoverOut.Start(); };
        Pill.ContextMenu = menu;
    }

    /// <summary>Opens the Settings window (or brings it to the front). Saving restarts the notch.</summary>
    public void OpenSettings()
    {
        if (_settings != null)
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(onSaved: Restart, previewTheme: ApplyTheme, checkUpdates: () => CheckForUpdatesAsync(manual: true));
        _settings.Closed += (s, e) =>
        {
            _settings = null;
            ApplyTheme(_cfg.Theme); // undo any theme you only previewed (Save restarts with the new one anyway)
        };
        _settings.Show();
        _settings.Activate();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath) { Arguments = "--restart", UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Restart: " + ex.Message); }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        try { Native.UnregisterHotKey(_hwnd, HotkeyId); } catch { }
        _voice?.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
