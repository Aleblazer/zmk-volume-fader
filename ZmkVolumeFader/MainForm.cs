using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using HidSharp;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace ZmkVolumeFader;

/// <summary>
/// Reads a ZMK dongle's hid-io fader joystick (report id 2: two signed
/// 16-bit LE axes, [1..2]=left [3..4]=right, raw wiper mV ~0..3300) and drives
/// the volume of two chosen Windows output devices. Each fader has its own
/// max-volume cap: the throw scales into 0..cap (top = cap, middle = cap/2).
/// The UI follows the OS light/dark theme with a green accent.
/// </summary>
public class MainForm : Form
{
    const int VID = 0x1D50, PID = 0x615E;

    // Fader value (raw wiper mV, ~0..3300 on a 16-bit axis) -> volume percent.
    // The pot reads strongly compressed at the top, so this piecewise curve
    // inverts that to feel ~linear across the throw. End points are continuous
    // dead bands (ValueToPercent clamps past them). Measured jitter bands:
    // bottom 0-3 (0% at 4), top 3220-3249 (100% at 3215). Recalibrate from the
    // live "raw (min-max)" readouts: sweep fully, set ends just outside each band.
    static readonly (int v, int pct)[] Curve =
        { (4, 0), (143, 25), (1612, 50), (3133, 75), (3215, 100) };

    // Output hysteresis (percent): hold the current % until the value moves more
    // than this off it, so boundary noise can't flip-flop 8<->9. Must be < 1.
    const double Hyst = 0.9;

    // ---- theme ------------------------------------------------------------

    sealed record Theme(bool Dark, Color Window, Color Card, Color Inset, Color Text,
                        Color Subtle, Color Accent, Color CtlBg, Color CtlBorder);

    static Color Hex(int r, int g, int b) => Color.FromArgb(r, g, b);

    static readonly Theme DarkTheme = new(
        Dark: true,
        Window: Hex(0x23, 0x25, 0x2B), Card: Hex(0x2B, 0x2E, 0x36), Inset: Hex(0x15, 0x17, 0x1B),
        Text: Hex(0xF2, 0xF3, 0xF5), Subtle: Hex(0x9A, 0xA0, 0xAA), Accent: Hex(0x46, 0xE0, 0x7A),
        CtlBg: Hex(0x1C, 0x1E, 0x23), CtlBorder: Hex(0x3A, 0x3D, 0x44));

    static readonly Theme LightTheme = new(
        Dark: false,
        Window: Hex(0xF3, 0xF3, 0xF4), Card: Hex(0xFF, 0xFF, 0xFF), Inset: Hex(0xE4, 0xE6, 0xEA),
        Text: Hex(0x1B, 0x1D, 0x21), Subtle: Hex(0x66, 0x6C, 0x74), Accent: Hex(0x1F, 0xA6, 0x4E),
        CtlBg: Hex(0xFF, 0xFF, 0xFF), CtlBorder: Hex(0xD2, 0xD5, 0xDA));

    Theme _theme = LightTheme;

    // ---- custom controls --------------------------------------------------

    // Flat rounded volume bar (replaces the segmented system ProgressBar).
    sealed class FaderBar : Control
    {
        int _value;
        public int Value { get => _value; set { int v = Math.Clamp(value, 0, 100); if (v != _value) { _value = v; Invalidate(); } } }
        public Color Track { get; set; } = Color.Gray;
        public Color Fill { get; set; } = Color.LimeGreen;

        public FaderBar() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                                      | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (Width < 2 || Height < 2) return;
            using (var tp = Pill(new RectangleF(0, 0, Width, Height)))
            using (var tb = new SolidBrush(Track)) g.FillPath(tb, tp);
            float fw = Width * (_value / 100f);
            if (fw >= 2)
            {
                using var fp = Pill(new RectangleF(0, 0, fw, Height));
                using var fb = new SolidBrush(Fill);
                g.FillPath(fb, fp);
            }
        }

        static GraphicsPath Pill(RectangleF r)
        {
            var p = new GraphicsPath();
            float d = Math.Min(r.Height, r.Width);
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }
    }

    // Panel clipped to a rounded rectangle, for the fader cards.
    sealed class CardPanel : Panel
    {
        public int Radius = 10;
        public CardPanel() => DoubleBuffered = true;
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0 || Height <= 0) return;
            int d = Radius * 2;
            using var p = new GraphicsPath();
            p.AddArc(0, 0, d, d, 180, 90);
            p.AddArc(Width - d - 1, 0, d, d, 270, 90);
            p.AddArc(Width - d - 1, Height - d - 1, d, d, 0, 90);
            p.AddArc(0, Height - d - 1, d, d, 90, 90);
            p.CloseFigure();
            Region = new Region(p);
        }
    }

    // Flat, themed numeric stepper (replaces NumericUpDown, whose spin buttons
    // can't be dark-themed). Click the upper/lower right zone, or scroll.
    sealed class Stepper : Control
    {
        int _value = 100, _min = 1, _max = 100;
        public event EventHandler? ValueChanged;
        public int Minimum { get => _min; set => _min = value; }
        public int Maximum { get => _max; set => _max = value; }
        public int Value
        {
            get => _value;
            set { int v = Math.Clamp(value, _min, _max); if (v != _value) { _value = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
        }
        public Color BorderColor { get; set; } = Color.Gray;
        public Color ChevronColor { get; set; } = Color.Gray;

        public Stepper()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(58, 23);
        }

        int Zone => 18;   // right strip holding the chevrons

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? BackColor);
            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Rounded(box, 6))
            {
                using var bg = new SolidBrush(BackColor);
                using var pen = new Pen(BorderColor);
                g.FillPath(bg, path);
                g.DrawPath(pen, path);
            }
            var textRect = new Rectangle(4, 0, Width - Zone - 4, Height);
            TextRenderer.DrawText(g, _value.ToString(), Font, textRect, ForeColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int cx = Width - Zone / 2 - 3, my = Height / 2;
            using var cb = new SolidBrush(ChevronColor);
            g.FillPolygon(cb, new[] { new Point(cx - 4, my - 2), new Point(cx + 4, my - 2), new Point(cx, my - 6) });
            g.FillPolygon(cb, new[] { new Point(cx - 4, my + 2), new Point(cx + 4, my + 2), new Point(cx, my + 6) });
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.X >= Width - Zone) Value += e.Y < Height / 2 ? 1 : -1;
            Focus();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value += Math.Sign(e.Delta);
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    sealed class DeviceItem
    {
        public required string Id;
        public required string Name;
        public required MMDevice Device;
        public override string ToString() => Name;
    }

    /// <summary>Per-fader UI + filtering state.</summary>
    sealed class Axis
    {
        public required ComboBox Combo;
        public required FaderBar Bar;
        public required Label Pct;     // big "62%" readout
        public required Label Raw;     // small "raw N (min-max)" calibration readout
        public required Stepper Limit;
        public double Sm = -1;          // EMA state (smoothed raw value)
        public int LastRaw;             // last raw value (so a cap change can re-render)
        public int LastApplied = -1;    // last volume % pushed to the device (deadband)
        public int RawMin = int.MaxValue, RawMax = int.MinValue;  // observed raw range
    }

    sealed class Settings
    {
        public string? LeftDeviceId { get; set; }
        public string? RightDeviceId { get; set; }
        public int LeftMax { get; set; } = 100;
        public int RightMax { get; set; } = 100;
    }

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZmkVolumeFader", "settings.json");

    // ---- controls ---------------------------------------------------------

    readonly ComboBox _cbLeft = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Right };
    readonly ComboBox _cbRight = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Right };
    readonly FaderBar _barLeft = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
    readonly FaderBar _barRight = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
    readonly Label _pctLeft = new() { Text = "—", AutoSize = true, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 15f) };
    readonly Label _pctRight = new() { Text = "—", AutoSize = true, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 15f) };
    readonly Label _rawLeft = new() { Text = "", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Segoe UI", 8.25f) };
    readonly Label _rawRight = new() { Text = "", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Segoe UI", 8.25f) };
    readonly Label _nameLeft = new() { Text = "Left fader", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(0, 6, 0, 0) };
    readonly Label _nameRight = new() { Text = "Right fader", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(0, 6, 0, 0) };
    readonly Stepper _limLeft = new() { Minimum = 1, Maximum = 100, Value = 100 };
    readonly Stepper _limRight = new() { Minimum = 1, Maximum = 100, Value = 100 };
    readonly Button _btnRefresh = new() { Text = "Refresh devices", AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 5, 10, 5), Margin = new Padding(0) };
    readonly Label _status = new() { Text = "Starting…", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label _statusDot = new() { Text = "●", AutoSize = true, Font = new Font("Segoe UI", 8f), Margin = new Padding(0, 3, 6, 0) };

    readonly CardPanel _cardL = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 122, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 10, 16, 12) };
    readonly CardPanel _cardR = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 122, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 10, 16, 12) };
    TableLayoutPanel _tlL = null!, _tlR = null!, _footer = null!;
    FlowLayoutPanel _maxL = null!, _maxR = null!;

    readonly MMDeviceEnumerator _enum = new();

    Axis _left = null!, _right = null!;

    Thread? _hidThread;
    volatile bool _run;
    bool _loadingSettings;

    readonly NotifyIcon _tray = new() { Text = "ZMK Volume Fader", Icon = LoadAppIcon() };
    bool _exiting;

    public MainForm()
    {
        Text = "ZMK Volume Fader";
        Icon = LoadAppIcon();
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(460, 340);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _left = new Axis { Combo = _cbLeft, Bar = _barLeft, Pct = _pctLeft, Raw = _rawLeft, Limit = _limLeft };
        _right = new Axis { Combo = _cbRight, Bar = _barRight, Pct = _pctRight, Raw = _rawRight, Limit = _limRight };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _tlL = BuildCard(_cardL, _nameLeft, _pctLeft, _barLeft, _rawLeft, _cbLeft, out _maxL, _limLeft);
        _tlR = BuildCard(_cardR, _nameRight, _pctRight, _barRight, _rawRight, _cbRight, out _maxR, _limRight);

        _footer = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _btnRefresh.Click += (_, _) => LoadDevices();
        _footer.Controls.Add(_btnRefresh, 0, 0);
        var statusFlow = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
        statusFlow.Controls.Add(_statusDot);
        statusFlow.Controls.Add(_status);
        _footer.Controls.Add(statusFlow, 1, 0);

        root.Controls.Add(_cardL, 0, 0);
        root.Controls.Add(_cardR, 0, 1);
        root.Controls.Add(_footer, 0, 2);
        Controls.Add(root);

        _cbLeft.SelectedIndexChanged += (_, _) => OnDevicePicked(_left);
        _cbRight.SelectedIndexChanged += (_, _) => OnDevicePicked(_right);
        _limLeft.ValueChanged += (_, _) => OnLimitChanged(_left);
        _limRight.ValueChanged += (_, _) => OnLimitChanged(_right);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = true;

        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) MinimizeToTray(); };

        Load += (_, _) => { ApplyTheme(CurrentTheme()); LoadDevices(); LoadSettings(); StartHid(); };
        FormClosing += OnFormClosing;
    }

    // Lay out one fader card; returns the inner table and (via out) the Max group.
    TableLayoutPanel BuildCard(CardPanel card, Label name, Label pct, FaderBar bar, Label raw,
                               ComboBox combo, out FlowLayoutPanel maxGroup, Stepper limit)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // name / pct
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));  // bar
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // raw readout
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // combo / max

        t.Controls.Add(name, 0, 0);
        t.Controls.Add(pct, 1, 0);
        t.Controls.Add(bar, 0, 1); t.SetColumnSpan(bar, 2);
        t.Controls.Add(raw, 0, 2); t.SetColumnSpan(raw, 2);
        t.Controls.Add(combo, 0, 3);
        maxGroup = MaxCap(limit);
        t.Controls.Add(maxGroup, 1, 3);

        card.Controls.Add(t);
        return t;
    }

    static FlowLayoutPanel MaxCap(Stepper n)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 0, 0) };
        p.Controls.Add(new Label { Text = "Max", AutoSize = true, Margin = new Padding(0, 4, 6, 0) });
        p.Controls.Add(n);
        p.Controls.Add(new Label { Text = "%", AutoSize = true, Margin = new Padding(4, 4, 0, 0) });
        return p;
    }

    // ---- theme application ------------------------------------------------

    static bool OsLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int i) return i != 0;
        }
        catch { }
        return true;
    }

    static Theme CurrentTheme() => OsLightTheme() ? LightTheme : DarkTheme;

    void ApplyTheme(Theme t)
    {
        _theme = t;
        BackColor = t.Window;
        _footer.BackColor = Color.Transparent;
        foreach (var card in new[] { _cardL, _cardR }) card.BackColor = t.Card;
        foreach (var tl in new Control[] { _tlL, _tlR, _maxL, _maxR }) tl.BackColor = Color.Transparent;

        foreach (var b in new[] { _barLeft, _barRight }) { b.Track = t.Inset; b.Fill = t.Accent; b.BackColor = t.Card; b.Invalidate(); }
        foreach (var c in new[] { _cbLeft, _cbRight })
        {
            c.BackColor = t.CtlBg; c.ForeColor = t.Text;
            // Win10+ dark combo: themes the drop button and the dropdown list.
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, t.Dark ? "DarkMode_CFD" : null, null);
            c.Invalidate();
        }
        foreach (var u in new[] { _limLeft, _limRight }) { u.BackColor = t.CtlBg; u.ForeColor = t.Text; u.BorderColor = t.CtlBorder; u.ChevronColor = t.Subtle; u.Invalidate(); }
        _btnRefresh.BackColor = t.CtlBg;
        _btnRefresh.ForeColor = t.Text;
        _btnRefresh.FlatAppearance.BorderColor = t.CtlBorder;

        // Most labels are muted; the big % is primary, the status dot is accent.
        WalkLabels(this, l => { l.ForeColor = t.Subtle; l.BackColor = Color.Transparent; });
        _pctLeft.ForeColor = _pctRight.ForeColor = t.Text;
        _statusDot.ForeColor = t.Accent;

        SetTitleBarDark(t.Dark);
    }

    static void WalkLabels(Control root, Action<Label> apply)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label l) apply(l);
            WalkLabels(c, apply);
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);

    void SetTitleBarDark(bool dark)
    {
        if (!IsHandleCreated) return;
        int v = dark ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+); 19 on older builds.
        if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x001A)   // WM_SETTINGCHANGE — re-read the OS theme
            ApplyTheme(CurrentTheme());
    }

    // ---- tray -------------------------------------------------------------

    void MinimizeToTray() => Hide();

    void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            var r = MessageBox.Show(this, "Minimize to tray instead of exiting?",
                "ZMK Volume Fader", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
        }

        _run = false;
        _tray.Visible = false;
        _tray.Dispose();
    }

    static Icon LoadAppIcon()
    {
        var asm = typeof(MainForm).Assembly;
        string name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        return new Icon(s);
    }

    // ---- audio devices ----------------------------------------------------

    void LoadDevices()
    {
        string? prevLeft = (_cbLeft.SelectedItem as DeviceItem)?.Id;
        string? prevRight = (_cbRight.SelectedItem as DeviceItem)?.Id;

        var items = _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new DeviceItem { Id = d.ID, Name = d.FriendlyName, Device = d })
            .OrderBy(d => d.Name)
            .ToArray();

        foreach (var cb in new[] { _cbLeft, _cbRight })
        {
            cb.BeginUpdate();
            cb.Items.Clear();
            cb.Items.AddRange(items);
            cb.EndUpdate();
        }
        SelectById(_cbLeft, prevLeft);
        SelectById(_cbRight, prevRight);
    }

    static void SelectById(ComboBox cb, string? id)
    {
        if (id == null) return;
        for (int i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] is DeviceItem di && di.Id == id) { cb.SelectedIndex = i; return; }
    }

    void OnDevicePicked(Axis a)
    {
        if (_loadingSettings) return;
        SaveSettings();
        a.LastApplied = -1;
    }

    void OnLimitChanged(Axis a)
    {
        if (!_loadingSettings) SaveSettings();
        Render(a);
    }

    // ---- settings ---------------------------------------------------------

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (s == null) return;
            _loadingSettings = true;
            SelectById(_cbLeft, s.LeftDeviceId);
            SelectById(_cbRight, s.RightDeviceId);
            _limLeft.Value = ClampLimit(s.LeftMax);
            _limRight.Value = ClampLimit(s.RightMax);
        }
        catch { }
        finally { _loadingSettings = false; }
    }

    void SaveSettings()
    {
        try
        {
            var s = new Settings
            {
                LeftDeviceId = (_cbLeft.SelectedItem as DeviceItem)?.Id,
                RightDeviceId = (_cbRight.SelectedItem as DeviceItem)?.Id,
                LeftMax = (int)_limLeft.Value,
                RightMax = (int)_limRight.Value,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    static int ClampLimit(int pct) => Math.Clamp(pct, 1, 100);

    // ---- calibration ------------------------------------------------------

    static double ValueToPercent(int v)
    {
        if (v <= Curve[0].v) return Curve[0].pct;
        for (int i = 1; i < Curve.Length; i++)
        {
            if (v <= Curve[i].v)
            {
                var (v0, p0) = Curve[i - 1];
                var (v1, p1) = Curve[i];
                return p0 + (double)(v - v0) / (v1 - v0) * (p1 - p0);
            }
        }
        return Curve[^1].pct;
    }

    // ---- HID --------------------------------------------------------------

    const uint JoystickUsage = 0x00010004;   // Generic Desktop / Joystick

    static SortedSet<uint> Usages(HidDevice d)
    {
        var usages = new SortedSet<uint>();
        try
        {
            foreach (var item in d.GetReportDescriptor().DeviceItems)
                foreach (var u in item.Usages.GetAllValues())
                    usages.Add(u);
        }
        catch { }
        return usages;
    }

    static HidDevice? FindFader()
    {
        var mine = DeviceList.Local.GetHidDevices()
            .Where(d => d.VendorID == VID && d.ProductID == PID)
            .ToArray();

        var joy = mine.FirstOrDefault(d => Usages(d).Contains(JoystickUsage));
        if (joy != null) return joy;

        return mine
            .Where(d => Usages(d).Any(u => (u >> 16) == 0x0001))
            .OrderBy(d => d.GetMaxInputReportLength())
            .FirstOrDefault();
    }

    void StartHid()
    {
        _run = true;
        _hidThread = new Thread(HidLoop) { IsBackground = true, Name = "fader-hid" };
        _hidThread.Start();
    }

    void HidLoop()
    {
        while (_run)
        {
            var dev = FindFader();
            if (dev == null) { SetStatus("Dongle not found — plug it in…", false); Thread.Sleep(1500); continue; }
            if (!dev.TryOpen(out HidStream stream)) { SetStatus("Found dongle, but couldn't open it", false); Thread.Sleep(1500); continue; }

            string devName; try { devName = dev.GetProductName(); } catch { devName = "ZMK keyboard"; }
            SetStatus($"Connected to {devName}", true);
            using (stream)
            {
                stream.ReadTimeout = 1000;
                var buf = new byte[dev.GetMaxInputReportLength()];
                while (_run)
                {
                    int n;
                    try { n = stream.Read(buf, 0, buf.Length); }
                    catch (TimeoutException) { continue; }
                    catch { break; }
                    if (n >= 5 && buf[0] == 0x02)
                        OnFaders(ReadAxis(buf, 1), ReadAxis(buf, 3));
                }
            }
            SetStatus("Reconnecting…", false);
        }
    }

    static int ReadAxis(byte[] b, int i) => (short)(b[i] | (b[i + 1] << 8));

    void OnFaders(int left, int right)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            ApplyAxis(_left, left);
            ApplyAxis(_right, right);
        });
    }

    void ApplyAxis(Axis a, int raw)
    {
        if (raw < a.RawMin) a.RawMin = raw;
        if (raw > a.RawMax) a.RawMax = raw;
        a.LastRaw = raw;

        // EMA smooth (16-bit value is finer but noisier, ~+/-15 counts).
        a.Sm = a.Sm < 0 ? raw : a.Sm * 0.85 + raw * 0.15;
        Render(a);
    }

    void Render(Axis a)
    {
        if (a.Sm < 0) return;

        int v = (int)Math.Round(a.Sm);
        double faderPct = ValueToPercent(v);
        int cap = (int)a.Limit.Value;
        double pf = Math.Clamp(faderPct * cap / 100.0, 0, 100);

        // Hysteresis: hold the current integer % until pf moves > Hyst off it.
        int applied = a.LastApplied < 0 || Math.Abs(pf - a.LastApplied) > Hyst
            ? (int)Math.Round(pf)
            : a.LastApplied;

        a.Bar.Value = applied;
        a.Pct.Text = $"{applied}%";
        a.Raw.Text = $"raw {a.LastRaw}  ·  {a.RawMin}–{a.RawMax}";

        if (applied == a.LastApplied) return;
        a.LastApplied = applied;
        if (a.Combo.SelectedItem is DeviceItem di)
        {
            try { di.Device.AudioEndpointVolume.MasterVolumeLevelScalar = applied / 100f; }
            catch { }
        }
    }

    void SetStatus(string text, bool connected)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _status.Text = text;
            _statusDot.ForeColor = connected ? _theme.Accent : _theme.Subtle;
        });
    }
}
