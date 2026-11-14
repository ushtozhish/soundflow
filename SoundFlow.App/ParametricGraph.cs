using System.ComponentModel;
using System.Drawing.Drawing2D;
using static SoundFlow.Lang;

namespace SoundFlow;

/// <summary>
/// Interactive frequency-response editor.
/// Drag a dot: frequency / gain. Drag the side arrows or use the wheel: Q.
/// Shift: fine adjustment. Ctrl while dragging: listen to this band alone.
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
    public event Action<int>? SoloChanged;
    public event Action<int, string>? TypeRequested;
    public event Action<int>? ToggleRequested;
    public event Action? RangeToggled;

    int selected = -1, hover = -1, qHandle, solo = -1;
    bool dragging;
    Point dragStart;
    double startFrequency, startGain, startQ;
    int range = 12;
    readonly Font tiny = new("Segoe UI", 7.5f), info = new("Segoe UI", 9, FontStyle.Bold);

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
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(14, 15, 20);
        Cursor = Cursors.Cross;
        TabStop = true;
    }

    List<Band> Bands => BandsProvider?.Invoke() ?? [];
    RectangleF Plot => new(46, 12, Math.Max(20, Width - 62), Math.Max(20, Height - 38));
    RectangleF RangeButton => new(4, Height - 22, 40, 18);

    float X(double f) { var p = Plot; return p.Left + (float)(Math.Log10(Math.Clamp(f, 20, 20000) / 20d) / 3d) * p.Width; }
    double FrequencyAt(float x) { var p = Plot; return 20 * Math.Pow(1000, Math.Clamp((x - p.Left) / p.Width, 0, 1)); }
    float Y(double db) { var p = Plot; return p.Top + (float)((range - Math.Clamp(db, -range, range)) / (2d * range)) * p.Height; }
    double GainAt(float y) { var p = Plot; return Math.Clamp(range - (y - p.Top) / p.Height * 2 * range, -range, range); }
    static double HandleGain(Band b) => Band.HasGain(b.Type) ? b.Gain : 0;
    static float HandleRadius(double q) => (float)Math.Clamp(72 / Math.Sqrt(Math.Max(.1, q)), 18, 115);

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
            g.DrawString(f >= 1000 ? $"{f / 1000:g}k" : f.ToString(), tiny, text, x - 8, p.Bottom + 5);
        }
        int stepDb = range switch { 12 => 3, 24 => 6, _ => 12 };
        for (int db = -range; db <= range; db += stepDb)
        {
            float y = Y(db);
            g.DrawLine(db == 0 ? major : grid, p.Left, y, p.Right, y);
            g.DrawString(db > 0 ? "+" + db : db.ToString(), tiny, text, 5, y - 6);
        }
        // range toggle
        var rb = RangeButton;
        using (var rbBrush = new SolidBrush(Color.FromArgb(34, 37, 45))) g.FillRoundedRectangle(rbBrush, rb, 4);
        g.DrawString($"±{range}", tiny, Brushes.Gainsboro, rb.X + 7, rb.Y + 3);

        DrawSpectrum(g, p);

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
            g.FillEllipse(brush, x - 6, y - 6, 12, 12);
            g.DrawString((i + 1).ToString(), tiny, Brushes.White, x - 4, y + 7);
            if (i == selected) DrawQHandles(g, x, y, b.Q, color);
            if (i == hover || i == selected)
            {
                using var ring = new Pen(i == solo ? Color.Gold : Color.White, 2);
                g.DrawEllipse(ring, x - 9, y - 9, 18, 18);
            }
        }

        int bubbleIndex = hover >= 0 ? hover : selected;
        if (bubbleIndex >= 0 && bubbleIndex < bands.Count)
        {
            var b = bands[bubbleIndex];
            float x = X(b.Frequency), y = Y(HandleGain(b));
            string gain = Band.HasGain(b.Type) ? $"   {b.Gain:+0.0;-0.0;0.0} dB" : "";
            string slope = b.Type is "LP" or "HP" ? $"   {b.Slope} " + L("dB/окт", "dB/oct") : "";
            string s = $"{bubbleIndex + 1}  {TypeName(b.Type)}   {FormatHz(b.Frequency)}{gain}   Q {b.Q:0.00}{slope}{(bubbleIndex == solo ? L("   СОЛО", "   SOLO") : "")}";
            var size = g.MeasureString(s, info);
            float bx = Math.Clamp(x - size.Width / 2 - 8, p.Left, Math.Max(p.Left, p.Right - size.Width - 16));
            float by = y - 44 < p.Top ? y + 16 : y - 44;
            using var bubble = new SolidBrush(Color.FromArgb(235, 30, 32, 39));
            g.FillRoundedRectangle(bubble, new RectangleF(bx, by, size.Width + 16, 27), 6);
            g.DrawString(s, info, Brushes.White, bx + 8, by + 5);
        }

        if (bands.Count == 0)
            g.DrawString(L("Двойной клик по графику — добавить полосу", "Double-click the graph to add a band"), info, text, p.Left + 12, p.Top + 10);
    }

    public static string FormatHz(double f) => f >= 1000 ? $"{f / 1000:0.##} " + L("кГц", "kHz") : $"{f:0.#} " + L("Гц", "Hz");

    public static string TypeName(string type) => type switch
    {
        "LS" => L("Низкая полка", "Low shelf"), "HS" => L("Высокая полка", "High shelf"), "LP" => L("Срез верхов", "Low-pass"),
        "HP" => L("Срез низов", "High-pass"), "NO" => L("Режекция (вырез)", "Notch"), "BP" => L("Полосовой", "Band-pass"), _ => L("Колокол", "Bell"),
    };

    void DrawSpectrum(Graphics g, RectangleF p)
    {
        var frame = SpectrumProvider?.Invoke();
        if (frame is null || frame.Values.Length < 2) return;
        var pts = new PointF[200];
        for (int i = 0; i < pts.Length; i++)
        {
            double f = 20 * Math.Pow(1000, i / (pts.Length - 1d));
            double binF = f * frame.FftSize / frame.SampleRate;
            int b0 = Math.Clamp((int)binF, 0, frame.Values.Length - 1), b1 = Math.Min(b0 + 1, frame.Values.Length - 1);
            double db = frame.Values[b0] + (frame.Values[b1] - frame.Values[b0]) * (binF - b0);
            db += 3 * Math.Log2(f / 1000); // tilt so music looks flat-ish
            db = Math.Clamp(db, -90, 0);
            pts[i] = new(X(f), p.Bottom - (float)((db + 90) / 90) * p.Height * .85f);
        }
        using var path = new GraphicsPath();
        path.AddLines(pts);
        path.AddLine(pts[^1].X, p.Bottom, pts[0].X, p.Bottom);
        path.CloseFigure();
        using var fill = new SolidBrush(Color.FromArgb(42, 205, 215, 205));
        g.FillPath(fill, path);
        using var edge = new Pen(Color.FromArgb(75, 215, 225, 215), 1);
        g.DrawLines(edge, pts);
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
        double distance = 14;
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
        if (selected < 0 || selected >= bands.Count) return 0;
        var b = bands[selected];
        float x = X(b.Frequency), y = Y(HandleGain(b)), r = HandleRadius(b.Q);
        if (Math.Abs(pt.Y - y) > 12) return 0;
        if (Math.Abs(pt.X - (x - r - 3)) < 12) return -1;
        if (Math.Abs(pt.X - (x + r + 3)) < 12) return 1;
        return 0;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Left && RangeButton.Contains(e.Location)) { RangeToggled?.Invoke(); return; }
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
        if ((ModifierKeys & Keys.Control) != 0) SetSolo(index);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging)
        {
            int h = Hit(e.Location);
            Cursor = h >= 0 ? Cursors.Hand : HitQHandle(e.Location) != 0 ? Cursors.SizeWE : Cursors.Cross;
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
        if (solo >= 0) SetSolo(-1);
        EditFinished?.Invoke();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!dragging && hover != -1) { hover = -1; Invalidate(); }
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
        double factor = (ModifierKeys & Keys.Shift) != 0 ? 1.03 : 1.12;
        b.Q = Math.Round(Math.Clamp(b.Q * Math.Pow(factor, e.Delta / 120d), .1, 30), 2);
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

    void SetSolo(int index)
    {
        if (solo == index) return;
        solo = index;
        SoloChanged?.Invoke(index);
        Invalidate();
    }

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
        menu.Items.Add(bands[index].Enabled ? L("Выключить полосу", "Disable band") : L("Включить полосу", "Enable band"), null, (_, _) => ToggleRequested?.Invoke(index));
        menu.Items.Add(L("Удалить полосу", "Remove band"), null, (_, _) => RemoveRequested?.Invoke(index));
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, location);
    }
}

internal static class GraphicsExtensions
{
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
