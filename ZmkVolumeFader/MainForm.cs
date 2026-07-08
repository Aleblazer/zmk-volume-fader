using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HidSharp;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ZmkVolumeFader;

/// <summary>How the UI picks its palette: follow Windows, or force one.</summary>
internal enum ThemeMode { Auto, Light, Dark }

/// <summary>What a slider controls: an output device, one app, or a category of apps.</summary>
internal enum TargetKind { Output, App, Category }

/// <summary>A named group of apps whose volume moves together.</summary>
internal sealed class Category
{
    public string Name { get; set; } = "";
    public List<string> AppKeys { get; set; } = new();   // process-name keys
}

/// <summary>
/// Reads a ZMK dongle's hid-io fader report (vendor page 0xFF00, report id 2:
/// up to eight signed 16-bit LE axes at bytes [1..], raw wiper mV ~0..3300) and drives
/// the volume of the chosen Windows output devices. Each fader has its own
/// max-volume cap: the throw scales into 0..cap (top = cap, middle = cap/2).
/// The UI follows the OS light/dark theme with a green accent.
/// </summary>
public class MainForm : Form
{
    const int VID = 0x1D50, PID = 0x615E;
    const int MaxAxes = 8;   // hid-io vendor report carries up to eight 16-bit axes

    // The git short hash the SetGitCommit build target embedded into
    // AssemblyInformationalVersion ("1.1.0+<hash>"), or "" when unavailable.
    static string CommitId()
    {
        var info = typeof(MainForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[(plus + 1)..] : "";
    }

    // Build id shown in the UI: release builds show the clean version; dev
    // (Debug) builds show the commit so you can tell dev builds apart.
    public static string VersionText()
    {
#if DEBUG
        string commit = CommitId();
        return string.IsNullOrEmpty(commit) ? "dev build" : $"dev · {commit}";
#else
        var ver = typeof(MainForm).Assembly.GetName().Version;
        return ver is null ? "" : $"v{ver.Major}.{ver.Minor}.{ver.Build}";
#endif
    }

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
    // is driven by the physical fader), styled to match it. Reused (ticks off) for
    // the Options dialog's live preview bars.
    internal sealed class FaderBar : Control
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
        public bool ShowTicks { get; set; } = true;
        // When true the fill/knob are drawn desaturated — the target isn't being
        // driven right now (unit unplugged, or an app target that isn't playing).
        bool _muted;
        public bool Muted { get => _muted; set { if (v_set(ref _muted, value)) Invalidate(); } }
        static bool v_set(ref bool f, bool v) { if (f == v) return false; f = v; return true; }

        // Interactive (virtual) faders let the user drag the knob to set the level.
        // Display-only (physical) faders leave this false and ignore the mouse.
        bool _interactive;
        public bool Interactive
        {
            get => _interactive;
            set { _interactive = value; Cursor = value ? Cursors.Hand : Cursors.Default; }
        }
        bool _dragging;
        // Raised while the user drags (live volume) and once more on release. The
        // bool is true on the final (mouse-up) event so the owner can persist then.
        public event Action<int, bool>? UserSet;

        public FaderBar() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                                      | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        // Horizontal span the knob centre travels across, matching OnPaint. Returns
        // false when the control is too small to interact with.
        bool Travel(out float left, out float right)
        {
            float knobR = Math.Max(6f, Math.Min(10f, Height / 2f - 4f));
            const float symW = 12f;
            left = symW + knobR;
            right = Width - symW - knobR;
            return right - left >= 4f;
        }

        int ValueFromX(int x)
        {
            if (!Travel(out float left, out float right)) return _value;
            return (int)Math.Round(Math.Clamp((x - left) / (right - left), 0f, 1f) * 100f);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!_interactive || e.Button != MouseButtons.Left) return;
            _dragging = true;
            Capture = true;
            Value = ValueFromX(e.X);
            UserSet?.Invoke(_value, false);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            Value = ValueFromX(e.X);
            UserSet?.Invoke(_value, false);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            UserSet?.Invoke(_value, true);   // committed — owner persists here
        }

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

            // Muted (target not driven): flatten the green→red gradient to one grey.
            Color grey = Lerp(Track, Knob, 0.45f);
            Color cLo = _muted ? grey : Fill, cMid = _muted ? grey : Mid, cHi = _muted ? grey : Hot;

            // Tick scale above and below the track (longer every 5th).
            if (ShowTicks)
            {
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
                    cLo, cHi, LinearGradientMode.Horizontal)
                {
                    InterpolationColors = new ColorBlend { Colors = new[] { cLo, cMid, cHi }, Positions = new[] { 0f, 0.5f, 1f } },
                };
                g.FillPath(grad, fpath);
            }

            // -/+ end glyphs (accent), like a physical fader's scale ends.
            using (var sp = new Pen(cLo, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
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
            using (var cd = new SolidBrush(_muted ? grey : ColorAt(f)))
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

        // Size the control to the (DPI-scaled) font so it isn't clipped/malformed
        // at high Windows scaling. Idempotent — safe to call after auto-scaling.
        public void SizeToFont()
        {
            Size = new Size(LogicalToDeviceUnits(60), _box.PreferredHeight + LogicalToDeviceUnits(8));
            LayoutBox();
        }

        int Zone => LogicalToDeviceUnits(18);   // right strip holding the chevrons

        void LayoutBox()
        {
            int h = _box.PreferredHeight;
            int padL = LogicalToDeviceUnits(7), padR = LogicalToDeviceUnits(9);
            _box.SetBounds(padL, Math.Max(0, (Height - h) / 2), Math.Max(10, Width - Zone - padR), h);
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
            int cx = Width - Zone / 2 - LogicalToDeviceUnits(3), my = Height / 2;
            int aw = LogicalToDeviceUnits(4), ah = LogicalToDeviceUnits(6), yo = LogicalToDeviceUnits(2);
            using var cb = new SolidBrush(ChevronColor);
            g.FillPolygon(cb, new[] { new Point(cx - aw, my - yo), new Point(cx + aw, my - yo), new Point(cx, my - ah) });
            g.FillPolygon(cb, new[] { new Point(cx - aw, my + yo), new Point(cx + aw, my + yo), new Point(cx, my + ah) });
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

    // A target that is an application's volume (a Windows mixer session), keyed by
    // executable/process name (or "#system" for System Sounds).
    sealed class AppItem
    {
        public required string Key;
        public required string Name;
        public override string ToString() => Name;
    }

    // A target that is a category of apps.
    sealed class CategoryItem
    {
        public required string Name;   // stored CategoryName (may be the Unassigned sentinel)
        public bool IsUnassigned => Name == UnassignedCategory;
        public override string ToString() => IsUnassigned ? UnassignedDisplay : Name;
    }

    const string SystemAppKey = "#system";

    // Synthetic device key for the home profile that holds virtual faders when no
    // physical fader unit is connected (so people without hardware still persist a
    // layout). A connected unit uses its own key; virtual faders can also live
    // mixed into a physical unit's profile.
    const string VirtualKey = "#virtual";

    // Sentinel category that captures every live app not placed in a real
    // category. Stored as CategoryName; never a user-created category (the '#'
    // prefix matches the System Sounds convention and can't be typed in the
    // category editor).
    const string UnassignedCategory = "#unassigned";
    const string UnassignedDisplay = "Everything Else";

    // Live mixer apps not assigned to any category — what the Unassigned
    // pseudo-category drives.
    IEnumerable<string> UnassignedKeys() =>
        _appSessions.Keys.Where(k => !_categories.Any(c => c.AppKeys.Contains(k)));

    // Fires a callback whenever the set of audio endpoints changes (plug/unplug,
    // enable/disable) so the app can re-pick each fader's active output live.
    // Callbacks arrive on an MMDevice thread; the handler marshals to the UI.
    sealed class DeviceNotify : IMMNotificationClient
    {
        readonly Action _changed;
        public DeviceNotify(Action changed) => _changed = changed;
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
        public void OnDeviceAdded(string pwstrDeviceId) => _changed();
        public void OnDeviceRemoved(string deviceId) => _changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    /// <summary>Per-fader UI + filtering state.</summary>
    sealed class Axis
    {
        public required RoundedComboBox Combo;
        public required FaderBar Bar;
        public required Label Pct;     // big "62%" readout
        public required Label Name;    // "Left fader" heading
        public required Stepper Limit;
        public required CardPanel Card;
        public RoundedButton[] Tabs = Array.Empty<RoundedButton>();  // Output/Apps/Categories
        public RoundedButton? Remove;   // virtual faders only: the delete button
        public int AxisIndex;                                 // which HID report axis (0..7) drives this slider
        // Virtual faders have no HID axis; VValue (0..100) is the mouse-set level
        // and stands in for the physical throw. AxisIndex is -1 for these.
        public bool IsVirtual;
        public int VValue = 50;
        public Calibration Cal = new();                       // value->% mapping (persisted)
        public (int v, int pct)[] Curve = Array.Empty<(int, int)>();  // built from Cal
        public double Sm = -1;          // EMA state (smoothed raw value)
        public int LastRaw;             // last raw value (so a cap change can re-render)
        public int LastApplied = -1;    // last volume % pushed to the device (deadband)

        // Ranked output preferences (highest first). The active output is the
        // top-most entry whose device is present; if a higher one (re)appears it
        // takes back over. Override is a device manually picked on the main window,
        // which wins while present until the user re-selects the auto target or
        // re-ranks the list. ActiveId is the output currently being driven.
        public List<OutputPref> Prefs = new();
        public string? OverrideId;
        public string? ActiveId;

        // Target: Output (ranked list above), App (AppKey), or Category
        // (CategoryName). The active picker tab mirrors Target.
        public TargetKind Target;
        public string? AppKey;
        public string? CategoryName;
    }

    // One slider within a device profile (persisted). Max lives per-output in
    // DeviceMax, not here.
    sealed class SliderConfig
    {
        public int AxisIndex { get; set; }
        public string Label { get; set; } = "";
        public Calibration Cal { get; set; } = new();
        public List<OutputPref> Outputs { get; set; } = new();
        public string? OverrideId { get; set; }
        public TargetKind Target { get; set; }
        public string? AppKey { get; set; }
        public string? CategoryName { get; set; }
        public int Max { get; set; } = 100;   // per-slider cap (used for app/category targets)
        // A virtual fader has no HID axis (AxisIndex = -1); it's driven by dragging
        // the on-screen bar with the mouse. Value is its last position (0..100).
        public bool IsVirtual { get; set; }
        public int Value { get; set; } = 50;
    }

    // Everything remembered for one physical fader unit (its sliders, in order).
    sealed class DeviceProfile
    {
        public string Name { get; set; } = "";
        public List<SliderConfig> Sliders { get; set; } = new();
    }

    sealed class Settings
    {
        // Per-device layouts keyed by device identity (serial, else VID:PID).
        public Dictionary<string, DeviceProfile> Devices { get; set; } = new();
        // Per-output max-volume cap, keyed by audio endpoint id (global across
        // devices — a given output's cap is the same whoever drives it).
        public Dictionary<string, int> DeviceMax { get; set; } = new();
        // process-name key -> display name for every app we've seen a session for
        // (so assigned/grouped apps keep a name while closed).
        public Dictionary<string, string> KnownApps { get; set; } = new();
        // User-defined app groups (global across devices).
        public List<Category> Categories { get; set; } = new();
        public ThemeMode ThemeMode { get; set; } = ThemeMode.Auto;

        // Legacy pre-multi-slider flat fields, read once to migrate settings.json.
        public string? LeftDeviceId { get; set; }
        public string? RightDeviceId { get; set; }
        public int LeftMax { get; set; } = 100;
        public int RightMax { get; set; } = 100;
        public List<OutputPref>? LeftOutputs { get; set; }
        public List<OutputPref>? RightOutputs { get; set; }
        public string? LeftOverrideId { get; set; }
        public string? RightOverrideId { get; set; }
        public Calibration? LeftCal { get; set; }
        public Calibration? RightCal { get; set; }
    }

    // Live copy of Settings.DeviceMax (endpoint id -> cap %). Persisted on change.
    Dictionary<string, int> _deviceMax = new();
    // Per-device profiles + the identity of the connected device whose profile is
    // applied to the sliders. _legacyProfile seeds the first device from migrated
    // old settings.
    Dictionary<string, DeviceProfile> _devices = new();
    string? _activeKey;
    DeviceProfile? _legacyProfile;

    // App-volume tracking. _knownApps is every app ever seen in the mixer
    // (persisted, keyed by process name); _appSessions is the live sessions per
    // app, refreshed by the poll timer; the target combos list both outputs and
    // known apps.
    readonly Dictionary<string, string> _knownApps = new();
    HashSet<string> _liveApps = new();   // apps with a session right now (i.e. in the mixer)
    Dictionary<string, List<AudioSessionControl>> _appSessions = new();
    // App-key -> extracted 16px exe icon (null once = couldn't extract, retried
    // cheaply while still null). Kept for the app lifetime.
    readonly Dictionary<string, Image?> _appIcons = new();
    List<Category> _categories = new();
    readonly System.Windows.Forms.Timer _sessionPoll = new() { Interval = 1000 };

    // Currently-present render endpoints (id -> item), rebuilt by LoadDevices.
    readonly Dictionary<string, DeviceItem> _present = new();
    // True while we set a combo's selection programmatically, so the
    // SelectedIndexChanged handler doesn't mistake it for a manual override.
    bool _applyingActive;

    static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZmkVolumeFader");
    // Multi-slider build uses its own file so it can't clobber the release build's
    // settings.json; the old file is read once to migrate.
    static string SettingsPath => Path.Combine(SettingsDir, "settings.v2.json");
    static string LegacySettingsPath => Path.Combine(SettingsDir, "settings.json");

    // ---- controls ---------------------------------------------------------

    readonly RoundedButton _btnRefresh = new() { Text = "Refresh devices", AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0) };
    readonly RoundedButton _btnOptions = new() { Text = "Options", AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(8, 0, 0, 0) };
    readonly Label _status = new() { Text = "Starting…", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label _statusDot = new() { Text = "●", AutoSize = true, Font = new Font("Segoe UI", 8f), Margin = new Padding(0, 3, 6, 0) };

    // Slider cards are built dynamically into this scrollable host (one row each).
    TableLayoutPanel _sliderHost = null!, _footer = null!;

    readonly MMDeviceEnumerator _enum = new();
    DeviceNotify? _notify;
    // Coalesces bursts of device notifications into a single refresh.
    readonly System.Windows.Forms.Timer _deviceDebounce = new() { Interval = 250 };

    Axis _left = null!, _right = null!;
    // All sliders in axis order (count comes from the active device profile).
    Axis[] _sliders = Array.Empty<Axis>();
    // Latest raw value per HID axis (0..5), independent of the slider mapping —
    // the setup wizard reads this to detect which fader is being moved.
    readonly int[] _lastAxisRaw = new int[MaxAxes];

    Thread? _hidThread;
    volatile bool _run;
    bool _loadingSettings;
    bool _calibrating;   // true while the calibration dialog is open (don't drive devices)
    string _connText = "Starting…";   // last dongle-connection status text
    bool _connected;                  // dongle currently connected

    readonly NotifyIcon _tray = new() { Text = "ZMK Volume Fader", Icon = LoadAppIcon() };
    bool _exiting;
    bool _trayHintShown;   // one-time "still running in the tray" balloon
    readonly ToolTip _tip = new() { AutoPopDelay = 8000, InitialDelay = 450, ReshowDelay = 150 };

    public MainForm()
    {
        // Literal sizes below are authored at 96 dpi; scale them to the actual
        // display so nothing clips at 125%/150%/… Windows scaling.
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ZMK Volume Fader";
#if DEBUG
        // Dev builds carry the commit in the title so you can tell them apart.
        var _cid = CommitId();
        if (!string.IsNullOrEmpty(_cid)) Text += $" — {_cid}";
#endif
        Icon = LoadAppIcon();
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(460, 364);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // Build the sliders. The list is what makes the count arbitrary; a later
        // phase sets it per device from the setup wizard. Two for now.
        _sliders = new[] { BuildSlider(0, "Left fader"), BuildSlider(1, "Right fader") };
        _left = _sliders[0];
        _right = _sliders[1];
        ClientSize = new Size(ClientSize.Width, WindowHeightFor(_sliders.Length));

        // Slider cards stack in a scrollable host; the footer stays pinned below.
        _sliderHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
        _sliderHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        PopulateSliderHost();

        _footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _btnRefresh.Click += (_, _) => LoadDevices();
        _btnOptions.Click += (_, _) => OpenOptions();
        _tip.SetToolTip(_btnRefresh, "Re-scan audio devices and apps");
        _tip.SetToolTip(_btnOptions, "Calibration, sliders, categories, and preferences");
        var leftBtns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        leftBtns.Controls.Add(_btnRefresh);
        leftBtns.Controls.Add(_btnOptions);
        _footer.Controls.Add(leftBtns, 0, 0);
        var statusFlow = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
        statusFlow.Controls.Add(_statusDot);
        statusFlow.Controls.Add(_status);
        _footer.Controls.Add(statusFlow, 1, 0);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(14), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // slider host (scrolls if needed)
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // footer (pinned)
        root.Controls.Add(_sliderHost, 0, 0);
        root.Controls.Add(_footer, 0, 1);
        Controls.Add(root);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = true;

        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) MinimizeToTray(); };
        // Moved to a monitor at a different scale — re-fit after the framework
        // rescales the controls.
        DpiChanged += (_, _) => BeginInvoke(FitWindowHeight);

        Load += (_, _) => { ApplyTheme(CurrentTheme()); LoadDevices(); LoadSettings(); MaybeActivateVirtualHome(); LoadCachedIcons(); PopulateCombos(); FitWindowHeight(); StartHid(); RegisterDeviceNotifications(); StartSessionPoll(); };
        FormClosing += OnFormClosing;
    }

    // Main-window height for N slider cards, capped (the host scrolls beyond).
    static int WindowHeightFor(int n) => Math.Clamp(n * 180 + 70, 300, 720);

    // Size the owner-drawn combos and the Max steppers to the (DPI-scaled) font so
    // neither the dropdown rows nor the closed box clip at 125%+ scaling.
    void TuneComboItemHeight()
    {
        foreach (var s in _sliders)
        {
            int fh = s.Combo.Font.Height;
            s.Combo.ItemHeight = fh + LogicalToDeviceUnits(8);   // dropdown list rows
            // Force the closed box taller than the font (a DropDownList otherwise
            // clamps to ~font height and clips descenders at high DPI).
            s.Combo.DesiredHeight = fh + LogicalToDeviceUnits(12);
            s.Combo.Height = s.Combo.DesiredHeight;
            s.Limit.SizeToFont();
        }
    }

    // Size the window to the cards' actual content. Fonts are point-based so this
    // is correct at any Windows display scaling; the host scrolls past the cap.
    void FitWindowHeight()
    {
        TuneComboItemHeight();
        int total = 0;
        foreach (var s in _sliders)
        {
            if (s.Card.Controls.Count > 0 && s.Card.Controls[0] is TableLayoutPanel inner)
                // +2 logical px so sub-pixel rounding (notably at 150%) can't let
                // the card's rounded clip region shave the combo's bottom edge.
                s.Card.Height = inner.PreferredSize.Height + s.Card.Padding.Vertical + LogicalToDeviceUnits(2);
            total += s.Card.Height + s.Card.Margin.Vertical;
        }
        int chrome = _footer.PreferredSize.Height + LogicalToDeviceUnits(14) * 2 + LogicalToDeviceUnits(12);
        int want = total + chrome;
        int cap = Math.Min(LogicalToDeviceUnits(760), Screen.FromControl(this).WorkingArea.Height - LogicalToDeviceUnits(80));
        // Force width to the DPI-scaled design width too — belt-and-braces in case
        // the framework auto-scale didn't already widen it.
        ClientSize = new Size(LogicalToDeviceUnits(460), Math.Clamp(want, LogicalToDeviceUnits(300), cap));
    }

    // (Re)build the slider host with one row per slider. Called on construction
    // and whenever the slider set changes.
    void PopulateSliderHost()
    {
        _sliderHost.SuspendLayout();
        _sliderHost.Controls.Clear();
        _sliderHost.RowStyles.Clear();
        _sliderHost.RowCount = _sliders.Length;
        for (int i = 0; i < _sliders.Length; i++)
        {
            _sliderHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _sliderHost.Controls.Add(_sliders[i].Card, 0, i);
        }
        _sliderHost.ResumeLayout();
    }

    // Build one slider: its card, controls, and wiring, for the given HID axis.
    // A virtual slider (isVirtual) has no axis — its bar is draggable and its
    // level is set by the mouse instead of the hardware.
    Axis BuildSlider(int axisIndex, string name, bool isVirtual = false)
    {
        // Dock.Fill so the combo fills its cell exactly (its height is forced via
        // DesiredHeight + reported through GetPreferredSize, so the row is sized to
        // match). Anchoring instead let the taller window spill below its cell and
        // clip against the card edge at 150% scaling.
        var combo = new RoundedComboBox { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22, Margin = new Padding(0), Dock = DockStyle.Fill, Placeholder = "No target selected" };
        var bar = new FaderBar { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4), Interactive = isVirtual };
        var pct = new Label { Text = "—", AutoSize = true, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 15f) };
        var nameLbl = new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(0, 6, 0, 0) };
        var limit = new Stepper { Minimum = 1, Maximum = 100, Value = 100 };
        var card = new CardPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 168, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 10, 16, 12) };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // name / pct
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));  // fader (track + ticks + knob)
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // target tabs
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // combo / max
        t.Controls.Add(nameLbl, 0, 0);
        t.Controls.Add(pct, 1, 0);
        t.Controls.Add(bar, 0, 1); t.SetColumnSpan(bar, 2);

        var axis = new Axis { AxisIndex = axisIndex, Combo = combo, Bar = bar, Pct = pct, Name = nameLbl, Limit = limit, Card = card, IsVirtual = isVirtual };
        axis.Curve = axis.Cal.BuildCurve();

        // Target-type tabs: Output | Apps | Categories (index = (int)TargetKind).
        // Explicit compact sizes — AutoSize in a percent-width cell blows them up.
        var tabRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 4) };
        var tabs = new RoundedButton[3];
        string[] tabNames = { "Output", "Apps", "Categories" };
        Action<Graphics, Rectangle, Color>[] tabIcons = { GlyphSpeaker, GlyphApps, GlyphTag };
        var tabFont = new Font("Segoe UI", 8.25f);
        const int tabIconSz = 11, tabH = 27;
        for (int k = 0; k < 3; k++)
        {
            int kind = k;
            int tw = TextRenderer.MeasureText(tabNames[k], tabFont).Width;
            var b = new RoundedButton { Text = tabNames[k], AutoSize = false, Font = tabFont, Margin = new Padding(0, 0, 5, 0), Radius = 7, DrawIcon = tabIcons[k], IconSize = tabIconSz };
            b.Size = new Size(tabIconSz + 6 + tw + 16, tabH);   // icon + gap + text + side padding
            b.Click += (_, _) => SetTab(axis, (TargetKind)kind);
            tabs[k] = b;
            tabRow.Controls.Add(b);
        }
        axis.Tabs = tabs;
        t.Controls.Add(tabRow, 0, 2); t.SetColumnSpan(tabRow, 2);

        t.Controls.Add(combo, 0, 3);
        // Physical faders show a Max % cap; virtual faders drag straight to the
        // final volume (no cap needed) and show a Remove button in its place.
        if (isVirtual) { axis.Remove = RemoveButton(axis); t.Controls.Add(axis.Remove, 1, 3); }
        else t.Controls.Add(MaxCap(limit), 1, 3);
        card.Controls.Add(t);

        combo.SelectedIndexChanged += (_, _) => OnDevicePicked(axis);
        combo.DrawItem += OnComboDrawItem;
        combo.DrawLeadingIcon = DrawComboIcon;

        // Double-click the heading to rename the fader.
        nameLbl.DoubleClick += (_, _) => BeginRename(axis);
        nameLbl.Cursor = Cursors.Hand;
        _tip.SetToolTip(nameLbl, "Double-click to rename");
        _tip.SetToolTip(combo, "Pick what this fader controls");
        _tip.SetToolTip(tabs[0], "Control an output device's volume");
        _tip.SetToolTip(tabs[1], "Control one app's volume");
        _tip.SetToolTip(tabs[2], "Control a group of apps together");
        if (isVirtual)
        {
            bar.UserSet += (val, committed) => OnVirtualSet(axis, val, committed);
            _tip.SetToolTip(bar, "Drag to set the volume");
        }
        else
        {
            _tip.SetToolTip(limit, "Maximum volume this fader can reach");
            limit.ValueChanged += (_, _) => OnLimitChanged(axis);
        }
        return axis;
    }

    // Small "Remove" button shown on a virtual fader's card (where a physical
    // fader shows its Max cap). Deletes just that virtual slider.
    RoundedButton RemoveButton(Axis axis)
    {
        var b = new RoundedButton
        {
            Text = "Remove", AutoSize = true, Font = new Font("Segoe UI", 8.25f),
            Padding = new Padding(10, 4, 10, 4), Margin = new Padding(10, 2, 0, 0),
            Anchor = AnchorStyles.Right, Radius = 7,
        };
        b.Click += (_, _) => RemoveSlider(axis);
        _tip.SetToolTip(b, "Delete this virtual fader");
        return b;
    }

    // Inline rename: overlay a themed text box on the fader heading. Commit on
    // Enter or focus-loss, cancel on Esc.
    void BeginRename(Axis a)
    {
        var scr = a.Name.PointToScreen(Point.Empty);
        var pt = a.Card.PointToClient(scr);
        var box = new TextBox
        {
            Text = a.Name.Text,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = _theme.CtlBg,
            ForeColor = _theme.Text,
            Font = a.Name.Font,
            Bounds = new Rectangle(pt.X, pt.Y - 1, 190, a.Name.Height + 4),
        };
        bool done = false;
        void Commit(bool save)
        {
            if (done) return;
            done = true;
            if (save)
            {
                var nm = box.Text.Trim();
                if (nm.Length > 0 && nm != a.Name.Text) { a.Name.Text = nm; SaveSettings(); }
            }
            a.Card.Controls.Remove(box);
            box.Dispose();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(true); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Commit(false); }
        };
        box.Leave += (_, _) => Commit(true);
        a.Card.Controls.Add(box);
        box.BringToFront();
        box.Focus();
        box.SelectAll();
    }

    // Switch a slider's target type (which tab is active) and repopulate its combo.
    void SetTab(Axis a, TargetKind kind)
    {
        if (a.Target == kind) return;
        a.Target = kind;
        StyleTabs(a);
        _applyingActive = true;
        PopulateCombo(a);
        _applyingActive = false;
        a.LastApplied = -1;
        ApplyActive(a);
        if (!_loadingSettings) SaveSettings();
    }

    void StyleTabs(Axis a)
    {
        for (int k = 0; k < a.Tabs.Length; k++)
        {
            var b = a.Tabs[k];
            bool on = k == (int)a.Target;
            b.Surround = _theme.Card;
            b.BackColor = on ? _theme.Accent : _theme.CtlBg;
            b.ForeColor = on ? AccentText() : _theme.Text;
            b.FlatAppearance.BorderColor = on ? _theme.Accent : _theme.CtlBorder;
            b.Invalidate();
        }
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

        foreach (var s in _sliders)
        {
            s.Card.BackColor = t.Card;

            var b = s.Bar;
            b.Track = t.Inset; b.Fill = t.Accent; b.BackColor = t.Card;
            b.Knob = t.Dark ? Hex(0xE6, 0xE8, 0xEB) : Hex(0xFF, 0xFF, 0xFF);
            b.KnobEdge = t.Dark ? Hex(0x0E, 0x10, 0x14) : Hex(0xC2, 0xC6, 0xCC);
            b.Tick = t.Subtle;
            b.Invalidate();

            var c = s.Combo;
            c.BackColor = t.CtlBg; c.ForeColor = t.Text;            // popup list colours
            c.Surround = t.Card; c.BoxColor = t.CtlBg; c.BorderColor = t.CtlBorder; c.ChevronColor = t.Subtle;
            c.PlaceholderColor = t.Subtle;
            // Win10+ dark combo: themes the (still-native) dropdown list.
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, t.Dark ? "DarkMode_CFD" : null, null);
            c.Invalidate();

            var u = s.Limit;
            u.BackColor = t.CtlBg; u.ForeColor = t.Text; u.BorderColor = t.CtlBorder; u.ChevronColor = t.Subtle; u.Surround = t.Card; u.Invalidate();

            if (s.Remove is { } rb)
            {
                rb.BackColor = t.CtlBg; rb.ForeColor = t.Text;
                rb.FlatAppearance.BorderColor = t.CtlBorder; rb.Surround = t.Card; rb.Invalidate();
            }

            StyleTabs(s);
        }
        foreach (var btn in new[] { _btnRefresh, _btnOptions })
        {
            btn.BackColor = t.CtlBg;
            btn.ForeColor = t.Text;
            btn.FlatAppearance.BorderColor = t.CtlBorder;
            btn.Surround = t.Window;
            btn.Invalidate();
        }

        // Most labels are muted; the big % is primary, the status dot is accent.
        WalkLabels(this, l => { l.ForeColor = t.Subtle; l.BackColor = Color.Transparent; });
        foreach (var s in _sliders) s.Pct.ForeColor = t.Text;
        _statusDot.ForeColor = t.Accent;

        // Theme the scrollbar (shown when many sliders overflow the window).
        if (_sliderHost.IsHandleCreated)
            SetWindowTheme(_sliderHost.Handle, t.Dark ? "DarkMode_Explorer" : "Explorer", null);

        SetTitleBarDark(t.Dark);
        RefreshStatus();   // recompute the dot/text colour for the current state
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
            var item = cb.Items[e.Index];
            int isz = LogicalToDeviceUnits(18);
            var ir = new Rectangle(e.Bounds.X + LogicalToDeviceUnits(5), e.Bounds.Y + (e.Bounds.Height - isz) / 2, isz, isz);
            DrawComboIcon(e.Graphics, ir, item, fg);
            int left = ir.Right + LogicalToDeviceUnits(5);
            var r = new Rectangle(left, e.Bounds.Y, e.Bounds.Right - left - 2, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, cb.GetItemText(item), cb.Font, r, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // Leading icon for a combo row / the closed box: real exe icon for apps,
    // drawn glyphs for outputs / system sounds / categories.
    void DrawComboIcon(Graphics g, Rectangle r, object? item, Color fg)
    {
        switch (item)
        {
            case DeviceItem:
                GlyphSpeaker(g, r, fg);
                break;
            case CategoryItem:
                GlyphTag(g, r, fg);
                break;
            case AppItem ai:
                var img = AppIcon(ai.Key);
                if (img != null)
                {
                    var save = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, r);
                    g.InterpolationMode = save;
                }
                else if (ai.Key == SystemAppKey) GlyphSpeaker(g, r, fg);
                else GlyphApps(g, r, fg);
                break;
        }
    }

    // --- monochrome vector glyphs (tinted to the caller's colour) --------------
    // Each fills a ~16px square; used for tab icons and non-app combo rows.

    static void GlyphSpeaker(Graphics g, Rectangle r, Color c)
    {
        var s = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        // Speaker body: a small square + a triangular cone.
        using var body = new GraphicsPath();
        float bx = x + w * 0.14f, by = y + h * 0.36f, bw = w * 0.18f, bh = h * 0.28f;
        body.AddRectangle(new RectangleF(bx, by, bw, bh));
        body.AddPolygon(new[]
        {
            new PointF(bx + bw, by - h * 0.06f),
            new PointF(x + w * 0.50f, y + h * 0.18f),
            new PointF(x + w * 0.50f, y + h * 0.82f),
            new PointF(bx + bw, by + bh + h * 0.06f),
        });
        using (var b = new SolidBrush(c)) g.FillPath(b, body);
        // Two sound arcs.
        using var pen = new Pen(c, Math.Max(1.2f, w * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, x + w * 0.52f, y + h * 0.30f, w * 0.24f, h * 0.40f, -55, 110);
        g.DrawArc(pen, x + w * 0.52f, y + h * 0.18f, w * 0.44f, h * 0.64f, -50, 100);
        g.SmoothingMode = s;
    }

    static void GlyphApps(Graphics g, Rectangle r, Color c)
    {
        var s = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = r.Width, h = r.Height;
        float cell = w * 0.34f, gap = w * 0.12f;
        float x0 = r.X + w * 0.14f, y0 = r.Y + h * 0.14f;
        using var b = new SolidBrush(c);
        for (int iy = 0; iy < 2; iy++)
            for (int ix = 0; ix < 2; ix++)
            {
                var cell2 = new RectangleF(x0 + ix * (cell + gap), y0 + iy * (cell + gap), cell, cell);
                using var p = RoundGfx.Round(cell2, cell * 0.28f);
                g.FillPath(b, p);
            }
        g.SmoothingMode = s;
    }

    static void GlyphTag(Graphics g, Rectangle r, Color c)
    {
        var s = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        using var b = new SolidBrush(c);
        // Folder: a small tab, then the body.
        using (var tab = RoundGfx.Round(new RectangleF(x + w * 0.12f, y + h * 0.24f, w * 0.42f, h * 0.20f), h * 0.06f))
            g.FillPath(b, tab);
        using (var body = RoundGfx.Round(new RectangleF(x + w * 0.12f, y + h * 0.34f, w * 0.76f, h * 0.42f), h * 0.08f))
            g.FillPath(b, body);
        g.SmoothingMode = s;
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

    void MinimizeToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            try { _tray.ShowBalloonTip(2500, "ZMK Volume Fader", "Still running in the tray — double-click to reopen.", ToolTipIcon.Info); }
            catch { }
        }
    }

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
        // Stop new device callbacks first, then the debounce they feed.
        if (_notify != null)
        {
            try { _enum.UnregisterEndpointNotificationCallback(_notify); } catch { }
            _notify = null;
        }
        _deviceDebounce.Stop();
        _deviceDebounce.Dispose();
        _sessionPoll.Stop();
        _sessionPoll.Dispose();
        // Release the audio COM wrappers we're holding.
        foreach (var it in _present.Values) { try { it.Device.Dispose(); } catch { } }
        _present.Clear();
        try { _enum.Dispose(); } catch { }
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
        var items = _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new DeviceItem { Id = d.ID, Name = d.FriendlyName, Device = d })
            .OrderBy(d => d.Name)
            .ToArray();

        // Old MMDevice COM wrappers are about to be replaced; release them after
        // the rebuild (the new active device gets a fresh instance below).
        var stale = _present.Values.ToArray();
        _present.Clear();
        foreach (var it in items) _present[it.Id] = it;

        PopulateCombos();

        foreach (var it in stale) { try { it.Device.Dispose(); } catch { } }
    }

    // Fill each slider's target combo with present output devices, then every
    // known app, and re-select each slider's active target. Rebuilt on device or
    // app changes.
    void PopulateCombos()
    {
        // Clearing items resets the combo selection and would otherwise fire
        // OnDevicePicked with no selection; suppress that (ApplyActive re-selects).
        _applyingActive = true;
        foreach (var s in _sliders) PopulateCombo(s);
        _applyingActive = false;
        foreach (var s in _sliders) ApplyActive(s);
    }

    // Fill one slider's combo with the items for its active tab.
    void PopulateCombo(Axis a)
    {
        var cb = a.Combo;
        cb.BeginUpdate();
        cb.Items.Clear();
        switch (a.Target)
        {
            case TargetKind.Output:
                cb.Items.AddRange(_present.Values.OrderBy(d => d.Name).Cast<object>().ToArray());
                break;
            case TargetKind.App:
                // Live mixer apps, plus this slider's assigned app if it's closed.
                var keys = _liveApps.ToHashSet();
                if (a.AppKey != null) keys.Add(a.AppKey);
                cb.Items.AddRange(keys
                    .Select(k => new AppItem { Key = k, Name = _knownApps.TryGetValue(k, out var nm) ? nm : k })
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Cast<object>().ToArray());
                break;
            case TargetKind.Category:
                cb.Items.AddRange(_categories
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new CategoryItem { Name = c.Name })
                    .Cast<object>().ToArray());
                // Always last: catch-all for apps in no category.
                cb.Items.Add(new CategoryItem { Name = UnassignedCategory });
                break;
        }
        cb.EndUpdate();
    }

    void RegisterDeviceNotifications()
    {
        try
        {
            _deviceDebounce.Tick += (_, _) => { _deviceDebounce.Stop(); LoadDevices(); };
            _notify = new DeviceNotify(OnAudioDevicesChanged);
            _enum.RegisterEndpointNotificationCallback(_notify);
        }
        catch { }
    }

    // An endpoint appeared/vanished/changed state. Callbacks arrive on a COM
    // thread and often in bursts (e.g. one unplug raises several), so kick a
    // short debounce on the UI thread and do a single LoadDevices when it settles.
    void OnAudioDevicesChanged()
    {
        try
        {
            if (IsHandleCreated)
                BeginInvoke((Action)(() => { _deviceDebounce.Stop(); _deviceDebounce.Start(); }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // All outputs we could rank, including unplugged/disabled ones, for the editor.
    IReadOnlyList<OutputPref> AllKnownOutputs() =>
        _enum.EnumerateAudioEndPoints(DataFlow.Render,
                DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled)
            .Select(d => new OutputPref { Id = d.ID, Name = d.FriendlyName })
            .OrderBy(o => o.Name)
            .ToList();

    // ---- app volume (audio sessions) --------------------------------------

    void StartSessionPoll()
    {
        _sessionPoll.Tick += (_, _) => PollSessions();
        _sessionPoll.Start();
        PollSessions();
    }

    // Enumerate every render endpoint's audio sessions, group live sessions by
    // app, and learn any app we haven't seen before (persisted). Refreshes the
    // target combos and re-drives app sliders when the app set changes.
    void PollSessions()
    {
        var byApp = new Dictionary<string, List<AudioSessionControl>>();
        bool namesChanged = false;
        bool iconsChanged = false;
        foreach (var di in _present.Values)
        {
            SessionCollection sessions;
            try
            {
                var mgr = di.Device.AudioSessionManager;
                mgr.RefreshSessions();
                sessions = mgr.Sessions;
            }
            catch { continue; }

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var sc = sessions[i];
                    // Any non-expired session = an entry in the Windows mixer.
                    if (sc.State == AudioSessionState.AudioSessionStateExpired) continue;

                    string key, name;
                    if (sc.IsSystemSoundsSession) { key = SystemAppKey; name = "System sounds"; }
                    else
                    {
                        var k = AppKeyForPid((int)sc.GetProcessID, out name, out var exePath);
                        if (k == null) continue;
                        key = k;
                        // Grab the exe icon the first time (retry cheaply while null,
                        // and upgrade any low-res icon left by an older cache).
                        if (!_appIcons.TryGetValue(key, out var have) || have == null || have.Width < IconStore)
                        {
                            var loaded = LoadExeIcon(exePath);
                            if (loaded != null) { _appIcons[key] = loaded; iconsChanged = true; SaveIconFile(key, loaded); }
                            else _appIcons.TryAdd(key, null);
                        }
                    }

                    if (!byApp.TryGetValue(key, out var list)) byApp[key] = list = new();
                    list.Add(sc);
                    if (!_knownApps.TryGetValue(key, out var prev) || prev != name) { _knownApps[key] = name; namesChanged = true; }
                }
                catch { continue; }
            }
        }
        _appSessions = byApp;

        // Repopulate the target lists when the set of mixer apps changes.
        var live = new HashSet<string>(byApp.Keys);
        if (namesChanged || !live.SetEquals(_liveApps))
        {
            _liveApps = live;
            if (namesChanged) SaveSettings();
            PopulateCombos();
        }
        else if (iconsChanged)
            foreach (var s in _sliders) s.Combo.Invalidate();   // late-loaded icon
        // Push app/category sliders in case their sessions just (re)appeared.
        foreach (var s in _sliders)
            if (s.Target != TargetKind.Output) { s.LastApplied = -1; Render(s); }
    }

    // App identity key (lowercased process name) + a friendly display name, and
    // the exe path (for icon extraction) when the main module is readable.
    static string? AppKeyForPid(int pid, out string name, out string? exePath)
    {
        name = "";
        exePath = null;
        if (pid <= 0) return null;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            try
            {
                var mod = p.MainModule;
                exePath = mod?.FileName;
                var fd = mod?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(fd)) name = fd!;
            }
            catch { }
            return p.ProcessName.ToLowerInvariant();
        }
        catch { return null; }
    }

    // Icons are stored at this size and downscaled when drawn, so they stay crisp
    // at high Windows display scaling (a fader row is ~16px at 100%, ~24px at 150%).
    const int IconStore = 32;

    // Extract an icon from an exe, normalized to IconStore px. Returns null on failure.
    static Image? LoadExeIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var ic = Icon.ExtractAssociatedIcon(path!);
            if (ic == null) return null;
            using var raw = ic.ToBitmap();
            var bmp = new Bitmap(IconStore, IconStore);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(raw, new Rectangle(0, 0, IconStore, IconStore));
            }
            return bmp;
        }
        catch { return null; }
    }

    // The cached icon for an app (may be null if none could be extracted).
    Image? AppIcon(string key) => _appIcons.TryGetValue(key, out var img) ? img : null;

    // Icons are cached to disk so a known app shows its real icon even when it
    // isn't currently running (extracted while it was open, kept across restarts).
    static string IconDir => Path.Combine(SettingsDir, "icons");
    static string IconFile(string key) =>
        Path.Combine(IconDir, string.Concat(key.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 ? c : '_')) + ".png");

    static void SaveIconFile(string key, Image img)
    {
        try { Directory.CreateDirectory(IconDir); img.Save(IconFile(key), System.Drawing.Imaging.ImageFormat.Png); }
        catch { }
    }

    static Image? LoadIconFile(string path)
    {
        try
        {
            // Copy into a detached bitmap so the file isn't locked for the session.
            using var fs = File.OpenRead(path);
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    // Load any previously-cached app icons (for apps that may be closed now).
    void LoadCachedIcons()
    {
        foreach (var key in _knownApps.Keys.ToArray())
        {
            if (_appIcons.ContainsKey(key)) continue;
            var f = IconFile(key);
            if (File.Exists(f) && LoadIconFile(f) is { } img) _appIcons[key] = img;
        }
    }

    // The output the ranking alone would choose: highest-ranked present device.
    string? AutoTarget(Axis a)
    {
        foreach (var p in a.Prefs)
            if (_present.ContainsKey(p.Id)) return p.Id;
        return null;
    }

    // The output to actually drive: a present manual override wins, else the
    // ranking's auto target.
    string? Resolve(Axis a) =>
        a.OverrideId != null && _present.ContainsKey(a.OverrideId) ? a.OverrideId : AutoTarget(a);

    // Reflect a slider's active target in its combo and re-push. App targets just
    // select the app; output targets resolve the ranked list and load the cap.
    void ApplyActive(Axis a)
    {
        if (a.Target != TargetKind.Output)
        {
            _applyingActive = true;
            if (a.Target == TargetKind.App) SelectAppInCombo(a.Combo, a.AppKey);
            else SelectCategoryInCombo(a.Combo, a.CategoryName);
            _applyingActive = false;
            a.ActiveId = null;
            Render(a);
            return;
        }

        string? id = Resolve(a);

        _applyingActive = true;
        SelectActiveInCombo(a.Combo, id);
        _applyingActive = false;

        if (id != a.ActiveId)
        {
            a.ActiveId = id;
            bool prevLoad = _loadingSettings;
            _loadingSettings = true;                 // setting the stepper isn't a manual cap edit
            a.Limit.Value = MaxForDevice(id);
            _loadingSettings = prevLoad;
            a.LastApplied = -1;                      // force a re-push to the (possibly new) device
        }
        Render(a);
    }

    static void SelectActiveInCombo(ComboBox cb, string? id)
    {
        if (id != null)
            for (int i = 0; i < cb.Items.Count; i++)
                if (cb.Items[i] is DeviceItem di && di.Id == id)
                { if (cb.SelectedIndex != i) cb.SelectedIndex = i; return; }
        if (cb.SelectedIndex != -1) cb.SelectedIndex = -1;
    }

    static void SelectAppInCombo(ComboBox cb, string? key)
    {
        if (key != null)
            for (int i = 0; i < cb.Items.Count; i++)
                if (cb.Items[i] is AppItem ai && ai.Key == key)
                { if (cb.SelectedIndex != i) cb.SelectedIndex = i; return; }
        if (cb.SelectedIndex != -1) cb.SelectedIndex = -1;
    }

    static void SelectCategoryInCombo(ComboBox cb, string? name)
    {
        if (name != null)
            for (int i = 0; i < cb.Items.Count; i++)
                if (cb.Items[i] is CategoryItem ci && ci.Name == name)
                { if (cb.SelectedIndex != i) cb.SelectedIndex = i; return; }
        if (cb.SelectedIndex != -1) cb.SelectedIndex = -1;
    }

    void OnDevicePicked(Axis a)
    {
        if (_loadingSettings || _applyingActive) return;
        var item = a.Combo.SelectedItem;
        if (item is AppItem app)
        {
            // Switch this slider to controlling an app's volume (everywhere it plays).
            a.Target = TargetKind.App;
            a.AppKey = app.Key;
            ApplyActive(a);
            SaveSettings();
            return;
        }
        if (item is CategoryItem cat)
        {
            a.Target = TargetKind.Category;
            a.CategoryName = cat.Name;
            ApplyActive(a);
            SaveSettings();
            return;
        }
        // Output pick. Picking the auto target (the device the ranking would
        // choose) clears the override; any other pick becomes an override.
        a.Target = TargetKind.Output;
        string? picked = (item as DeviceItem)?.Id;
        a.OverrideId = (picked != null && picked == AutoTarget(a)) ? null : picked;
        ApplyActive(a);
        SaveSettings();
    }

    void OnLimitChanged(Axis a)
    {
        if (!_loadingSettings)
        {
            // Output sliders remember the cap per output; app sliders keep it in
            // the slider's own config (persisted via SaveSettings).
            if (a.Target == TargetKind.Output && (a.Combo.SelectedItem as DeviceItem)?.Id is string id)
                _deviceMax[id] = (int)a.Limit.Value;
            SaveSettings();
        }
        Render(a);
    }

    // The user dragged a virtual fader. Push the new level to its target live;
    // persist only when the drag ends (committed) to avoid a write per pixel.
    void OnVirtualSet(Axis a, int value, bool committed)
    {
        a.VValue = value;
        Render(a);
        if (committed && !_loadingSettings) SaveSettings();
    }

    // Delete one virtual fader: rebuild the layout from the remaining sliders.
    void RemoveSlider(Axis a)
    {
        var configs = _sliders.Where(s => s != a).Select(ToConfig).ToList();
        if (_activeKey != null && _devices.TryGetValue(_activeKey, out var p)) p.Sliders = configs;
        RebuildSliders(configs);
        SaveSettings();
    }

    // Snapshot one live slider into its persistable config.
    static SliderConfig ToConfig(Axis s) => new()
    {
        AxisIndex = s.AxisIndex,
        Label = s.Name.Text,
        Cal = s.Cal,
        Outputs = s.Prefs,
        OverrideId = s.OverrideId,
        Target = s.Target,
        AppKey = s.AppKey,
        CategoryName = s.CategoryName,
        Max = (int)s.Limit.Value,
        IsVirtual = s.IsVirtual,
        Value = s.VValue,
    };

    // Remembered cap for an output (default 100% for outputs we haven't capped).
    int MaxForDevice(string? id) =>
        id != null && _deviceMax.TryGetValue(id, out int m) ? ClampLimit(m) : 100;

    // Friendly name for an endpoint id if it's currently present, else the id.
    string NameFor(string id) => _present.TryGetValue(id, out var di) ? di.Name : id;

    static List<OutputPref> ClonePrefs(IEnumerable<OutputPref> src) =>
        src.Select(p => new OutputPref { Id = p.Id, Name = p.Name }).ToList();

    // ---- settings ---------------------------------------------------------

    void LoadSettings()
    {
        try
        {
            _loadingSettings = true;
            // Prefer the multi-slider file; fall back to the release build's
            // settings.json to migrate an existing setup on first run.
            Settings? s = null;
            if (File.Exists(SettingsPath)) s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            else if (File.Exists(LegacySettingsPath)) s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(LegacySettingsPath));
            if (s == null) return;

            _deviceMax = s.DeviceMax != null ? new(s.DeviceMax) : new();
            _devices = s.Devices != null ? new(s.Devices) : new();
            _knownApps.Clear();
            if (s.KnownApps != null) foreach (var kv in s.KnownApps) _knownApps[kv.Key] = kv.Value;
            _categories = s.Categories ?? new();
            _themeMode = s.ThemeMode;

            // No per-device profiles yet but old flat settings present -> build a
            // seed profile to apply to the first device that connects.
            if (_devices.Count == 0 && (s.LeftOutputs != null || s.LeftDeviceId != null || s.LeftCal != null))
                _legacyProfile = LegacyToProfile(s);

            ApplyTheme(CurrentTheme());
        }
        catch { }
        finally { _loadingSettings = false; }
    }

    // Turn old flat Left/Right settings into a 2-slider profile (and carry the
    // old per-output caps into DeviceMax).
    DeviceProfile LegacyToProfile(Settings s)
    {
        SliderConfig Make(int axis, string label, string? devId, List<OutputPref>? outs, string? over, Calibration? cal, int max)
        {
            var cfg = new SliderConfig { AxisIndex = axis, Label = label, Cal = cal ?? new(), Outputs = outs != null ? new(outs) : new(), OverrideId = over };
            if (cfg.Outputs.Count == 0 && devId != null) cfg.Outputs.Add(new OutputPref { Id = devId, Name = NameFor(devId) });
            if (devId != null && !_deviceMax.ContainsKey(devId)) _deviceMax[devId] = ClampLimit(max);
            return cfg;
        }
        return new DeviceProfile
        {
            Sliders =
            {
                Make(0, "Left fader", s.LeftDeviceId, s.LeftOutputs, s.LeftOverrideId, s.LeftCal, s.LeftMax),
                Make(1, "Right fader", s.RightDeviceId, s.RightOutputs, s.RightOverrideId, s.RightCal, s.RightMax),
            }
        };
    }

    void SaveSettings()
    {
        try
        {
            // Snapshot the live sliders into the connected device's profile.
            if (_activeKey != null)
            {
                string name = _devices.TryGetValue(_activeKey, out var ex) ? ex.Name : "";
                _devices[_activeKey] = new DeviceProfile
                {
                    Name = name,
                    Sliders = _sliders.Select(ToConfig).ToList(),
                };
            }

            var s = new Settings { Devices = _devices, DeviceMax = _deviceMax, KnownApps = new(_knownApps), Categories = _categories, ThemeMode = _themeMode };
            Directory.CreateDirectory(SettingsDir);
            // Write to a temp file then swap it in, so a crash mid-write can't
            // leave a half-written (corrupt) file behind.
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch { }
    }

    // ---- per-device profiles ----------------------------------------------

    static string DeviceKey(HidDevice d)
    {
        string? serial = null;
        try { serial = d.GetSerialNumber(); } catch { }
        string vp = $"{d.VendorID:X4}:{d.ProductID:X4}";
        return string.IsNullOrWhiteSpace(serial) ? vp : $"{vp}:{serial}";
    }

    // From the HID thread: a device connected. Switch to its profile on the UI.
    void OnDeviceConnected(string key, string name)
    {
        if (!IsHandleCreated) return;
        try { BeginInvoke(() => ActivateDevice(key, name)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    void ActivateDevice(string key, string name)
    {
        if (key == _activeKey) return;   // same unit reconnecting — keep state
        _activeKey = key;
        bool freshDefault = false;
        if (!_devices.TryGetValue(key, out var profile))
        {
            // First time we've seen this unit: seed from migrated settings, else a
            // default two-slider layout that the setup wizard can refine.
            if (_legacyProfile != null) { profile = _legacyProfile; _legacyProfile = null; }
            else { profile = new DeviceProfile { Sliders = DefaultSliders() }; freshDefault = true; }
            profile.Name = name;
            _devices[key] = profile;
        }
        // Rebuild the cards if this unit's slider count — or its physical/virtual
        // makeup — differs from what's shown (virtual cards are wired differently,
        // so ApplyProfile alone can't convert one kind into the other).
        bool needRebuild = profile.Sliders.Count > 0 &&
            (profile.Sliders.Count != _sliders.Length ||
             profile.Sliders.Where((c, i) => c.IsVirtual != _sliders[i].IsVirtual).Any());
        if (needRebuild)
            RebuildSliders(profile.Sliders);
        else
            ApplyProfile(profile);
        SaveSettings();
        if (freshDefault) PromptSetup(name);
    }

    static List<SliderConfig> DefaultSliders() => new()
    {
        new SliderConfig { AxisIndex = 0, Label = "Left fader" },
        new SliderConfig { AxisIndex = 1, Label = "Right fader" },
    };

    // At startup with no physical unit connected, show the saved virtual-fader
    // home (if any) so people without hardware get their draggable faders back. A
    // real unit connecting later takes over via ActivateDevice.
    void MaybeActivateVirtualHome()
    {
        if (_activeKey != null) return;
        if (_devices.TryGetValue(VirtualKey, out var vp) && vp.Sliders.Count > 0)
        {
            _activeKey = VirtualKey;
            RebuildSliders(vp.Sliders);
        }
    }

    // Push a profile onto the live sliders. (Phase 2 keeps the slider count fixed
    // at two; the wizard will rebuild the host to the profile's count.)
    void ApplyProfile(DeviceProfile profile)
    {
        _loadingSettings = true;
        int n = Math.Min(_sliders.Length, profile.Sliders.Count);
        for (int i = 0; i < n; i++)
        {
            var cfg = profile.Sliders[i];
            var s = _sliders[i];
            s.AxisIndex = cfg.AxisIndex;
            if (!string.IsNullOrEmpty(cfg.Label)) s.Name.Text = cfg.Label;
            s.Prefs = ClonePrefs(cfg.Outputs);
            s.OverrideId = cfg.OverrideId;
            s.Target = cfg.Target;
            s.AppKey = cfg.AppKey;
            s.CategoryName = cfg.CategoryName;
            s.VValue = cfg.Value;
            if (s.IsVirtual) s.Bar.Value = cfg.Value;
            if (cfg.Target != TargetKind.Output) s.Limit.Value = ClampLimit(cfg.Max);
            ApplyCalibration(s, cfg.Cal);
        }
        _loadingSettings = false;
        // Refresh each card to its loaded target: restyle the tab row and refill
        // the combo for that tab (Output/App/Category) before selecting within it.
        // Without this the combo still holds the previous tab's items and an
        // App/Category target can't be re-selected (falls back to no selection).
        foreach (var s in _sliders)
        {
            StyleTabs(s);
            _applyingActive = true;
            PopulateCombo(s);
            _applyingActive = false;
            ApplyActive(s);   // output cap comes from DeviceMax
        }
    }

    // Rebuild the on-screen sliders from a profile's slider set (count, axes,
    // labels, calibration, outputs). Used by the setup wizard and when a connected
    // unit's layout differs from what's shown.
    void RebuildSliders(List<SliderConfig> configs)
    {
        var old = _sliders;
        _sliders = configs.Select((c, i) =>
        {
            var a = BuildSlider(c.AxisIndex, string.IsNullOrEmpty(c.Label) ? $"Fader {i + 1}" : c.Label, c.IsVirtual);
            a.Prefs = ClonePrefs(c.Outputs);
            a.OverrideId = c.OverrideId;
            a.Target = c.Target;
            a.AppKey = c.AppKey;
            a.CategoryName = c.CategoryName;
            a.VValue = c.Value;
            if (a.IsVirtual) a.Bar.Value = c.Value;
            if (c.Target != TargetKind.Output) { _loadingSettings = true; a.Limit.Value = ClampLimit(c.Max); _loadingSettings = false; }
            ApplyCalibration(a, c.Cal);
            return a;
        }).ToArray();
        if (_sliders.Length == 0) _sliders = new[] { BuildSlider(0, "Fader 1") };
        _left = _sliders[0];
        _right = _sliders.Length > 1 ? _sliders[1] : _sliders[0];

        PopulateSliderHost();
        ApplyTheme(CurrentTheme());
        FitWindowHeight();

        LoadDevices();   // repopulate combos + re-pick active outputs
        foreach (var s in old) s.Card.Dispose();
    }

    // Run the guided setup: move each fader to detect its axis and capture travel.
    void RunSetupWizard()
    {
        bool prev = _calibrating;
        _calibrating = true;   // don't drive outputs mid-sweep
        using (var dlg = new SetupDialog(_theme, () => (int[])_lastAxisRaw.Clone()))
        {
            var result = dlg.ShowDialog(this);
            _calibrating = prev;
            if (result == DialogResult.OK && dlg.Result.Count > 0)
            {
                var configs = dlg.Result.Select((r, i) => r.Axis < 0
                    ? new SliderConfig { IsVirtual = true, AxisIndex = -1, Label = $"Fader {i + 1}", Value = 50 }
                    : new SliderConfig
                    {
                        AxisIndex = r.Axis,
                        Label = $"Fader {i + 1}",
                        Cal = new Calibration { Min = r.Min, Max = r.Max, Taper = TaperKind.Linear },
                    }).ToList();
                // With no physical unit connected, virtual faders live in the
                // synthetic virtual-home profile so they persist and reappear.
                string key = _activeKey ?? VirtualKey;
                _activeKey = key;
                if (!_devices.TryGetValue(key, out var p))
                {
                    p = new DeviceProfile { Name = key == VirtualKey ? "Virtual faders" : "" };
                    _devices[key] = p;
                }
                p.Sliders = configs;
                RebuildSliders(configs);
                SaveSettings();
            }
        }
        foreach (var s in _sliders) { s.LastApplied = -1; ApplyActive(s); }
    }

    void PromptSetup(string name)
    {
        var r = MessageBox.Show(this,
            $"New fader device “{name}” detected.\n\nRun setup to map its faders? " +
            "You'll move each fader fully bottom-to-top so the app can detect it and capture its range.",
            "ZMK Volume Fader", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes) RunSetupWizard();
    }

    static int ClampLimit(int pct) => Math.Clamp(pct, 1, 100);

    // ---- options ----------------------------------------------------------

    void OpenOptions()
    {
        var cals = _sliders.Select(s => s.Cal.Clone()).ToArray();
        var raws = _sliders.Select(s => (Func<int>)(() => s.LastRaw)).ToArray();
        var outs = _sliders.Select(s => ClonePrefs(s.Prefs)).ToArray();
        var labels = _sliders.Select(s => s.Name.Text).ToArray();
        var cats = _categories.Select(c => new Category { Name = c.Name, AppKeys = new(c.AppKeys) }).ToList();
        using var dlg = new OptionsDialog(_theme, _themeMode, GetStartWithWindows(),
            cals, raws, outs, labels, AllKnownOutputs(), _present.Keys.ToArray(), cats, _knownApps, _appIcons);
        _calibrating = true;                 // stop driving devices while sweeping
        var result = dlg.ShowDialog(this);
        _calibrating = false;
        if (result == DialogResult.OK)
        {
            for (int i = 0; i < _sliders.Length; i++)
            {
                ApplyCalibration(_sliders[i], dlg.Cals[i]);
                ApplyOutputs(_sliders[i], dlg.Outputs[i]);
            }
            _categories = dlg.Categories;
            _themeMode = dlg.SelectedTheme;
            ApplyTheme(CurrentTheme());
            SetStartWithWindows(dlg.StartWithWindows);
            SaveSettings();
            if (dlg.SetupRequested) { RunSetupWizard(); return; }
        }
        // Re-push (categories/outputs may have changed, so repopulate combos too).
        foreach (var s in _sliders) s.LastApplied = -1;
        PopulateCombos();
    }

    // Adopt an edited ranked list. Re-ranking returns the fader to automatic
    // (drops any manual override), per the agreed behaviour.
    void ApplyOutputs(Axis a, List<OutputPref> prefs)
    {
        bool changed = !a.Prefs.Select(p => p.Id).SequenceEqual(prefs.Select(p => p.Id));
        a.Prefs = prefs;
        if (changed) a.OverrideId = null;
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
        var all = DeviceList.Local.GetHidDevices().ToArray();

        // Match on our unique vendor fader usage (0xFF000001) rather than the
        // exact VID/PID: over Bluetooth (HID-over-GATT) Windows can assign a
        // different product id than the USB build, so keying off the usage lets
        // the same app find the device on either transport. Prefer our VID when
        // present, but don't require it.
        var byUsage = all.Where(d => Usages(d).Contains(FaderUsage)).ToArray();
        var hit = byUsage.FirstOrDefault(d => d.VendorID == VID) ?? byUsage.FirstOrDefault();
        if (hit != null) return hit;

        // Fallback: any device on our vendor usage page, preferring our VID.
        var byPage = all.Where(d => Usages(d).Any(u => (u >> 16) == 0xFF00)).ToArray();
        return byPage.FirstOrDefault(d => d.VendorID == VID)
               ?? byPage.OrderBy(d => d.GetMaxInputReportLength()).FirstOrDefault();
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
            OnDeviceConnected(DeviceKey(dev), devName);   // load this unit's profile
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
                    // Report id 2: up to eight 16-bit LE axes at bytes 1.. (two
                    // bytes each), then a button byte. Read whatever axes are
                    // present so any number of sliders (1..8) can be driven.
                    if (n >= 3 && buf[0] == 0x02)
                    {
                        int count = Math.Min(MaxAxes, (n - 1) / 2);
                        var axes = new int[count];
                        for (int i = 0; i < count; i++) axes[i] = ReadAxis(buf, 1 + 2 * i);
                        OnFaders(axes);
                    }
                }
            }
            SetStatus("Reconnecting…", false);
        }
    }

    static int ReadAxis(byte[] b, int i) => (short)(b[i] | (b[i + 1] << 8));

    void OnFaders(int[] axes)
    {
        if (!IsHandleCreated) return;
        // The handle can be destroyed between the check and the post during
        // shutdown; an unhandled throw here is on the HID thread and would crash.
        try
        {
            BeginInvoke(() =>
            {
                for (int i = 0; i < axes.Length && i < MaxAxes; i++) _lastAxisRaw[i] = axes[i];
                foreach (var s in _sliders)
                    if (s.AxisIndex >= 0 && s.AxisIndex < axes.Length) ApplyAxis(s, axes[s.AxisIndex]);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    void ApplyAxis(Axis a, int raw)
    {
        a.LastRaw = raw;

        // EMA smooth (16-bit value is finer but noisier, ~+/-15 counts).
        a.Sm = a.Sm < 0 ? raw : a.Sm * 0.85 + raw * 0.15;
        Render(a);
    }

    // Is this slider actually driving something right now? False if the unit is
    // unplugged, an output target isn't present, or an app/category target has no
    // live session. Drives the muted (greyed) look on the fader bar.
    bool Drivable(Axis a)
    {
        // Virtual faders don't depend on the dongle, so a disconnect doesn't grey
        // them; they still grey when their app/category target isn't playing.
        if (!a.IsVirtual && !_connected) return false;
        switch (a.Target)
        {
            case TargetKind.App:
                return a.AppKey != null && _appSessions.ContainsKey(a.AppKey);
            case TargetKind.Category:
                if (a.CategoryName == UnassignedCategory) return UnassignedKeys().Any();
                var c = _categories.FirstOrDefault(x => x.Name == a.CategoryName);
                return c != null && c.AppKeys.Any(_appSessions.ContainsKey);
            default:
                return Resolve(a) != null;
        }
    }

    // Refresh a slider's "active vs idle" visuals (muted bar + dimmed %).
    void UpdateSliderState(Axis a)
    {
        bool live = Drivable(a);
        a.Bar.Muted = !live;
        a.Pct.ForeColor = live ? _theme.Text : _theme.Subtle;
    }

    void UpdateAllSliderStates() { foreach (var s in _sliders) UpdateSliderState(s); }

    void Render(Axis a)
    {
        UpdateSliderState(a);
        if (!a.IsVirtual && a.Sm < 0) return;

        // Virtual faders drag straight to the final volume (no taper curve, no cap);
        // physical faders map their smoothed raw value through the calibration curve.
        double faderPct = a.IsVirtual ? a.VValue : Calibration.Eval(a.Curve, (int)Math.Round(a.Sm));
        int cap = a.IsVirtual ? 100 : (int)a.Limit.Value;
        double pf = Math.Clamp(faderPct * cap / 100.0, 0, 100);

        // Hysteresis: hold the current integer % until pf moves > Hyst off it.
        int applied = a.LastApplied < 0 || Math.Abs(pf - a.LastApplied) > Hyst
            ? (int)Math.Round(pf)
            : a.LastApplied;

        a.Bar.Value = applied;
        a.Pct.Text = $"{applied}%";

        if (applied == a.LastApplied) return;
        a.LastApplied = applied;
        UpdateTrayText();
        if (_calibrating) return;   // visualize, but don't drive anything while calibrating

        float scalar = applied / 100f;
        if (a.Target == TargetKind.App)
        {
            // Drive the app on every output it's currently playing to. Nothing is
            // driven when it has no live session; it takes effect once one appears.
            ApplyAppVolume(a.AppKey, scalar);
        }
        else if (a.Target == TargetKind.Category)
        {
            // Move every app in the category together.
            if (a.CategoryName == UnassignedCategory)
                foreach (var key in UnassignedKeys()) ApplyAppVolume(key, scalar);
            else
            {
                var cat = _categories.FirstOrDefault(c => c.Name == a.CategoryName);
                if (cat != null) foreach (var key in cat.AppKeys) ApplyAppVolume(key, scalar);
            }
        }
        // Each fader drives its own active output. If two sliders resolve to the
        // same endpoint they both write its volume (last move wins) — by design,
        // so point them at different outputs to use them independently.
        else if (a.Combo.SelectedItem is DeviceItem di)
        {
            try { di.Device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar; }
            catch { }
        }
    }

    void ApplyAppVolume(string? key, float scalar)
    {
        if (key != null && _appSessions.TryGetValue(key, out var list))
            foreach (var sc in list) { try { sc.SimpleAudioVolume.Volume = scalar; } catch { } }
    }

    void SetStatus(string text, bool connected)
    {
        if (!IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                _connText = text;
                _connected = connected;
                RefreshStatus();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // Footer status reflects the dongle connection. (A fader with no output to
    // drive is shown in-place via the dropdown's "No output selected" placeholder.)
    void RefreshStatus()
    {
        if (!IsHandleCreated) return;
        _status.Text = _connText;
        _statusDot.ForeColor = _connected ? _theme.Accent : DisconnectColor;
        UpdateAllSliderStates();
        UpdateTrayText();
    }

    static readonly Color DisconnectColor = Color.FromArgb(0xE0, 0x4F, 0x4F);

    // Tray tooltip: connection + each fader's current level (kept under the
    // NotifyIcon 63-char limit).
    void UpdateTrayText()
    {
        string body = _connected
            ? string.Join(" · ", _sliders.Select(s => $"{s.Bar.Value}%"))
            : "Disconnected";
        string t = $"ZMK Volume Fader\n{body}";
        _tray.Text = t.Length <= 63 ? t : t[..63];
    }
}
