using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using static SoundFlow.Lang;

namespace SoundFlow;

internal sealed class MainForm : Form
{
    public static readonly int[] GraphicFrequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    sealed record BuiltIn(string Id, string Ru, string En, double[] Values);
    static readonly BuiltIn[] BuiltInProfiles =
    [
        new("flat", "Ровный", "Flat", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new("bass", "Больше баса", "Bass boost", [6, 5, 3, 1, 0, -1, -1, 0, 1, 1]),
        new("vocal", "Вокал", "Vocal", [-2, -1, 0, 2, 4, 4, 3, 1, 0, -1]),
        new("games", "Игры", "Games", [3, 2, 0, -1, -1, 1, 3, 4, 3, 2]),
        new("night", "Ночной режим", "Night", [-4, -3, -1, 1, 3, 3, 2, 0, -2, -3]),
        new("bright", "Яркий", "Bright", [-2, -1, 0, 0, 1, 2, 3, 4, 5, 5]),
    ];

    /// <summary>Item of the profile list: Id is a built-in id or "★ name" for user profiles.</summary>
    sealed record ProfileItem(string Id, string Display) { public override string ToString() => Display; }
    const string UserMark = "★ ";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static Color Acid => Color.FromArgb(200, 255, 56);
    static Color Muted => Color.FromArgb(140, 146, 157);
    static Color Warn => Color.FromArgb(231, 179, 56);
    static Color Bad => Color.FromArgb(255, 98, 90);
    static Color Card => Color.FromArgb(18, 20, 25);

    Settings s;
    readonly bool startHidden;
    bool holdApply;               // fresh settings + an existing APO config: don't overwrite until the user changes something
    bool updating;                // UI is being filled from settings
    int soloBand = -1;
    DeviceInfo device = DeviceInfo.None;
    ApoWriteResult lastWrite = ApoWriteResult.Ok;
    bool? apoAttached;

    readonly List<string> history = [];
    int historyIndex = -1;

    readonly DeviceWatcher watcher = new();
    readonly SpectrumAnalyzer analyzer = new();
    readonly System.Windows.Forms.Timer applyTimer = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 500 };
    readonly System.Windows.Forms.Timer visualTimer = new() { Interval = 40 };
    readonly System.Windows.Forms.Timer watchdog = new() { Interval = 10000 };
    readonly NotifyIcon tray = new();
    readonly ToolTip tips = new() { AutoPopDelay = 15000 };

    readonly Label deviceLabel = new(), preampValue = new(), headroom = new(), status = new();
    readonly CheckBox enabled = new(), autoComp = new();
    readonly TrackBar preamp = new();
    readonly TabControl tabs = new();
    readonly TrackBar[] graphic = new TrackBar[10];
    readonly Label[] graphicValues = new Label[10];
    readonly ParametricGraph graph = new();
    readonly DataGridView grid = new();
    readonly NoWheelComboBox profile = new();
    readonly TableLayoutPanel channelTable = new();
    readonly Label channelNote = new();
    ToolStripMenuItem? trayEnabled, trayAutostart;
    Font? boldGridFont;

    public MainForm(bool startHidden)
    {
        this.startHidden = startHidden;
        (s, bool found) = Store.Load();
        if (!found && Apo.ReadCurrent() is { } existing && existing.Contains("Preamp", StringComparison.OrdinalIgnoreCase))
        {
            holdApply = true;
            Log.Info("Настройки не найдены, но конфиг APO уже есть — он не будет перезаписан до первого изменения.");
        }

        Text = L("SoundFlow — системный эквалайзер", "SoundFlow — system equalizer");
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new(1000, 680);
        Size = new(1200, 780);
        BackColor = Color.FromArgb(10, 11, 15);
        ForeColor = Color.FromArgb(244, 243, 239);
        Font = new("Segoe UI", 9);
        DoubleBuffered = true;
        KeyPreview = true;

        updating = true; // UI events must not touch the settings while the window is being built
        BuildUi();
        BuildTray();

        graph.BandsProvider = () => s.Parametric;
        graph.SpectrumProvider = analyzer.Snapshot;
        applyTimer.Tick += (_, _) => { applyTimer.Stop(); Apply(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Store.Save(s); Commit(); };
        visualTimer.Tick += (_, _) => { if (tabs.SelectedIndex == 1 && Visible && WindowState != FormWindowState.Minimized) graph.Invalidate(); };
        watchdog.Tick += (_, _) => Watchdog();
        watcher.Changed += () => { try { if (IsHandleCreated) BeginInvoke(new Action(RefreshDevice)); } catch { } };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        RefreshDevice();
        FillFromSettings();
        Commit();
        ApplyAutostart();
        if (!holdApply) Apply(); else UpdateStatus();
        visualTimer.Start();
        watchdog.Start();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(1000, "SoundFlow", L("Эквалайзер продолжает работать. SoundFlow — в области уведомлений.", "The equalizer keeps working. SoundFlow is in the notification area."), ToolTipIcon.Info);
                return;
            }
            Store.Save(s);
        };
        FormClosed += (_, _) => Shutdown();
        VisibleChanged += (_, _) => UpdateAnalyzer();
        Resize += (_, _) => UpdateAnalyzer();
    }

    // ---------- window / tray ----------

    protected override void SetVisibleCore(bool value)
    {
        if (startHidden && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    public void RestoreWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    void BuildTray()
    {
        tray.Icon = Icon;
        tray.Text = "SoundFlow";
        tray.Visible = true;
        tray.DoubleClick += (_, _) => RestoreWindow();
        var menu = new ContextMenuStrip();
        menu.Items.Add(L("Открыть SoundFlow", "Open SoundFlow"), null, (_, _) => RestoreWindow());
        trayEnabled = new ToolStripMenuItem(L("Обработка включена", "Processing on")) { CheckOnClick = true };
        trayEnabled.Click += (_, _) => enabled.Checked = trayEnabled.Checked;
        menu.Items.Add(trayEnabled);
        trayAutostart = new ToolStripMenuItem(L("Запускать вместе с Windows", "Start with Windows")) { CheckOnClick = true };
        trayAutostart.Click += (_, _) => { s.StartWithWindows = trayAutostart.Checked; ApplyAutostart(); ScheduleSave(); };
        menu.Items.Add(trayAutostart);
        menu.Items.Add(L("Папка с настройками и журналом", "Settings and log folder"), null, (_, _) => OpenFolder(Store.Folder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L("Выход", "Exit"), null, (_, _) => { Store.Save(s); tray.Visible = false; Application.Exit(); });
        menu.Opening += (_, _) => { trayEnabled.Checked = s.Enabled; trayAutostart.Checked = s.StartWithWindows; };
        tray.ContextMenuStrip = menu;
    }

    void Shutdown()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        Store.Save(s);
        analyzer.Dispose();
        watcher.Dispose();
        tray.Visible = false;
        tray.Dispose();
    }

    void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Store.Save(s);

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        try { BeginInvoke(new Action(() => { RefreshDevice(); if (!holdApply) Apply(force: true); })); } catch { }
    }

    void UpdateAnalyzer()
    {
        bool want = Visible && WindowState != FormWindowState.Minimized && tabs.SelectedIndex == 1;
        if (want) analyzer.Start(); else analyzer.Stop();
    }

    static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\""); } catch { }
    }

    // ---------- UI ----------

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(30, 18, 30, 16), RowCount = 4, ColumnCount = 1, BackColor = BackColor };
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(root);

        // header
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = new(0, 0, 0, 6) };
        header.ColumnStyles.Add(new(SizeType.Percent, 33));
        header.ColumnStyles.Add(new(SizeType.Percent, 42));
        header.ColumnStyles.Add(new(SizeType.Percent, 25));
        header.Controls.Add(new Label { Text = "SOUNDFLOW", AutoSize = true, Font = new("Segoe UI", 23, FontStyle.Bold), ForeColor = Acid, Anchor = AnchorStyles.Left }, 0, 0);
        deviceLabel.AutoSize = false;
        deviceLabel.Dock = DockStyle.Fill;
        deviceLabel.TextAlign = ContentAlignment.MiddleCenter;
        deviceLabel.ForeColor = Muted;
        deviceLabel.MinimumSize = new(0, 46);
        header.Controls.Add(deviceLabel, 1, 0);
        enabled.AutoSize = true;
        enabled.Anchor = AnchorStyles.Right;
        enabled.ForeColor = Acid;
        enabled.CheckedChanged += (_, _) =>
        {
            enabled.Text = EnabledText(enabled.Checked);
            enabled.ForeColor = enabled.Checked ? Acid : Muted;
            if (updating) return;
            s.Enabled = enabled.Checked;
            Changed();
        };
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false };
        right.Controls.Add(enabled);
        var language = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Anchor = AnchorStyles.Right, Margin = new(0, 6, 0, 0) };
        language.Items.AddRange("Русский", "English");
        language.SelectedIndex = Lang.En ? 1 : 0;
        language.SelectionChangeCommitted += (_, _) => SwitchLanguage(language.SelectedIndex == 1 ? "en" : "ru");
        right.Controls.Add(language);
        header.Controls.Add(right, 2, 0);
        root.Controls.Add(header, 0, 0);

        // preamp row
        var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true, Margin = new(0, 0, 0, 6) };
        controls.ColumnStyles.Add(new(SizeType.AutoSize));
        controls.ColumnStyles.Add(new(SizeType.Percent, 100));
        controls.ColumnStyles.Add(new(SizeType.Absolute, 90));
        controls.ColumnStyles.Add(new(SizeType.AutoSize));
        controls.Controls.Add(new Label { Text = L("ПРЕДУСИЛЕНИЕ", "PREAMP"), ForeColor = Muted, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 0, 10, 0) }, 0, 0);
        preamp.Minimum = -24;
        preamp.Maximum = 24;
        preamp.TickFrequency = 4;
        preamp.SmallChange = 1;
        preamp.LargeChange = 2;
        preamp.Dock = DockStyle.Fill;
        ResetOnRightClick(preamp);
        preamp.ValueChanged += (_, _) =>
        {
            preampValue.Text = Db(preamp.Value / 2d);
            if (updating) return;
            s.Preamp = preamp.Value / 2d;
            Changed();
        };
        controls.Controls.Add(preamp, 1, 0);
        preampValue.AutoSize = true;
        preampValue.Anchor = AnchorStyles.Left;
        preampValue.Font = new("Segoe UI", 10, FontStyle.Bold);
        controls.Controls.Add(preampValue, 2, 0);
        autoComp.Text = L("Компенсировать подъём EQ", "Compensate EQ boost");
        autoComp.AutoSize = true;
        autoComp.Anchor = AnchorStyles.Right;
        autoComp.CheckedChanged += (_, _) => { if (updating) return; s.AutoCompensate = autoComp.Checked; Changed(); };
        tips.SetToolTip(autoComp, L(
            "Выключено (обычно): громкость задаёт только ползунок предусиления.\n" +
            "Включено: если эквалайзер где-то поднимает звук, например +6 dB на басу, SoundFlow сам\n" +
            "убирает эти 6 dB на выходе, чтобы самые громкие места не стали громче. Ползунок при этом не двигается.",
            "Off (usual): only the preamp slider sets the level.\n" +
            "On: if the EQ raises something, e.g. +6 dB of bass, SoundFlow takes those 6 dB off the output\n" +
            "so the loudest parts don't get louder. The slider itself is not moved."));
        controls.Controls.Add(autoComp, 3, 0);
        root.Controls.Add(controls, 0, 1);

        // tabs
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new(8) };
        root.Controls.Add(card, 0, 2);
        tabs.Dock = DockStyle.Fill;
        tabs.Font = new("Segoe UI", 9, FontStyle.Bold);
        tabs.Padding = new(14, 5);
        var graphicPage = new TabPage(L("ГРАФИЧЕСКИЙ 10-ПОЛОСНЫЙ", "GRAPHIC 10-BAND")) { BackColor = Card, ForeColor = ForeColor, Padding = new(8) };
        var paramPage = new TabPage(L("ПАРАМЕТРИЧЕСКИЙ", "PARAMETRIC")) { BackColor = Card, ForeColor = ForeColor, Padding = new(8) };
        var channelsPage = new TabPage(L("КАНАЛЫ", "CHANNELS")) { BackColor = Card, ForeColor = ForeColor, Padding = new(8) };
        tabs.TabPages.Add(graphicPage);
        tabs.TabPages.Add(paramPage);
        tabs.TabPages.Add(channelsPage);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            UpdateAnalyzer();
            if (updating || tabs.SelectedIndex > 1) return;
            s.Mode = tabs.SelectedIndex;
            Changed();
        };
        card.Controls.Add(tabs);
        BuildGraphic(graphicPage);
        BuildParametric(paramPage);
        BuildChannels(channelsPage);

        // footer
        var foot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, AutoSize = true, Margin = new(0, 10, 0, 0) };
        foot.ColumnStyles.Add(new(SizeType.AutoSize));
        foot.ColumnStyles.Add(new(SizeType.Absolute, 200));
        foot.ColumnStyles.Add(new(SizeType.AutoSize));
        foot.ColumnStyles.Add(new(SizeType.AutoSize));
        foot.ColumnStyles.Add(new(SizeType.Percent, 100));
        foot.ColumnStyles.Add(new(SizeType.AutoSize));
        foot.Controls.Add(new Label { Text = L("ПРОФИЛЬ", "PROFILE"), ForeColor = Muted, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 0, 10, 0) }, 0, 0);
        profile.DropDownStyle = ComboBoxStyle.DropDownList;
        profile.Dock = DockStyle.Fill;
        profile.SelectionChangeCommitted += (_, _) => SelectProfile();
        foot.Controls.Add(profile, 1, 0);
        var saveProfile = SmallButton(L("Сохранить как…", "Save as…"), Acid);
        saveProfile.Click += (_, _) => SaveProfile();
        foot.Controls.Add(saveProfile, 2, 0);
        var deleteProfile = SmallButton(L("Удалить", "Delete"), Color.FromArgb(255, 130, 120));
        deleteProfile.Click += (_, _) => DeleteProfile();
        foot.Controls.Add(deleteProfile, 3, 0);
        headroom.AutoSize = true;
        headroom.Anchor = AnchorStyles.None;
        tips.SetToolTip(headroom, L("Насколько итоговый уровень после предусиления и эквалайзера выше или ниже 0 dB.", "How far the final level after preamp and EQ is above or below 0 dB."));
        foot.Controls.Add(headroom, 4, 0);
        status.AutoSize = true;
        status.Anchor = AnchorStyles.Right;
        status.Cursor = Cursors.Hand;
        status.Click += (_, _) => FixProblem();
        foot.Controls.Add(status, 5, 0);
        root.Controls.Add(foot, 0, 3);
    }

    /// <summary>Right click on a slider returns it to 0 dB.</summary>
    void ResetOnRightClick(TrackBar bar)
    {
        bar.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) bar.Value = Math.Clamp(0, bar.Minimum, bar.Maximum); };
        tips.SetToolTip(bar, L("Правый клик — сбросить в 0 dB", "Right-click — reset to 0 dB"));
    }

    static string EnabledText(bool on) => on ? L("  ОБРАБОТКА ВКЛЮЧЕНА", "  PROCESSING ON") : L("  ОБРАБОТКА ВЫКЛЮЧЕНА", "  PROCESSING OFF");
    static string SlopeText(int slope) => $"{slope} " + L("dB/окт", "dB/oct");

    static Button IconButton(string icon) => new()
    {
        Text = icon, Size = new(40, 32), FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro, Margin = new(6, 0, 0, 0),
        Font = new("Segoe UI Symbol", 13, FontStyle.Bold), BackColor = Color.FromArgb(22, 24, 30), TextAlign = ContentAlignment.MiddleCenter,
    };

    CheckBox ToggleButton(string text, Color onColor, bool on, string tip)
    {
        var b = new CheckBox
        {
            Text = text, Appearance = Appearance.Button, Checked = on, Size = new(34, 30), Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat, Font = new("Segoe UI", 10, FontStyle.Bold), Margin = new(4, 0, 0, 0),
        };
        void Paint() { b.BackColor = b.Checked ? onColor : Color.FromArgb(30, 33, 40); b.ForeColor = b.Checked ? Color.Black : Color.Gainsboro; }
        b.FlatAppearance.BorderColor = Color.FromArgb(60, 64, 74);
        b.CheckedChanged += (_, _) => Paint();
        Paint();
        tips.SetToolTip(b, tip);
        return b;
    }

    static Button SmallButton(string text, Color color) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = color, Margin = new(6, 0, 0, 0),
        Anchor = AnchorStyles.Left, BackColor = Color.FromArgb(22, 24, 30),
    };

    void BuildGraphic(TabPage page)
    {
        var eq = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 10, RowCount = 3 };
        for (int i = 0; i < 10; i++) eq.ColumnStyles.Add(new(SizeType.Percent, 10));
        eq.RowStyles.Add(new(SizeType.AutoSize));
        eq.RowStyles.Add(new(SizeType.Percent, 100));
        eq.RowStyles.Add(new(SizeType.AutoSize));
        page.Controls.Add(eq);
        for (int i = 0; i < 10; i++)
        {
            int n = i;
            graphicValues[i] = new Label { Text = "0.0", Dock = DockStyle.Fill, AutoSize = true, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(190, 195, 204), Padding = new(0, 4, 0, 4) };
            graphic[i] = new TrackBar { Orientation = Orientation.Vertical, Minimum = -24, Maximum = 24, TickFrequency = 4, TickStyle = TickStyle.Both, Dock = DockStyle.Fill, SmallChange = 1, LargeChange = 2 };
            graphic[i].ValueChanged += (_, _) =>
            {
                graphicValues[n].Text = (graphic[n].Value > 0 ? "+" : "") + (graphic[n].Value / 2d).ToString("0.0");
                if (updating) return;
                s.Graphic[n] = graphic[n].Value / 2d;
                Changed();
            };
            ResetOnRightClick(graphic[i]);
            int f = GraphicFrequencies[i];
            eq.Controls.Add(graphicValues[i], i, 0);
            eq.Controls.Add(graphic[i], i, 1);
            eq.Controls.Add(new Label { Text = (f < 1000 ? f.ToString() : f / 1000 + "k") + L("\nГц", "\nHz"), AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new("Segoe UI", 9, FontStyle.Bold), Padding = new(0, 4, 0, 0) }, i, 2);
        }
    }

    void BuildParametric(TabPage page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Percent, 62));
        layout.RowStyles.Add(new(SizeType.Percent, 38));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        page.Controls.Add(layout);

        graph.Dock = DockStyle.Fill;
        graph.Margin = new(0, 0, 0, 6);
        graph.BandChanged += i => { UpdateGridRow(i); Changed(); };
        graph.EditFinished += ScheduleSave;
        graph.SelectionChanged += SelectGridRow;
        graph.AddRequested += (f, g) => AddBand(new Band { Type = "PK", Frequency = f, Gain = g, Q = 1 });
        graph.RemoveRequested += RemoveBand;
        graph.SoloChanged += i => { soloBand = i; Apply(); };
        graph.TypeRequested += (i, t) => { if (i < s.Parametric.Count) { s.Parametric[i].Type = t; UpdateGridRow(i); Changed(); ScheduleSave(); } };
        graph.ToggleRequested += i => { if (i < s.Parametric.Count) { s.Parametric[i].Enabled = !s.Parametric[i].Enabled; UpdateGridRow(i); Changed(); ScheduleSave(); } };
        graph.RangeToggled += () => { s.DisplayRange = s.DisplayRange switch { 12 => 24, 24 => 48, _ => 12 }; graph.Range = s.DisplayRange; ScheduleSave(); };
        layout.Controls.Add(graph, 0, 0);

        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        grid.BackgroundColor = Card;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Color.FromArgb(45, 49, 58);
        grid.DefaultCellStyle.BackColor = Color.FromArgb(24, 27, 33);
        grid.DefaultCellStyle.ForeColor = Color.White;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(70, 64, 120);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 33, 40);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Acid;
        grid.ColumnHeadersDefaultCellStyle.Font = new("Segoe UI", 8.5f, FontStyle.Bold);
        grid.EnableHeadersVisualStyles = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "N", HeaderText = "№", FillWeight = 22, ReadOnly = true });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = L("ВКЛ", "ON"), FillWeight = 28 });
        var type = new DataGridViewComboBoxColumn { Name = "Type", HeaderText = L("ТИП", "TYPE"), FillWeight = 105, FlatStyle = FlatStyle.Flat };
        foreach (var t in new[] { "PK", "LS", "HS", "HP", "LP", "NO", "BP" }) type.Items.Add(ParametricGraph.TypeName(t));
        grid.Columns.Add(type);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Fc", HeaderText = L("ЧАСТОТА, Гц", "FREQ, Hz"), FillWeight = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Gain", HeaderText = L("УСИЛЕНИЕ, dB", "GAIN, dB"), FillWeight = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Q", HeaderText = "Q", FillWeight = 50 });
        var slope = new DataGridViewComboBoxColumn { Name = "Slope", HeaderText = L("КРУТИЗНА", "SLOPE"), FillWeight = 60, FlatStyle = FlatStyle.Flat };
        slope.Items.AddRange("—", SlopeText(12), SlopeText(24), SlopeText(36), SlopeText(48));
        grid.Columns.Add(slope);
        grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Ch", HeaderText = L("КАНАЛЫ", "CHANNELS"), FillWeight = 70, FlatStyle = FlatStyle.Flat });
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell or DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) => { if (!updating && e.RowIndex >= 0) GridEdited(e.RowIndex); };
        grid.SelectionChanged += (_, _) => { if (!updating && grid.CurrentRow is { } r) graph.Selected = r.Index; };
        grid.DataError += (_, e) => e.ThrowException = false;
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < s.Parametric.Count && e.ColumnIndex == 0)
            {
                e.CellStyle!.ForeColor = ParametricGraph.ColorOf(e.RowIndex);
                e.CellStyle.Font = boldGridFont ??= new Font(grid.Font, FontStyle.Bold);
            }
        };
        layout.Controls.Add(grid, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new(0, 6, 0, 0) };
        var add = SmallButton(L("＋ Добавить полосу", "＋ Add band"), Acid);
        add.Click += (_, _) => AddBand(new Band { Type = "PK", Frequency = 1000, Q = 1 });
        var remove = SmallButton(L("− Удалить выбранную", "− Remove selected"), Color.FromArgb(255, 130, 120));
        remove.Click += (_, _) => { if (grid.CurrentRow is { } r) RemoveBand(r.Index); };
        var undo = IconButton("↶");
        undo.Click += (_, _) => Undo();
        var redo = IconButton("↷");
        redo.Click += (_, _) => Redo();
        tips.SetToolTip(undo, L("Отменить (Ctrl+Z)", "Undo (Ctrl+Z)"));
        tips.SetToolTip(redo, L("Вернуть (Ctrl+Y)", "Redo (Ctrl+Y)"));
        buttons.Controls.AddRange([add, remove, undo, redo]);
        buttons.Controls.Add(new Label
        {
            Text = L("Тяни точку — частота и усиление  •  стрелки по бокам или колесо — ширина (Q)  •  Shift — точнее  •  Ctrl + тянуть — слушать только эту полосу  •  двойной клик — добавить / удалить  •  правый клик — сбросить в 0 dB  •  Shift + правый клик — тип фильтра",
                     "Drag a dot — frequency and gain  •  side arrows or wheel — width (Q)  •  Shift — finer  •  Ctrl + drag — listen to this band alone  •  double-click — add / remove  •  right-click — reset to 0 dB  •  Shift + right-click — filter type"),
            MaximumSize = new(1100, 0),
            AutoSize = true, ForeColor = Muted, Padding = new(10, 6, 0, 0),
        });
        layout.Controls.Add(buttons, 0, 2);
    }

    void BuildChannels(TabPage page)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        page.Controls.Add(layout);
        channelNote.AutoSize = true;
        channelNote.ForeColor = Muted;
        channelNote.Padding = new(4, 4, 4, 10);
        layout.Controls.Add(channelNote, 0, 0);
        channelTable.Dock = DockStyle.Fill;
        channelTable.AutoScroll = true;
        channelTable.ColumnCount = 5;
        channelTable.ColumnStyles.Add(new(SizeType.Absolute, 110));
        channelTable.ColumnStyles.Add(new(SizeType.Percent, 100));
        channelTable.ColumnStyles.Add(new(SizeType.Absolute, 80));
        channelTable.ColumnStyles.Add(new(SizeType.AutoSize));
        channelTable.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(channelTable, 0, 1);
    }

    void RebuildChannels()
    {
        channelTable.SuspendLayout();
        channelTable.Controls.Clear();
        channelTable.RowStyles.Clear();
        var names = device.ChannelNames;
        channelTable.RowCount = names.Length + 1;
        for (int i = 0; i < names.Length; i++)
        {
            int index = i + 1;
            s.Channels.TryGetValue(index, out var trim);
            trim ??= new ChannelTrim();
            channelTable.RowStyles.Add(new(SizeType.AutoSize));
            var name = new Label { Text = names[i], AutoSize = true, Anchor = AnchorStyles.Left, Font = new("Segoe UI", 10, FontStyle.Bold), ForeColor = ParametricGraph.ColorOf(i) };
            var slider = new TrackBar { Minimum = -48, Maximum = 24, TickFrequency = 4, SmallChange = 1, LargeChange = 2, Dock = DockStyle.Fill, Value = (int)Math.Clamp(Math.Round(trim.Gain * 2), -48, 24) };
            var value = new Label { Text = Db(slider.Value / 2d), AutoSize = true, Anchor = AnchorStyles.Left };
            var mute = ToggleButton("M", Bad, trim.Mute, L("Заглушить канал (mute)", "Mute channel"));
            var solo = ToggleButton("S", Warn, trim.Solo, L("Слушать только этот канал (solo)", "Listen to this channel alone (solo)"));
            slider.ValueChanged += (_, _) =>
            {
                value.Text = Db(slider.Value / 2d);
                if (updating) return;
                Trim(index).Gain = slider.Value / 2d;
                Changed();
                ScheduleSave();
            };
            ResetOnRightClick(slider);
            mute.CheckedChanged += (_, _) => { if (updating) return; Trim(index).Mute = mute.Checked; Changed(); ScheduleSave(); };
            solo.CheckedChanged += (_, _) => { if (updating) return; Trim(index).Solo = solo.Checked; Changed(); ScheduleSave(); };
            channelTable.Controls.Add(name, 0, i);
            channelTable.Controls.Add(slider, 1, i);
            channelTable.Controls.Add(value, 2, i);
            channelTable.Controls.Add(mute, 3, i);
            channelTable.Controls.Add(solo, 4, i);
        }
        channelTable.RowStyles.Add(new(SizeType.Percent, 100));
        channelTable.ResumeLayout();
        channelNote.Text = L($"Громкость каждого канала устройства «{device.Name}» ({names.Length} кан.). Работает поверх эквалайзера.  M — заглушить канал, S — слушать только его.\n" +
                             "Каналов столько, сколько выставлено в Windows: Параметры звука → устройство → Формат / Настроить динамики.",
                             $"Level of each channel of “{device.Name}” ({names.Length} ch). Applied on top of the EQ.  M — mute the channel, S — listen to it alone.\n" +
                             "The channel count is what Windows is set to: Sound settings → device → Format / Configure speakers.");
    }

    ChannelTrim Trim(int index)
    {
        if (!s.Channels.TryGetValue(index, out var t)) s.Channels[index] = t = new ChannelTrim();
        return t;
    }

    // ---------- settings <-> UI ----------

    void FillFromSettings()
    {
        updating = true;
        try
        {
            enabled.Checked = s.Enabled;
            enabled.Text = EnabledText(s.Enabled);
            enabled.ForeColor = s.Enabled ? Acid : Muted;
            preamp.Value = (int)Math.Clamp(Math.Round(s.Preamp * 2), preamp.Minimum, preamp.Maximum);
            preampValue.Text = Db(preamp.Value / 2d);
            autoComp.Checked = s.AutoCompensate;
            for (int i = 0; i < 10; i++)
            {
                graphic[i].Value = (int)Math.Clamp(Math.Round(s.Graphic[i] * 2), -24, 24);
                graphicValues[i].Text = (graphic[i].Value > 0 ? "+" : "") + (graphic[i].Value / 2d).ToString("0.0");
            }
            if (tabs.SelectedIndex != 2) tabs.SelectedIndex = s.Mode;
            graph.Range = s.DisplayRange;
            RebuildGrid();
            RebuildChannels();
            FillProfiles();
        }
        finally { updating = false; }
        graph.Invalidate();
        UpdateHeadroom();
    }

    void FillProfiles()
    {
        profile.Items.Clear();
        foreach (var b in BuiltInProfiles) profile.Items.Add(new ProfileItem(b.Id, L(b.Ru, b.En)));
        foreach (var p in s.UserProfiles) profile.Items.Add(new ProfileItem(UserMark + p.Name, UserMark + p.Name));
        // settings of 1.1.0 stored the Russian display name of built-ins
        var legacy = BuiltInProfiles.FirstOrDefault(b => b.Ru == s.Profile);
        if (legacy is not null) s.Profile = legacy.Id;
        int index = -1;
        for (int i = 0; i < profile.Items.Count; i++)
            if (((ProfileItem)profile.Items[i]!).Id == s.Profile) index = i;
        profile.SelectedIndex = index;
    }

    string[] ChannelOptions()
    {
        var names = device.ChannelNames;
        var list = new List<string> { L("Все", "All") };
        list.AddRange(names);
        if (names.Length >= 2) list.Add("L+R");
        if (names.Length >= 6) list.Add(L("Тыл L+R", "Rear L+R"));
        return [.. list];
    }

    string ChannelLabel(string stored)
    {
        stored = stored.Trim();
        if (stored.Length == 0) return L("Все", "All");
        var names = device.ChannelNames;
        if (stored == "1 2" && names.Length >= 2) return "L+R";
        if (stored == "5 6" && names.Length >= 6) return L("Тыл L+R", "Rear L+R");
        if (int.TryParse(stored, out int n)) return n >= 1 && n <= names.Length ? names[n - 1] : L("Канал ", "Channel ") + n;
        return L("Каналы ", "Channels ") + stored;
    }

    string ChannelValue(string label)
    {
        var names = device.ChannelNames;
        if (label == L("Все", "All")) return "";
        if (label == "L+R") return "1 2";
        if (label == L("Тыл L+R", "Rear L+R")) return "5 6";
        int i = Array.IndexOf(names, label);
        if (i >= 0) return (i + 1).ToString();
        string one = L("Канал ", "Channel "), many = L("Каналы ", "Channels ");
        if (label.StartsWith(one) && int.TryParse(label[one.Length..], out int n)) return n.ToString();
        if (label.StartsWith(many)) return label[many.Length..];
        return "";
    }

    void RebuildGrid()
    {
        bool was = updating;
        updating = true;
        try
        {
            var ch = (DataGridViewComboBoxColumn)grid.Columns["Ch"]!;
            ch.Items.Clear();
            ch.Items.AddRange(ChannelOptions());
            foreach (var b in s.Parametric)
            {
                var label = ChannelLabel(b.Channels);
                if (!ch.Items.Contains(label)) ch.Items.Add(label);
            }
            grid.Rows.Clear();
            for (int i = 0; i < s.Parametric.Count; i++)
            {
                grid.Rows.Add();
                UpdateGridRow(i);
            }
            SelectGridRow(graph.Selected);
        }
        finally { updating = was; }
    }

    void UpdateGridRow(int i)
    {
        if (i < 0 || i >= s.Parametric.Count || i >= grid.Rows.Count) return;
        bool was = updating;
        updating = true;
        try
        {
            var b = s.Parametric[i];
            var r = grid.Rows[i];
            r.Cells["N"].Value = (i + 1).ToString();
            r.Cells["On"].Value = b.Enabled;
            r.Cells["Type"].Value = ParametricGraph.TypeName(b.Type);
            r.Cells["Fc"].Value = b.Frequency.ToString("0.##", CultureInfo.CurrentCulture);
            bool hasGain = Band.HasGain(b.Type), isCut = b.Type is "LP" or "HP";
            r.Cells["Gain"].Value = hasGain ? b.Gain.ToString("0.##", CultureInfo.CurrentCulture) : "—";
            r.Cells["Gain"].ReadOnly = !hasGain;
            r.Cells["Q"].Value = isCut && b.Slope > 12 ? L("авто", "auto") : b.Q.ToString("0.##", CultureInfo.CurrentCulture);
            r.Cells["Q"].ReadOnly = isCut && b.Slope > 12;
            r.Cells["Slope"].Value = isCut ? SlopeText(b.Slope) : "—";
            r.Cells["Slope"].ReadOnly = !isCut;
            var label = ChannelLabel(b.Channels);
            var ch = (DataGridViewComboBoxColumn)grid.Columns["Ch"]!;
            if (!ch.Items.Contains(label)) ch.Items.Add(label);
            r.Cells["Ch"].Value = label;
        }
        finally { updating = was; }
    }

    void SelectGridRow(int i)
    {
        bool was = updating;
        updating = true;
        try
        {
            if (i >= 0 && i < grid.Rows.Count)
            {
                if (grid.CurrentCell?.RowIndex != i) grid.CurrentCell = grid.Rows[i].Cells["Fc"];
                grid.Rows[i].Selected = true;
            }
            else grid.ClearSelection();
        }
        catch { }
        finally { updating = was; }
    }

    static double ParseNumber(object? value, double fallback)
    {
        var text = (value?.ToString() ?? "").Trim().Replace(',', '.').Replace(" ", "");
        double multiplier = 1;
        foreach (var suffix in new[] { "кгц", "khz", "к", "k" })
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { text = text[..^suffix.Length]; multiplier = 1000; break; }
        foreach (var suffix in new[] { "гц", "hz", "db", "дб" })
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { text = text[..^suffix.Length]; break; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * multiplier : fallback;
    }

    void GridEdited(int i)
    {
        if (i >= s.Parametric.Count) return;
        var b = s.Parametric[i];
        var r = grid.Rows[i];
        b.Enabled = r.Cells["On"].Value is true;
        var typeName = r.Cells["Type"].Value?.ToString();
        b.Type = new[] { "PK", "LS", "HS", "HP", "LP", "NO", "BP" }.FirstOrDefault(t => ParametricGraph.TypeName(t) == typeName) ?? b.Type;
        b.Frequency = ParseNumber(r.Cells["Fc"].Value, b.Frequency);
        if (Band.HasGain(b.Type)) b.Gain = ParseNumber(r.Cells["Gain"].Value, b.Gain);
        if (!(b.Type is "LP" or "HP" && b.Slope > 12)) b.Q = ParseNumber(r.Cells["Q"].Value, b.Q);
        if (r.Cells["Slope"].Value?.ToString() is { } sl && int.TryParse(sl.Split(' ')[0], out int slopeValue)) b.Slope = slopeValue;
        b.Channels = ChannelValue(r.Cells["Ch"].Value?.ToString() ?? L("Все", "All"));
        Store.NormalizeBand(b);
        UpdateGridRow(i);
        graph.Invalidate();
        Changed();
        ScheduleSave();
    }

    void AddBand(Band b)
    {
        if (s.Parametric.Count >= 24) return;
        Store.NormalizeBand(b);
        s.Parametric.Add(b);
        graph.Selected = s.Parametric.Count - 1;
        RebuildGrid();
        Changed();
        ScheduleSave();
    }

    void RemoveBand(int i)
    {
        if (i < 0 || i >= s.Parametric.Count) return;
        s.Parametric.RemoveAt(i);
        graph.Selected = Math.Min(i, s.Parametric.Count - 1);
        RebuildGrid();
        Changed();
        ScheduleSave();
    }

    // ---------- profiles ----------

    void SelectProfile()
    {
        if (profile.SelectedItem is not ProfileItem item) return;
        if (BuiltInProfiles.FirstOrDefault(b => b.Id == item.Id) is { } builtIn)
        {
            s.Graphic = [.. builtIn.Values];
            s.Mode = 0;
        }
        else if (item.Id.StartsWith(UserMark) && s.UserProfiles.FirstOrDefault(p => p.Name == item.Id[UserMark.Length..]) is { } p)
        {
            s.Mode = p.Mode;
            s.Graphic = [.. p.Graphic];
            s.Parametric = p.Parametric.Select(b => b.Clone()).ToList();
        }
        else return;
        s.Profile = item.Id;  // the preamp is intentionally NOT changed by profiles
        FillFromSettings();
        Changed();
        ScheduleSave();
    }

    void SaveProfile()
    {
        string suggested = s.Profile.StartsWith(UserMark) ? s.Profile[UserMark.Length..] : L("Мой профиль", "My profile");
        var name = Prompt.Show(this, L("Сохранить профиль", "Save profile"), L("Название профиля (сохраняется текущий эквалайзер, без предусиления):", "Profile name (saves the current EQ, without the preamp):"), suggested)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        s.UserProfiles.RemoveAll(p => p.Name == name);
        s.UserProfiles.Add(new EqProfile { Name = name, Mode = s.Mode, Graphic = [.. s.Graphic], Parametric = s.Parametric.Select(b => b.Clone()).ToList() });
        s.Profile = UserMark + name;
        FillProfiles();
        ScheduleSave();
    }

    void DeleteProfile()
    {
        if (profile.SelectedItem is not ProfileItem { Id: var name } || !name.StartsWith(UserMark))
        {
            MessageBox.Show(this, L("Встроенные профили удалить нельзя — только свои (со звёздочкой).", "Built-in profiles can't be deleted — only your own (with a star)."), "SoundFlow");
            return;
        }
        if (MessageBox.Show(this, L($"Удалить профиль «{name[UserMark.Length..]}»?", $"Delete profile “{name[UserMark.Length..]}”?"), "SoundFlow", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        s.UserProfiles.RemoveAll(p => p.Name == name[UserMark.Length..]);
        s.Profile = "";
        FillProfiles();
        ScheduleSave();
    }

    // ---------- undo / redo ----------

    string Snapshot()
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(s, SettingsJson.Default.Settings), SettingsJson.Default.Settings)!;
        copy.UserProfiles = [];
        copy.StartWithWindows = false;
        copy.Profile = "";
        return JsonSerializer.Serialize(copy, SettingsJson.Default.Settings);
    }

    void Commit()
    {
        var snap = Snapshot();
        if (historyIndex >= 0 && history[historyIndex] == snap) return;
        if (historyIndex < history.Count - 1) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(snap);
        if (history.Count > 100) history.RemoveAt(0);
        historyIndex = history.Count - 1;
    }

    void Undo() { Commit(); if (historyIndex > 0) Restore(history[--historyIndex]); }
    void Redo() { if (historyIndex < history.Count - 1) Restore(history[++historyIndex]); }

    void Restore(string snap)
    {
        var restored = JsonSerializer.Deserialize(snap, SettingsJson.Default.Settings);
        if (restored is null) return;
        restored.UserProfiles = s.UserProfiles;
        restored.StartWithWindows = s.StartWithWindows;
        restored.Profile = s.Profile;
        restored.Normalize();
        s = restored;
        int sel = graph.Selected;
        FillFromSettings();
        graph.Selected = Math.Min(sel, s.Parametric.Count - 1);
        Changed();
        Store.Save(s);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!grid.IsCurrentCellInEditMode || grid.CurrentCell is not DataGridViewTextBoxCell)
        {
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- applying ----------

    void Changed()
    {
        if (updating) return;
        holdApply = false;
        UpdateHeadroom();
        status.Text = L("● ИЗМЕНЕНИЕ…", "● CHANGING…");
        status.ForeColor = Warn;
        applyTimer.Stop();
        applyTimer.Start();
        ScheduleSave();
    }

    void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    double EqPeak()
    {
        if (s.Mode == 0)
            return Filters.PeakDb(GraphicFrequencies.Select((f, i) => new Band { Type = "PK", Frequency = f, Gain = s.Graphic[i], Q = 1.41 }));
        return Filters.PeakDb(s.Parametric.Where(b => b.Type is "PK" or "LS" or "HS"));
    }

    double EffectivePreamp(double peak) => s.Preamp - (s.AutoCompensate ? Math.Max(0, peak) : 0);

    void UpdateHeadroom()
    {
        double peak = EqPeak();
        double total = (s.Enabled ? EffectivePreamp(peak) + peak : 0);
        headroom.Text = total <= 0.05 ? L("Запас: ", "Headroom: ") + $"{-total:0.0} dB" : L("ВЫШЕ 0 dB: +", "ABOVE 0 dB: +") + $"{total:0.0} dB";
        headroom.ForeColor = total <= 0.05 ? Muted : Warn;
    }

    void Apply(bool force = false)
    {
        if (holdApply && !force) return;
        try
        {
            double peak = EqPeak();
            var text = Apo.Build(s, EffectivePreamp(peak), soloBand, device.Channels);
            lastWrite = Apo.Write(text);
            if (lastWrite != ApoWriteResult.Ok) Log.Info($"Запись конфига APO: {lastWrite}");
        }
        catch (Exception ex)
        {
            lastWrite = ApoWriteResult.Error;
            Log.Error("Применение настроек", ex);
        }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        (status.Text, status.ForeColor) = lastWrite switch
        {
            ApoWriteResult.NotInstalled => (L("НУЖЕН EQUALIZER APO — переустановите SoundFlow", "EQUALIZER APO NEEDED — reinstall SoundFlow"), Bad),
            ApoWriteResult.NoAccess => (L("НЕТ ДОСТУПА К КОНФИГУ APO — нажмите, чтобы исправить", "NO ACCESS TO APO CONFIG — click to fix"), Bad),
            ApoWriteResult.Error => (L("ОШИБКА ЗАПИСИ — подробности в журнале", "WRITE ERROR — see the log"), Bad),
            _ when apoAttached == false => (L("APO НЕ ПОДКЛЮЧЁН К ЭТОМУ УСТРОЙСТВУ — нажмите", "APO IS NOT ON THIS DEVICE — click"), Warn),
            _ when holdApply => (L("● ЖДЁТ ИЗМЕНЕНИЙ (конфиг сохранён)", "● WAITING FOR CHANGES (config kept)"), Muted),
            _ => (s.Enabled ? L("● В РЕАЛЬНОМ ВРЕМЕНИ", "● LIVE") : L("● ОБРАБОТКА ВЫКЛЮЧЕНА", "● PROCESSING OFF"), s.Enabled ? Acid : Muted),
        };
        tray.Text = "SoundFlow — " + (s.Enabled ? L("предусиление ", "preamp ") + Db(s.Preamp) : L("выключено", "off"));
    }

    void FixProblem()
    {
        try
        {
            if (lastWrite is ApoWriteResult.NoAccess)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--prepare-apo") { UseShellExecute = true, Verb = "runas" });
                p?.WaitForExit(30000);
                Apply(force: true);
            }
            else if (apoAttached == false && Apo.Configurator is { } configurator)
            {
                MessageBox.Show(this, L($"Откроется настройка Equalizer APO.\n\nОтметьте галочкой устройство «{device.Name}», нажмите OK и перезагрузите компьютер.",
                                         $"Equalizer APO setup will open.\n\nTick the device “{device.Name}”, press OK and restart the computer."), "SoundFlow");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(configurator) { UseShellExecute = true, Verb = "runas" });
            }
            else if (lastWrite is ApoWriteResult.Error) OpenFolder(Store.Folder);
        }
        catch (System.ComponentModel.Win32Exception) { /* user declined UAC */ }
        catch (Exception ex) { Log.Error("Исправление проблемы", ex); }
    }

    void RefreshDevice()
    {
        var previous = device;
        device = watcher.Current();
        apoAttached = Apo.IsAttached(device.Id);
        deviceLabel.Text = L("УСТРОЙСТВО ПО УМОЛЧАНИЮ", "DEFAULT DEVICE") + $"\n{device.Name} · {device.Channels} " + L("кан.", "ch");
        if (previous.Id != device.Id || previous.Channels != device.Channels)
        {
            Log.Info($"Устройство: {device.Name}, каналов {device.Channels}, APO подключён: {apoAttached?.ToString() ?? "неизвестно"}");
            bool was = updating;
            updating = true;
            try { RebuildChannels(); RebuildGrid(); } finally { updating = was; }
            if (!ReferenceEquals(previous, DeviceInfo.None)) analyzer.Restart();
            if (!holdApply) Apply();
        }
        UpdateStatus();
    }

    void Watchdog()
    {
        if (holdApply) return;
        // Something (APO reinstall, another tool) replaced our config — put it back.
        var expected = Apo.Build(s, EffectivePreamp(EqPeak()), soloBand, device.Channels);
        if (Apo.ReadCurrent() != expected) { Log.Info("Конфиг APO отличается от ожидаемого — восстанавливаю"); Apply(force: true); }
        var attached = Apo.IsAttached(device.Id);
        if (attached != apoAttached) { apoAttached = attached; UpdateStatus(); }
    }

    void SwitchLanguage(string code)
    {
        if ((code == "en") == Lang.En) return;
        s.Language = code;
        Store.Save(s);
        // restart: the new copy waits until this one has exited
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, $"--wait {Environment.ProcessId}") { UseShellExecute = false });
        tray.Visible = false;
        Application.Exit();
    }

    void ApplyAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (s.StartWithWindows) key.SetValue("SoundFlow", $"\"{Application.ExecutablePath}\" --tray");
            else key.DeleteValue("SoundFlow", false);
        }
        catch (Exception ex) { Log.Error("Автозапуск", ex); }
    }

    static string Db(double v) => (v > 0 ? "+" : v < 0 ? "−" : "") + Math.Abs(v).ToString("0.0") + " dB";
}

/// <summary>ComboBox that ignores the mouse wheel unless its list is open (no accidental profile switching).</summary>
internal sealed class NoWheelComboBox : ComboBox
{
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!DroppedDown) { if (e is HandledMouseEventArgs h) h.Handled = true; return; }
        base.OnMouseWheel(e);
    }
}

internal static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string text, string value)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new(420, 130), BackColor = Color.FromArgb(18, 20, 25),
            ForeColor = Color.White, Font = new("Segoe UI", 9),
        };
        var label = new Label { Text = text, Left = 14, Top = 12, Width = 392, Height = 34 };
        var box = new TextBox { Text = value, Left = 14, Top = 50, Width = 392 };
        var ok = new Button { Text = L("Сохранить", "Save"), DialogResult = DialogResult.OK, Left = 226, Top = 88, Width = 90 };
        var cancel = new Button { Text = L("Отмена", "Cancel"), DialogResult = DialogResult.Cancel, Left = 316, Top = 88, Width = 90 };
        form.Controls.AddRange([label, box, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}
