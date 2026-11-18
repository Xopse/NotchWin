using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace NotchWin;

public class ShortcutItem
{
    public string Name { get; set; } = "";
    /// <summary>Either a Segoe Fluent icon code like "E8B7", or any short text/emoji.</summary>
    public string Icon { get; set; } = "";
    /// <summary>An exe path, a URL, a ms-settings: link, a folder... anything you could type into Win+R.</summary>
    public string Target { get; set; } = "";
    public string Args { get; set; } = "";
}

/// <summary>
/// Everything you can tweak. Edited through the Settings window
/// (right-click the notch → Settings), stored at %AppData%\NotchWin\config.json.
/// </summary>
public class AppConfig
{
    public static string Folder =>
        Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchWin")).FullName;

    public static string FilePath => Path.Combine(Folder, "config.json");

    // ---------- General ----------
    public bool RunOnStartup { get; set; } = true;
    /// <summary>Install new versions from GitHub Releases automatically.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>The GitHub repository updates come from ("owner/name"). It must be public for updates to work.</summary>
    public string UpdateRepo { get; set; } = "Xopse/NotchWin";
    /// <summary>"Games" = hide only while a game is in front, "AnyFullscreen" = hide for any fullscreen app, "Never".</summary>
    public string HideMode { get; set; } = "Games";

    // ---------- Notch look & feel ----------
    /// <summary>midnight, nightfall, aurora, sunset, ocean, neon, sakura, ember, glass, daylight or vibe.</summary>
    public string Theme { get; set; } = "midnight";
    public double CompactWidth { get; set; } = 210;
    public double CompactHeight { get; set; } = 34;
    public int HoverDelayMs { get; set; } = 120;
    public bool ShowWeatherInPill { get; set; } = true;
    public bool ShowSongInPill { get; set; } = true;
    public bool ShowEqualizer { get; set; } = true;
    public bool ShowSongPopups { get; set; } = true;

    /// <summary>Where the notch sits: the centre X of the pill (-1 = middle of the main screen) and its top Y.
    /// Drag the notch to change it. Top 0 = docked to the top edge.</summary>
    public double PosX { get; set; } = -1;
    public double PosY { get; set; } = 0;
    /// <summary>"top", "bottom", "left", "right" (docked to that screen edge; sides are vertical) or "float".</summary>
    public string Dock { get; set; } = "top";

    // ---------- Music ----------
    /// <summary>"youtube music" or "spotify": what the Music tile opens and where "play ..." goes.</summary>
    public string MusicService { get; set; } = "youtube music";

    // ---------- Weather ----------
    /// <summary>Leave empty to auto-detect from your IP address. Or set e.g. "Pune".</summary>
    public string City { get; set; } = "";
    public bool UseFahrenheit { get; set; } = false;

    // ---------- Streaming (OBS) ----------
    public int ObsPort { get; set; } = 4455;
    public string ObsPassword { get; set; } = "";

    // ---------- Voice assistant ----------
    /// <summary>"WakeWord" = always listening for the wake word, "Hotkey" = mic only on Ctrl+Alt+Space, "Off".</summary>
    public string VoiceMode { get; set; } = "WakeWord";
    public List<string> WakeWords { get; set; } = new() { "computer", "jarvis" };
    /// <summary>"low" (fewest false alarms), "medium" or "high" (reacts to quieter / quicker wake words).</summary>
    public string WakeSensitivity { get; set; } = "medium";
    public string VoskModelUrl { get; set; } = VoskIndian;
    public bool SpeakReplies { get; set; } = true;
    /// <summary>"auto" = Groq Whisper when a Groq key is available (much more accurate), otherwise offline. "offline" = never send audio.</summary>
    public string SpeechEngine { get; set; } = "auto";
    /// <summary>Only needed if your AI provider isn't Groq but you still want Groq's speech recognition.</summary>
    public string GroqSpeechKey { get; set; } = "";

    public const string VoskIndian = "https://alphacephei.com/vosk/models/vosk-model-small-en-in-0.4.zip";
    public const string VoskUs = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";

    // ---------- AI (for general questions) ----------
    /// <summary>"groq" (free tier), "xai" (Grok), "anthropic" (Claude) or "custom" (any OpenAI-compatible API).</summary>
    public string AiProvider { get; set; } = "groq";
    public string AiApiKey { get; set; } = "";
    public string AiModel { get; set; } = "";
    /// <summary>Only used when AiProvider is "custom".</summary>
    public string AiBaseUrl { get; set; } = "";

    // ---------- Notifications ----------
    public bool ShowAllNotifications { get; set; } = false;
    /// <summary>How long a notification pops up before it tucks into the bell.</summary>
    public double NotificationPopupSeconds { get; set; } = 3;
    /// <summary>Shorter while you're watching a video or using a fullscreen app.</summary>
    public double NotificationPopupSecondsWhileWatching { get; set; } = 1.2;
    public List<string> ImportantApps { get; set; } = new()
        { "WhatsApp", "Discord", "Telegram", "Outlook", "Teams", "Instagram", "Phone", "Slack" };
    public List<string> ImportantKeywords { get; set; } = new()
        { "OTP", "verification code", "urgent", "calling", "missed call", "meeting", "payment", "debited", "credited" };

    // ---------- Games ----------
    public List<string> GameFolders { get; set; } = new()
    {
        @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\", @"\Riot Games\",
        @"\GOG Galaxy\Games\", @"\EA Games\", @"\Ubisoft Game Launcher\games\", @"\Games\"
    };
    public List<string> ExtraGameExes { get; set; } = new();
    public List<string> IgnoreExes { get; set; } = new()
    {
        "UnityCrashHandler64.exe", "UnityCrashHandler32.exe", "CrashReportClient.exe", "EpicGamesLauncher.exe",
        "EpicWebHelper.exe", "RiotClientServices.exe", "RiotClientUx.exe", "RiotClientUxRender.exe",
        "RiotClientCrashHandler.exe", "vgc.exe", "vgtray.exe", "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe",
        "BEService.exe", "UplayWebCore.exe", "upc.exe", "EADesktop.exe", "GameBarPresenceWriter.exe"
    };
    public List<string> IgnoreGames { get; set; } = new()
        { "Steamworks Shared", "wallpaper_engine", "SteamVR", "Steam Controller Configs", "Launcher", "DirectXRedist" };

    // ---------- Shortcuts (shown in the expanded notch) ----------
    public List<ShortcutItem> Shortcuts { get; set; } = new()
    {
        new() { Name = "Files",    Icon = "E8B7", Target = "explorer.exe" },
        new() { Name = "Music",    Icon = "E8D6", Target = "music:" }, // opens your music app (Settings → Voice & AI)
        new() { Name = "Steam",    Icon = "E7FC", Target = "steam://open/main" },
        new() { Name = "Tasks",    Icon = "E9D9", Target = "taskmgr.exe" },
        new() { Name = "Calc",     Icon = "E8EF", Target = "calc.exe" },
        new() { Name = "Settings", Icon = "E713", Target = "ms-settings:" },
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOpts);
                if (cfg != null)
                {
                    cfg.Save(); // writes back any new settings added in updates
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Config load failed, using defaults: " + ex.Message);
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }

        var def = new AppConfig();
        def.Save();
        return def;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch (Exception ex) { Log.Write("Config save failed: " + ex.Message); }
    }
}
