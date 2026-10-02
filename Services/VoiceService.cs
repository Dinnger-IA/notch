using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentManagerNotch.Models;

namespace AgentManagerNotch.Services
{
    /// <summary>
    /// Voz de los agentes con el servidor TTS/STT interno (API compatible con la de audio de OpenAI):
    /// lee en voz alta las respuestas (<c>/v1/audio/speech</c>, PCM 24 kHz en streaming) y transcribe lo que dice el
    /// usuario (<c>/v1/audio/transcriptions</c>). El audio va por waveOut/waveIn de Windows, sin dependencias.
    /// </summary>
    public sealed class VoiceService : INotifyPropertyChanged, IDisposable
    {
        public const string DefaultUrl = "https://10.6.100.60:8880";
        public const string DefaultVoice = "es_MX-claude-high";
        private const int TtsRate = 24000, MicRate = 16000;
        /// <summary>El servidor rechaza textos demasiado largos: se envía por trozos de como mucho esto.</summary>
        private const int MaxChunk = 1200;

        private readonly Func<AppSettings> _settings;
        private readonly HttpClient _http;
        private CancellationTokenSource? _speakCts;
        private PcmPlayer? _player;
        private MicRecorder? _recorder;

        public VoiceService(Func<AppSettings> settings)
        {
            _settings = settings;
            var handler = new HttpClientHandler
            {
                // El servidor de voz usa un certificado propio: se acepta solo para ese host.
                ServerCertificateCustomValidationCallback = (req, _, _, errors) =>
                    errors == SslPolicyErrors.None ||
                    string.Equals(req.RequestUri?.Host, BaseUri.Host, StringComparison.OrdinalIgnoreCase)
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        }

        private Uri BaseUri => Uri.TryCreate((_settings().VoiceUrl ?? "").Trim().TrimEnd('/'), UriKind.Absolute, out var u) ? u : new Uri(DefaultUrl);
        private Uri Endpoint(string path) => new(BaseUri.AbsoluteUri.TrimEnd('/') + path);

        // =============================================================== estado
        private AgentEntry? _speaker;
        /// <summary>Agente que está hablando ahora (null si no habla nadie).</summary>
        public AgentEntry? Speaker
        {
            get => _speaker;
            private set
            {
                if (_speaker == value) return;
                if (_speaker != null) _speaker.IsSpeaking = false;
                _speaker = value;
                if (_speaker != null) _speaker.IsSpeaking = true;
                OnChanged(); OnChanged(nameof(IsSpeaking));
            }
        }
        public bool IsSpeaking => _speaker != null;

        private bool _isRecording;
        public bool IsRecording { get => _isRecording; private set { if (_isRecording != value) { _isRecording = value; OnChanged(); } } }

        /// <summary>Volumen de lo que suena (o de lo que capta el micrófono), 0..1: mueve la boca del personaje.</summary>
        public double Level => _player?.Level ?? _recorder?.Level ?? 0;

        // =============================================================== hablar
        /// <summary>Lee en voz alta un texto (Markdown) con la voz configurada. Corta lo que estuviera sonando.</summary>
        public async Task SpeakAsync(AgentEntry? who, string markdown)
        {
            StopSpeaking();
            var text = ForSpeech(markdown);
            if (text.Length == 0) return;
            var cts = _speakCts = new CancellationTokenSource();
            var player = _player = new PcmPlayer(TtsRate);
            Speaker = who;
            try
            {
                var st = _settings();
                foreach (var chunk in Chunks(text))
                {
                    var body = JsonSerializer.Serialize(new
                    {
                        model = "tts-1",
                        input = chunk,
                        voice = string.IsNullOrWhiteSpace(st.Voice) ? DefaultVoice : st.Voice,
                        response_format = "pcm",
                        speed = Math.Clamp(st.VoiceSpeed <= 0 ? 1 : st.VoiceSpeed, 0.5, 2)
                    });
                    using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint("/v1/audio/speech"))
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                    using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (!res.IsSuccessStatusCode)
                        throw new InvalidOperationException($"El servidor de voz respondió {(int)res.StatusCode}: {Short(await res.Content.ReadAsStringAsync(cts.Token))}");
                    await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
                    var buf = new byte[TtsRate / 5 * 2]; // 200 ms
                    int n;
                    while ((n = await stream.ReadAsync(buf, cts.Token)) > 0) player.Write(buf, n);
                }
                await player.DrainAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Voz: leer en voz alta", ex);
                throw new InvalidOperationException(Friendly(ex), ex);
            }
            finally
            {
                if (_player == player) { _player = null; Speaker = null; }
                player.Dispose();
                if (_speakCts == cts) _speakCts = null;
                cts.Dispose();
            }
        }

        public void StopSpeaking()
        {
            try { _speakCts?.Cancel(); } catch (ObjectDisposedException) { }
            _player?.Stop();
        }

        // =============================================================== escuchar
        public void StartRecording()
        {
            StopSpeaking();
            if (_recorder != null) return;
            _recorder = new MicRecorder(MicRate);
            IsRecording = true;
        }

        /// <summary>Para el micrófono y devuelve lo que dijo el usuario (vacío si no se entendió nada).</summary>
        public async Task<string> StopRecordingAsync(string? hotwords = null)
        {
            var rec = _recorder;
            if (rec == null) return "";
            _recorder = null;
            IsRecording = false;
            var wav = rec.StopAndGetWav();
            rec.Dispose();
            if (wav.Length < 44 + MicRate / 2) return ""; // menos de un cuarto de segundo
            try
            {
                using var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(wav);
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "voz.wav");
                form.Add(new StringContent("whisper-1"), "model");
                form.Add(new StringContent("es"), "language");
                if (!string.IsNullOrWhiteSpace(hotwords)) form.Add(new StringContent(hotwords), "hotwords");
                using var res = await _http.PostAsync(Endpoint("/v1/audio/transcriptions"), form);
                var json = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"El servidor de voz respondió {(int)res.StatusCode}: {Short(json)}");
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
            }
            catch (Exception ex)
            {
                Log.Error("Voz: transcribir", ex);
                throw new InvalidOperationException(Friendly(ex), ex);
            }
        }

        public void CancelRecording()
        {
            var rec = _recorder;
            _recorder = null;
            IsRecording = false;
            rec?.Dispose();
        }

        private static string Friendly(Exception ex) => ex switch
        {
            InvalidOperationException { InnerException: null } => ex.Message,
            HttpRequestException or TaskCanceledException => "No se pudo conectar con el servidor de voz. Revisa la red o la dirección en ⚙ Configuración → Voz.",
            _ => "Falló la voz: " + ex.Message
        };

        private static string Short(string s) { s = s.Trim(); return s.Length > 200 ? s[..200] + "…" : s; }

        // =============================================================== texto para leer
        /// <summary>
        /// Markdown → texto natural para la voz: sin bloques de código, marcas, enlaces largos ni bloques internos;
        /// las tablas se leen fila a fila.
        /// </summary>
        public static string ForSpeech(string md)
        {
            var sb = new StringBuilder();
            bool inCode = false;
            foreach (var raw in md.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("```"))
                {
                    if (!inCode) sb.Append("(Te dejo un bloque de código en el chat.)\n");
                    inCode = !inCode;
                    continue;
                }
                if (inCode || line.Length == 0) { if (!inCode) sb.Append('\n'); continue; }
                if (Regex.IsMatch(line, @"^\|?\s*:?-{2,}")) continue; // separador de tabla
                if (line.StartsWith('|'))
                    line = string.Join(", ", line.Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0)) + ".";
                if (Regex.IsMatch(line, @"^([-*_])\1{2,}$")) continue;
                line = Regex.Replace(line, @"^#{1,6}\s+", "");
                line = Regex.Replace(line, @"^>\s?", "");
                line = Regex.Replace(line, @"^[-*+]\s+(\[[ x~]\]\s+)?", "");
                line = Regex.Replace(line, @"\[([^\]]+)\]\((https?://[^)\s]+)\)", "$1");
                line = Regex.Replace(line, @"https?://\S+", "el enlace");
                line = Regex.Replace(line, @"<br\s*/?>", ", ", RegexOptions.IgnoreCase);
                line = Regex.Replace(line, @"(\*\*|__|\*|`|~~)", "");
                sb.Append(line).Append('\n');
            }
            var text = Regex.Replace(sb.ToString(), @"\[\[[^\]]*\]\]", "");
            return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        }

        /// <summary>Trozos de como mucho <see cref="MaxChunk"/> caracteres, cortando por párrafos o frases.</summary>
        private static IEnumerable<string> Chunks(string text)
        {
            var current = new StringBuilder();
            foreach (var piece in Regex.Split(text, @"(?<=[.!?…:;\n])\s+"))
            {
                if (current.Length > 0 && current.Length + piece.Length + 1 > MaxChunk)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                var p = piece;
                while (p.Length > MaxChunk) { yield return p[..MaxChunk]; p = p[MaxChunk..]; }
                if (current.Length > 0) current.Append(' ');
                current.Append(p);
            }
            if (current.Length > 0) yield return current.ToString();
        }

        public void Dispose()
        {
            StopSpeaking();
            CancelRecording();
            _http.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        // =============================================================== audio de Windows (winmm)
        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormat
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
            public static WaveFormat Pcm16Mono(int rate) => new()
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)rate, nAvgBytesPerSec = (uint)rate * 2,
                nBlockAlign = 2, wBitsPerSample = 16
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct MmTime
        {
            [FieldOffset(0)] public uint wType;
            [FieldOffset(4)] public uint value;
            [FieldOffset(8)] public ulong pad;
        }

        private const uint WHDR_DONE = 1, TIME_SAMPLES = 2;
        private static readonly int HdrSize = Marshal.SizeOf<WaveHdr>();
        private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags));
        private static readonly int RecordedOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwBytesRecorded));

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr h, int device, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr h, ref MmTime t, int size);
        [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr h, int device, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr h);

        private static IntPtr NewHeader(int bytes)
        {
            var hdr = Marshal.AllocHGlobal(HdrSize);
            Marshal.StructureToPtr(new WaveHdr { lpData = Marshal.AllocHGlobal(bytes), dwBufferLength = (uint)bytes }, hdr, false);
            return hdr;
        }
        private static void FreeHeader(IntPtr hdr)
        {
            Marshal.FreeHGlobal(Marshal.PtrToStructure<WaveHdr>(hdr).lpData);
            Marshal.FreeHGlobal(hdr);
        }
        private static bool IsDone(IntPtr hdr) => ((uint)Marshal.ReadInt32(hdr, FlagsOffset) & WHDR_DONE) != 0;

        /// <summary>Volumen (0..1) de un trozo de PCM s16le.</summary>
        private static double Rms(ReadOnlySpan<byte> pcm)
        {
            int samples = pcm.Length / 2;
            if (samples == 0) return 0;
            double sum = 0;
            for (int i = 0; i < samples; i++) { double v = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768.0; sum += v * v; }
            return Math.Clamp(Math.Sqrt(sum / samples) * 4.5, 0, 1);
        }

        /// <summary>Reproductor de PCM en streaming: cada trozo que llega se encola en waveOut.</summary>
        private sealed class PcmPlayer : IDisposable
        {
            private const int Window = 20; // ms por punto de la envolvente de volumen
            private readonly object _gate = new();
            private readonly IntPtr _h;
            private readonly int _rate, _windowSamples;
            private readonly List<IntPtr> _queued = new();
            private readonly List<double> _envelope = new();
            private readonly List<byte> _pending = new(); // bytes que aún no llenan una ventana de la envolvente
            private byte? _odd;
            private bool _open;

            public PcmPlayer(int rate)
            {
                _rate = rate;
                _windowSamples = rate * Window / 1000;
                var fmt = WaveFormat.Pcm16Mono(rate);
                if (waveOutOpen(out _h, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                    throw new InvalidOperationException("No se pudo abrir la salida de audio.");
                _open = true;
            }

            public void Write(byte[] data, int count)
            {
                var bytes = new List<byte>(count + 1);
                if (_odd is byte b) { bytes.Add(b); _odd = null; }
                for (int i = 0; i < count; i++) bytes.Add(data[i]);
                if (bytes.Count % 2 == 1) { _odd = bytes[^1]; bytes.RemoveAt(bytes.Count - 1); }
                if (bytes.Count == 0) return;
                var arr = bytes.ToArray();
                lock (_gate)
                {
                    if (!_open) return;
                    _pending.AddRange(arr);
                    int w = _windowSamples * 2;
                    while (_pending.Count >= w)
                    {
                        _envelope.Add(Rms(_pending.GetRange(0, w).ToArray()));
                        _pending.RemoveRange(0, w);
                    }
                    var hdr = NewHeader(arr.Length);
                    Marshal.Copy(arr, 0, Marshal.PtrToStructure<WaveHdr>(hdr).lpData, arr.Length);
                    waveOutPrepareHeader(_h, hdr, HdrSize);
                    waveOutWrite(_h, hdr, HdrSize);
                    _queued.Add(hdr);
                    Reap();
                }
            }

            private void Reap()
            {
                for (int i = _queued.Count - 1; i >= 0; i--)
                {
                    if (!IsDone(_queued[i])) continue;
                    waveOutUnprepareHeader(_h, _queued[i], HdrSize);
                    FreeHeader(_queued[i]);
                    _queued.RemoveAt(i);
                }
            }

            public double Level
            {
                get
                {
                    lock (_gate)
                    {
                        if (!_open) return 0;
                        var t = new MmTime { wType = TIME_SAMPLES };
                        if (waveOutGetPosition(_h, ref t, Marshal.SizeOf<MmTime>()) != 0) return 0;
                        int idx = (int)(t.value / (uint)_windowSamples);
                        return idx < _envelope.Count && _queued.Count > 0 ? _envelope[idx] : 0;
                    }
                }
            }

            /// <summary>Espera a que suene todo lo encolado.</summary>
            public async Task DrainAsync(CancellationToken ct)
            {
                while (true)
                {
                    lock (_gate) { if (!_open) return; Reap(); if (_queued.Count == 0) return; }
                    await Task.Delay(40, ct);
                }
            }

            public void Stop()
            {
                lock (_gate) { if (_open) waveOutReset(_h); }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (!_open) return;
                    _open = false;
                    waveOutReset(_h);
                    foreach (var hdr in _queued) { waveOutUnprepareHeader(_h, hdr, HdrSize); FreeHeader(hdr); }
                    _queued.Clear();
                    waveOutClose(_h);
                }
            }
        }

        /// <summary>Micrófono: va llenando buffers de 100 ms de waveIn y los guarda en memoria.</summary>
        private sealed class MicRecorder : IDisposable
        {
            private const int Buffers = 8;
            private readonly IntPtr _h;
            private readonly int _rate;
            private readonly IntPtr[] _hdrs = new IntPtr[Buffers];
            private readonly MemoryStream _pcm = new();
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;
            private readonly object _gate = new();
            private bool _closed;
            public double Level { get; private set; }

            public MicRecorder(int rate)
            {
                _rate = rate;
                var fmt = WaveFormat.Pcm16Mono(rate);
                if (waveInOpen(out _h, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                    throw new InvalidOperationException("No se pudo abrir el micrófono. Revisa que haya uno conectado y que Windows permita usarlo.");
                for (int i = 0; i < Buffers; i++)
                {
                    _hdrs[i] = NewHeader(rate / 10 * 2);
                    waveInPrepareHeader(_h, _hdrs[i], HdrSize);
                    waveInAddBuffer(_h, _hdrs[i], HdrSize);
                }
                waveInStart(_h);
                _loop = Task.Run(async () =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        Collect(requeue: true);
                        try { await Task.Delay(25, _cts.Token); } catch (OperationCanceledException) { }
                    }
                });
            }

            private void Collect(bool requeue)
            {
                lock (_gate)
                {
                    if (_closed) return;
                    foreach (var hdr in _hdrs)
                    {
                        if (!IsDone(hdr)) continue;
                        int n = Marshal.ReadInt32(hdr, RecordedOffset);
                        if (n > 0)
                        {
                            var bytes = new byte[n];
                            Marshal.Copy(Marshal.PtrToStructure<WaveHdr>(hdr).lpData, bytes, 0, n);
                            _pcm.Write(bytes);
                            Level = Rms(bytes);
                        }
                        Marshal.WriteInt32(hdr, FlagsOffset, Marshal.ReadInt32(hdr, FlagsOffset) & ~(int)WHDR_DONE);
                        if (requeue) waveInAddBuffer(_h, hdr, HdrSize);
                    }
                }
            }

            public byte[] StopAndGetWav()
            {
                _cts.Cancel();
                try { _loop.Wait(500); } catch { }
                lock (_gate) { if (!_closed) { waveInStop(_h); waveInReset(_h); } }
                Collect(requeue: false);
                var pcm = _pcm.ToArray();
                var wav = new MemoryStream(44 + pcm.Length);
                using (var w = new BinaryWriter(wav, Encoding.ASCII, leaveOpen: true))
                {
                    w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
                    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(_rate); w.Write(_rate * 2);
                    w.Write((short)2); w.Write((short)16);
                    w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
                }
                return wav.ToArray();
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _loop.Wait(500); } catch { }
                lock (_gate)
                {
                    if (_closed) return;
                    _closed = true;
                    waveInStop(_h);
                    waveInReset(_h);
                    foreach (var hdr in _hdrs) { waveInUnprepareHeader(_h, hdr, HdrSize); FreeHeader(hdr); }
                    waveInClose(_h);
                }
            }
        }
    }
}
