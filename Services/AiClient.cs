using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;

namespace NotchWin.Services;

public record AiProviderInfo(string Id, string Name, string BaseUrl, string DefaultModel, string KeyUrl, string Note);

/// <summary>
/// Talks to the AI you picked in Settings. Groq, Grok (xAI) and most others speak the
/// "OpenAI-compatible" format; Claude has its own, so it gets a separate path.
/// </summary>
public class AiClient
{
    public static readonly AiProviderInfo[] Providers =
    {
        new("groq", "Groq — free tier", "https://api.groq.com/openai/v1", "openai/gpt-oss-120b",
            "https://console.groq.com/keys", "Free plan, no card needed. Fast."),
        new("xai", "Grok (xAI)", "https://api.x.ai/v1", "grok-4.3",
            "https://console.x.ai", "Paid per use (sometimes promo credits)."),
        new("anthropic", "Claude (Anthropic)", "https://api.anthropic.com/v1", "claude-haiku-5-5",
            "https://console.anthropic.com", "Paid per use."),
        new("custom", "Custom (OpenAI-compatible)", "", "",
            "", "Any server with a /chat/completions endpoint (LM Studio, Ollama, OpenRouter...)."),
    };

    public static AiProviderInfo Find(string id) =>
        Providers.FirstOrDefault(p => p.Id.Equals(id ?? "", StringComparison.OrdinalIgnoreCase)) ?? Providers[0];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string BaseUrlFor(AppConfig cfg)
    {
        var p = Find(cfg.AiProvider);
        string url = p.Id == "custom" ? cfg.AiBaseUrl : p.BaseUrl;
        return (url ?? "").TrimEnd('/');
    }

    public static string ModelFor(AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.AiModel) ? Find(cfg.AiProvider).DefaultModel : cfg.AiModel.Trim();

    /// <summary>Sends the conversation and returns the reply text. Throws with a readable message on failure.</summary>
    public static async Task<string> ChatAsync(AppConfig cfg, string system, IList<(string role, string text)> messages,
                                               CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.AiApiKey) && cfg.AiProvider != "custom")
            throw new Exception("No API key set (Settings → Voice & AI).");

        string baseUrl = BaseUrlFor(cfg);
        string model = ModelFor(cfg);
        if (string.IsNullOrEmpty(baseUrl)) throw new Exception("No API address set.");
        if (string.IsNullOrEmpty(model)) throw new Exception("No model set.");

        HttpRequestMessage req;
        if (cfg.AiProvider == "anthropic")
        {
            req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/messages")
            {
                Content = JsonContent.Create(new
                {
                    model,
                    max_tokens = 400,
                    system,
                    messages = messages.Select(m => new { role = m.role, content = m.text }).ToArray(),
                }),
            };
            req.Headers.Add("x-api-key", cfg.AiApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            var all = new List<object> { new { role = "system", content = system } };
            all.AddRange(messages.Select(m => (object)new { role = m.role, content = m.text }));
            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = all,
                ["max_tokens"] = 1024, // reasoning models spend some of this "thinking"
            };
            // gpt-oss models think before answering; keep that short so replies are quick.
            if (model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)) body["reasoning_effort"] = "low";

            req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions") { Content = JsonContent.Create(body) };
            if (!string.IsNullOrWhiteSpace(cfg.AiApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.AiApiKey.Trim());
        }

        using (req)
        using (var resp = await Http.SendAsync(req, ct))
        {
            string json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new Exception($"{(int)resp.StatusCode}: {ErrorText(json)}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string text;
            if (cfg.AiProvider == "anthropic")
            {
                text = string.Concat(root.GetProperty("content").EnumerateArray()
                    .Where(b => b.GetProperty("type").GetString() == "text")
                    .Select(b => b.GetProperty("text").GetString()));
            }
            else
            {
                var msg = root.GetProperty("choices")[0].GetProperty("message");
                text = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "";
            }
            text = (text ?? "").Trim();
            if (text.Length == 0) throw new Exception("The AI sent back an empty answer.");
            return text;
        }
    }

    /// <summary>Lists the model names your key can use (for the dropdown in Settings).</summary>
    public static async Task<List<string>> ListModelsAsync(AppConfig cfg)
    {
        string baseUrl = BaseUrlFor(cfg);
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
        if (cfg.AiProvider == "anthropic")
        {
            req.Headers.Add("x-api-key", cfg.AiApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(cfg.AiApiKey))
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.AiApiKey.Trim());
        }

        using var resp = await Http.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"{(int)resp.StatusCode}: {ErrorText(json)}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrEmpty(id))
            // speech / image / moderation models can't chat
            .Where(id => !System.Text.RegularExpressions.Regex.IsMatch(id, "whisper|tts|guard|embed|image|vision-only|playai|orpheus", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderBy(id => id)
            .ToList();
    }

    private static string ErrorText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString();
                if (e.TryGetProperty("message", out var m)) return m.GetString();
            }
        }
        catch { }
        return json.Length > 200 ? json[..200] : json;
    }
}
