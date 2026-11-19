using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace NotchWin.Services;

/// <summary>
/// Finds the thing you asked to open: your shortcuts, folders like Downloads, every app in the Start menu
/// (including Store apps like WhatsApp and Spotify), and common websites. Matching is forgiving, so
/// "riot games" finds "Riot Client" (and the assistant asks before opening a not-quite-exact match).
/// </summary>
public class AppFinder
{
    public record Target(string Name, Action Open);
    public record Match(Target Target, double Score);

    private readonly AppConfig _cfg;
    private List<Target> _apps = new();
    private DateTime _appsLoaded = DateTime.MinValue;
    private readonly object _lock = new();

    public AppFinder(AppConfig cfg) => _cfg = cfg;

    public Match Find(string spoken)
    {
        string s = Clean(spoken);
        if (s.Length == 0) return null;

        var all = new List<Target>();
        all.AddRange(Folders());
        all.AddRange(_cfg.Shortcuts.Select(sc => new Target(sc.Name, () =>
        {
            if (CommandRouter.IsMusicTile(sc)) CommandRouter.OpenMusicApp(_cfg);
            else CommandRouter.Open(sc.Target, sc.Args);
        })));
        all.AddRange(Apps());
        all.AddRange(Websites());

        return all
            .Select(t => new Match(t, Similarity(s, Clean(t.Name))))
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Target.Name.Length) // "Steam" before "Steam Link" on a tie
            .FirstOrDefault();
    }

    private static string Clean(string s)
    {
        s = (s ?? "").ToLowerInvariant();
        s = Regex.Replace(s, @"\b(the|my|app|application|folder|program|please)\b", " ");
        s = Regex.Replace(s, @"[^a-z0-9 ]", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // ---------------- what can be opened ----------------

    private static IEnumerable<Target> Folders()
    {
        Target F(string name, string path) => new(name, () => CommandRouter.Open("explorer.exe", $"\"{path}\""));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return F("Downloads", KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B")) ?? Path.Combine(profile, "Downloads"));
        yield return F("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        yield return F("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        yield return F("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        yield return F("Photos folder", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        yield return F("Music folder", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        yield return F("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        yield return F("Screenshots", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"));
        yield return F("User folder", profile);
        yield return new Target("This PC", () => CommandRouter.Open("explorer.exe", "shell:MyComputerFolder"));
        yield return new Target("File Explorer", () => CommandRouter.Open("explorer.exe"));
        yield return new Target("Recycle Bin", () => CommandRouter.Open("explorer.exe", "shell:RecycleBinFolder"));
        yield return new Target("Control Panel", () => CommandRouter.Open("control.exe"));
        yield return new Target("Windows Settings", () => CommandRouter.Open("ms-settings:"));
        yield return new Target("Bluetooth settings", () => CommandRouter.Open("ms-settings:bluetooth"));
        yield return new Target("Sound settings", () => CommandRouter.Open("ms-settings:sound"));
        yield return new Target("Display settings", () => CommandRouter.Open("ms-settings:display"));
        yield return new Target("Wi-Fi settings", () => CommandRouter.Open("ms-settings:network-wifi"));
    }

    private static IEnumerable<Target> Websites()
    {
        var sites = new (string name, string url)[]
        {
            ("YouTube", "https://www.youtube.com"), ("YouTube Music", "https://music.youtube.com"),
            ("Gmail", "https://mail.google.com"), ("Google", "https://www.google.com"),
            ("GitHub", "https://github.com"), ("Reddit", "https://www.reddit.com"),
            ("Twitter", "https://x.com"), ("X", "https://x.com"), ("Instagram", "https://www.instagram.com"),
            ("Netflix", "https://www.netflix.com"), ("Twitch", "https://www.twitch.tv"),
            ("ChatGPT", "https://chatgpt.com"), ("Claude", "https://claude.ai"),
            ("WhatsApp Web", "https://web.whatsapp.com"), ("Amazon", "https://www.amazon.in"),
            ("Hotstar", "https://www.hotstar.com"), ("Prime Video", "https://www.primevideo.com"),
        };
        return sites.Select(x => new Target(x.name, () => CommandRouter.Open(x.url)));
    }

    /// <summary>Every app in the Start menu, including Store apps (read via the Windows shell, cached for 10 minutes).</summary>
    private List<Target> Apps()
    {
        lock (_lock)
        {
            if ((DateTime.Now - _appsLoaded).TotalMinutes < 10 && _apps.Count > 0) return _apps;

            var list = new List<Target>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The shell's "Applications" folder lists exactly what the Start menu shows. It's a COM object,
            // which is happiest on its own STA thread.
            var t = new Thread(() =>
            {
                try
                {
                    var shellType = Type.GetTypeFromProgID("Shell.Application");
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic folder = shell.NameSpace("shell:AppsFolder");
                    foreach (dynamic item in folder.Items())
                    {
                        string name = item.Name;
                        string id = item.Path;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(name)) continue;
                        list.Add(new Target(name, () => CommandRouter.Open("explorer.exe", "shell:AppsFolder\\" + id)));
                    }
                }
                catch (Exception ex) { Log.Write("App list: " + ex.Message); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(TimeSpan.FromSeconds(8));

            // Fallback / extra: Start menu shortcuts.
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     })
            {
                try
                {
                    foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileNameWithoutExtension(lnk);
                        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                        string path = lnk;
                        list.Add(new Target(name, () => CommandRouter.Open(path)));
                    }
                }
                catch { }
            }

            _apps = list;
            _appsLoaded = DateTime.Now;
            return _apps;
        }
    }

    // ---------------- how close two names are (0..1) ----------------

    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1.0;
        string ca = a.Replace(" ", ""), cb = b.Replace(" ", "");
        if (ca == cb) return 0.98;                                   // "you tube" = "youtube"

        double best = 0;
        var wa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matched = wa.Count(x => wb.Any(y => WordsMatch(x, y)));
        int exact = wa.Count(x => wb.Contains(x));

        // Every word you said is a word of the name ("chrome" → "Google Chrome", "word" → "Microsoft Word").
        // Whole-word matches beat partial ones ("word" → Microsoft Word, not WordPad).
        if (matched == wa.Length)
            best = Math.Max(best, Math.Max(0.6, (exact == wa.Length ? 0.88 : 0.8) - 0.03 * (wb.Length - wa.Length)));
        // The name starts with / contains what you said ("download" → "Downloads").
        if (cb.StartsWith(ca) && ca.Length >= 3) best = Math.Max(best, 0.86 - 0.2 * (cb.Length - ca.Length) / cb.Length);
        else if (cb.Contains(ca) && ca.Length >= 4) best = Math.Max(best, 0.75 - 0.2 * (cb.Length - ca.Length) / cb.Length);

        // Some words in common: "riot games" vs "riot client".
        double words = matched / (double)Math.Max(wa.Length, wb.Length);
        if (matched > 0 && WordsMatch(wa[0], wb[0])) words = Math.Min(0.8, words + 0.15); // same first word
        best = Math.Max(best, words);

        // Spelling: "discrod" vs "discord".
        best = Math.Max(best, 1.0 - Distance(ca, cb) / (double)Math.Max(ca.Length, cb.Length));
        return best;
    }

    private static bool WordsMatch(string x, string y) =>
        x == y || (x.Length >= 4 && y.Length >= 4 && (x.StartsWith(y) || y.StartsWith(x) || Distance(x, y) <= 1));

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // ---------------- Windows "known folders" (finds Downloads even if you moved it) ----------------

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }
}
