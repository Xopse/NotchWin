using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace NotchWin.Services;

/// <summary>
/// The red dot. Talks to OBS (built-in WebSocket server, OBS 28+) to know if you're LIVE or recording,
/// and checks Windows' privacy records to know if any app is using your mic or camera.
/// </summary>
public class StreamService
{
    private readonly AppConfig _cfg;

    public bool IsStreaming { get; private set; }
    public bool IsRecording { get; private set; }
    public bool ObsConnected { get; private set; }
    public bool MicInUse { get; private set; }
    public bool CameraInUse { get; private set; }
    public string MicApp { get; private set; } = "";

    public StreamService(AppConfig cfg) => _cfg = cfg;

    public void Start()
    {
        _ = Task.Run(ObsLoop);
        _ = Task.Run(PrivacyLoop);
    }

    // ---------------- OBS ----------------

    private static bool ObsRunning()
    {
        foreach (var pid in Native.GetProcessIds())
        {
            string path = Native.GetProcessPath(pid);
            if (path != null && (path.EndsWith("\\obs64.exe", StringComparison.OrdinalIgnoreCase) ||
                                 path.EndsWith("\\obs32.exe", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private async Task ObsLoop()
    {
        while (true)
        {
            if (!ObsRunning())
            {
                IsStreaming = IsRecording = ObsConnected = false;
                await Task.Delay(10000);
                continue;
            }

            try { await RunObsSession(); }
            catch (Exception ex) { Log.Write("OBS: " + ex.Message); }

            IsStreaming = IsRecording = ObsConnected = false;
            await Task.Delay(5000);
        }
    }

    private async Task RunObsSession()
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("obswebsocket.json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_cfg.ObsPort}"), cts.Token);

        // 1) Hello (op 0) → maybe contains an auth challenge
        using (var hello = await ReceiveJson(ws))
        {
            var d = hello.RootElement.GetProperty("d");
            var identify = new Dictionary<string, object>
            {
                ["rpcVersion"] = 1,
                ["eventSubscriptions"] = 64, // Outputs category: stream/record state changes
            };
            if (d.TryGetProperty("authentication", out var auth))
            {
                string challenge = auth.GetProperty("challenge").GetString();
                string salt = auth.GetProperty("salt").GetString();
                string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(_cfg.ObsPassword + salt)));
                identify["authentication"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
            }
            await SendJson(ws, new { op = 1, d = identify });
        }

        // 2) Identified (op 2)
        using (var identified = await ReceiveJson(ws))
        {
            if (identified.RootElement.GetProperty("op").GetInt32() != 2)
                throw new Exception("OBS refused the connection (wrong password in config.json?)");
        }
        ObsConnected = true;

        // 3) Ask for the current state, then just listen to events.
        await SendJson(ws, new { op = 6, d = new { requestType = "GetStreamStatus", requestId = "stream" } });
        await SendJson(ws, new { op = 6, d = new { requestType = "GetRecordStatus", requestId = "record" } });

        while (ws.State == WebSocketState.Open)
        {
            using var msg = await ReceiveJson(ws);
            var root = msg.RootElement;
            int op = root.GetProperty("op").GetInt32();
            var d = root.GetProperty("d");

            if (op == 7) // request response
            {
                string id = d.GetProperty("requestId").GetString();
                if (d.TryGetProperty("responseData", out var data) && data.TryGetProperty("outputActive", out var active))
                {
                    if (id == "stream") IsStreaming = active.GetBoolean();
                    if (id == "record") IsRecording = active.GetBoolean();
                }
            }
            else if (op == 5) // event
            {
                string type = d.GetProperty("eventType").GetString();
                if (d.TryGetProperty("eventData", out var ed) && ed.TryGetProperty("outputActive", out var active))
                {
                    if (type == "StreamStateChanged") IsStreaming = active.GetBoolean();
                    if (type == "RecordStateChanged") IsRecording = active.GetBoolean();
                }
                if (type == "ExitStarted") break;
            }
        }
    }

    private static async Task SendJson(ClientWebSocket ws, object obj)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonDocument> ReceiveJson(ClientWebSocket ws)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) throw new Exception("OBS closed the connection");
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonDocument.Parse(ms.ToArray());
    }

    // ---------------- Mic / camera in use ----------------

    private async Task PrivacyLoop()
    {
        string self = (Environment.ProcessPath ?? "").Replace('\\', '#');
        while (true)
        {
            try
            {
                var mic = AppsUsing("microphone", self);
                MicInUse = mic.Count > 0;
                MicApp = mic.FirstOrDefault() ?? "";
                CameraInUse = AppsUsing("webcam", self).Count > 0;
            }
            catch (Exception ex) { Log.Write("Privacy check: " + ex.Message); }
            await Task.Delay(3000);
        }
    }

    /// <summary>
    /// Windows records start/stop times for every app that uses the mic/camera.
    /// "Stop time = 0" means it's in use right now.
    /// </summary>
    private static List<string> AppsUsing(string capability, string selfKeyName)
    {
        var result = new List<string>();
        string basePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + capability;
        using var root = Registry.CurrentUser.OpenSubKey(basePath);
        if (root == null) return result;

        void Check(RegistryKey parent, string sub, bool nonPackaged)
        {
            using var k = parent.OpenSubKey(sub);
            if (k == null) return;
            if (k.GetValue("LastUsedTimeStop") is long stop && stop == 0 &&
                k.GetValue("LastUsedTimeStart") is long start && start > 0)
            {
                if (nonPackaged && sub.Equals(selfKeyName, StringComparison.OrdinalIgnoreCase)) return; // that's us listening for the wake word
                string name = nonPackaged ? Path.GetFileNameWithoutExtension(sub.Replace('#', '\\')) : sub.Split('_')[0];
                result.Add(name);
            }
        }

        foreach (var sub in root.GetSubKeyNames())
        {
            if (sub == "NonPackaged")
            {
                using var np = root.OpenSubKey("NonPackaged");
                if (np == null) continue;
                foreach (var app in np.GetSubKeyNames()) Check(np, app, true);
            }
            else Check(root, sub, false);
        }
        return result;
    }
}
