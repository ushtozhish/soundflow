using System.ComponentModel;
using System.Drawing.Drawing2D;
using static SoundFlow.Lang;

namespace SoundFlow;

/// <summary>
/// Interactive frequency-response editor.
/// Drag a dot: frequency / gain. Drag the side arrows or use the wheel: Q.
/// Shift: fine adjustment. Listening to one band is the "Listen to band" button in the window.
/// Double-click on empty space: new band. Double-click on a dot or Delete: remove. Right click: gain to 0. Shift+right click: menu.
/// </summary>
internal sealed class ParametricGraph : Control
{
    static readonly Color[] Palette =
    [
        Color.FromArgb(175, 255, 78), Color.FromArgb(77, 198, 255), Color.FromArgb(255, 105, 105), Color.FromArgb(160, 130, 255),
        Color.FromArgb(255, 190, 55), Color.FromArgb(70, 220, 170), Color.FromArgb(255, 120, 210), Color.FromArgb(120, 160, 255),
    ];
    public static Color ColorOf(int i) => Palette[i % Palette.Length];

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<List<Band>>? BandsProvider { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<SpectrumFrame?>? SpectrumProvider { get; set; }
    public event Action<int>? BandChanged;
    public event Action? EditFinished;
    public event Action<int>? SelectionChanged;
    public event Action<double, double>? AddRequested;
    public event Action<int>? RemoveRequested;
    public event Action<int, string>? TypeRequested;
    public event Action<int>? ToggleRequested;
    public event Action<int>? RangeChosen;

    int selected = -1, hover = -1, qHandle, solo = -1;
    bool dragging;
    Point dragStart;
    double startFrequency, startGain, startQ;
    int startSlope;
    int range = 12;
    readonly Font tiny = new("Segoe UI", Ui.Pt(7.5f)), info = new("Segoe UI", Ui.Pt(9), FontStyle.Bold);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Range { get => range; set { range = value is 24 or 48 ? value : 12; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Selected
    {
        get => selected;
        set { if (selected != value) { selected = value; Invalidate(); } }
    }

    public ParametricGraph()
    {
        DoubleBuffered = true;
        Array.Fill(peakHold, -200);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(14, 15, 20);
        Cursor = Cursors.Cross;
        TabStop = true;
    }

    List<Band> Bands => BandsProvider?.Invoke() ?? [];
    RectangleF Plot => new(Ui.S(52), Ui.S(36), Math.Max(20, Width - Ui.S(68)), Math.Max(20, Height - Ui.S(62)));
    /// <summary>The dB-scale button sits at the top of the dB axis: "±12 dB ▾".</summary>
    RectangleF RangeButton => new(Ui.S(4), Ui.S(4), Ui.S(84), Ui.S(26));
    bool rangeHover;

    float X(double f) { var p = Plot; return p.Left + (float)(Math.Log10(Math.Clamp(f, 20, 20000) / 20d) / 3d) * p.Width; }
    double FrequencyAt(float x) { var p = Plot; return 20 * Math.Pow(1000, Math.Clamp((x - p.Left) / p.Width, 0, 1)); }
    float Y(double db) { var p = Plot; return p.Top + (float)((range - Math.Clamp(db, -range, range)) / (2d * range)) * p.Height; }
    double GainAt(float y) { var p = Plot; return Math.Clamp(range - (y - p.Top) / p.Height * 2 * range, -range, range); }
    static double HandleGain(Band b) => Band.HasGain(b.Type) ? b.Gain : 0;
    static float HandleRadius(double q) => Ui.Sf((float)Math.Clamp(72 / Math.Sqrt(Math.Max(.1, q)), 18, 115));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Plot;
        using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

        using var grid = new Pen(Color.FromArgb(35, 255, 255, 255), 1);
        using var major = new Pen(Color.FromArgb(58, 255, 255, 255), 1);
        using var text = new SolidBrush(Color.FromArgb(125, 135, 145));
        foreach (var f in new double[] { 20, 30, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
        {
            float x = X(f);
            g.DrawLine(f is 100 or 1000 or 10000 ? major : grid, x, p.Top, x, p.Bottom);
            g.DrawString(f >= 1000 ? $"{f / 1000:g}k" : f.ToString(), tiny, text, x - Ui.S(8), p.Bottom + Ui.S(5));
        }
        int stepDb = range switch { 12 => 3, 24 => 6, _ => 12 };
        // keep dB labels apart on a low graph
        while (stepDb < range && Math.Abs(Y(0) - Y(stepDb)) < tiny.GetHeight(g) * 1.3f) stepDb *= 2;
        for (int db = -range; db <= range; db += stepDb)
        {
            float y = Y(db);
            g.DrawLine(db == 0 ? major : grid, p.Left, y, p.Right, y);
            g.DrawString(db > 0 ? "+" + db : db.ToString(), tiny, text, Ui.S(8), y - Ui.S(7));
        }
        // dB-scale button
        var rb = RangeButton;
        using (var rbBrush = new SolidBrush(rangeHover ? Color.FromArgb(60, 66, 80) : Color.FromArgb(38, 42, 52))) g.FillRoundedRectangle(rbBrush, rb, 5);
        using (var rbPen = new Pen(rangeHover ? Color.FromArgb(200, 255, 56) : Color.FromArgb(80, 86, 100))) g.DrawRoundedRectangle(rbPen, rb, 5);
        var rbText = $"±{range} dB ▾";
        var rbSize = g.MeasureString(rbText, info);
        g.DrawString(rbText, info, Brushes.White, rb.X + (rb.Width - rbSize.Width) / 2, rb.Y + (rb.Height - rbSize.Height) / 2);

        DrawSpectrum(g, p);
        if (solo >= 0 && solo < Bands.Count)
        {
            var (lo, hi) = ListenRange(Bands[solo]);
            float x0 = X(Math.Max(lo, 20)), x1 = X(Math.Min(hi, 20000));
            using var window = new SolidBrush(Color.FromArgb(38, 255, 210, 60));
            g.FillRectangle(window, x0, p.Top, Math.Max(2, x1 - x0), p.Height);
            using var edgePen = new Pen(Color.FromArgb(150, 255, 210, 60), 1) { DashStyle = DashStyle.Dash };
            g.DrawLine(edgePen, x0, p.Top, x0, p.Bottom);
            g.DrawLine(edgePen, x1, p.Top, x1, p.Bottom);
        }

        var bands = Bands;
        for (int i = 0; i < bands.Count; i++)
        {
            if (!bands[i].Enabled) continue;
            using var pen = new Pen(Color.FromArgb(i == selected ? 150 : 70, ColorOf(i)), 1);
            var pts = CurvePoints([bands[i]], p);
            if (pts.Length > 1) g.DrawLines(pen, pts);
        }

        var points = CurvePoints(solo >= 0 && solo < bands.Count ? [bands[solo]] : bands, p);
        if (points.Length > 1)
        {
            using var fillPath = new GraphicsPath();
            fillPath.AddLines(points);
            fillPath.AddLine(points[^1].X, Y(0), points[0].X, Y(0));
            fillPath.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(40, 220, 170, 40));
            g.FillPath(fill, fillPath);
            using var total = new Pen(Color.FromArgb(245, 194, 255, 56), 2.2f);
            g.DrawLines(total, points);
        }

        for (int i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            float x = X(b.Frequency), y = Y(HandleGain(b));
            var color = b.Enabled ? ColorOf(i) : Color.FromArgb(90, 95, 105);
            using var brush = new SolidBrush(color);
            float hr = Ui.Sf(6); g.FillEllipse(brush, x - hr, y - hr, hr * 2, hr * 2);
            g.DrawString((i + 1).ToString(), tiny, Brushes.White, x - Ui.S(4), y + Ui.S(7));
            if (i == selected && !IsCut(b)) DrawQHandles(g, x, y, b.Q, color);
            if (i == selected && IsCut(b)) DrawSlopeHint(g, x, y, color);
            if (i == hover || i == selected)
            {
                using var ring = new Pen(i == solo ? Color.Gold : Color.White, 2);
                float rr = Ui.Sf(9); g.DrawEllipse(ring, x - rr, y - rr, rr * 2, rr * 2);
            }
        }

        int bubbleIndex = hover >= 0 ? hover : selected;
        if (bubbleIndex >= 0 && bubbleIndex < bands.Count)
        {
            var b = bands[bubbleIndex];
            float x = X(b.Frequency), y = Y(HandleGain(b));
            string gain = Band.HasGain(b.Type) ? $"   {b.Gain:+0.0;-0.0;0.0} dB" : "";
            string slope = IsCut(b) ? $"   {b.Slope} dB/oct" : $"   Q {b.Q:0.00}";
            string s = $"{bubbleIndex + 1}  {TypeName(b.Type)}   {FormatHz(b.Frequency)}{gain}{slope}{(bubbleIndex == solo ? "   " + L("СЛУШАЮ", "LISTENING", "ТЫҢДАУ") : "")}";
            var size = g.MeasureString(s, info);
            float bx = Math.Clamp(x - size.Width / 2 - 8, p.Left, Math.Max(p.Left, p.Right - size.Width - 16));
            float bh = size.Height + Ui.Sf(10), by = y - bh - Ui.Sf(16) < p.Top ? y + Ui.Sf(16) : y - bh - Ui.Sf(16);
            using var bubble = new SolidBrush(Color.FromArgb(235, 30, 32, 39));
            g.FillRoundedRectangle(bubble, new RectangleF(bx, by, size.Width + Ui.Sf(16), bh), 6);
            g.DrawString(s, info, Brushes.White, bx + Ui.Sf(8), by + Ui.Sf(5));
        }

        if (bands.Count == 0)
            g.DrawString(L("Двойной клик по графику — добавить полосу", "Double-click the graph to add a band", "Жолақ қосу үшін графикті қос шертіңіз"), info, text, p.Left + 12, p.Top + 10);
    }

    public static string FormatHz(double f) => f >= 1000 ? $"{f / 1000:0.##} " + L("кГц", "kHz", "кГц") : $"{f:0.#} " + L("Гц", "Hz", "Гц");

    /// <summary>Filter type names are the same English terms in every language (author's decision).</summary>
    public static string TypeName(string type) => type switch
    {
        "LS" => "Low shelf", "HS" => "High shelf", "LP" => "Low-pass", "HP" => "High-pass", "NO" => "Notch", "BP" => "Band-pass", _ => "Bell",
    };

    static bool IsCut(Band b) => b.Type is "LP" or "HP";

    /// <summary>Under a cut's dot: small up/down marks — drag vertically or use the wheel to change the slope.</summary>
    void DrawSlopeHint(Graphics g, float x, float y, Color color)
    {
        using var b = new SolidBrush(color);
        g.FillPolygon(b, new PointF[] { new(x, y - 22), new(x - 6, y - 14), new(x + 6, y - 14) });
        g.FillPolygon(b, new PointF[] { new(x, y + 30), new(x - 6, y + 22), new(x + 6, y + 22) });
    }

    const int SpectrumPoints = 240;
    readonly double[] peakHold = new double[SpectrumPoints];
    readonly long[] peakTime = new long[SpectrumPoints];
    SpectrumFrame? lastFrame;

    /// <summary>
    /// Live spectrum of what the computer is playing: filled area = current level, bright line = peaks
    /// (held for a second, then falling slowly), so you can watch peaks move while you shape the EQ.
    /// </summary>
    void DrawSpectrum(Graphics g, RectangleF p)
    {
        var frame = SpectrumProvider?.Invoke();
        if (frame is null || frame.Values.Length < 2) { Array.Fill(peakHold, -200); return; }
        bool fresh = !ReferenceEquals(frame, lastFrame);
        lastFrame = frame;
        long now = Environment.TickCount64;
        var pts = new PointF[SpectrumPoints];
        var peaks = new PointF[SpectrumPoints];
        float Yspec(double db) => p.Bottom - (float)((Math.Clamp(db, -84, 0) + 84) / 84) * p.Height;
        for (int i = 0; i < SpectrumPoints; i++)
        {
            double f0 = 20 * Math.Pow(1000, i / (double)SpectrumPoints), f1 = 20 * Math.Pow(1000, (i + 1) / (double)SpectrumPoints);
            double bin0 = f0 * frame.FftSize / frame.SampleRate, bin1 = f1 * frame.FftSize / frame.SampleRate;
            double db;
            if (bin1 - bin0 < 1)
            {
                // low frequencies: fewer bins than pixels — interpolate
                int b0 = Math.Clamp((int)bin0, 0, frame.Values.Length - 2);
                db = frame.Values[b0] + (frame.Values[b0 + 1] - frame.Values[b0]) * (bin0 - b0);
            }
            else
            {
                // high frequencies: many bins per pixel — take the loudest so peaks are not averaged away
                db = -200;
                for (int b = (int)bin0; b <= Math.Min((int)bin1, frame.Values.Length - 1); b++) db = Math.Max(db, frame.Values[b]);
            }
            db += 4.5 * Math.Log2(Math.Max(f0, 20) / 1000); // pink-noise tilt: music looks level instead of falling off
            if (fresh)
            {
                if (db >= peakHold[i]) { peakHold[i] = db; peakTime[i] = now; }
                else if (now - peakTime[i] > 1000) peakHold[i] -= 0.6;
            }
            double x = X(Math.Sqrt(f0 * f1));
            pts[i] = new((float)x, Yspec(db));
            peaks[i] = new((float)x, Yspec(peakHold[i]));
        }
        using var path = new GraphicsPath();
        path.AddLines(pts);
        path.AddLine(pts[^1].X, p.Bottom, pts[0].X, p.Bottom);
        path.CloseFigure();
        using var fill = new LinearGradientBrush(new RectangleF(p.Left, p.Top, p.Width, p.Height + 1),
            Color.FromArgb(120, 120, 200, 255), Color.FromArgb(25, 60, 110, 180), LinearGradientMode.Vertical);
        g.FillPath(fill, path);
        using var edge = new Pen(Color.FromArgb(150, 150, 215, 255), 1);
        g.DrawLines(edge, pts);
        using var peakPen = new Pen(Color.FromArgb(210, 255, 235, 150), 1.4f);
        g.DrawLines(peakPen, peaks);
    }

    void DrawQHandles(Graphics g, float x, float y, double q, Color color)
    {
        float r = HandleRadius(q);
        using var line = new Pen(Color.FromArgb(115, color), 1) { DashStyle = DashStyle.Dot };
        g.DrawLine(line, x - r, y, x + r, y);
        using var b = new SolidBrush(color);
        g.FillPolygon(b, new PointF[] { new(x - r - 8, y), new(x - r + 2, y - 7), new(x - r + 2, y + 7) });
        g.FillPolygon(b, new PointF[] { new(x + r + 8, y), new(x + r - 2, y - 7), new(x + r - 2, y + 7) });
    }

    PointF[] CurvePoints(List<Band> bands, RectangleF p)
    {
        int n = Math.Max(80, (int)p.Width / 3);
        var pts = new PointF[n];
        var active = bands.Where(b => b.Enabled).ToList();
        for (int i = 0; i < n; i++)
        {
            double f = 20 * Math.Pow(1000, i / (double)(n - 1)), db = 0;
            foreach (var b in active) db += Filters.MagnitudeDb(b, f);
            pts[i] = new(X(f), Y(db));
        }
        return pts;
    }

    int Hit(Point pt)
    {
        var bands = Bands;
        int best = -1;
        double distance = Ui.S(14);
        for (int i = 0; i < bands.Count; i++)
        {
            double dx = X(bands[i].Frequency) - pt.X, dy = Y(HandleGain(bands[i])) - pt.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d < distance) { distance = d; best = i; }
        }
        return best;
    }

    int HitQHandle(Point pt)
    {
        var bands = Bands;
        if (selected < 0 || selected >= bands.Count || IsCut(bands[selected])) return 0;
        var b = bands[selected];
        float x = X(b.Frequency), y = Y(HandleGain(b)), r = HandleRadius(b.Q);
        if (Math.Abs(pt.Y - y) > Ui.S(12)) return 0;
        if (Math.Abs(pt.X - (x - r - 3)) < Ui.S(12)) return -1;
        if (Math.Abs(pt.X - (x + r + 3)) < Ui.S(12)) return 1;
        return 0;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Left && RangeButton.Contains(e.Location)) { ShowRangeMenu(); return; }
        var bands = Bands;
        qHandle = e.Button == MouseButtons.Left ? HitQHandle(e.Location) : 0;
        int index = qHandle != 0 ? selected : Hit(e.Location);
        if (index != selected) { selected = index; SelectionChanged?.Invoke(index); }
        if (index < 0) { Invalidate(); return; }

        if (e.Button == MouseButtons.Right)
        {
            if ((ModifierKeys & Keys.Shift) != 0) { ShowMenu(index, e.Location); return; }
            // right click = back to neutral
            if (Band.HasGain(bands[index].Type) && bands[index].Gain != 0)
            {
                bands[index].Gain = 0;
                BandChanged?.Invoke(index);
                EditFinished?.Invoke();
            }
            Invalidate();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        dragging = true;
        Capture = true;
        dragStart = e.Location;
        startFrequency = bands[index].Frequency;
        startGain = bands[index].Gain;
        startQ = bands[index].Q;
        startSlope = bands[index].Slope;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging)
        {
            int h = Hit(e.Location);
            bool overRange = RangeButton.Contains(e.Location);
            if (overRange != rangeHover) { rangeHover = overRange; Invalidate(); }
            Cursor = h >= 0 || overRange ? Cursors.Hand : HitQHandle(e.Location) != 0 ? Cursors.SizeWE : Cursors.Cross;
            if (h != hover) { hover = h; Invalidate(); }
            return;
        }
        var bands = Bands;
        if (selected < 0 || selected >= bands.Count) return;
        var b = bands[selected];
        bool fine = (ModifierKeys & Keys.Shift) != 0;
        double scale = fine ? .2 : 1;
        if (qHandle != 0)
        {
            double startDistance = HandleRadius(startQ);
            double distance = Math.Max(12, startDistance + qHandle * (e.X - dragStart.X) * scale);
            b.Q = Math.Round(Math.Clamp(Math.Pow(72 / distance, 2), .1, 30), 2);
        }
        else
        {
            float x = (float)(X(startFrequency) + (e.X - dragStart.X) * scale);
            b.Frequency = Math.Round(Math.Clamp(FrequencyAt(x), 10, 22000), b.Frequency < 100 ? 1 : 0);
            if (Band.HasGain(b.Type))
            {
                float y = (float)(Y(startGain) + (e.Y - dragStart.Y) * scale);
                b.Gain = Math.Round(GainAt(y) * (fine ? 10 : 2)) / (fine ? 10 : 2);
            }
            else if (IsCut(b))
            {
                // drag down = steeper cut, up = gentler (12 dB/oct steps, every ~28 px)
                int steps = (int)Math.Round((e.Y - dragStart.Y) * scale / 28);
                b.Slope = Math.Clamp(startSlope + steps * 12, 12, 96);
            }
        }
        BandChanged?.Invoke(selected);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false;
        Capture = false;
        qHandle = 0;
        EditFinished?.Invoke();
        Invalidate();
    }


    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || RangeButton.Contains(e.Location)) return;
        int index = Hit(e.Location);
        if (index >= 0) RemoveRequested?.Invoke(index);
        else if (Plot.Contains(e.Location)) AddRequested?.Invoke(Math.Round(FrequencyAt(e.X)), Math.Round(GainAt(e.Y) * 2) / 2);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int index = Hit(e.Location);
        if (index < 0) index = selected;
        var bands = Bands;
        if (index < 0 || index >= bands.Count) return;
        if (e is HandledMouseEventArgs h) h.Handled = true;
        var b = bands[index];
        if (IsCut(b)) b.Slope = Math.Clamp(b.Slope + Math.Sign(e.Delta) * 12, 12, 96);
        else
        {
            double factor = (ModifierKeys & Keys.Shift) != 0 ? 1.03 : 1.12;
            b.Q = Math.Round(Math.Clamp(b.Q * Math.Pow(factor, e.Delta / 120d), .1, 30), 2);
        }
        if (index != selected) { selected = index; SelectionChanged?.Invoke(index); }
        BandChanged?.Invoke(index);
        EditFinished?.Invoke();
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Delete or Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var bands = Bands;
        if (selected < 0 || selected >= bands.Count) return;
        var b = bands[selected];
        bool fine = e.Shift;
        switch (e.KeyCode)
        {
            case Keys.Delete: RemoveRequested?.Invoke(selected); return;
            case Keys.Left: b.Frequency = Math.Clamp(b.Frequency / Math.Pow(2, fine ? 1 / 48d : 1 / 12d), 10, 22000); break;
            case Keys.Right: b.Frequency = Math.Clamp(b.Frequency * Math.Pow(2, fine ? 1 / 48d : 1 / 12d), 10, 22000); break;
            case Keys.Up when Band.HasGain(b.Type): b.Gain = Math.Clamp(b.Gain + (fine ? .1 : .5), Band.MinGain, Band.MaxGain); break;
            case Keys.Up when IsCut(b): b.Slope = Math.Max(12, b.Slope - 12); break;
            case Keys.Down when IsCut(b): b.Slope = Math.Min(96, b.Slope + 12); break;
            case Keys.Down when Band.HasGain(b.Type): b.Gain = Math.Clamp(b.Gain - (fine ? .1 : .5), Band.MinGain, Band.MaxGain); break;
            default: return;
        }
        b.Frequency = Math.Round(b.Frequency, 1);
        b.Gain = Math.Round(b.Gain, 1);
        e.Handled = true;
        BandChanged?.Invoke(selected);
        EditFinished?.Invoke();
        Invalidate();
    }

    void ShowRangeMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (var v in new[] { 12, 24, 48 })
        {
            int value = v;
            var item = new ToolStripMenuItem($"±{v} dB") { Checked = range == v };
            item.Click += (_, _) => RangeChosen?.Invoke(value);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, new Point((int)RangeButton.Left, (int)RangeButton.Bottom + 2));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        rangeHover = false;
        if (!dragging && hover != -1) hover = -1;
        Invalidate();
    }

    /// <summary>Band being listened to (gold ring, window shaded), -1 = none. Set by the window.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Solo
    {
        get => solo;
        set { if (solo != value) { solo = value; Invalidate(); } }
    }

    /// <summary>Frequency range heard while listening to a band (same as the APO listen filter).</summary>
    static (double lo, double hi) ListenRange(Band b) => b.Type switch
    {
        "LS" => (20, b.Frequency * 1.5),
        "HP" => (20, b.Frequency * 2),
        "HS" => (b.Frequency / 1.5, 20000),
        "LP" => (b.Frequency / 2, 20000),
        _ => (b.Frequency / Math.Pow(2, Bw(b.Q) / 2), b.Frequency * Math.Pow(2, Bw(b.Q) / 2)),
    };

    static double Bw(double q) { q = Math.Clamp(q, 0.5, 8); return 2 * Math.Log(1 / (2 * q) + Math.Sqrt(1 / (4 * q * q) + 1)) / Math.Log(2); }

    void ShowMenu(int index, Point location)
    {
        var bands = Bands;
        if (index < 0 || index >= bands.Count) return;
        var menu = new ContextMenuStrip();
        foreach (var t in new[] { "PK", "LS", "HS", "HP", "LP", "NO", "BP" })
        {
            var item = new ToolStripMenuItem(TypeName(t)) { Checked = bands[index].Type == t };
            string type = t;
            item.Click += (_, _) => TypeRequested?.Invoke(index, type);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(bands[index].Enabled ? L("Выключить полосу", "Disable band", "Жолақты өшіру") : L("Включить полосу", "Enable band", "Жолақты қосу"), null, (_, _) => ToggleRequested?.Invoke(index));
        menu.Items.Add(L("Удалить полосу", "Remove band", "Жолақты жою"), null, (_, _) => RemoveRequested?.Invoke(index));
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, location);
    }
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, RectangleF r, float radius)
    {
        using var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }

    public static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF r, float radius)
    {
        using var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
