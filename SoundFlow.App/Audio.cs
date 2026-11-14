using System.Numerics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using static SoundFlow.Lang;

namespace SoundFlow;

internal sealed record DeviceInfo(string Id, string Name, int Channels)
{
    public static DeviceInfo None { get; } = new("", "—", 2);

    /// <summary>Speaker names for the standard Windows layouts (same order as APO channel numbers).</summary>
    public string[] ChannelNames => Channels switch
    {
        1 => [L("Моно", "Mono")],
        2 => ["L", "R"],
        4 => ["L", "R", L("Тыл L", "Rear L"), L("Тыл R", "Rear R")],
        6 => ["L", "R", L("Центр", "Center"), L("Сабвуфер", "Sub"), L("Тыл L", "Rear L"), L("Тыл R", "Rear R")],
        8 => ["L", "R", L("Центр", "Center"), L("Сабвуфер", "Sub"), L("Тыл L", "Rear L"), L("Тыл R", "Rear R"), L("Бок L", "Side L"), L("Бок R", "Side R")],
        _ => Enumerable.Range(1, Channels).Select(i => L("Канал ", "Channel ") + i).ToArray(),
    };
}

/// <summary>
/// Tracks the default playback device (plug/unplug, switch in Windows, speaker setup change).
/// Polls the default endpoint id every 1.5 s on a background thread — cheap and independent of library internals.
/// </summary>
internal sealed class DeviceWatcher : IDisposable
{
    readonly System.Threading.Timer timer;
    readonly object gate = new();
    string lastId = "";
    int lastChannels = -1, ticks;
    public event Action? Changed;

    public DeviceWatcher() => timer = new System.Threading.Timer(_ => Poll(), null, 1500, 1500);

    public DeviceInfo Current()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return DeviceInfo.None;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            int channels = 2;
            try { channels = device.AudioClient.MixFormat.Channels; } catch { }
            var info = new DeviceInfo(device.ID, device.FriendlyName, Math.Clamp(channels, 1, 16));
            lock (gate) { lastId = info.Id; lastChannels = info.Channels; }
            return info;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось получить устройство по умолчанию", ex);
            return DeviceInfo.None;
        }
    }

    void Poll()
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            string id = "";
            int channels = lastChannels;
            using (var enumerator = new MMDeviceEnumerator())
            {
                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    id = device.ID;
                    // the channel layout changes rarely — check it every ~6 s or when the device changed
                    if (id != lastId || ++ticks % 4 == 0)
                        try { channels = device.AudioClient.MixFormat.Channels; } catch { }
                }
            }
            if (id == lastId && channels == lastChannels) return;
            lastId = id;
            lastChannels = channels;
        }
        catch { return; }
        finally { if (Monitor.IsEntered(gate)) Monitor.Exit(gate); }
        Changed?.Invoke();
    }

    public void Dispose() => timer.Dispose();
}

internal sealed record SpectrumFrame(float[] Values, int SampleRate, int FftSize);

/// <summary>Loopback spectrum of the default output. Restarts itself after device loss; runs only while the window is visible.</summary>
internal sealed class SpectrumAnalyzer : IDisposable
{
    const int N = 4096;
    readonly object gate = new();
    readonly float[] ring = new float[N];
    readonly Complex[] work = new Complex[N];
    readonly double[] window = Enumerable.Range(0, N).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (N - 1))).ToArray();
    WasapiLoopbackCapture? capture;
    int position, sinceAnalysis;
    volatile SpectrumFrame? latest;
    bool wanted;

    public SpectrumFrame? Snapshot() => latest;

    public void Start()
    {
        lock (gate)
        {
            wanted = true;
            if (capture is not null) return;
            try
            {
                var c = new WasapiLoopbackCapture();
                c.DataAvailable += OnData;
                c.RecordingStopped += OnStopped;
                c.StartRecording();
                capture = c;
            }
            catch (Exception ex)
            {
                Log.Error("Анализатор спектра не запустился", ex);
                capture = null;
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            wanted = false;
            StopCapture();
        }
        latest = null;
    }

    /// <summary>Call after the default device changed.</summary>
    public void Restart()
    {
        bool w;
        lock (gate) { w = wanted; StopCapture(); }
        if (w) Start();
    }

    void StopCapture()
    {
        var c = capture;
        capture = null;
        if (c is null) return;
        c.DataAvailable -= OnData;
        c.RecordingStopped -= OnStopped;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        Log.Error("Анализатор спектра остановлен (устройство пропало?)", e.Exception);
        lock (gate) { if (ReferenceEquals(sender, capture)) capture = null; }
        // try again a bit later — the new default device may need a moment
        Task.Delay(1500).ContinueWith(_ => { if (wanted) Start(); });
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (sender is not WasapiLoopbackCapture c) return;
            var f = c.WaveFormat;
            int bytes = f.BitsPerSample / 8, step = f.BlockAlign;
            if (bytes <= 0 || step <= 0) return;
            for (int offset = 0; offset + step <= e.BytesRecorded; offset += step)
            {
                double sum = 0;
                for (int ch = 0; ch < f.Channels; ch++) sum += Sample(e.Buffer, offset + ch * bytes, f);
                ring[position] = (float)(sum / f.Channels);
                position = (position + 1) % N;
                if (++sinceAnalysis >= N / 2) { sinceAnalysis = 0; Analyze(f.SampleRate); }
            }
        }
        catch (Exception ex) { Log.Error("Анализатор спектра", ex); }
    }

    static float Sample(byte[] b, int o, WaveFormat f)
    {
        // The WASAPI shared-mode mix format (what loopback delivers) is 32-bit float in practice.
        if (f.BitsPerSample == 32 && f.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
            return BitConverter.ToSingle(b, o);
        return f.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(b, o) / 32768f,
            24 => ((b[o] | b[o + 1] << 8 | b[o + 2] << 16) << 8 >> 8) / 8388608f,
            32 => BitConverter.ToInt32(b, o) / 2147483648f,
            _ => 0,
        };
    }

    void Analyze(int sampleRate)
    {
        for (int i = 0; i < N; i++) work[i] = new Complex(ring[(position + i) % N] * window[i], 0);
        Fft(work);
        var previous = latest?.Values;
        var values = new float[N / 2];
        for (int i = 0; i < values.Length; i++)
        {
            float db = (float)(20 * Math.Log10(Math.Max(1e-7, work[i].Magnitude / (N * .25))));
            values[i] = previous is not null && previous.Length == values.Length
                ? (db > previous[i] ? previous[i] * .45f + db * .55f : previous[i] * .82f + db * .18f)
                : db;
        }
        latest = new(values, sampleRate, N);
    }

    static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            Complex wlen = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    Complex u = a[i + j], v = a[i + j + len / 2] * w;
                    a[i + j] = u + v;
                    a[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
