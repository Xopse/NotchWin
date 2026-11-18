using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace NotchWin.Services;

public class NotificationItem
{
    public string App = "";
    public string Title = "";
    public string Body = "";
    /// <summary>Windows' id for the sending app (used to open it when you click the notification).</summary>
    public string Handler = "";
    public DateTime Time = DateTime.Now;
}

/// <summary>
/// Windows doesn't let normal (non-Store) apps subscribe to other apps' notifications,
/// but it keeps every toast in a small SQLite database in your user folder. We peek at it
/// every couple of seconds (read-only) and pass the important ones to the notch.
/// </summary>
public class NotificationService
{
    private readonly AppConfig _cfg;
    private readonly string _dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Notifications\wpndatabase.db");
    private long _lastId = -1;
    private bool _warned;

    public event Action<NotificationItem> Received;

    public NotificationService(AppConfig cfg)
    {
        _cfg = cfg;
        SQLitePCL.Batteries_V2.Init(); // use Windows' built-in SQLite (winsqlite3.dll)
    }

    private FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private int _pendingPoll;

    /// <summary>
    /// Instead of checking constantly, we let Windows tell us when the notification database changes,
    /// plus a slow safety check every minute.
    /// </summary>
    public void Start()
    {
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_dbPath), "wpndatabase.db*")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (s, e) =>
            {
                // Our own reads can touch the "-shm" file; only real writes (db / wal) mean a new toast.
                if (e.Name != null && e.Name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)) return;
                SchedulePoll();
            };
        }
        catch (Exception ex) { Log.Write("Notification watcher: " + ex.Message); }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await SafePollAsync();
                await Task.Delay(TimeSpan.FromSeconds(60));
            }
        });
    }

    /// <summary>The database is written several times per toast; wait a moment and read once.</summary>
    private void SchedulePoll()
    {
        if (Interlocked.Exchange(ref _pendingPoll, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            Interlocked.Exchange(ref _pendingPoll, 0);
            await SafePollAsync();
        });
    }

    private async Task SafePollAsync()
    {
        await _pollGate.WaitAsync();
        try { Poll(); }
        catch (Exception ex)
        {
            if (!_warned) { Log.Write("Notifications: " + ex.Message); _warned = true; }
        }
        finally { _pollGate.Release(); }
    }

    private void Poll()
    {
        if (!File.Exists(_dbPath)) return;

        string path = _dbPath;
        string tempCopy = null;
        SqliteConnection conn = null;
        try
        {
            try
            {
                conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Shared;Pooling=False");
                conn.Open();
            }
            catch
            {
                // Locked? Read a snapshot copy instead.
                conn?.Dispose();
                tempCopy = Path.Combine(Path.GetTempPath(), "notchwin_wpn.db");
                File.Copy(_dbPath, tempCopy, true);
                if (File.Exists(_dbPath + "-wal")) File.Copy(_dbPath + "-wal", tempCopy + "-wal", true);
                conn = new SqliteConnection($"Data Source={tempCopy};Pooling=False");
                conn.Open();
            }

            if (_lastId < 0)
            {
                // First run: start from "now" so we don't replay old notifications.
                using var max = conn.CreateCommand();
                max.CommandText = "SELECT IFNULL(MAX(Id), 0) FROM Notification";
                _lastId = Convert.ToInt64(max.ExecuteScalar());
                return;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT n.Id, n.Payload, h.PrimaryId
                FROM Notification n
                LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                WHERE n.Id > $last AND n.Type = 'toast'
                ORDER BY n.Id";
            cmd.Parameters.AddWithValue("$last", _lastId);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                _lastId = Math.Max(_lastId, r.GetInt64(0));
                string payload = r.IsDBNull(1) ? "" :
                    r.GetValue(1) is byte[] b ? Encoding.UTF8.GetString(b) : r.GetString(1);
                string handler = r.IsDBNull(2) ? "" : r.GetString(2);

                var item = Parse(payload, handler);
                if (item != null && IsImportant(item)) Received?.Invoke(item);
            }
        }
        finally
        {
            conn?.Dispose();
            if (tempCopy != null) { try { File.Delete(tempCopy); File.Delete(tempCopy + "-wal"); } catch { } }
        }
    }

    private static NotificationItem Parse(string xml, string handler)
    {
        try
        {
            var doc = XDocument.Parse(xml.TrimEnd('\0'));
            var texts = doc.Descendants("text").Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
            if (texts.Count == 0) return null;
            return new NotificationItem
            {
                Handler = handler ?? "",
                App = FriendlyApp(handler),
                Title = texts[0],
                Body = string.Join(" · ", texts.Skip(1)),
            };
        }
        catch { return null; }
    }

    private static string FriendlyApp(string handler)
    {
        if (string.IsNullOrEmpty(handler)) return "Notification";
        string h = handler.ToLowerInvariant();
        if (h.Contains("whatsapp")) return "WhatsApp";
        if (h.Contains("discord")) return "Discord";
        if (h.Contains("telegram")) return "Telegram";
        if (h.Contains("outlook")) return "Outlook";
        if (h.Contains("teams")) return "Teams";
        if (h.Contains("instagram")) return "Instagram";
        if (h.Contains("yourphone") || h.Contains("phonelink")) return "Phone Link";
        if (h.Contains("chrome")) return "Chrome";
        if (h.Contains("msedge")) return "Edge";
        string name = handler.Split('!')[0].Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private bool IsImportant(NotificationItem n)
    {
        if (_cfg.ShowAllNotifications) return true;
        if (_cfg.ImportantApps.Any(a => n.App.Contains(a, StringComparison.OrdinalIgnoreCase))) return true;
        string text = n.Title + " " + n.Body;
        return _cfg.ImportantKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
