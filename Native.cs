using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text;

namespace NotchWin;

/// <summary>All the raw Windows API calls in one place.</summary>
internal static class Native
{
    // ----- Window styles -----
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;   // hide from Alt+Tab
    public const int WS_EX_NOACTIVATE = 0x08000000;   // never steal focus from your game/app
    public const int WS_EX_TRANSPARENT = 0x00000020;  // mouse clicks pass straight through

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ----- Foreground / fullscreen detection -----
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    /// <summary>Titles of all visible top-level windows belonging to a process (e.g. browser windows).</summary>
    public static List<string> WindowTitlesOf(Func<uint, bool> pidFilter)
    {
        var titles = new List<string>();
        var sb = new StringBuilder(512);
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pidFilter(pid)) return true;
            sb.Clear();
            if (GetWindowText(h, sb, sb.Capacity) > 0) titles.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    // QUERY_USER_NOTIFICATION_STATE
    public const int QUNS_BUSY = 2;                 // a fullscreen app is running
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3; // exclusive-fullscreen DirectX game
    public const int QUNS_PRESENTATION_MODE = 4;

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    // ----- Processes -----
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    /// <summary>Full exe path of a process, works even for most elevated/protected processes.</summary>
    public static string GetProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", EntryPoint = "K32EnumProcesses")]
    private static extern bool EnumProcesses([Out] uint[] ids, int size, out int needed);

    /// <summary>All process IDs. Much lighter than Process.GetProcesses(), which builds a big object per process.</summary>
    public static uint[] GetProcessIds()
    {
        var buf = new uint[1024];
        while (true)
        {
            if (!EnumProcesses(buf, buf.Length * 4, out int needed)) return Array.Empty<uint>();
            if (needed < buf.Length * 4)
            {
                var result = new uint[needed / 4];
                Array.Copy(buf, result, result.Length);
                return result;
            }
            buf = new uint[buf.Length * 2];
        }
    }

    // ----- Memory trimming -----
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Hands unused memory back to Windows (what Task Manager shows drops a lot while idle).</summary>
    public static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Optimized, false);
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
    }

    // ----- CPU & RAM -----
    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    // ----- Global hotkey -----
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ----- Simulated media/volume keys -----
    public const byte VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static void PressKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
