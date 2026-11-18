using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Net.Http;
using System.IO.Compression;
using System.Speech.Synthesis;
using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace NotchWin.Services;

/// <summary>
/// Offline voice assistant.
/// Wake-word mode: say "computer ..." any time. Hotkey mode: press Ctrl+Alt+Space and talk (mic is off otherwise).
/// Speech recognition runs locally with Vosk, nothing is sent anywhere unless you add an AI key.
/// </summary>
public class VoiceAssistant : IDisposable
{
    public enum State { Off, Downloading, Idle, Listening, Thinking }

    private readonly AppConfig _cfg;
    private Model _model;
    private VoskRecognizer _rec;
    private WaveInEvent _mic;
    private SpeechSynthesizer _tts;
    private volatile bool _listening;
    private DateTime _listenUntil;
    private int _speakCount; // >0 while the assistant is talking
    private DateTime _ignoreUntil;
    private readonly object _recLock = new();
    private bool _micOn;
    private readonly object _micLock = new();

    /// <summary>Wake-word mode keeps the mic open; hotkey mode only opens it when you press Ctrl+Alt+Space.</summary>
    private bool AlwaysListening => string.Equals(_cfg.VoiceMode, "WakeWord", StringComparison.OrdinalIgnoreCase);

    public State Status { get; private set; } = State.Off;
    public event Action<State, string> StatusChanged;   // state + optional detail text
    public event Action<string> CommandHeard;           // what you said after the wake word

    public VoiceAssistant(AppConfig cfg) => _cfg = cfg;

    public void Start() => _ = Task.Run(async () =>
    {
        try
        {
            string modelDir = await EnsureModelAsync();
            Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(modelDir);
            _rec = new VoskRecognizer(_model, 16000f);

            _tts = new SpeechSynthesizer();
            _tts.SetOutputToDefaultAudioDevice();
            _tts.SpeakCompleted += (s, e) =>
            {
                if (Interlocked.Decrement(ref _speakCount) < 0) Interlocked.Exchange(ref _speakCount, 0);
                _ignoreUntil = DateTime.Now.AddMilliseconds(400);
            };

            _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
            _mic.DataAvailable += OnAudio;
            _mic.RecordingStopped += (s, e) => { if (e.Exception != null) Log.Write("Mic stopped: " + e.Exception.Message); };
            if (AlwaysListening) MicOn();

            SetStatus(State.Idle);
            _ = Task.Run(ListenTimeoutLoop);
        }
        catch (Exception ex)
        {
            Log.Write("Voice assistant failed to start: " + ex);
            SetStatus(State.Off, "Voice off: " + ex.Message);
        }
    });

    private void SetStatus(State s, string detail = null)
    {
        Status = s;
        StatusChanged?.Invoke(s, detail);
    }

    /// <summary>Start listening for a command without the wake word (hotkey / right-click menu).</summary>
    public void ListenNow()
    {
        if (_rec == null) return;
        MicOn();
        _listening = true;
        _listenUntil = DateTime.Now.AddSeconds(7);
        lock (_recLock) _rec.Reset();
        SetStatus(State.Listening);
    }

    public void Done() { if (_rec != null) SetStatus(State.Idle); }

    private void MicOn()
    {
        lock (_micLock)
        {
            if (_micOn || _mic == null) return;
            try { _mic.StartRecording(); _micOn = true; }
            catch (Exception ex) { Log.Write("Mic start: " + ex.Message); }
        }
    }

    /// <summary>In hotkey mode, close the mic again once we're done listening.</summary>
    private void MicOffIfHotkeyMode()
    {
        if (AlwaysListening) return;
        _ = Task.Run(() =>
        {
            lock (_micLock)
            {
                if (!_micOn || _listening) return;
                try { _mic.StopRecording(); } catch { }
                _micOn = false;
            }
        });
    }
    public void Thinking(string heard) => SetStatus(State.Thinking, heard);

    private async Task ListenTimeoutLoop()
    {
        while (true)
        {
            await Task.Delay(300);
            if (_listening && DateTime.Now > _listenUntil)
            {
                _listening = false;
                SetStatus(State.Idle);
                MicOffIfHotkeyMode();
            }
        }
    }

    private void OnAudio(object sender, WaveInEventArgs e)
    {
        // Don't listen to ourselves talking.
        if (Volatile.Read(ref _speakCount) > 0 || DateTime.Now < _ignoreUntil) return;

        string text;
        lock (_recLock)
        {
            if (!_rec.AcceptWaveform(e.Buffer, e.BytesRecorded))
            {
                // Show "Listening..." the moment the wake word is heard, before the sentence ends.
                if (!_listening)
                {
                    string partial = ReadField(_rec.PartialResult(), "partial");
                    if (FindWake(partial) >= 0)
                    {
                        _listening = true;
                        _listenUntil = DateTime.Now.AddSeconds(7);
                        SetStatus(State.Listening);
                    }
                }
                else
                {
                    // Still talking: keep the window open.
                    string partial = ReadField(_rec.PartialResult(), "partial");
                    if (!string.IsNullOrWhiteSpace(partial)) _listenUntil = DateTime.Now.AddSeconds(3);
                }
                return;
            }
            text = ReadField(_rec.Result(), "text");
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        int wakeEnd = FindWake(text);
        if (wakeEnd >= 0)
        {
            string command = text[wakeEnd..].Trim();
            if (command.Length > 1) Dispatch(command);
            else
            {
                // Just the wake word: wait for the command.
                _listening = true;
                _listenUntil = DateTime.Now.AddSeconds(7);
                SetStatus(State.Listening);
            }
        }
        else if (_listening)
        {
            Dispatch(text.Trim());
        }
    }

    private void Dispatch(string command)
    {
        _listening = false;
        MicOffIfHotkeyMode();
        SetStatus(State.Thinking, command);
        CommandHeard?.Invoke(command);
    }

    /// <summary>Returns the index right after the wake word, or -1.</summary>
    private int FindWake(string text)
    {
        if (string.IsNullOrEmpty(text)) return -1;
        string t = " " + text.ToLowerInvariant() + " ";
        foreach (var w in _cfg.WakeWords)
        {
            if (string.IsNullOrWhiteSpace(w)) continue;
            string key = " " + w.ToLowerInvariant().Trim() + " ";
            int i = t.IndexOf(key, StringComparison.Ordinal);
            if (i >= 0) return Math.Min(text.Length, i + key.Length - 1);
        }
        return -1;
    }

    private static string ReadField(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    public void Speak(string text)
    {
        if (!_cfg.SpeakReplies || _tts == null || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _tts.SpeakAsyncCancelAll();
            Interlocked.Increment(ref _speakCount);
            _tts.SpeakAsync(text);
        }
        catch (Exception ex) { Interlocked.Exchange(ref _speakCount, 0); Log.Write("TTS: " + ex.Message); }
    }

    /// <summary>Downloads and unzips the speech model the first time (~36 MB).</summary>
    private async Task<string> EnsureModelAsync()
    {
        string modelsRoot = Directory.CreateDirectory(Path.Combine(AppConfig.Folder, "models")).FullName;
        string zipName = Path.GetFileNameWithoutExtension(new Uri(_cfg.VoskModelUrl).LocalPath);
        string dir = Path.Combine(modelsRoot, zipName);

        if (!Directory.Exists(Path.Combine(dir, "am")))
        {
            SetStatus(State.Downloading, "Downloading voice model (one time)...");
            string zipPath = Path.Combine(modelsRoot, zipName + ".zip");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(_cfg.VoskModelUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs);
            }
            ZipFile.ExtractToDirectory(zipPath, modelsRoot, true);
            File.Delete(zipPath);
        }

        // The zip normally contains a single folder; find the one with the acoustic model in it.
        if (Directory.Exists(Path.Combine(dir, "am"))) return dir;
        var found = Directory.GetDirectories(modelsRoot).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "am")));
        return found ?? throw new Exception("Voice model folder not found in " + modelsRoot);
    }

    public void Dispose()
    {
        try { _mic?.StopRecording(); _mic?.Dispose(); } catch { }
        try { _rec?.Dispose(); _model?.Dispose(); _tts?.Dispose(); } catch { }
    }
}
