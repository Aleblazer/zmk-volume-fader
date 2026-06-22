using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using HidSharp;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace ZmkVolumeFader;

/// <summary>How the UI picks its palette: follow Windows, or force one.</summary>
internal enum ThemeMode { Auto, Light, Dark }

/// <summary>
/// Reads a ZMK dongle's hid-io fader report (vendor page 0xFF00, report id 2:
/// two signed 16-bit LE axes, [1..2]=left [3..4]=right, raw wiper mV ~0..3300) and drives
/// the volume of two chosen Windows output devices. Each fader has its own
/// max-volume cap: the throw scales into 0..cap (top = cap, middle = cap/2).
/// The UI follows the OS light/dark theme with a green accent.
/// </summary>
public class MainForm : Form
{
    const int VID = 0x1D50, PID = 0x615E;

    // The per-fader value->% mapping lives in each Axis.Cal / Axis.Curve (see
    // Calibration). It's edited live via the Calibrate dialog and persisted to
    // settings, so there's no hardcoded curve here.

    // Output hysteresis (percent): hold the current % until the value moves more
    // than this off it, so boundary noise can't flip-flop 8<->9. Must be < 1.
    const double Hyst = 0.9;

    // ---- theme ------------------------------------------------------------

    internal sealed record Theme(bool Dark, Color Window, Color Card, Color Inset, Color Text,
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
    ThemeMode _themeMode = ThemeMode.Auto;   // Auto follows the OS; Light/Dark force it

    // ---- custom controls --------------------------------------------------

    // Console-style fader: a tick-scaled track with a knob at the current level,
    // -/+ ends, and a green->yellow->red colored fill. Display-only (the position
    // is driven by the physical fader), styled to match it.
    sealed class FaderBar : Control
    {
        int _value;
        public int Value { get => _value; set { int v = Math.Clamp(value, 0, 100); if (v != _value) { _value = v; Invalidate(); } } }
        public Color Track { get; set; } = Color.Gray;       // unfilled groove
        public Color Fill { get; set; } = Color.LimeGreen;   // green (low)
        public Color Mid { get; set; } = Color.FromArgb(0xF2, 0xC4, 0x3D);   // yellow (mid)
        public Color Hot { get; set; } = Color.FromArgb(0xE0, 0x4F, 0x4F);   // red (high)
        public Color Knob { get; set; } = Color.White;
        public Color KnobEdge { get; set; } = Color.Gray;
        public Color Tick { get; set; } = Color.Gray;

        public FaderBar() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                                      | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int w = Width, h = Height;
            if (w < 16 || h < 12) return;

            float cy = h / 2f;
            float knobR = Math.Max(6f, Math.Min(10f, h / 2f - 4f));
            float thk = Math.Max(4f, knobR * 0.7f);
            const float symW = 12f;                                  // -/+ glyph zone
            float left = symW + knobR, right = w - symW - knobR;     // knob-centre travel
            if (right - left < 4) return;
            float f = Math.Clamp(_value / 100f, 0f, 1f);
            float kx = left + (right - left) * f;

            // Tick scale above and below the track (longer every 5th).
            const int N = 21;
            float gap = thk / 2f + 3f;
            for (int i = 0; i < N; i++)
            {
                float x = left + (right - left) * i / (N - 1);
                float tl = (i % 5 == 0) ? 7f : 4f;
                using var tp = new Pen(Color.FromArgb(i % 5 == 0 ? 150 : 90, Tick), 1f);
                g.DrawLine(tp, x, cy - gap - tl, x, cy - gap);
                g.DrawLine(tp, x, cy + gap, x, cy + gap + tl);
            }

            // Unfilled groove, then the colored fill up to the knob (gradient mapped
            // across the full travel so the leading edge's hue tracks the level).
            using (var tb = new SolidBrush(Track))
            using (var tpath = Pill(new RectangleF(left, cy - thk / 2f, right - left, thk)))
                g.FillPath(tb, tpath);
            if (kx - left >= thk * 0.5f)
            {
                using var fpath = Pill(new RectangleF(left, cy - thk / 2f, kx - left, thk));
                using var grad = new LinearGradientBrush(new RectangleF(left, cy - thk / 2f, right - left, thk),
                    Fill, Hot, LinearGradientMode.Horizontal)
                {
                    InterpolationColors = new ColorBlend { Colors = new[] { Fill, Mid, Hot }, Positions = new[] { 0f, 0.5f, 1f } },
                };
                g.FillPath(grad, fpath);
            }

            // -/+ end glyphs (accent), like a physical fader's scale ends.
            using (var sp = new Pen(Fill, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(sp, 3f, cy, 3f + 7f, cy);                 // minus
                float px = w - 7f;
                g.DrawLine(sp, px - 3.5f, cy, px + 3.5f, cy);        // plus (horizontal)
                g.DrawLine(sp, px, cy - 3.5f, px, cy + 3.5f);        // plus (vertical)
            }

            // Knob: soft shadow, light body, themed edge, center dot tinted to level.
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillEllipse(sh, kx - knobR, cy - knobR + 1.5f, knobR * 2, knobR * 2);
            using (var kb = new SolidBrush(Knob))
                g.FillEllipse(kb, kx - knobR, cy - knobR, knobR * 2, knobR * 2);
            using (var ke = new Pen(KnobEdge, 1.4f))
                g.DrawEllipse(ke, kx - knobR, cy - knobR, knobR * 2, knobR * 2);
            float dotR = knobR * 0.42f;
            using (var cd = new SolidBrush(ColorAt(f)))
                g.FillEllipse(cd, kx - dotR, cy - dotR, dotR * 2, dotR * 2);
        }

        // Green->yellow->red at fraction f, matching the fill gradient.
        Color ColorAt(float f) =>
            f <= 0 ? Fill : f >= 1 ? Hot :
            f < 0.5f ? Lerp(Fill, Mid, f / 0.5f) : Lerp(Mid, Hot, (f - 0.5f) / 0.5f);

        static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
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
    // can't be dark-themed). A borderless child text box handles typing/caret;
    // this control paints the frame + chevrons. Type then Enter, click a
    // chevron, use Up/Down, or scroll.
    sealed class Stepper : Control
    {
        readonly TextBox _box = new() { BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Right, MaxLength = 3 };
        int _value = 100, _min = 1, _max = 100;
        public event EventHandler? ValueChanged;
        public int Minimum { get => _min; set => _min = value; }
        public int Maximum { get => _max; set => _max = value; }
        public int Value { get => _value; set => Set(value); }
        public Color BorderColor { get; set; } = Color.Gray;
        public Color ChevronColor { get; set; } = Color.Gray;
        public Color Surround { get; set; } = Color.FromArgb(0x2B, 0x2E, 0x36);  // fills the rounded corners

        public Stepper()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(62, 23);
            _box.Text = _value.ToString();
            _box.KeyDown += OnBoxKeyDown;
            _box.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            _box.LostFocus += (_, _) => Commit();
            _box.MouseWheel += (_, e) => Set(_value + Math.Sign(e.Delta));
            Controls.Add(_box);
            LayoutBox();
        }

        int Zone => 18;   // right strip holding the chevrons

        void LayoutBox()
        {
            int h = _box.PreferredHeight;
            _box.SetBounds(7, Math.Max(0, (Height - h) / 2), Math.Max(10, Width - Zone - 9), h);
        }

        void Set(int v)
        {
            v = Math.Clamp(v, _min, _max);
            bool changed = v != _value;
            _value = v;
            string s = v.ToString();
            if (_box.Text != s) _box.Text = s;
            Invalidate();
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        void Commit()
        {
            if (int.TryParse(_box.Text, out int v)) Set(v);
            else _box.Text = _value.ToString();
        }

        void OnBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Up) { Set(_value + 1); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { Set(_value - 1); e.SuppressKeyPress = true; }
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); LayoutBox(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _box.Font = Font; LayoutBox(); }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); _box.BackColor = BackColor; }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); _box.ForeColor = ForeColor; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Surround);
            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Rounded(box, 6))
            {
                using var bg = new SolidBrush(BackColor);
                using var pen = new Pen(BorderColor);
                g.FillPath(bg, path);
                g.DrawPath(pen, path);
            }
            int cx = Width - Zone / 2 - 3, my = Height / 2;
            using var cb = new SolidBrush(ChevronColor);
            g.FillPolygon(cb, new[] { new Point(cx - 4, my - 2), new Point(cx + 4, my - 2), new Point(cx, my - 6) });
            g.FillPolygon(cb, new[] { new Point(cx - 4, my + 2), new Point(cx + 4, my + 2), new Point(cx, my + 6) });
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.X >= Width - Zone) { Set(_value + (e.Y < Height / 2 ? 1 : -1)); _box.Focus(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Set(_value + Math.Sign(e.Delta));
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
        public required Stepper Limit;
        public Calibration Cal = new();                       // value->% mapping (persisted)
        public (int v, int pct)[] Curve = Array.Empty<(int, int)>();  // built from Cal
        public double Sm = -1;          // EMA state (smoothed raw value)
        public int LastRaw;             // last raw value (so a cap change can re-render)
        public int LastApplied = -1;    // last volume % pushed to the device (deadband)
    }

    sealed class Settings
    {
        public string? LeftDeviceId { get; set; }
        public string? RightDeviceId { get; set; }
        public int LeftMax { get; set; } = 100;
        public int RightMax { get; set; } = 100;
        public Calibration? LeftCal { get; set; }
        public Calibration? RightCal { get; set; }
        public ThemeMode ThemeMode { get; set; } = ThemeMode.Auto;
    }

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZmkVolumeFader", "settings.json");

    // ---- controls ---------------------------------------------------------

    readonly ComboBox _cbLeft = new() { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22, Dock = DockStyle.Fill, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Right };
    readonly ComboBox _cbRight = new() { DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22, Dock = DockStyle.Fill, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Right };
    readonly FaderBar _barLeft = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
    readonly FaderBar _barRight = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
    readonly Label _pctLeft = new() { Text = "—", AutoSize = true, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 15f) };
    readonly Label _pctRight = new() { Text = "—", AutoSize = true, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 15f) };
    readonly Label _nameLeft = new() { Text = "Left fader", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(0, 6, 0, 0) };
    readonly Label _nameRight = new() { Text = "Right fader", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(0, 6, 0, 0) };
    readonly Stepper _limLeft = new() { Minimum = 1, Maximum = 100, Value = 100 };
    readonly Stepper _limRight = new() { Minimum = 1, Maximum = 100, Value = 100 };
    readonly Button _btnRefresh = new() { Text = "Refresh devices", AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 5, 10, 5), Margin = new Padding(0) };
    readonly Button _btnOptions = new() { Text = "Options", AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 5, 10, 5), Margin = new Padding(8, 0, 0, 0) };
    readonly Label _status = new() { Text = "Starting…", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label _statusDot = new() { Text = "●", AutoSize = true, Font = new Font("Segoe UI", 8f), Margin = new Padding(0, 3, 6, 0) };

    readonly CardPanel _cardL = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 134, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 10, 16, 12) };
    readonly CardPanel _cardR = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 134, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 10, 16, 12) };
    TableLayoutPanel _tlL = null!, _tlR = null!, _footer = null!;
    FlowLayoutPanel _maxL = null!, _maxR = null!;

    readonly MMDeviceEnumerator _enum = new();

    Axis _left = null!, _right = null!;

    Thread? _hidThread;
    volatile bool _run;
    bool _loadingSettings;
    bool _calibrating;   // true while the calibration dialog is open (don't drive devices)

    readonly NotifyIcon _tray = new() { Text = "ZMK Volume Fader", Icon = LoadAppIcon() };
    bool _exiting;

    public MainForm()
    {
        Text = "ZMK Volume Fader";
        Icon = LoadAppIcon();
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(460, 364);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _left = new Axis { Combo = _cbLeft, Bar = _barLeft, Pct = _pctLeft, Limit = _limLeft };
        _right = new Axis { Combo = _cbRight, Bar = _barRight, Pct = _pctRight, Limit = _limRight };
        _left.Curve = _left.Cal.BuildCurve();
        _right.Curve = _right.Cal.BuildCurve();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _tlL = BuildCard(_cardL, _nameLeft, _pctLeft, _barLeft, _cbLeft, out _maxL, _limLeft);
        _tlR = BuildCard(_cardR, _nameRight, _pctRight, _barRight, _cbRight, out _maxR, _limRight);

        _footer = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _btnRefresh.Click += (_, _) => LoadDevices();
        _btnOptions.Click += (_, _) => OpenOptions();
        var leftBtns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        leftBtns.Controls.Add(_btnRefresh);
        leftBtns.Controls.Add(_btnOptions);
        _footer.Controls.Add(leftBtns, 0, 0);
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
        _cbLeft.DrawItem += OnComboDrawItem;
        _cbRight.DrawItem += OnComboDrawItem;
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
    TableLayoutPanel BuildCard(CardPanel card, Label name, Label pct, FaderBar bar,
                               ComboBox combo, out FlowLayoutPanel maxGroup, Stepper limit)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // name / pct
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));  // fader (track + ticks + knob)
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // combo / max

        t.Controls.Add(name, 0, 0);
        t.Controls.Add(pct, 1, 0);
        t.Controls.Add(bar, 0, 1); t.SetColumnSpan(bar, 2);
        t.Controls.Add(combo, 0, 2);
        maxGroup = MaxCap(limit);
        t.Controls.Add(maxGroup, 1, 2);

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

    Theme CurrentTheme() => _themeMode switch
    {
        ThemeMode.Light => LightTheme,
        ThemeMode.Dark => DarkTheme,
        _ => OsLightTheme() ? LightTheme : DarkTheme,   // Auto
    };

    // ---- "start with Windows" (HKCU Run key) ------------------------------

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "ZMK Volume Fader";

    static bool GetStartWithWindows()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k?.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }

    static void SetStartWithWindows(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (k == null) return;
            if (on)
            {
                var exe = Environment.ProcessPath;
                if (exe != null) k.SetValue(RunValueName, $"\"{exe}\"");
            }
            else if (k.GetValue(RunValueName) != null)
            {
                k.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    void ApplyTheme(Theme t)
    {
        _theme = t;
        BackColor = t.Window;
        _footer.BackColor = Color.Transparent;
        foreach (var card in new[] { _cardL, _cardR }) card.BackColor = t.Card;
        foreach (var tl in new Control[] { _tlL, _tlR, _maxL, _maxR }) tl.BackColor = Color.Transparent;

        foreach (var b in new[] { _barLeft, _barRight })
        {
            b.Track = t.Inset; b.Fill = t.Accent; b.BackColor = t.Card;
            b.Knob = t.Dark ? Hex(0xE6, 0xE8, 0xEB) : Hex(0xFF, 0xFF, 0xFF);
            b.KnobEdge = t.Dark ? Hex(0x0E, 0x10, 0x14) : Hex(0xC2, 0xC6, 0xCC);
            b.Tick = t.Subtle;
            b.Invalidate();
        }
        foreach (var c in new[] { _cbLeft, _cbRight })
        {
            c.BackColor = t.CtlBg; c.ForeColor = t.Text;
            // Win10+ dark combo: themes the drop button and the dropdown list.
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, t.Dark ? "DarkMode_CFD" : null, null);
            c.Invalidate();
        }
        foreach (var u in new[] { _limLeft, _limRight }) { u.BackColor = t.CtlBg; u.ForeColor = t.Text; u.BorderColor = t.CtlBorder; u.ChevronColor = t.Subtle; u.Surround = t.Card; u.Invalidate(); }
        foreach (var btn in new[] { _btnRefresh, _btnOptions })
        {
            btn.BackColor = t.CtlBg;
            btn.ForeColor = t.Text;
            btn.FlatAppearance.BorderColor = t.CtlBorder;
        }

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

    // Owner-draw the device combos so the closed box and list match the theme
    // (CtlBg fill, theme text) and the highlighted item uses the green accent.
    void OnComboDrawItem(object? sender, DrawItemEventArgs e)
    {
        var cb = (ComboBox)sender!;
        bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
        bool hi = inList && (e.State & DrawItemState.Selected) != 0;
        Color bg = hi ? _theme.Accent : _theme.CtlBg;
        Color fg = hi ? AccentText() : _theme.Text;
        using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
        if (e.Index >= 0)
        {
            var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font, r, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // Black or white, whichever reads on the accent green.
    Color AccentText()
    {
        var a = _theme.Accent;
        double lum = (0.299 * a.R + 0.587 * a.G + 0.114 * a.B) / 255.0;
        return lum > 0.55 ? Color.FromArgb(0x10, 0x18, 0x12) : Color.White;
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
            if (s.LeftCal != null) ApplyCalibration(_left, s.LeftCal);
            if (s.RightCal != null) ApplyCalibration(_right, s.RightCal);
            _themeMode = s.ThemeMode;
            ApplyTheme(CurrentTheme());
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
                LeftCal = _left.Cal,
                RightCal = _right.Cal,
                ThemeMode = _themeMode,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    static int ClampLimit(int pct) => Math.Clamp(pct, 1, 100);

    // ---- options ----------------------------------------------------------

    void OpenOptions()
    {
        using var dlg = new OptionsDialog(_theme, _themeMode, GetStartWithWindows(),
            _left.Cal.Clone(), _right.Cal.Clone(),
            () => _left.LastRaw, () => _right.LastRaw);
        _calibrating = true;                 // stop driving devices while sweeping
        var result = dlg.ShowDialog(this);
        _calibrating = false;
        if (result == DialogResult.OK)
        {
            ApplyCalibration(_left, dlg.LeftCal);
            ApplyCalibration(_right, dlg.RightCal);
            _themeMode = dlg.SelectedTheme;
            ApplyTheme(CurrentTheme());
            SetStartWithWindows(dlg.StartWithWindows);
            SaveSettings();
        }
        // Re-push the current position to the devices now that we're live again.
        _left.LastApplied = _right.LastApplied = -1;
        Render(_left);
        Render(_right);
    }

    void ApplyCalibration(Axis a, Calibration c)
    {
        a.Cal = c;
        a.Curve = c.BuildCurve();
        a.LastApplied = -1;
    }

    // ---- HID --------------------------------------------------------------

    const uint FaderUsage = 0xFF000001;   // vendor-defined fader page (0xFF00, usage 0x01)

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

        var fader = mine.FirstOrDefault(d => Usages(d).Contains(FaderUsage));
        if (fader != null) return fader;

        return mine
            .Where(d => Usages(d).Any(u => (u >> 16) == 0xFF00))
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
        a.LastRaw = raw;

        // EMA smooth (16-bit value is finer but noisier, ~+/-15 counts).
        a.Sm = a.Sm < 0 ? raw : a.Sm * 0.85 + raw * 0.15;
        Render(a);
    }

    void Render(Axis a)
    {
        if (a.Sm < 0) return;

        int v = (int)Math.Round(a.Sm);
        double faderPct = Calibration.Eval(a.Curve, v);
        int cap = (int)a.Limit.Value;
        double pf = Math.Clamp(faderPct * cap / 100.0, 0, 100);

        // Hysteresis: hold the current integer % until pf moves > Hyst off it.
        int applied = a.LastApplied < 0 || Math.Abs(pf - a.LastApplied) > Hyst
            ? (int)Math.Round(pf)
            : a.LastApplied;

        a.Bar.Value = applied;
        a.Pct.Text = $"{applied}%";

        if (applied == a.LastApplied) return;
        a.LastApplied = applied;
        if (_calibrating) return;   // visualize, but don't drive the device while calibrating
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
