using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Vosk;

namespace NotchWin.Services;

/// <summary>
/// The voice assistant, in two stages:
///
///  1. WAKE WORD (always offline): a small speech model that only listens for your wake words
///     ("computer", "jarvis"). Because it's only allowed to hear those few words, it's far more
///     reliable than asking it to understand everything.
///
///  2. YOUR COMMAND: after the wake word, the next few seconds of audio are recorded until you stop
///     talking, then turned into text by
///       • Groq Whisper (free with a Groq key): very accurate, handles accents and song names, or
///       • the offline model, if you chose offline or have no Groq key.
///
/// Saying the wake word while the assistant is talking cuts it off and listens ("computer, stop").
/// </summary>
public class VoiceAssistant : IDisposable
{
    public enum State { Off, Downloading, Idle, Listening, Thinking }

    private const int Rate = 16000;
    private readonly AppConfig _cfg;
    private Model _model;
    private VoskRecognizer _wakeRec;   // only knows the wake words
    private VoskRecognizer _fullRec;   // full vocabulary (offline fallback for commands)
    private WaveInEvent _mic;
    private SpeechSynthesizer _tts;
    private readonly object _lock = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // speaking
    private int _speakCount;

    // recording a command
    private bool _capturing;
    private MemoryStream _capture;
    private DateTime _captureStart, _lastVoice;
    private bool _heardSpeech;
    private bool _interruptedSpeech;            // the wake word was said while the assistant was talking
    private bool _needsVerify;                  // started by the wake word: double-check it really was said
    private bool _pausedSpeech;                 // we paused the reply while checking
    private int _wakeHits;                      // how many audio chunks in a row looked like the wake word
    private VoskRecognizer _checkRec;           // full-vocabulary recogniser used for the quick re-check
    private readonly object _checkLock = new();
    private bool _silent;                       // recording, but the wake word isn't confirmed yet (nothing shown)
    private bool _checkStarted;
    private Task<bool> _check;
    private int _captureChunks;
    private int _captureId;
    private DateTime _ignoreWakeUntil = DateTime.MinValue;
    private readonly Queue<byte[]> _preBuffer = new(); // last ~2.5 s, so the wake word itself is never cut off
    private readonly Queue<double> _recentLoudness = new(); // loudness of the last ~0.8 s
    private double _noiseFloor = 300;

    // mic on/off (hotkey-only mode keeps it off until needed)
    private bool _micOn;
    private bool AlwaysListening => string.Equals(_cfg.VoiceMode, "WakeWord", StringComparison.OrdinalIgnoreCase);

    public State Status { get; private set; } = State.Off;
    public event Action<State, string> StatusChanged;   // state + optional detail text
    public event Action<string> CommandHeard;           // what you said (wake word removed)

    public VoiceAssistant(AppConfig cfg) => _cfg = cfg;

    /// <summary>Key used for Groq Whisper: the separate speech key, or your AI key when Groq is your AI provider.</summary>
    private string SpeechKey =>
        !string.IsNullOrWhiteSpace(_cfg.GroqSpeechKey) ? _cfg.GroqSpeechKey.Trim()
        : string.Equals(_cfg.AiProvider, "groq", StringComparison.OrdinalIgnoreCase) ? (_cfg.AiApiKey ?? "").Trim() : "";

    public bool UsesCloudSpeech =>
        !string.Equals(_cfg.SpeechEngine, "offline", StringComparison.OrdinalIgnoreCase) && SpeechKey.Length > 0;

    // =====================================================================
    //  Start-up
    // =====================================================================

    public void Start() => _ = Task.Run(async () =>
    {
        try
        {
            string modelDir = await EnsureModelAsync();
            Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(modelDir);

            // Wake recogniser: only the wake words (plus "[unk]" = anything else).
            var words = _cfg.WakeWords.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim().ToLowerInvariant()).ToList();
            if (words.Count == 0) words.Add("computer");
            _wakeRec = new VoskRecognizer(_model, Rate, JsonSerializer.Serialize(words.Append("[unk]")));
            _fullRec = new VoskRecognizer(_model, Rate);
            _checkRec = new VoskRecognizer(_model, Rate);

            _tts = new SpeechSynthesizer();
            _tts.SetOutputToDefaultAudioDevice();
            _tts.SpeakCompleted += (s, e) =>
            {
                if (Interlocked.Decrement(ref _speakCount) < 0) Interlocked.Exchange(ref _speakCount, 0);
            };

            _mic = new WaveInEvent { WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 100 };
            _mic.DataAvailable += OnAudio;
            _mic.RecordingStopped += (s, e) => { if (e.Exception != null) Log.Write("Mic stopped: " + e.Exception.Message); };
            if (AlwaysListening) MicOn();

            SetStatus(State.Idle);
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

    public bool IsSpeaking => Volatile.Read(ref _speakCount) > 0;

    /// <summary>Hotkey / "Ask" tile: stop talking and listen right away (no wake word needed).</summary>
    public void ListenNow()
    {
        if (_model == null) return;
        bool wasSpeaking = IsSpeaking;
        StopSpeaking();
        MicOn();
        lock (_lock) BeginCapture(includePreBuffer: false, interrupted: wasSpeaking, needsVerify: false);
    }

    public void Done() { if (_model != null && !_capturing) SetStatus(State.Idle); }

    private string _listenPrompt;

    /// <summary>
    /// After asking you something ("Did you mean Riot Client?"), wait for the question to finish being
    /// spoken, then listen for your answer without needing the wake word.
    /// </summary>
    public void ListenAfterSpeaking(string prompt)
    {
        if (_model == null) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            var until = DateTime.Now.AddSeconds(15);
            while (IsSpeaking && DateTime.Now < until) await Task.Delay(100);
            await Task.Delay(150);
            _listenPrompt = prompt;
            MicOn();
            lock (_lock) { if (!_capturing) BeginCapture(includePreBuffer: false, interrupted: false, needsVerify: false, followUp: true); }
        });
    }

    // =====================================================================
    //  Audio in
    // =====================================================================

    private void OnAudio(object sender, WaveInEventArgs e)
    {
        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
        double rms = Rms(chunk);

        lock (_lock)
        {
            if (_capturing)
            {
                _capture.Write(chunk, 0, chunk.Length);
                _captureChunks++;
                if (!UsesCloudSpeech) _fullRec.AcceptWaveform(chunk, chunk.Length);

                // Quick re-check of the wake word with the full-vocabulary model (runs in the background).
                if (_silent && !_checkStarted && _captureChunks >= 4) StartQuickCheck();

                var now = DateTime.Now;
                if (rms > SpeechThreshold) { _heardSpeech = true; _lastVoice = now; }

                // A slightly longer pause is allowed after the wake word, so "computer… play X" isn't cut short.
                double pauseMs = _needsVerify || _followUp ? 1200 : 900;
                bool doneTalking = _heardSpeech && (now - _lastVoice).TotalMilliseconds > pauseMs;
                bool tooLong = (now - _captureStart).TotalSeconds > 10;
                bool saidNothing = !_heardSpeech && (now - _captureStart).TotalSeconds > 5;
                if (doneTalking || tooLong || saidNothing) FinishCapture();
                return;
            }

            // Not recording: remember the last ~2.5 s and learn how loud the room is.
            _preBuffer.Enqueue(chunk);
            while (_preBuffer.Count > 25) _preBuffer.Dequeue();
            _recentLoudness.Enqueue(rms);
            while (_recentLoudness.Count > 8) _recentLoudness.Dequeue();
            if (rms < _noiseFloor * 3) _noiseFloor = _noiseFloor * 0.95 + rms * 0.05;

            // Listen for the wake word (also while talking, so you can interrupt).
            // It must be one of the most recent words heard, and someone must have spoken clearly in the
            // last moment (not just background murmur). The transcript check afterwards catches the rest.
            bool final = _wakeRec.AcceptWaveform(chunk, chunk.Length);
            string heard = ReadField(final ? _wakeRec.Result() : _wakeRec.PartialResult(), final ? "text" : "partial");
            double peak = _recentLoudness.Count > 0 ? _recentLoudness.Max() : rms;
            bool loud = WakeLoudness <= 0 || peak > Math.Max(350, _noiseFloor * WakeLoudness);
            if (WakeWordRecent(heard) && (loud || _wakeHits > 0)) _wakeHits++;
            else _wakeHits = 0;
            if (final && _wakeHits == 0) _wakeRec.Reset();

            if ((_wakeHits >= WakeHitsNeeded || (final && _wakeHits > 0)) && DateTime.Now >= _ignoreWakeUntil)
            {
                _wakeHits = 0;
                _wakeRec.Reset();
                // Start recording quietly. Nothing is shown (and a reply isn't interrupted) until the
                // quick re-check confirms the wake word was really said.
                BeginCapture(includePreBuffer: true, interrupted: IsSpeaking, needsVerify: true, silent: true);
            }
        }
    }

    private double SpeechThreshold => Math.Max(450, _noiseFloor * 2.5);

    private static double Rms(byte[] pcm16)
    {
        if (pcm16.Length < 2) return 0;
        double sum = 0;
        int n = pcm16.Length / 2;
        for (int i = 0; i < n; i++)
        {
            short v = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
            sum += (double)v * v;
        }
        return Math.Sqrt(sum / n);
    }

    private int WakeHitsNeeded => (_cfg.WakeSensitivity ?? "medium").ToLowerInvariant() == "low" ? 2 : 1;

    /// <summary>How much louder than the room you must be (0 = no check).</summary>
    private double WakeLoudness => (_cfg.WakeSensitivity ?? "medium").ToLowerInvariant() switch
    {
        "high" => 0,
        "low" => 3.0,
        _ => 2.0,
    };

    /// <summary>Wake word among the last three words the offline model heard, i.e. it was just said.</summary>
    private bool WakeWordRecent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(3);
        return tokens.Any(t => _cfg.WakeWords.Any(w => !string.IsNullOrWhiteSpace(w) && t == w.Trim().ToLowerInvariant()));
    }

    /// <summary>
    /// Second check, on the accurate transcript: was a wake word really said (in the first few words)?
    /// Allows small spelling differences ("jarvis" / "javis", "computer" / "computers").
    /// </summary>
    private bool TranscriptHasWakeWord(string text, bool strict = false)
    {
        var words = Regex.Split((text ?? "").ToLowerInvariant(), @"[^a-z']+").Where(w => w.Length > 0).Take(12).ToList();
        foreach (var wake in _cfg.WakeWords.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim().ToLowerInvariant()))
        {
            int allowed = strict ? (wake.Length >= 7 ? 1 : 0) : (wake.Length >= 6 ? 2 : 1);
            if (words.Any(w => Distance(w, wake) <= allowed)) return true;
        }
        return false;
    }

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

    // =====================================================================
    //  Recording a command
    // =====================================================================

    /// <summary>Must be called inside _lock.</summary>
    private bool _followUp;

    private void BeginCapture(bool includePreBuffer, bool interrupted, bool needsVerify = false, bool silent = false, bool followUp = false)
    {
        _capturing = true;
        _followUp = followUp;
        _silent = silent;
        _checkStarted = false;
        _check = null;
        _captureChunks = 0;
        _captureId++;
        _interruptedSpeech = interrupted;
        _needsVerify = needsVerify;
        _heardSpeech = false;
        _captureStart = _lastVoice = DateTime.Now;
        _capture = new MemoryStream();
        _fullRec.Reset();
        if (includePreBuffer)
        {
            // Include what was said right before/with the wake word ("computer play …" in one breath).
            foreach (var c in _preBuffer)
            {
                _capture.Write(c, 0, c.Length);
                if (!UsesCloudSpeech) _fullRec.AcceptWaveform(c, c.Length);
                if (Rms(c) > SpeechThreshold) _heardSpeech = true;
            }
            // The wake word itself counts as speech; wait for the pause after the whole sentence.
            // (A follow-up recording is different: it waits for you to start talking.)
            if (!followUp) _heardSpeech = true;
            _lastVoice = DateTime.Now;
        }
        _preBuffer.Clear();
        _recentLoudness.Clear();
        if (!silent) SetStatus(State.Listening, _listenPrompt);
        _listenPrompt = null;
    }

    /// <summary>
    /// Re-listens to the recording so far with the full-vocabulary model. That model isn't forced to choose
    /// between "computer" and nothing, so random noise won't come out as the wake word.
    /// Must be called inside _lock.
    /// </summary>
    private void StartQuickCheck()
    {
        _checkStarted = true;
        int id = _captureId;
        byte[] audio = _capture.ToArray();
        _check = Task.Run(() => VerifyWakeAsync(audio, id, canGrow: true));
        _check.ContinueWith(t =>
        {
            bool ok = t.Status == TaskStatus.RanToCompletion && t.Result;
            lock (_lock)
            {
                if (id != _captureId || !_capturing) return; // already finished; FinishCapture handles it
                if (!ok)
                {
                    AbortCapture();
                    return;
                }
                ConfirmWake();
            }
        });
    }

    /// <summary>Same quick check, used when the recording ended before it started. Inside _lock.</summary>
    private void StartQuickCheckOn(byte[] audio)
    {
        _checkStarted = true;
        _check = Task.Run(() => VerifyWakeAsync(audio, -1, canGrow: false));
    }

    private readonly Queue<DateTime> _cloudChecks = new();

    /// <summary>
    /// Was the wake word really said? Yes if the full-vocabulary offline model hears it (or something very
    /// close, like "compute"), or, when it isn't sure and a Groq key is set, if Whisper hears it.
    /// </summary>
    private async Task<bool> VerifyWakeAsync(byte[] audio, int id, bool canGrow)
    {
        string offline;
        lock (_checkLock)
        {
            _checkRec.Reset();
            _checkRec.AcceptWaveform(audio, audio.Length);
            offline = ReadField(_checkRec.FinalResult(), "text");
        }
        if (OfflineHeardWakeWord(offline)) return true;

        if (!UsesCloudSpeech || !CloudCheckAllowed()) return false;

        // Give it a moment more audio so the whole word is in the clip, then ask Whisper.
        if (canGrow)
        {
            await Task.Delay(400);
            lock (_lock)
            {
                if (id == _captureId && _capturing && _capture != null) audio = _capture.ToArray();
            }
        }
        try { return TranscriptHasWakeWord(await TranscribeWithGroqAsync(audio)); }
        catch (Exception ex) { Log.Write("Wake check (Groq): " + ex.Message); return false; }
    }

    /// <summary>At most 15 Whisper wake checks a minute, so false alarms can't use up the free Groq allowance.</summary>
    private bool CloudCheckAllowed()
    {
        lock (_cloudChecks)
        {
            while (_cloudChecks.Count > 0 && (DateTime.Now - _cloudChecks.Peek()).TotalSeconds > 60) _cloudChecks.Dequeue();
            if (_cloudChecks.Count >= 15) return false;
            _cloudChecks.Enqueue(DateTime.Now);
            return true;
        }
    }

    /// <summary>Forgiving match for the offline model, which often writes "compute", "computers", etc.</summary>
    private bool OfflineHeardWakeWord(string text)
    {
        if (TranscriptHasWakeWord(text)) return true;
        var words = Regex.Split((text ?? "").ToLowerInvariant(), @"[^a-z']+").Where(w => w.Length > 0);
        return _cfg.WakeWords.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length >= 6)
            .Any(wake => words.Any(w => w.Length >= 5 && w.StartsWith(wake[..5], StringComparison.Ordinal)));
    }

    /// <summary>The wake word is confirmed: now show "Listening…" and pause a reply that's playing. Inside _lock.</summary>
    private void ConfirmWake()
    {
        if (!_silent) return;
        _silent = false;
        _lastVoice = DateTime.Now; // the pause timer starts now that you can see it's listening
        if (_interruptedSpeech && IsSpeaking)
        {
            try { _tts.Pause(); _pausedSpeech = true; } catch { }
        }
        SetStatus(State.Listening, _listenPrompt);
    }

    /// <summary>False alarm: throw the recording away without showing anything. Inside _lock.</summary>
    private void AbortCapture()
    {
        _capturing = false;
        _silent = false;
        _capture?.Dispose();
        _capture = null;
        _wakeRec.Reset();
        _ignoreWakeUntil = DateTime.Now.AddSeconds(1);
    }

    /// <summary>Must be called inside _lock.</summary>
    private void FinishCapture()
    {
        _capturing = false;
        byte[] audio = _capture.ToArray();
        _capture.Dispose();
        _capture = null;
        bool interrupted = _interruptedSpeech;
        bool verify = _needsVerify;
        bool heardAnything = _heardSpeech;
        bool stillSilent = _silent;
        _silent = false;
        if (stillSilent && !_checkStarted) StartQuickCheckOn(audio);
        Task<bool> check = _check;
        bool paused = _pausedSpeech;
        _pausedSpeech = false;
        string offlineText = UsesCloudSpeech ? null : ReadField(_fullRec.FinalResult(), "text");
        _wakeRec.Reset();

        // Nobody said anything after "Listening…": don't send silence off (Whisper tends to "hear"
        // things like "Thank you." in silence).
        if (!heardAnything && !verify)
        {
            SetStatus(State.Idle);
            MicOffIfHotkeyMode();
            return;
        }

        if (!verify) SetStatus(State.Thinking, "");
        _ = Task.Run(async () =>
        {
            // Finished talking before the quick re-check came back: wait for it, and drop false alarms silently.
            if (stillSilent)
            {
                bool ok = false;
                try { ok = check != null && await check; } catch { }
                if (!ok) { lock (_lock) _ignoreWakeUntil = DateTime.Now.AddSeconds(1); return; }
                if (interrupted && IsSpeaking) { try { _tts.Pause(); paused = true; } catch { } }
                SetStatus(State.Listening);
            }

            string text = offlineText;
            if (UsesCloudSpeech)
            {
                try { text = await TranscribeWithGroqAsync(audio); }
                catch (Exception ex)
                {
                    Log.Write("Groq speech: " + ex.Message);
                    text = null;
                }
            }

            text ??= "";
            MicOffIfHotkeyMode();

            // False alarm? (The wake word wasn't really in what you said.) Quietly carry on as before.
            if (verify && !TranscriptHasWakeWord(text))
            {
                if (text.Length > 0) Log.Write($"Ignored (no wake word in \"{text}\")");
                if (paused) { try { _tts.Resume(); } catch { } }
                SetStatus(State.Idle);
                return;
            }
            // Real wake word while it was talking: now actually stop the reply.
            if (paused)
            {
                try { _tts.Resume(); _tts.SpeakAsyncCancelAll(); } catch { }
            }

            text = StripWakeWord(text, anywhere: verify);
            // Whisper's well-known "phantom" phrases when it only hears noise.
            if (Regex.IsMatch(text, @"^(thank you|thanks|thanks for watching|thank you for watching|you|bye|okay|so)\W*$", RegexOptions.IgnoreCase))
                text = "";

            if (text.Length < 2)
            {
                if (verify)
                {
                    // You said just "computer" (and paused): keep listening for the command.
                    // Whatever you said while we were checking is in the buffer, so nothing is lost.
                    lock (_lock)
                    {
                        if (!_capturing)
                        {
                            MicOn();
                            BeginCapture(includePreBuffer: true, interrupted: false, needsVerify: false, silent: false, followUp: true);
                        }
                    }
                    return;
                }
                SetStatus(State.Idle);
                return;
            }
            // "computer, stop" while it was talking: just stop talking (don't pause your music).
            if (interrupted && Regex.IsMatch(text, @"^(stop|stop it|stop talking|shut up|quiet|be quiet|cancel|never ?mind|ok|okay|enough)\W*$", RegexOptions.IgnoreCase))
            {
                SetStatus(State.Idle);
                return;
            }
            SetStatus(State.Thinking, text);
            CommandHeard?.Invoke(text);
        });
    }

    /// <summary>
    /// Keeps only what you said after the wake word. The recording starts a little early (so the wake word
    /// is never cut off), which can include background talk from before it; that's dropped too.
    /// </summary>
    private string StripWakeWord(string text, bool anywhere)
    {
        string t = (text ?? "").Trim();
        var wakes = _cfg.WakeWords.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim().ToLowerInvariant()).ToList();
        int index = 0;
        foreach (Match m in Regex.Matches(t, @"[A-Za-z']+"))
        {
            // Hotkey (no wake word needed): only strip a wake word you happened to say at the very start.
            if (!anywhere && index++ >= 2) break;
            string word = m.Value.ToLowerInvariant();
            if (wakes.Any(w => Distance(word, w) <= (w.Length >= 6 ? 2 : 1)))
            {
                t = t[(m.Index + m.Length)..];
                break;
            }
        }
        return t.Trim().TrimStart(',', '.', '!', '?', ' ').TrimEnd('.', '!', '?').Trim();
    }

    /// <summary>Sends the recording to Groq's Whisper (whisper-large-v3-turbo) and returns the text.</summary>
    private async Task<string> TranscribeWithGroqAsync(byte[] pcm)
    {
        byte[] wav = ToWav(pcm);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent("whisper-large-v3-turbo"), "model");
        form.Add(new StringContent("en"), "language");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        // Hints about the words it will hear most. (Deliberately not the wake word: the transcript is
        // used to double-check that it was really said, so we mustn't nudge Whisper into "hearing" it.)
        form.Add(new StringContent("Voice commands for a PC assistant: play, pause, next song, previous, volume up, " +
                                   "open Discord, YouTube Music, Spotify, what's the weather."), "prompt");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/transcriptions") { Content = form };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SpeechKey);
        using var resp = await _http.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"{(int)resp.StatusCode}: {json}");
        return ReadField(json, "text");
    }

    private static byte[] ToWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);            // PCM header size
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(Rate);
        w.Write(Rate * 2);      // bytes per second
        w.Write((short)2);      // block align
        w.Write((short)16);     // bits per sample
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
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

    // =====================================================================
    //  Mic on/off
    // =====================================================================

    private void MicOn()
    {
        lock (_lock)
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
        lock (_lock)
        {
            if (!_micOn || _capturing) return;
            try { _mic.StopRecording(); } catch { }
            _micOn = false;
        }
    }

    // =====================================================================
    //  Talking back
    // =====================================================================

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

    public void StopSpeaking()
    {
        try { _tts?.SpeakAsyncCancelAll(); } catch { }
    }

    // =====================================================================
    //  Model download
    // =====================================================================

    /// <summary>Downloads and unzips the offline speech model the first time (~36 MB).</summary>
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

        if (Directory.Exists(Path.Combine(dir, "am"))) return dir;
        var found = Directory.GetDirectories(modelsRoot).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "am")));
        return found ?? throw new Exception("Voice model folder not found in " + modelsRoot);
    }

    public void Dispose()
    {
        try { _mic?.StopRecording(); _mic?.Dispose(); } catch { }
        try { _wakeRec?.Dispose(); _fullRec?.Dispose(); _checkRec?.Dispose(); _model?.Dispose(); _tts?.Dispose(); } catch { }
    }
}
