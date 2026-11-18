using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NotchWin.Services;

/// <summary>
/// Keeps NotchWin up to date from GitHub Releases.
/// When you push a new version tag, GitHub builds a release; every installed copy notices it
/// (checked at start-up and every 6 hours), downloads it, swaps its files and restarts itself.
///
/// Note: other people's copies can only see releases of a PUBLIC repository.
/// </summary>
public static class Updater
{
    public record Release(Version Version, string Tag, string ZipUrl);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static Updater()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("NotchWin-Updater");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Only update the installed copy (not a copy you're running from the source folder while developing).</summary>
    public static bool IsInstalledCopy =>
        AppContext.BaseDirectory.TrimEnd('\\').EndsWith(@"\Programs\NotchWin", StringComparison.OrdinalIgnoreCase);

    /// <summary>The newest release on GitHub, or an explanation of why it couldn't be checked.</summary>
    public static async Task<(Release release, string problem)> GetLatestAsync(string repo)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, "No public release found. Updates only work once the GitHub repository is public and has a release.");
            if (!resp.IsSuccessStatusCode) return (null, $"GitHub answered {(int)resp.StatusCode}.");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return (null, $"Release tag '{tag}' isn't a version number.");
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));

            string zip = root.GetProperty("assets").EnumerateArray()
                .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString()))
                .Where(a => a.name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.url)
                .FirstOrDefault();
            if (zip == null) return (null, $"Release {tag} has no NotchWin download attached (yet).");
            return (new Release(version, tag, zip), null);
        }
        catch (Exception ex) { return (null, "Couldn't reach GitHub: " + ex.Message); }
    }

    /// <summary>Downloads and unpacks the release. Returns the folder that holds the new NotchWin.exe.</summary>
    public static async Task<(string appDir, string pkgRoot)> DownloadAsync(Release r)
    {
        string root = Path.Combine(Path.GetTempPath(), "NotchWin-update-" + r.Tag);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        string zipPath = Path.Combine(root, "package.zip");
        using (var resp = await Http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs);
        }

        string pkg = Path.Combine(root, "package");
        ZipFile.ExtractToDirectory(zipPath, pkg, true);
        string exe = Directory.EnumerateFiles(pkg, "NotchWin.exe", SearchOption.AllDirectories).FirstOrDefault()
                     ?? throw new Exception("The download doesn't contain NotchWin.exe.");
        return (Path.GetDirectoryName(exe), pkg);
    }

    /// <summary>
    /// Starts a tiny helper that waits for NotchWin to close, copies the new files in and starts it again.
    /// The caller should exit the app right after this.
    /// </summary>
    public static void ApplyAndRestart(string appDir, string pkgRoot, Version version)
    {
        string dest = AppContext.BaseDirectory.TrimEnd('\\');
        string script = Path.Combine(Path.GetTempPath(), "notchwin-update.ps1");
        File.WriteAllText(script, @"
param([int]$ProcId, [string]$Source, [string]$Dest, [string]$Version, [string]$PkgRoot)
try { Wait-Process -Id $ProcId -Timeout 30 -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Milliseconds 800
Copy-Item (Join-Path $Source '*') $Dest -Recurse -Force
$un = Join-Path $PkgRoot 'uninstall.ps1'
if (Test-Path $un) { Copy-Item $un $Dest -Force }
Get-ChildItem $Dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NotchWin' -Name DisplayVersion -Value $Version -ErrorAction SilentlyContinue
Start-Process (Join-Path $Dest 'NotchWin.exe')
");
        string args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
                      $"-ProcId {Environment.ProcessId} -Source \"{appDir}\" -Dest \"{dest}\" -Version \"{version}\" -PkgRoot \"{pkgRoot}\"";
        Process.Start(new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, CreateNoWindow = true });
    }
}
