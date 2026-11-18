using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NotchWin.Services;

public class SystemStats
{
    public double Cpu;          // 0-100
    public double Ram;          // 0-100
    public double RamUsedGb;
    public double RamTotalGb;
    public double Gpu = -1;     // 0-100, -1 = unknown
    public double DownBytesPerSec;
    public double UpBytesPerSec;
    public bool Online;
    public string NetworkName = "";
    public long PingMs = -1;
}

/// <summary>CPU, RAM, GPU, network speed and ping, refreshed once a second on a background thread.</summary>
public class SystemStatsService
{
    public SystemStats Current { get; private set; } = new();

    /// <summary>Only measure while the expanded notch is open: costs nothing the rest of the time.</summary>
    public volatile bool Active;

    private long _prevIdle, _prevKernel, _prevUser;
    private long _prevRx, _prevTx;
    private DateTime _prevNetTime;
    private readonly List<PerformanceCounter> _gpuCounters = new();
    private DateTime _gpuRefreshed = DateTime.MinValue;
    private bool _gpuUnsupported;
    private long _lastPing = -1;

    public void Start()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "Stats", Priority = ThreadPriority.BelowNormal };
        t.Start();
        _ = Task.Run(PingLoop);
    }

    private void Loop()
    {
        bool wasActive = false;
        DateTime inactiveSince = DateTime.Now;
        while (true)
        {
            if (!Active)
            {
                if (wasActive) { wasActive = false; inactiveSince = DateTime.Now; }
                // Free the GPU counters if the notch has been closed for a while.
                if (_gpuCounters.Count > 0 && (DateTime.Now - inactiveSince).TotalSeconds > 30) DisposeGpu();
                Thread.Sleep(300);
                continue;
            }
            if (!wasActive)
            {
                // Just opened: take a quick first sample so the numbers aren't averaged over minutes.
                wasActive = true;
                try { ReadCpu(new SystemStats()); ReadNetwork(new SystemStats()); } catch { }
                Thread.Sleep(250);
            }

            try
            {
                var s = new SystemStats();
                ReadCpu(s);
                ReadRam(s);
                ReadGpu(s);
                ReadNetwork(s);
                s.PingMs = _lastPing;
                Current = s;
            }
            catch (Exception ex) { Log.Write("Stats: " + ex.Message); }
            Thread.Sleep(1000);
        }
    }

    private void ReadCpu(SystemStats s)
    {
        if (!Native.GetSystemTimes(out long idle, out long kernel, out long user)) return;
        long dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        long total = dKernel + dUser; // kernel time includes idle time
        s.Cpu = total > 0 ? Math.Clamp((total - dIdle) * 100.0 / total, 0, 100) : 0;
    }

    private static void ReadRam(SystemStats s)
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) return;
        s.Ram = m.dwMemoryLoad;
        s.RamTotalGb = m.ullTotalPhys / 1073741824.0;
        s.RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
    }

    /// <summary>Same numbers Task Manager shows: sum of the "3D" engine utilisation of every process.</summary>
    private void ReadGpu(SystemStats s)
    {
        if (_gpuUnsupported) return;
        try
        {
            // Processes come and go, so rebuild the counter list every 10 seconds.
            if ((DateTime.Now - _gpuRefreshed).TotalSeconds > 10)
            {
                DisposeGpu();
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var name in cat.GetInstanceNames())
                {
                    if (!name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                    var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                    pc.NextValue(); // first read is always 0
                    _gpuCounters.Add(pc);
                }
                _gpuRefreshed = DateTime.Now;
            }

            double sum = 0;
            foreach (var c in _gpuCounters)
            {
                try { sum += c.NextValue(); } catch { /* process exited */ }
            }
            s.Gpu = Math.Clamp(sum, 0, 100);
        }
        catch (Exception ex)
        {
            Log.Write("GPU counters unavailable: " + ex.Message);
            _gpuUnsupported = true;
        }
    }

    private void DisposeGpu()
    {
        foreach (var c in _gpuCounters) c.Dispose();
        _gpuCounters.Clear();
        _gpuRefreshed = DateTime.MinValue;
    }

    private void ReadNetwork(SystemStats s)
    {
        long rx = 0, tx = 0;
        string name = "";
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var st = ni.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
                if (name == "" && ni.GetIPProperties().GatewayAddresses.Count > 0)
                    name = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : ni.Name;
            }
            catch { }
        }

        var now = DateTime.Now;
        double secs = (now - _prevNetTime).TotalSeconds;
        if (_prevNetTime != default && secs > 0)
        {
            s.DownBytesPerSec = Math.Max(0, (rx - _prevRx) / secs);
            s.UpBytesPerSec = Math.Max(0, (tx - _prevTx) / secs);
        }
        _prevRx = rx; _prevTx = tx; _prevNetTime = now;
        s.NetworkName = name;
        s.Online = NetworkInterface.GetIsNetworkAvailable() && name != "";
    }

    private async Task PingLoop()
    {
        using var ping = new Ping();
        while (true)
        {
            if (!Active) { await Task.Delay(500); continue; }
            try
            {
                var reply = await ping.SendPingAsync("1.1.1.1", 1500);
                _lastPing = reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
            }
            catch { _lastPing = -1; }
            await Task.Delay(3000);
        }
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576:0.0} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }
}
