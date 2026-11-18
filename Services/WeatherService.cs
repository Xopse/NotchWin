using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Net.Http;
using System.Globalization;
using System.Text.Json;

namespace NotchWin.Services;

public class WeatherInfo
{
    public string City = "";
    public double Temp;
    public double FeelsLike;
    public int Humidity;
    public double Wind;
    public string Description = "";
    public string Icon = "";
    public string Unit = "°C";
}

/// <summary>Weather from Open-Meteo (free, no API key). Refreshes every 15 minutes.</summary>
public class WeatherService
{
    private readonly AppConfig _cfg;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private double? _lat, _lon;
    private string _city;

    public WeatherInfo Current { get; private set; }

    public WeatherService(AppConfig cfg)
    {
        _cfg = cfg;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NotchWin/1.0");
    }

    public void Start() => _ = Task.Run(async () =>
    {
        while (true)
        {
            bool ok = await RefreshAsync();
            await Task.Delay(ok ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(1));
        }
    });

    private async Task<bool> RefreshAsync()
    {
        try
        {
            if (_lat == null) await ResolveLocationAsync();
            if (_lat == null) return false;

            var inv = CultureInfo.InvariantCulture;
            string url = "https://api.open-meteo.com/v1/forecast" +
                         $"?latitude={_lat.Value.ToString(inv)}&longitude={_lon.Value.ToString(inv)}" +
                         "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day" +
                         (_cfg.UseFahrenheit ? "&temperature_unit=fahrenheit" : "");
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var c = doc.RootElement.GetProperty("current");
            int code = c.GetProperty("weather_code").GetInt32();
            bool day = c.TryGetProperty("is_day", out var d) && d.GetInt32() == 1;
            var (desc, icon) = Describe(code, day);
            Current = new WeatherInfo
            {
                City = _city ?? "",
                Temp = c.GetProperty("temperature_2m").GetDouble(),
                FeelsLike = c.GetProperty("apparent_temperature").GetDouble(),
                Humidity = (int)c.GetProperty("relative_humidity_2m").GetDouble(),
                Wind = c.GetProperty("wind_speed_10m").GetDouble(),
                Description = desc,
                Icon = icon,
                Unit = _cfg.UseFahrenheit ? "°F" : "°C",
            };
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Weather: " + ex.Message);
            return false;
        }
    }

    private async Task ResolveLocationAsync()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.City))
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&name=" + Uri.EscapeDataString(_cfg.City.Trim());
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            if (doc.RootElement.TryGetProperty("results", out var res) && res.GetArrayLength() > 0)
            {
                var r = res[0];
                _lat = r.GetProperty("latitude").GetDouble();
                _lon = r.GetProperty("longitude").GetDouble();
                _city = r.GetProperty("name").GetString();
                return;
            }
            Log.Write($"City '{_cfg.City}' not found, falling back to IP location.");
        }

        // Approximate location from your IP address.
        using var ip = JsonDocument.Parse(await _http.GetStringAsync("https://ipwho.is/"));
        var root = ip.RootElement;
        if (root.TryGetProperty("success", out var okProp) && !okProp.GetBoolean()) return;
        _lat = root.GetProperty("latitude").GetDouble();
        _lon = root.GetProperty("longitude").GetDouble();
        _city = root.TryGetProperty("city", out var cityProp) ? cityProp.GetString() : "";
    }

    /// <summary>WMO weather codes → text + a simple symbol.</summary>
    private static (string, string) Describe(int code, bool day) => code switch
    {
        0 => ("Clear", day ? "☀" : "☾"),
        1 => ("Mostly clear", day ? "☀" : "☾"),
        2 => ("Partly cloudy", "⛅"),
        3 => ("Cloudy", "☁"),
        45 or 48 => ("Fog", "≋"),
        51 or 53 or 55 or 56 or 57 => ("Drizzle", "☂"),
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => ("Rain", "☂"),
        71 or 73 or 75 or 77 or 85 or 86 => ("Snow", "❄"),
        95 or 96 or 99 => ("Thunderstorm", "⚡"),
        _ => ("—", "☁"),
    };

    public string SpokenSummary()
    {
        var w = Current;
        if (w == null) return "I don't have the weather yet.";
        string where = string.IsNullOrEmpty(w.City) ? "" : $" in {w.City}";
        return $"It's {Math.Round(w.Temp)} degrees and {w.Description.ToLowerInvariant()}{where}, feels like {Math.Round(w.FeelsLike)}.";
    }
}
