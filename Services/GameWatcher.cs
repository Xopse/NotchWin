using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace NotchWin.Services;

/// <summary>
/// Knows which running programs are games, announces new ones, and tells the notch when to hide.
/// A program counts as a game if:
///   • Windows Game Bar knows it as a game, or
///   • its exe lives in a game library (Steam, Epic, Xbox, Riot, GOG, EA, Ubisoft, any \Games\ folder), or
///   • you added it yourself (Settings → Games, or right-click the notch → "Treat ... as a game").
/// </summary>
public class GameWatcher
{
    private readonly AppConfig _cfg;
    private HashSet<uint> _known = new();
    private readonly Dictionary<uint, string> _runningGames = new();
    private readonly Dictionary<string, DateTime> _announced = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _gameBarGames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _gameBarLoaded = DateTime.MinValue;
    private readonly object _lock = new();

    /// <summary>Raised with the game's name when a new game process starts.</summary>
    public event Action<string> GameLaunched;

    public GameWatcher(AppConfig cfg) => _cfg = cfg;

    public void Start()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "Games", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    private void Loop()
    {
        // First pass: learn what's already running (so a game started before the notch still hides it),
        // but don't announce anything.
        try { Scan(announce: false); } catch (Exception ex) { Log.Write("Game scan: " + ex.Message); }
        while (true)
        {
            Thread.Sleep(3000);
            try { Scan(announce: true); } catch (Exception ex) { Log.Write("Game scan: " + ex.Message); }
        }
    }

    private void Scan(bool announce)
    {
        if ((DateTime.Now - _gameBarLoaded).TotalMinutes > 5) LoadGameBarList();

        var ids = Native.GetProcessIds();
        var now = new HashSet<uint>(ids);
        foreach (var pid in ids)
        {
            if (_known.Contains(pid)) continue; // only look at new processes: keeps this very cheap

            string path = Native.GetProcessPath(pid);
            string game = path != null ? GameNameFor(path) : null;
            if (game == null) continue;

            lock (_lock) _runningGames[pid] = game;

            if (!announce) continue;
            // Many games start 2-3 processes (launcher, game, anti-cheat): announce once.
            if (_announced.TryGetValue(game, out var when) && (DateTime.Now - when).TotalMinutes < 2) continue;
            _announced[game] = DateTime.Now;
            GameLaunched?.Invoke(game);
        }

        lock (_lock)
        {
            foreach (var pid in _runningGames.Keys.Where(k => !now.Contains(k)).ToList())
                _runningGames.Remove(pid);
        }
        _known = now;
    }

    /// <summary>Games that Windows Game Bar has recognised (HKCU\System\GameConfigStore).</summary>
    private void LoadGameBarList()
    {
        _gameBarLoaded = DateTime.Now;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children != null)
            {
                foreach (var name in children.GetSubKeyNames())
                {
                    using var k = children.OpenSubKey(name);
                    if (k?.GetValue("MatchedExeFullPath") is string p && p.Length > 0) set.Add(p);
                }
            }
        }
        catch (Exception ex) { Log.Write("Game Bar list: " + ex.Message); }
        _gameBarGames = set;
    }

    /// <summary>Returns a nice game name if this exe looks like a game, otherwise null.</summary>
    private string GameNameFor(string path)
    {
        string exe = Path.GetFileName(path);
        if (_cfg.IgnoreExes.Any(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase))) return null;

        if (_cfg.ExtraGameExes.Any(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase)) || _gameBarGames.Contains(path))
            return Clean(ProductName(path) ?? Path.GetFileNameWithoutExtension(exe));

        foreach (var folder in _cfg.GameFolders)
        {
            int i = path.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            // The folder right after e.g. "\steamapps\common\" is the game's name.
            string rest = path[(i + folder.Length)..].TrimStart('\\');
            string first = rest.Split('\\')[0];
            string name = string.IsNullOrWhiteSpace(first) || first.Equals(exe, StringComparison.OrdinalIgnoreCase)
                ? ProductName(path) ?? Path.GetFileNameWithoutExtension(exe)
                : first;
            if (_cfg.IgnoreGames.Any(g => g.Equals(name, StringComparison.OrdinalIgnoreCase))) return null;
            return Clean(name);
        }
        return null;
    }

    private string Clean(string name) =>
        _cfg.IgnoreGames.Any(g => g.Equals(name, StringComparison.OrdinalIgnoreCase)) ? null : name;

    private static string ProductName(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path).ProductName;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch { return null; }
    }

    public bool IsGamePid(uint pid)
    {
        lock (_lock) return _runningGames.ContainsKey(pid);
    }

    /// <summary>Right-click → "Treat X as a game": remember it and start hiding for it right away.</summary>
    public void MarkAsGame(uint pid, string exePath)
    {
        string exe = Path.GetFileName(exePath);
        if (!_cfg.ExtraGameExes.Any(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase)))
        {
            _cfg.ExtraGameExes.Add(exe);
            _cfg.Save();
        }
        lock (_lock) _runningGames[pid] = Path.GetFileNameWithoutExtension(exe);
    }

    /// <summary>The program you're currently using (not the notch), for the right-click menu.</summary>
    public static (uint pid, string path) ForegroundApp()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return (0, null);
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return (0, null);
        return (pid, Native.GetProcessPath(pid));
    }

    /// <summary>Should the notch hide right now? Called by the UI twice a second; very cheap.</summary>
    public bool ShouldHide(IntPtr ownWindow)
    {
        string mode = _cfg.HideMode ?? "Games";
        if (mode.Equals("Never", StringComparison.OrdinalIgnoreCase)) return false;

        // DirectX exclusive fullscreen is always a game.
        if (Native.SHQueryUserNotificationState(out int state) == 0 && state == Native.QUNS_RUNNING_D3D_FULL_SCREEN)
            return true;

        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ownWindow) return false;
        if (fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow()) return false;

        var cls = new StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        string c = cls.ToString();
        if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (IsGamePid(pid)) return true; // a game is in front: hide, windowed or not

        return mode.Equals("AnyFullscreen", StringComparison.OrdinalIgnoreCase) && IsFullscreen(fg);
    }

    /// <summary>True when the app in front fills the main screen (fullscreen video, presentation...).</summary>
    public static bool IsForegroundFullscreen(IntPtr ownWindow)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ownWindow || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow()) return false;
        var cls = new StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW") return false;
        return IsFullscreen(fg);
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        if ((mi.dwFlags & 1) == 0) return false; // fullscreen on another monitor: the notch can stay
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }
}
