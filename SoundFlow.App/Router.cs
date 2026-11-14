using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundFlow;

internal sealed record OutputDevice(string Id, string Name, int Channels, int SampleRate, bool IsDefault);

/// <summary>
/// Copies the sound of the Windows default device to other output devices.
/// Loopback capture of the default device → per-target channel matrix → ring buffer →
/// adaptive resampler (follows each card's own clock, so there is no drift and no clicks) → WASAPI output.
/// </summary>
internal sealed class Router : IDisposable
{
    readonly object gate = new();
    readonly Dictionary<string, RouteOutput> outputs = [];
    readonly System.Threading.Timer watchdog;
    WasapiLoopbackCapture? capture;
    string captureDeviceId = "";
    Dictionary<string, RouteTarget> wanted = [];
    string defaultId = "";
    bool disposed;

    public event Action<string, string?>? OutputStateChanged; // device id, error text (null = ok)

    public Router() => watchdog = new System.Threading.Timer(_ => Reconcile(), null, 3000, 3000);

    public static List<OutputDevice> ListOutputs()
    {
        var list = new List<OutputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string def = enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID : "";
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (d)
                {
                    int ch = 2, rate = 48000;
                    try { var f = d.AudioClient.MixFormat; ch = f.Channels; rate = f.SampleRate; } catch { }
                    list.Add(new OutputDevice(d.ID, d.FriendlyName, Math.Clamp(ch, 1, 16), rate, d.ID == def));
                }
            }
        }
        catch (Exception ex) { Log.Error("Список устройств вывода", ex); }
        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Apply new routing. Only targets other than the default device need the engine.</summary>
    public void Update(Dictionary<string, RouteTarget> routes, string defaultDeviceId)
    {
        lock (gate)
        {
            wanted = routes.Where(r => r.Key != defaultDeviceId && r.Value.Pairs.Count > 0)
                           .ToDictionary(r => r.Key, r => new RouteTarget { Name = r.Value.Name, Gain = r.Value.Gain, Mute = r.Value.Mute, Pairs = [.. r.Value.Pairs] });
            defaultId = defaultDeviceId;
        }
        Reconcile();
    }

    void Reconcile()
    {
        if (disposed) return;
        lock (gate)
        {
            try
            {
                // capture follows the default device
                bool needCapture = wanted.Count > 0;
                if (!needCapture || captureDeviceId != defaultId) StopCapture();

                foreach (var id in outputs.Keys.ToList())
                {
                    if (!wanted.ContainsKey(id) || outputs[id].Failed)
                    {
                        outputs[id].Dispose();
                        outputs.Remove(id);
                    }
                }
                foreach (var (id, target) in wanted)
                {
                    if (!outputs.TryGetValue(id, out var o))
                    {
                        try
                        {
                            o = new RouteOutput(id);
                            outputs[id] = o;
                            OutputStateChanged?.Invoke(id, null);
                        }
                        catch (Exception ex)
                        {
                            OutputStateChanged?.Invoke(id, ex.Message);
                            continue;
                        }
                    }
                    o.Configure(target);
                }
                if (needCapture && capture is null && defaultId.Length > 0) StartCapture();
            }
            catch (Exception ex) { Log.Error("Маршрутизация", ex); }
        }
    }

    void StartCapture()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(defaultId);
            var c = new WasapiLoopbackCapture(device);
            c.DataAvailable += OnData;
            c.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Log.Error("Захват для маршрутизации остановлен", e.Exception);
                lock (gate) { if (ReferenceEquals(capture, c)) { capture = null; captureDeviceId = ""; } }
            };
            c.StartRecording();
            capture = c;
            captureDeviceId = defaultId;
            Log.Info($"Маршрутизация: захват {device.FriendlyName}, {c.WaveFormat.Channels} кан., {c.WaveFormat.SampleRate} Гц");
        }
        catch (Exception ex) { Log.Error("Маршрутизация: захват не запустился", ex); capture = null; }
    }

    void StopCapture()
    {
        var c = capture;
        capture = null;
        captureDeviceId = "";
        if (c is null) return;
        c.DataAvailable -= OnData;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }

    float[] scratch = [];

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture c) return;
        var f = c.WaveFormat;
        if (f.BitsPerSample != 32) return; // the shared mix format is float32
        int channels = f.Channels, frames = e.BytesRecorded / f.BlockAlign;
        if (frames <= 0) return;
        int samples = frames * channels;
        if (scratch.Length < samples) scratch = new float[samples];
        Buffer.BlockCopy(e.Buffer, 0, scratch, 0, samples * 4);
        RouteOutput[] targets;
        lock (gate) targets = [.. outputs.Values];
        foreach (var o in targets) o.Push(scratch, frames, channels, f.SampleRate);
    }

    public void Dispose()
    {
        disposed = true;
        watchdog.Dispose();
        lock (gate)
        {
            StopCapture();
            foreach (var o in outputs.Values) o.Dispose();
            outputs.Clear();
        }
    }
}

/// <summary>One output card: channel matrix + ring buffer + clock-following resampler.</summary>
internal sealed class RouteOutput : IWaveProvider, IDisposable
{
    readonly WasapiOut player;
    readonly int outChannels, outRate;
    readonly object gate = new();
    float[] ring;
    int write, count;               // in frames
    double readPos;                 // fractional frame position relative to the oldest frame
    double ratio = 1, correction = 1;
    int inRate = 48000;
    bool primed;
    volatile float[,] matrix = new float[0, 0]; // [src, dst] gain
    public bool Failed { get; private set; }
    public WaveFormat WaveFormat { get; }

    const double TargetMs = 45;

    public RouteOutput(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        var mix = device.AudioClient.MixFormat;
        outChannels = mix.Channels;
        outRate = mix.SampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outRate, outChannels);
        ring = new float[outRate * 2 * outChannels];
        player = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
        player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) { Log.Error($"Вывод на {device.FriendlyName} остановлен", e.Exception); Failed = true; }
        };
        player.Init(this);
        player.Play();
        Log.Info($"Маршрутизация: вывод на {device.FriendlyName}, {outChannels} кан., {outRate} Гц");
    }

    public void Configure(RouteTarget target)
    {
        int maxSrc = target.Parsed().Select(p => p.Src).DefaultIfEmpty(0).Max();
        var m = new float[Math.Max(maxSrc, 1), outChannels];
        float gain = target.Mute ? 0 : (float)Math.Pow(10, target.Gain / 20);
        foreach (var (src, dst) in target.Parsed())
            if (dst <= outChannels) m[src - 1, dst - 1] = gain;
        matrix = m;
    }

    /// <summary>Called from the capture thread with interleaved float input.</summary>
    public void Push(float[] input, int frames, int inChannels, int sampleRate)
    {
        var m = matrix;
        int srcCount = Math.Min(m.GetLength(0), inChannels);
        lock (gate)
        {
            inRate = sampleRate;
            ratio = (double)inRate / outRate;
            int capacity = ring.Length / outChannels;
            for (int i = 0; i < frames; i++)
            {
                int o = write * outChannels;
                for (int d = 0; d < outChannels; d++)
                {
                    float v = 0;
                    for (int s = 0; s < srcCount; s++)
                    {
                        float g = m[s, d];
                        if (g != 0) v += input[i * inChannels + s] * g;
                    }
                    ring[o + d] = v;
                }
                write = (write + 1) % capacity;
                if (count < capacity) count++;
                else readPos = Math.Max(0, readPos - 1); // overflow: drop the oldest frame
            }
        }
    }

    public int Read(Span<byte> buffer)
    {
        int frames = buffer.Length / (4 * outChannels);
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer[..(frames * 4 * outChannels)]);
        lock (gate)
        {
            int capacity = ring.Length / outChannels;
            double targetFrames = TargetMs / 1000 * inRate;
            if (!primed && count >= targetFrames) primed = true;
            if (!primed) { span.Clear(); return frames * 4 * outChannels; }

            // follow the clock: buffer above target → read slightly faster, below → slower
            double error = (count - readPos - targetFrames) / targetFrames;
            correction = correction * 0.995 + (1 + Math.Clamp(error, -1, 1) * 0.01) * 0.005;
            double step = ratio * correction;
            int oldest = (write - count + capacity) % capacity;

            for (int i = 0; i < frames; i++)
            {
                if (readPos + 1 >= count)
                {
                    // underrun (source went silent or stalled): output silence and wait for the buffer again
                    span[(i * outChannels)..].Clear();
                    primed = false;
                    count = 0;
                    readPos = 0;
                    break;
                }
                int p0 = (int)readPos;
                float frac = (float)(readPos - p0);
                int a = ((oldest + p0) % capacity) * outChannels, b = ((oldest + p0 + 1) % capacity) * outChannels;
                for (int d = 0; d < outChannels; d++)
                    span[i * outChannels + d] = ring[a + d] + (ring[b + d] - ring[a + d]) * frac;
                readPos += step;
            }
            // drop the frames already consumed
            int consumed = (int)readPos;
            if (consumed > 0) { count -= consumed; readPos -= consumed; }
        }
        return frames * 4 * outChannels;
    }

    public void Dispose()
    {
        try { player.Stop(); } catch { }
        try { player.Dispose(); } catch { }
    }
}

/// <summary>
/// The cube matrix: rows = channels of the Windows device, columns = channels of every output device.
/// Click a cube to send that channel there. The Windows device's own column shows its normal 1:1 playback.
/// </summary>
internal sealed class RouteMatrix : Control
{
    int Sc(int v) => (int)Math.Round(v * DeviceDpi / 96f);
    public int RowHeight => Sc(36);
    public int HeaderHeight => Sc(64);
    int Cell => Sc(28);
    int Gap => Sc(6);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string[]>? SourceNames { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<List<OutputDevice>>? Devices { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, int, int, bool>? IsOn { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, string?>? ErrorOf { get; set; }
    public event Action<string, int, int>? Toggled;
    public event Action<string>? ResetRequested;
    (string id, int src, int dst)? hover;
    readonly Font small = new("Segoe UI", 8), bold = new("Segoe UI", 8.5f, FontStyle.Bold);
    readonly ToolTip tip = new();

    public RouteMatrix()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(18, 20, 25);
    }

    IEnumerable<(OutputDevice dev, int x)> Columns()
    {
        int x = 8;
        foreach (var d in Devices?.Invoke() ?? [])
        {
            yield return (d, x);
            x += ColumnWidth(d) + Gap * 3;
        }
    }

    int ColumnWidth(OutputDevice d) => Math.Max(d.Channels * (Cell + Sc(4)), Sc(130));
    public int PreferredWidth => Columns().Select(c => c.x + ColumnWidth(c.dev)).DefaultIfEmpty(200).Max() + 16;

    static string ChannelShort(int count, int index) => new DeviceInfo("", "", count).ChannelNames[index];

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        int rows = SourceNames?.Invoke().Length ?? 0;
        using var text = new SolidBrush(Color.FromArgb(190, 196, 205));
        using var acid = new SolidBrush(Color.FromArgb(200, 255, 56));
        using var off = new SolidBrush(Color.FromArgb(34, 37, 45));
        using var hot = new Pen(Color.White, 1.5f);
        foreach (var (dev, x) in Columns())
        {
            int width = ColumnWidth(dev);
            var err = ErrorOf?.Invoke(dev.Id);
            using var header = new SolidBrush(dev.IsDefault ? Color.FromArgb(40, 200, 255, 56) : Color.FromArgb(28, 31, 38));
            g.FillRectangle(header, x - 4, 4, width + 4, HeaderHeight - Sc(10));
            string title = (dev.IsDefault ? "◆ " : "") + dev.Name;
            g.DrawString(title, bold, err is null ? Brushes.White : Brushes.OrangeRed, new RectangleF(x, Sc(8), width, Sc(30)));
            for (int c = 0; c < dev.Channels; c++)
                g.DrawString(ChannelShort(dev.Channels, c), small, text, x + c * (Cell + Sc(4)), HeaderHeight - Sc(22));
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < dev.Channels; c++)
                {
                    var rect = new Rectangle(x + c * (Cell + Sc(4)), HeaderHeight + r * RowHeight + Sc(4), Cell, Cell);
                    bool on = IsOn?.Invoke(dev.Id, r + 1, c + 1) ?? false;
                    g.FillRectangle(on ? acid : off, rect);
                    if (hover is { } h && h.id == dev.Id && h.src == r + 1 && h.dst == c + 1) g.DrawRectangle(hot, rect);
                }
        }
        if (rows == 0) g.DrawString(Lang.L("Нет устройства воспроизведения", "No playback device", "Ойнату құрылғысы жоқ"), bold, text, 10, HeaderHeight);
    }

    (string id, int src, int dst)? HitCell(Point p)
    {
        int rows = SourceNames?.Invoke().Length ?? 0;
        int r = (p.Y - HeaderHeight) / RowHeight;
        if (p.Y < HeaderHeight || r >= rows) return null;
        foreach (var (dev, x) in Columns())
            for (int c = 0; c < dev.Channels; c++)
            {
                var rect = new Rectangle(x + c * (Cell + Sc(4)), HeaderHeight + r * RowHeight + Sc(4), Cell, Cell);
                if (rect.Contains(p)) return (dev.Id, r + 1, c + 1);
            }
        return null;
    }

    string? HitHeader(Point p)
    {
        if (p.Y > HeaderHeight) return null;
        foreach (var (dev, x) in Columns())
            if (p.X >= x - 4 && p.X <= x + ColumnWidth(dev)) return dev.Id;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitCell(e.Location);
        if (h != hover) { hover = h; Invalidate(); }
        Cursor = h is not null || HitHeader(e.Location) is not null ? Cursors.Hand : Cursors.Default;
        string? id = h?.id ?? HitHeader(e.Location);
        var err = id is null ? null : ErrorOf?.Invoke(id);
        tip.SetToolTip(this, err ?? (h is null ? Lang.L("Правый клик по названию устройства — сбросить его маршруты", "Right-click a device name — reset its routes", "Құрылғы атауын оң жақ шерту — оның бағыттарын тазарту") : ""));
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HitCell(e.Location) is { } c) { Toggled?.Invoke(c.id, c.src, c.dst); Invalidate(); }
        else if (e.Button == MouseButtons.Right && HitHeader(e.Location) is { } id) { ResetRequested?.Invoke(id); Invalidate(); }
    }
}
