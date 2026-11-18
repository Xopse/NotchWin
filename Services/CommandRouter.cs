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

    public CommandRouter(AppConfig cfg, MediaService media, WeatherService weather)
    {
        _cfg = cfg;
        _media = media;
        _weather = weather;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    /// <summary>Runs the command and returns what the assistant should say back.</summary>
    public async Task<string> HandleAsync(string said)
    {
        string c = Normalize(said);

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
        if (c.Contains("volume up") || c.Contains("louder") || c.Contains("increase the volume") || c.Contains("turn it up"))
            return await RunActionAsync(new AiAction("volume_up"));
        if (c.Contains("volume down") || c.Contains("quieter") || c.Contains("decrease the volume") || c.Contains("turn it down"))
            return await RunActionAsync(new AiAction("volume_down"));
        if (c is "mute" or "unmute" or "mute it" or "unmute it" || c.Contains("mute the volume"))
            return await RunActionAsync(new AiAction("mute"));

        // ----- play something ("play ipad on youtube music", "put on lofi beats") -----
        var play = PlayRegex.Match(c);
        if (play.Success)
        {
            string query = play.Groups[1].Value.Trim();
            string where = play.Groups[2].Success ? play.Groups[2].Value : "youtube music";
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
            string where = Get("where").ToLowerInvariant();
            if (where.Length > 0) action.Where = where;
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
            case "volume_up": for (int i = 0; i < 5; i++) Native.PressKey(Native.VK_VOLUME_UP); return "Louder.";
            case "volume_down": for (int i = 0; i < 5; i++) Native.PressKey(Native.VK_VOLUME_DOWN); return "Quieter.";
            case "mute": Native.PressKey(Native.VK_VOLUME_MUTE); return "Done.";

            case "play":
                if (string.IsNullOrWhiteSpace(a.Query)) { await _media.PlayAsync(); return "Playing."; }
                if (a.Where.Contains("spotify"))
                {
                    Open("spotify:search:" + Uri.EscapeDataString(a.Query));
                    return $"Searching Spotify for {a.Query}.";
                }
                bool music = !a.Where.Equals("youtube", StringComparison.OrdinalIgnoreCase);
                await PlayOnYouTubeAsync(a.Query, music);
                return $"Playing {a.Query}{(music ? "" : " on YouTube")}.";

            case "open":
                if (string.IsNullOrWhiteSpace(a.Target)) return "Open what?";
                return TryOpen(a.Target) ? $"Opening {a.Target}." : $"I couldn't find {a.Target}.";

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

    private bool TryOpen(string name)
    {
        // 1) Your shortcuts from config.json
        var sc = _cfg.Shortcuts.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
              ?? _cfg.Shortcuts.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (sc != null) return Open(sc.Target, sc.Args);

        // 2) Anything in the Start menu
        var lnk = FindStartMenuShortcut(name);
        if (lnk != null) return Open(lnk);

        // 3) Common websites ("open youtube")
        string site = name.Replace(" ", "");
        if (Regex.IsMatch(site, @"^[a-z0-9-]+$") && new[] { "youtube", "gmail", "github", "reddit", "twitter", "instagram", "netflix", "chatgpt", "claude", "whatsapp", "twitch" }.Contains(site))
        {
            string url = site switch
            {
                "gmail" => "https://mail.google.com",
                "claude" => "https://claude.ai",
                "chatgpt" => "https://chatgpt.com",
                "whatsapp" => "https://web.whatsapp.com",
                "twitter" => "https://x.com",
                _ => $"https://www.{site}.com",
            };
            return Open(url);
        }
        return false;
    }

    private static string FindStartMenuShortcut(string name)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        var all = new List<string>();
        foreach (var r in roots)
        {
            try { all.AddRange(Directory.EnumerateFiles(r, "*.lnk", SearchOption.AllDirectories)); } catch { }
        }
        string n = name.Replace(" ", "");
        // Exact name first, then "starts with", then "contains". Skip uninstallers.
        var candidates = all.Where(p => !Path.GetFileName(p).Contains("uninstall", StringComparison.OrdinalIgnoreCase)).ToList();
        string Clean(string p) => Path.GetFileNameWithoutExtension(p).Replace(" ", "");
        return candidates.FirstOrDefault(p => Clean(p).Equals(n, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(p => Clean(p).StartsWith(n, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(p => Clean(p).Contains(n, StringComparison.OrdinalIgnoreCase));
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
            "{\"action\":\"volume_up\"} {\"action\":\"volume_down\"} {\"action\":\"mute\"}\n" +
            "{\"action\":\"open\",\"target\":\"<app or website name>\"}\n" +
            "{\"action\":\"search\",\"query\":\"<web search>\"}\n" +
            "{\"action\":\"weather\"} {\"action\":\"time\"}\n" +
            "For play, use \"youtube music\" unless they name YouTube or Spotify, and put the song name in its normal spelling " +
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
