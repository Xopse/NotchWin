using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Net.Http;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NotchWin.Services;

/// <summary>
/// Turns what you said into an action. Simple commands are handled locally;
/// anything else goes to the AI (if you added an API key).
/// </summary>
public class CommandRouter
{
    private readonly AppConfig _cfg;
    private readonly MediaService _media;
    private readonly WeatherService _weather;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly List<(string role, string text)> _history = new();
    private readonly AppFinder _finder;

    // A question we asked ("Did you mean Riot Client?") and what to do if you say yes.
    private Func<Task<string>> _pendingYes;
    private DateTime _pendingUntil;

    /// <summary>True right after asking you a yes/no question; the notch then listens for the answer.</summary>
    public bool ExpectsAnswer { get; private set; }

    public CommandRouter(AppConfig cfg, MediaService media, WeatherService weather)
    {
        _cfg = cfg;
        _media = media;
        _weather = weather;
        _finder = new AppFinder(cfg);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <summary>Runs the command and returns what the assistant should say back.</summary>
    public async Task<string> HandleAsync(string said)
    {
        string c = Normalize(said);
        ExpectsAnswer = false;

        // 0) Answer to our last question?
        if (_pendingYes != null)
        {
            var yes = _pendingYes;
            bool fresh = DateTime.Now < _pendingUntil;
            _pendingYes = null;
            if (fresh && Regex.IsMatch(c, @"^(yes|yeah|yep|yup|ya|yes please|sure|correct|right|that's right|ok|okay|do it|open it|go ahead|haan|haa|han|ha)\b"))
                return await yes();
            if (fresh && Regex.IsMatch(c, @"^(no|nope|nah|na|nahi|wrong|cancel|never ?mind|not that)\b"))
                return "Okay, never mind.";
            // Anything else: treat it as a new command.
        }

        // 1) Quick local commands: instant, no internet needed.
        string local = await TryLocalAsync(c);
        if (local != null) return local;

        // 2) Anything else goes to the AI. It either answers, or tells us which action to run
        //    (so "yo can you put on that ipad song" still plays music instead of chatting about it).
        if (!string.IsNullOrWhiteSpace(_cfg.AiApiKey) || _cfg.AiProvider == "custom")
        {
            string reply = await AskAiAsync(said);
            var action = TryParseAction(reply);
            return action != null ? await RunActionAsync(action) : reply;
        }
        return "I didn't catch a command. Add a free Groq key in Settings to ask me anything.";
    }

    // ---------------- understanding what you said ----------------

    private static readonly Regex Filler = new(
        @"^(hey|hi|ok|okay|yo|so|um|uh|please|kindly|just|now|can you|could you|would you|will you|can u|could u|" +
        @"i want you to|i need you to|i want to|i wanna|i would like to|i'd like to|let's|lets|go ahead and|you can)\s+",
        RegexOptions.Compiled);

    private static readonly Regex TrailingFiller = new(@"\s+(please|for me|now|right now|thanks|thank you)$", RegexOptions.Compiled);

    /// <summary>Lower-case, strip punctuation and polite filler, fix common speech-to-text splits.</summary>
    private static string Normalize(string said)
    {
        string c = (said ?? "").ToLowerInvariant();
        c = Regex.Replace(c, @"[^\w\s']", " ");
        c = Regex.Replace(c, @"\b(you|u) tube\b", "youtube");
        c = Regex.Replace(c, @"\byt music\b", "youtube music");
        c = Regex.Replace(c, @"\bspot ify\b", "spotify");
        c = Regex.Replace(c, @"\s+", " ").Trim();

        string before;
        do
        {
            before = c;
            c = Filler.Replace(c, "");
            c = TrailingFiller.Replace(c, "");
        } while (c != before);
        return c;
    }

    private static readonly Regex PlayRegex = new(
        @"^(?:play|start playing|put on|listen to|play me|queue up)\s+(?:the song\s+|song\s+|the video\s+|video\s+)?(.+?)" +
        @"(?:\s+(?:song|track))?(?:\s+(?:on|in|from|using|with)\s+(youtube music|youtube|spotify))?(?:\s+(?:song|track))?$",
        RegexOptions.Compiled);

    private async Task<string> TryLocalAsync(string c)
    {
        // ----- media controls -----
        if (c is "pause" or "stop" or "stop music" or "pause music" or "pause the music" or "stop the music" or "pause it" or "stop it")
            return await RunActionAsync(new AiAction("pause"));
        if (c is "resume" or "play" or "continue" or "play music" or "resume music" or "resume the music" or "unpause" or "play it")
            return await RunActionAsync(new AiAction("resume"));
        if (c is "next" or "skip" or "next song" or "skip song" or "skip this song" or "next track" or "skip this" or "skip it" or "play the next song")
            return await RunActionAsync(new AiAction("next"));
        if (c is "previous" or "back" or "previous song" or "go back" or "last song" or "play the previous song")
            return await RunActionAsync(new AiAction("previous"));
        if (c.StartsWith("what's playing") || c.StartsWith("what is playing") || c.Contains("what song is this") || c.Contains("which song is this"))
        {
            var m = _media.Current;
            return m == null ? "Nothing's playing." : $"{m.Title}{(string.IsNullOrEmpty(m.Artist) ? "" : " by " + m.Artist)}.";
        }

        // ----- volume -----
        // "set volume to 50", "volume 30", "make it 70 percent"
        var setVol = Regex.Match(c, @"\b(?:set |put |make |change )?(?:the )?(?:volume|sound|it)\s*(?:to|at)?\s*(\d{1,3})\s*(?:percent|%)?$");
        if (setVol.Success && (c.Contains("volume") || c.Contains("sound") || c.Contains(" to ")))
            return await RunActionAsync(new AiAction("set_volume") { Amount = int.Parse(setVol.Groups[1].Value) });
        // "reduce the volume by 10", "turn it up by 20 percent"
        var byVol = Regex.Match(c, @"\b(reduce|lower|decrease|down|turn it down|turn down|increase|raise|up|turn it up|turn up)\b.*?\bby\s+(\d{1,3})");
        if (byVol.Success)
        {
            bool down = Regex.IsMatch(byVol.Groups[1].Value, "reduce|lower|decrease|down");
            int amount = int.Parse(byVol.Groups[2].Value);
            return await RunActionAsync(new AiAction("change_volume") { Amount = down ? -amount : amount });
        }
        if (c.Contains("volume up") || c.Contains("louder") || c.Contains("increase the volume") || c.Contains("turn it up"))
            return await RunActionAsync(new AiAction("change_volume") { Amount = 10 });
        if (c.Contains("volume down") || c.Contains("quieter") || c.Contains("decrease the volume") || c.Contains("reduce the volume") || c.Contains("turn it down"))
            return await RunActionAsync(new AiAction("change_volume") { Amount = -10 });
        if (Regex.IsMatch(c, @"^(what's the volume|what is the volume|volume)$"))
            return $"Volume is at {Math.Round(Volume.Get())} percent.";

        // ----- bluetooth -----
        if (Regex.IsMatch(c, @"^(turn |switch )?(on )?bluetooth (on)$|^(turn|switch) on bluetooth$|^enable bluetooth$"))
            return await RunActionAsync(new AiAction("bluetooth_on"));
        if (Regex.IsMatch(c, @"^(turn |switch )?bluetooth off$|^(turn|switch) off bluetooth$|^disable bluetooth$"))
            return await RunActionAsync(new AiAction("bluetooth_off"));
        var bt = Regex.Match(c, @"^(connect|disconnect|reconnect)\s*(?:to\s+)?(?:my\s+|the\s+)?(.*)$");
        if (bt.Success)
        {
            string what = bt.Groups[2].Value.Trim();
            return await RunActionAsync(new AiAction(bt.Groups[1].Value == "disconnect" ? "bluetooth_disconnect" : "bluetooth_connect") { Target = what });
        }
        if (c is "mute" or "unmute" or "mute it" or "unmute it" || c.Contains("mute the volume"))
            return await RunActionAsync(new AiAction("mute"));

        // ----- play something ("play ipad on youtube music", "put on lofi beats") -----
        var play = PlayRegex.Match(c);
        if (play.Success)
        {
            string query = play.Groups[1].Value.Trim();
            string where = play.Groups[2].Success ? play.Groups[2].Value : (_cfg.MusicService ?? "youtube music");
            if (query is "something" or "some music" or "music" or "a song" or "some songs")
                return await RunActionAsync(new AiAction("resume"));
            return await RunActionAsync(new AiAction("play") { Query = query, Where = where });
        }

        // ----- open apps / sites -----
        var open = Regex.Match(c, @"^(?:open|launch|start|run)\s+(?:the\s+|up\s+)?(.+?)(?:\s+app)?$");
        if (open.Success) return await RunActionAsync(new AiAction("open") { Target = open.Groups[1].Value.Trim() });

        var search = Regex.Match(c, @"^(?:search|google|look up)\s+(?:for\s+)?(.+)$");
        if (search.Success) return await RunActionAsync(new AiAction("search") { Query = search.Groups[1].Value });

        // ----- info -----
        if (Regex.IsMatch(c, @"^(what's the time|what is the time|what time is it|time)$")) return $"It's {DateTime.Now:h:mm tt}.";
        if (Regex.IsMatch(c, @"^(what's the date|what is the date|what's today's date|what day is it|date)$")) return $"It's {DateTime.Now:dddd, d MMMM}.";
        if (Regex.IsMatch(c, @"^(what's the weather|what is the weather|weather|how's the weather|what's the temperature)( today| outside)?$"))
            return _weather.SpokenSummary();

        return null; // not a simple command
    }

    // ---------------- actions ----------------

    private class AiAction
    {
        public AiAction(string name) => Name = name;
        public string Name;
        public string Query = "";
        public string Where = "youtube music";
        public string Target = "";
        public int Amount;
    }

    /// <summary>The AI replies with a line of JSON when you asked it to do something. Anything else is a normal answer.</summary>
    private static AiAction TryParseAction(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        int a = reply.IndexOf('{'), b = reply.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try
        {
            using var doc = JsonDocument.Parse(reply[a..(b + 1)]);
            var r = doc.RootElement;
            if (!r.TryGetProperty("action", out var act) || act.ValueKind != JsonValueKind.String) return null;
            string Get(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
            var action = new AiAction(act.GetString().ToLowerInvariant())
            {
                Query = Get("query"),
                Target = Get("target"),
            };
            if (r.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number) action.Amount = (int)Math.Round(amt.GetDouble());
            string where = Get("where").ToLowerInvariant();
            if (where.Length > 0) action.Where = where;
            else action.Where = "";
            return action;
        }
        catch { return null; }
    }

    private async Task<string> RunActionAsync(AiAction a)
    {
        switch (a.Name)
        {
            case "pause": await _media.PauseAsync(); return "Paused.";
            case "resume": await _media.PlayAsync(); return "Playing.";
            case "next": await _media.NextAsync(); return "Next one.";
            case "previous": await _media.PreviousAsync(); return "Going back.";
            case "volume_up": return await RunActionAsync(new AiAction("change_volume") { Amount = a.Amount != 0 ? Math.Abs(a.Amount) : 10 });
            case "volume_down": return await RunActionAsync(new AiAction("change_volume") { Amount = a.Amount != 0 ? -Math.Abs(a.Amount) : -10 });
            case "mute": Native.PressKey(Native.VK_VOLUME_MUTE); return "Done.";

            case "play":
                if (string.IsNullOrWhiteSpace(a.Query)) { await _media.PlayAsync(); return "Playing."; }
                if (string.IsNullOrWhiteSpace(a.Where)) a.Where = _cfg.MusicService ?? "youtube music";
                if (a.Where.Contains("spotify"))
                {
                    return await PlayOnSpotifyAsync(a.Query)
                        ? $"Playing {a.Query} on Spotify."
                        : $"Here's {a.Query} on Spotify. Tap play on the top result.";
                }
                bool music = !a.Where.Equals("youtube", StringComparison.OrdinalIgnoreCase);
                await PlayOnYouTubeAsync(a.Query, music);
                return $"Playing {a.Query}{(music ? "" : " on YouTube")}.";

            case "open":
                if (string.IsNullOrWhiteSpace(a.Target)) return "Open what?";
                return await Task.Run(() => OpenSmart(a.Target));

            case "set_volume":
                Volume.Set(Math.Clamp(a.Amount, 0, 100));
                return $"Volume {Math.Clamp(a.Amount, 0, 100)} percent.";
            case "change_volume":
            {
                double now = Volume.Get();
                double target = Math.Clamp(Math.Round(now + a.Amount), 0, 100);
                Volume.Set(target);
                return $"Volume {target} percent.";
            }

            case "bluetooth_on":
                return await BluetoothAudio.SetRadioAsync(true) ? "Bluetooth is on." : OpenBluetoothSettings("I couldn't switch Bluetooth on myself, so here are the settings.");
            case "bluetooth_off":
                return await BluetoothAudio.SetRadioAsync(false) ? "Bluetooth is off." : OpenBluetoothSettings("I couldn't switch Bluetooth off myself, so here are the settings.");
            case "bluetooth_connect":
            case "bluetooth_disconnect":
                return await Task.Run(() => ConnectBluetooth(a.Target, a.Name == "bluetooth_connect"));

            case "search":
                Open("https://www.google.com/search?q=" + Uri.EscapeDataString(a.Query));
                return "Here you go.";

            case "weather": return _weather.SpokenSummary();
            case "time": return $"It's {DateTime.Now:h:mm tt}.";

            default: return "I'm not sure how to do that yet.";
        }
    }

    // ---------------- helpers ----------------

    /// <summary>Finds the top YouTube result and opens it, so the song starts straight away.</summary>
    private async Task PlayOnYouTubeAsync(string query, bool music)
    {
        // YouTube Music: search its "Songs" section, so you get the song itself, not the music video.
        if (music)
        {
            string songId = await FindYouTubeMusicSongAsync(query);
            if (songId != null)
            {
                Open($"https://music.youtube.com/watch?v={songId}");
                return;
            }
        }

        string id = null;
        try
        {
            string html = await _http.GetStringAsync("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
            var m = Regex.Match(html, "\"videoRenderer\":\\{\"videoId\":\"([A-Za-z0-9_-]{11})\"");
            if (!m.Success) m = Regex.Match(html, "\"videoId\":\"([A-Za-z0-9_-]{11})\"");
            if (m.Success) id = m.Groups[1].Value;
        }
        catch (Exception ex) { Log.Write("YouTube search: " + ex.Message); }

        string host = music ? "https://music.youtube.com" : "https://www.youtube.com";
        Open(id != null
            ? $"{host}/watch?v={id}"
            : music ? $"{host}/search?q={Uri.EscapeDataString(query)}" : $"{host}/results?search_query={Uri.EscapeDataString(query)}");
    }

    /// <summary>The top result in YouTube Music's "Songs" (audio versions), using the same search the website uses.</summary>
    private async Task<string> FindYouTubeMusicSongAsync(string query)
    {
        try
        {
            var body = new
            {
                context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20251020.01.00", hl = "en", gl = "IN" } },
                query,
                @params = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D", // "Songs" filter
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.youtube.com/youtubei/v1/search?prettyPrint=false")
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { Log.Write($"YouTube Music search: {(int)resp.StatusCode}"); return null; }
            string json = await resp.Content.ReadAsStringAsync();
            var m = Regex.Match(json, "\"videoId\":\"([A-Za-z0-9_-]{11})\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception ex) { Log.Write("YouTube Music search: " + ex.Message); return null; }
    }

    /// <summary>
    /// Spotify doesn't let other apps start a song directly, so: open its search, then press the
    /// Play button of the matching result through Windows accessibility (the same way a screen reader would).
    /// </summary>
    private static async Task<bool> PlayOnSpotifyAsync(string query)
    {
        if (!SpotifyInstalled())
        {
            Open("https://open.spotify.com/search/" + Uri.EscapeDataString(query));
            return false;
        }
        Open("spotify:search:" + Uri.EscapeDataString(query));
        for (int attempt = 0; attempt < 7; attempt++)
        {
            await Task.Delay(900);
            try { if (await Task.Run(() => ClickSpotifyPlay(query))) return true; }
            catch (Exception ex) { Log.Write("Spotify play: " + ex.Message); }
        }
        return false;
    }

    private static bool ClickSpotifyPlay(string query)
    {
        IntPtr hwnd = IntPtr.Zero;
        foreach (var p in Process.GetProcessesByName("Spotify"))
        {
            if (hwnd == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero) hwnd = p.MainWindowHandle;
            p.Dispose();
        }
        if (hwnd == IntPtr.Zero) return false;

        var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
        var buttons = root.FindAll(System.Windows.Automation.TreeScope.Descendants,
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Button));

        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToList();
        System.Windows.Automation.AutomationElement best = null;
        int bestScore = 0;
        foreach (System.Windows.Automation.AutomationElement b in buttons)
        {
            string name = b.Current.Name ?? "";
            // Result play buttons are labelled "Play <song> by <artist>"; the player bar's is just "Play".
            if (!name.StartsWith("Play ", StringComparison.OrdinalIgnoreCase)) continue;
            string rest = name[5..].ToLowerInvariant();
            int score = words.Count(w => rest.Contains(w));
            if (score > bestScore) { bestScore = score; best = b; }
        }
        if (best == null) return false;
        if (best.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var pattern))
        {
            ((System.Windows.Automation.InvokePattern)pattern).Invoke();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Opens the best match for what you said. Close-but-not-sure matches ("riot games" → Riot Client)
    /// are asked about first: "Did you mean Riot Client?", then the notch listens for yes / no.
    /// </summary>
    private string OpenSmart(string name)
    {
        string n = name.Trim();
        if (n is "music" or "my music" or "music app") { OpenMusicApp(_cfg); return "Opening your music."; }

        var match = _finder.Find(n);
        if (match == null || match.Score < 0.45) return $"I couldn't find {n}.";
        if (match.Score >= 0.82)
        {
            match.Target.Open();
            return $"Opening {match.Target.Name}.";
        }
        return Ask($"Did you mean {match.Target.Name}?", () =>
        {
            match.Target.Open();
            return Task.FromResult($"Opening {match.Target.Name}.");
        });
    }

    /// <summary>Asks a yes/no question; if the next thing you say is "yes", runs the action.</summary>
    private string Ask(string question, Func<Task<string>> onYes)
    {
        _pendingYes = onYes;
        _pendingUntil = DateTime.Now.AddSeconds(25);
        ExpectsAnswer = true;
        return question;
    }

    private string ConnectBluetooth(string spoken, bool connect)
    {
        var devices = BluetoothAudio.List();
        if (devices.Count == 0)
            return OpenBluetoothSettings("I couldn't find any paired Bluetooth headphones or speakers, so here are the Bluetooth settings.");

        string wanted = System.Text.RegularExpressions.Regex.Replace(spoken ?? "", @"\b(bluetooth|device|devices|headphones?|headset|earbuds|buds|speaker|speakers|my|the|to|it)\b", " ").Trim();
        var candidates = devices.Where(d => d.Connected != connect).ToList(); // only ones that can change
        BluetoothAudio.Device target;
        if (wanted.Length > 0) target = BluetoothAudio.Find(devices, spoken) ?? BluetoothAudio.Find(devices, wanted);
        else if (candidates.Count == 1) target = candidates[0];
        else if (candidates.Count == 0)
            return connect ? "Your Bluetooth audio is already connected." : "Nothing's connected over Bluetooth.";
        else
            return Ask($"Which one? {string.Join(", or ", candidates.Select(d => d.Name))}. Say connect and the name.", () => Task.FromResult("Okay."));

        if (target == null) return $"I couldn't find {spoken} in your Bluetooth devices.";
        if (target.Connected == connect) return connect ? $"{target.Name} is already connected." : $"{target.Name} isn't connected.";

        bool ok = BluetoothAudio.SetConnected(target, connect);
        if (ok) return connect ? $"Connecting {target.Name}." : $"Disconnected {target.Name}.";
        return OpenBluetoothSettings($"{target.Name} didn't respond. Is it switched on and nearby? Here are the Bluetooth settings.");
    }

    private static string OpenBluetoothSettings(string message)
    {
        Open("ms-settings:bluetooth");
        return message;
    }

    /// <summary>The "Music" tile follows your music-app setting (older configs pointed it straight at YouTube Music).</summary>
    public static bool IsMusicTile(ShortcutItem s) =>
        s.Target.Equals("music:", StringComparison.OrdinalIgnoreCase) ||
        (s.Name.Equals("Music", StringComparison.OrdinalIgnoreCase) && s.Target.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase));

    public static bool SpotifyInstalled()
    {
        try { using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("spotify"); return k != null; }
        catch { return false; }
    }

    /// <summary>Opens the music app you picked in Settings (Spotify app, Spotify web, or YouTube Music).</summary>
    public static void OpenMusicApp(AppConfig cfg)
    {
        if ((cfg.MusicService ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            if (!(SpotifyInstalled() && Open("spotify:"))) Open("https://open.spotify.com");
        }
        else Open("https://music.youtube.com");
    }

    public static bool Open(string target, string args = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { Arguments = args ?? "", UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Open '{target}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Asks the AI picked in Settings, keeping a few turns of memory so follow-up questions work.</summary>
    private async Task<string> AskAiAsync(string question)
    {
        _history.Add(("user", question));
        while (_history.Count > 10) _history.RemoveAt(0);
        if (_history[0].role != "user") _history.RemoveAt(0);

        string system =
            "You are a voice assistant that lives in a small notch at the top of the user's Windows PC screen. " +
            $"Today is {DateTime.Now:dddd d MMMM yyyy}, {DateTime.Now:h:mm tt}. " +
            "The text you get comes from speech recognition, so it may have small mistakes; guess what was meant.\n\n" +
            "If the user wants you to DO something on the PC, reply with ONLY one line of JSON and nothing else, using one of:\n" +
            "{\"action\":\"play\",\"query\":\"<song, artist or video>\",\"where\":\"youtube music|youtube|spotify\"}\n" +
            "{\"action\":\"pause\"} {\"action\":\"resume\"} {\"action\":\"next\"} {\"action\":\"previous\"}\n" +
            "{\"action\":\"set_volume\",\"amount\":50} (exact %), {\"action\":\"change_volume\",\"amount\":-10} (up/down by %), {\"action\":\"mute\"}\n" +
            "{\"action\":\"bluetooth_connect\",\"target\":\"<device name or empty>\"} {\"action\":\"bluetooth_disconnect\",\"target\":\"...\"} " +
            "{\"action\":\"bluetooth_on\"} {\"action\":\"bluetooth_off\"}\n" +
            "{\"action\":\"open\",\"target\":\"<app or website name>\"}\n" +
            "{\"action\":\"search\",\"query\":\"<web search>\"}\n" +
            "{\"action\":\"weather\"} {\"action\":\"time\"}\n" +
            "For play, leave \"where\" empty unless they name YouTube, YouTube Music or Spotify, and put the song name in its normal spelling " +
            "(e.g. \"i pad\" -> \"iPad\"). Never pretend you did something without sending the JSON.\n\n" +
            "Otherwise, answer the question. Your answer is shown in a tiny box and read aloud, so use one to three short " +
            "sentences, plain text only: no markdown, lists or emoji.";
        try
        {
            string answer = await AiClient.ChatAsync(_cfg, system, _history);
            _history.Add(("assistant", answer));
            return answer;
        }
        catch (Exception ex)
        {
            Log.Write("AI: " + ex.Message);
            _history.RemoveAt(_history.Count - 1);
            return ex.Message.Contains("401") || ex.Message.Contains("403")
                ? "The AI rejected the API key. Check it in Settings."
                : ex.Message.Contains("429") ? "The AI is rate limited right now, try again in a minute."
                : "I couldn't reach the AI right now.";
        }
    }
}
