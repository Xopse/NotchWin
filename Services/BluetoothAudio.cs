using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace NotchWin.Services;

/// <summary>
/// Connects / disconnects paired Bluetooth headphones, earbuds and speakers, and turns Bluetooth on/off.
///
/// Windows has no simple "connect" function for Bluetooth audio, but the Bluetooth audio driver accepts
/// a "reconnect" / "disconnect" request, which is what the Connect button in Settings uses. We reach it
/// through the Windows audio system: audio device → the driver piece it's attached to → send the request.
/// </summary>
public static class BluetoothAudio
{
    public record Device(string Name, bool Connected, List<string> EndpointIds);

    // ----- COM interfaces from the Windows audio system (devicetopology.h / mmdeviceapi.h / ks.h) -----

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig]
        int GetConnectorCount(out uint count);
        [PreserveSig]
        int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig]
        int GetType(out int type);
        [PreserveSig]
        int GetDataFlow(out int flow);
        [PreserveSig]
        int ConnectTo(IConnector other);
        [PreserveSig]
        int Disconnect();
        [PreserveSig]
        int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig]
        int GetConnectedTo(out IConnector other);
        [PreserveSig]
        int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig]
        int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig]
        int KsProperty(ref KSPROPERTY property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KSPROPERTY
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    private static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
    private const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;
    private const uint KSPROPERTY_TYPE_GET = 1;
    private const int CLSCTX_ALL = 23;

    /// <summary>Paired Bluetooth audio devices (grouped by device, e.g. "Galaxy Buds2").</summary>
    public static List<Device> List()
    {
        var byName = new Dictionary<string, (bool connected, List<string> ids)>(StringComparer.OrdinalIgnoreCase);
        var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active | DeviceState.Unplugged))
        {
            try
            {
                if (!IsBluetooth(d)) continue;
                string name = d.DeviceFriendlyName;
                if (string.IsNullOrWhiteSpace(name)) name = d.FriendlyName;
                if (!byName.TryGetValue(name, out var entry)) entry = (false, new List<string>());
                entry.connected |= d.State == DeviceState.Active;
                entry.ids.Add(d.ID);
                byName[name] = entry;
            }
            catch { }
        }
        return byName.Select(kv => new Device(kv.Key, kv.Value.connected, kv.Value.ids)).ToList();
    }

    private static bool IsBluetooth(MMDevice d)
    {
        // Bluetooth audio endpoints come from the "BTHENUM" / "BTHHFENUM" drivers.
        try
        {
            var store = d.Properties;
            for (int i = 0; i < store.Count; i++)
            {
                string v;
                try { v = store[i].Value as string; } catch { continue; } // some property types can't be read
                if (v != null && (v.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) || v.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Sends connect (true) or disconnect (false) to every audio part of a device. Returns true if any worked.</summary>
    public static bool SetConnected(Device device, bool connect)
    {
        bool any = false;
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            foreach (var id in device.EndpointIds)
            {
                try { if (Send(en, id, connect ? KSPROPERTY_ONESHOT_RECONNECT : KSPROPERTY_ONESHOT_DISCONNECT)) any = true; }
                catch (Exception ex) { Log.Write("Bluetooth: " + ex.Message); }
            }
        }
        finally { Marshal.ReleaseComObject(en); }
        return any;
    }

    private static bool Send(IMMDeviceEnumerator en, string endpointId, uint request)
    {
        if (en.GetDevice(endpointId, out var endpoint) != 0 || endpoint == null) return false;
        var topoIid = typeof(IDeviceTopology).GUID;
        if (endpoint.Activate(ref topoIid, CLSCTX_ALL, IntPtr.Zero, out var topoObj) != 0) return false;
        var topology = (IDeviceTopology)topoObj;
        if (topology.GetConnector(0, out var connector) != 0) return false;
        // The driver piece ("KS filter") this endpoint is plugged into.
        if (connector.GetDeviceIdConnectedTo(out var filterId) != 0 || string.IsNullOrEmpty(filterId)) return false;
        if (en.GetDevice(filterId, out var filter) != 0 || filter == null) return false;
        var ksIid = typeof(IKsControl).GUID;
        if (filter.Activate(ref ksIid, CLSCTX_ALL, IntPtr.Zero, out var ksObj) != 0) return false;

        var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = request, Flags = KSPROPERTY_TYPE_GET };
        int hr = ((IKsControl)ksObj).KsProperty(ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
        return hr == 0;
    }

    /// <summary>Best match for a spoken name ("my buds", "galaxy buds", "boat").</summary>
    public static Device Find(List<Device> devices, string spoken)
    {
        if (devices.Count == 0) return null;
        string s = Normalize(spoken);
        if (s.Length == 0) return null;
        return devices
            .Select(d => (d, score: AppFinder.Similarity(s, Normalize(d.Name))))
            .Where(x => x.score >= 0.4)
            .OrderByDescending(x => x.score)
            .Select(x => x.d)
            .FirstOrDefault();
    }

    private static string Normalize(string s) =>
        System.Text.RegularExpressions.Regex.Replace((s ?? "").ToLowerInvariant(), @"\b(my|the|bluetooth|device|headphones?|headset|earbuds|speaker)\b", " ").Trim();

    // ----- Bluetooth radio on / off -----

    /// <summary>Turns the Bluetooth radio on or off. Returns false if Windows didn't allow it.</summary>
    public static async Task<bool> SetRadioAsync(bool on)
    {
        try
        {
            var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
            if (access != Windows.Devices.Radios.RadioAccessStatus.Allowed) return false;
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);
            if (bt == null) return false;
            var result = await bt.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off);
            return result == Windows.Devices.Radios.RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Write("Bluetooth radio: " + ex.Message);
            return false;
        }
    }
}
