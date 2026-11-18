using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NotchWin.Services;

/// <summary>
/// Reads the GPU temperature without admin rights or extra drivers.
///  1) The same source Task Manager uses (Windows graphics kernel "adapter perf data", WDDM 2.4+ drivers):
///     works for most NVIDIA and AMD cards on up-to-date drivers.
///  2) NVIDIA's own management library (nvml.dll, installed with the NVIDIA driver) as a fallback.
/// Returns null if neither is available (common for Intel integrated graphics).
/// </summary>
public static class GpuTemperature
{
    // ---------------- 1) Windows graphics kernel ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;      // tenths of a degree Celsius
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER data);

    private static bool _kmtBroken, _nvmlBroken, _nvmlReady;

    /// <summary>Hottest GPU in °C, or null when it can't be read on this PC.</summary>
    public static double? Read()
    {
        double? t = null;
        if (!_kmtBroken) t = ReadKmt();
        if (t == null && !_nvmlBroken) t = ReadNvml();
        return t;
    }

    private static double? ReadKmt()
    {
        var handles = new List<uint>();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var en = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref en) != 0 || en.NumAdapters == 0) { _kmtBroken = true; return null; }

            int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            buffer = Marshal.AllocHGlobal(size * (int)en.NumAdapters);
            en.pAdapters = buffer;
            if (D3DKMTEnumAdapters2(ref en) != 0) { _kmtBroken = true; return null; }

            double? best = null;
            bool anyAnswered = false;
            for (int i = 0; i < en.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buffer + i * size);
                handles.Add(info.hAdapter);

                var perf = new D3DKMT_ADAPTER_PERFDATA { PhysicalAdapterIndex = 0 };
                int perfSize = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
                IntPtr pPerf = Marshal.AllocHGlobal(perfSize);
                try
                {
                    Marshal.StructureToPtr(perf, pPerf, false);
                    var q = new D3DKMT_QUERYADAPTERINFO
                    {
                        hAdapter = info.hAdapter,
                        Type = KMTQAITYPE_ADAPTERPERFDATA,
                        pPrivateDriverData = pPerf,
                        PrivateDriverDataSize = (uint)perfSize,
                    };
                    if (D3DKMTQueryAdapterInfo(ref q) != 0) continue;
                    anyAnswered = true;
                    perf = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(pPerf);
                }
                finally { Marshal.FreeHGlobal(pPerf); }

                double c = perf.Temperature / 10.0;
                if (c > 1 && c < 130) best = best == null ? c : System.Math.Max(best.Value, c);
            }
            // No adapter supports it at all: stop asking. (A laptop GPU that's asleep answers but reports 0: keep asking.)
            if (!anyAnswered) _kmtBroken = true;
            return best;
        }
        catch { _kmtBroken = true; return null; }
        finally
        {
            foreach (var h in handles)
            {
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = h };
                try { D3DKMTCloseAdapter(ref close); } catch { }
            }
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------- 2) NVIDIA NVML ----------------

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int NvmlInit();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int NvmlCount(out uint count);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int NvmlHandle(uint index, out IntPtr device);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")] private static extern int NvmlTemp(IntPtr device, int sensor, out uint temp);

    private static double? ReadNvml()
    {
        try
        {
            if (!_nvmlReady)
            {
                if (NvmlInit() != 0) { _nvmlBroken = true; return null; }
                _nvmlReady = true;
            }
            if (NvmlCount(out uint count) != 0 || count == 0) { _nvmlBroken = true; return null; }
            double? best = null;
            for (uint i = 0; i < count; i++)
            {
                if (NvmlHandle(i, out var dev) != 0) continue;
                if (NvmlTemp(dev, 0, out uint t) == 0 && t > 0 && t < 130)
                    best = best == null ? t : System.Math.Max(best.Value, t);
            }
            if (best == null) _nvmlBroken = true;
            return best;
        }
        catch { _nvmlBroken = true; return null; } // no NVIDIA driver
    }
}
