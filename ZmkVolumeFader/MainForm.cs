using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HidSharp;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ZmkVolumeFader;

/// <summary>How the UI picks its palette: follow Windows, or force one.</summary>
internal enum ThemeMode { Auto, Light, Dark }

/// <summary>What closing the window does: ask each time, minimize to the tray,
/// or exit the app. Configurable in Options.</summary>
internal enum CloseBehavior { Ask, Tray, Exit }

/// <summary>What a slider controls: an output device, one app, or a category of apps.</summary>
internal enum TargetKind { Output, App, Category }

/// <summary>A named group of apps whose volume moves together.</summary>
internal sealed class Category
{
    public string Name { get; set; } = "";
    public List<string> AppKeys { get; set; } = new();   // process-name keys
}

/// <summary>A global hotkey binding: a virtual-key code plus modifier flags.
/// Vk == 0 means unbound. Observed via a low-level keyboard hook (pass-through,
/// so the key still reaches the focused app — Discord-style, not swallowed).</summary>
internal sealed class Hotkey
{
    public int Vk { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    public bool IsBound => Vk != 0;

    public bool Matches(int vk, bool ctrl, bool alt, bool shift, bool win)
        => IsBound && vk == Vk && ctrl == Ctrl && alt == Alt && shift == Shift && win == Win;

    /// <summary>Same chord as another binding (both bound, same key + modifiers).</summary>
    public bool SameAs(Hotkey o)
        => IsBound && o.IsBound && Vk == o.Vk && Ctrl == o.Ctrl && Alt == o.Alt && Shift == o.Shift && Win == o.Win;

    // Extended F-keys (F13..F24) and media keys are safe to bind bare — nothing
    // else uses them. Any other bare key fires during normal use (a "hint" case).
    public bool IsBareCommonKey =>
        IsBound && !Ctrl && !Alt && !Shift && !Win
        && !(Vk >= 0x7C && Vk <= 0x87)   // VK_F13..VK_F24
        && !(Vk >= 0xAD && Vk <= 0xB3);  // VK_VOLUME_* / VK_MEDIA_*

    public override string ToString()
    {
        if (!IsBound) return "Unbound";
        var s = "";
        if (Ctrl) s += "Ctrl+";
        if (Alt) s += "Alt+";
        if (Shift) s += "Shift+";
        if (Win) s += "Win+";
        return s + KeyName(Vk);
    }

    // Friendly key names: Keys.ToString() yields "D1", "Oemtilde", "OemMinus"…
    // Map digits and the common OEM keys to what's printed on the keycap.
    static string KeyName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),   // 0-9 (Keys.D0..D9)
        0x21 => "PageUp", 0x22 => "PageDown",           // aliased enum names
        0xC0 => "`", 0xBD => "-", 0xBB => "=",
        0xDB => "[", 0xDD => "]", 0xDC => "\\",
        0xBA => ";", 0xDE => "'", 0xBC => ",", 0xBE => ".", 0xBF => "/",
        0xAD => "Mute", 0xAE => "VolumeDown", 0xAF => "VolumeUp",
        0xB0 => "NextTrack", 0xB1 => "PrevTrack", 0xB2 => "MediaStop", 0xB3 => "PlayPause",
        _ => ((System.Windows.Forms.Keys)vk).ToString(),
    };
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
    // AssemblyInformationalVersion ("<version>+<hash>"), or "" when unavailable.
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
        string commit = CommitId();
        var ver = typeof(MainForm).Assembly.GetName().Version;
        string version = ver is null ? "" : $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        return string.IsNullOrEmpty(commit) ? version : $"{version} · {commit}";
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
    CloseBehavior _closeBehavior = CloseBehavior.Ask;   // what the X button does
    bool _softTakeover = true;               // require a physical fader to meet the current Windows volume first

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
        public Color Hot { get; set; } = Color.FromArgb(0xF0, 0x8A, 0x3C);   // warm orange (high)
        public Color Knob { get; set; } = Color.White;
        public Color KnobEdge { get; set; } = Color.Gray;
        public Color Tick { get; set; } = Color.Gray;
        public bool ShowTicks { get; set; } = true;
        int? _pickupPosition;
        // While soft takeover is armed, Value is the current Windows level and
        // this marker shows where the physical fader currently sits.
        public int? PickupPosition
        {
            get => _pickupPosition;
            set
            {
                int? v = value is null ? null : Math.Clamp(value.Value, 0, 100);
                if (v == _pickupPosition) return;
                _pickupPosition = v;
                Invalidate();
            }
        }
        // When true the fill/knob are drawn desaturated — the target isn't being
        // driven right now (unit unplugged, or an app target that isn't playing).
        bool _muted;
        public bool Muted { get => _muted; set { if (v_set(ref _muted, value)) Invalidate(); } }
        static bool v_set(ref bool f, bool v) { if (f == v) return false; f = v; return true; }

        // Interactive (virtual) faders let the user drag the knob to set the level,
        // or focus the bar (Tab / click) and nudge it with the keyboard.
        // Display-only (physical) faders leave this false and ignore input.
        bool _interactive;
        public bool Interactive
        {
            get => _interactive;
            set
            {
                _interactive = value;
                Cursor = value ? Cursors.Hand : Cursors.Default;
                SetStyle(ControlStyles.Selectable, value);
                TabStop = value;
            }
        }
        bool _dragging;
        bool _keyAdjusting;   // arrow-key nudge in progress; commit on key-up
        // Raised while the user drags (live volume) and once more on release. The
        // bool is true on the final (mouse-up / key-up) event so the owner can
        // persist then.
        public event Action<int, bool>? UserSet;

        public FaderBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);   // display-only until Interactive
            TabStop = false;
        }

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
            Focus();   // so arrow keys work right after a click
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

        // Keyboard nudging when focused: arrows ±1, PageUp/Down ±10, Home/End to
        // the ends. Arrows are dialog-navigation keys, so claim them via IsInputKey.
        protected override bool IsInputKey(Keys keyData) =>
            (_interactive && (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down
                or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End)
            || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!_interactive) return;
            int v = e.KeyCode switch
            {
                Keys.Left or Keys.Down => _value - 1,
                Keys.Right or Keys.Up => _value + 1,
                Keys.PageDown => _value - 10,
                Keys.PageUp => _value + 10,
                Keys.Home => 0,
                Keys.End => 100,
                _ => _value,
            };
            if (v == _value) return;
            e.Handled = true;
            Value = Math.Clamp(v, 0, 100);
            _keyAdjusting = true;
            UserSet?.Invoke(_value, false);   // live while held (auto-repeat)
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (!_keyAdjusting) return;
            _keyAdjusting = false;
            UserSet?.Invoke(_value, true);    // committed — persist once per nudge
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

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

            // Soft-takeover marker: a small outlined diamond at the physical
            // position, while the main knob remains at the Windows volume.
            if (_pickupPosition is int pickup)
            {
                float px = left + (right - left) * pickup / 100f;
                float r = Math.Max(3.5f, knobR * 0.48f);
                PointF[] diamond =
                {
                    new(px, cy - r), new(px + r, cy), new(px, cy + r), new(px - r, cy),
                };
                using var pp = new Pen(Knob, 1.8f) { LineJoin = LineJoin.Round };
                g.DrawPolygon(pp, diamond);
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

            // Keyboard-focus cue for interactive (virtual) faders.
            if (_interactive && Focused)
                ControlPaint.DrawFocusRectangle(g, new Rectangle(0, 0, w, h));
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
            var old = Region;
            Region = new Region(p);
            old?.Dispose();
        }
    }

    // Flat, themed numeric stepper (replaces NumericUpDown, whose spin buttons
    // can't be dark-themed). A borderless child text box handles typing/caret;
    // this control paints the frame + chevrons. Type then Enter, click a
    // chevron, use Up/Down, or scroll.
    internal sealed class Stepper : Control
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
        public override string ToString() => Name;
    }

    // A target that is an application's volume (a Windows mixer session), keyed by
    // executable/process name (or "#system" for System Sounds).
    sealed class AppItem
    {
        public required string Key;
        public required string Name;
        public bool Live;
        public override string ToString() => Live ? Name : $"{Name}  (not running)";
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
    // pseudo-category drives. Apps another fader targets *directly* are also
    // excluded, so "Everything Else" never fights a dedicated app fader.
    // (System Sounds counts as unassigned on purpose.)
    IEnumerable<string> UnassignedKeys() =>
        _liveApps.Where(k =>
            !_categories.Any(c => c.AppKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
            && !_sliders.Any(s => IsFaderConnected(s)
                                  && s.Target == TargetKind.App
                                  && string.Equals(s.AppKey, k, StringComparison.OrdinalIgnoreCase)));

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
        public RoundedButton? Mute;
        public RoundedButton? More;     // virtual faders only: reset/hotkeys/remove menu
        // Which fader group (device) this slider belongs to — a device key for
        // physical faders, or VirtualKey for the virtual home. Axis reports from a
        // device only drive sliders whose GroupKey matches, so two devices can be
        // live at once without their axes colliding.
        public string GroupKey = VirtualKey;
        public int AxisIndex;                                 // which HID report axis (0..7) drives this slider
        // Virtual faders have no HID axis; they stand in for the physical throw.
        // VTarget (0..100) is the goal set by mouse drag or a hotkey; VCur eases
        // toward it so hotkey steps glide instead of spiking. AxisIndex is -1.
        public bool IsVirtual;
        public int VTarget = 50;
        public double VCur = 50;
        public bool VMuted;
        public int VPreMute = 50;       // level to restore when unmuting
        // Physical soft takeover: Windows stays at PickupTarget until the real
        // fader reaches/crosses it. PickupPosition is the previous mapped sample.
        public bool PickupArmed;
        public bool PickupReady;
        public int PickupTarget;
        public int? PickupPosition;
        public int PickupGeneration;
        // Per-fader hotkey bindings + step (virtual faders only; persisted).
        public Hotkey HkUp = new(), HkDown = new(), HkMute = new();
        public int Step = 5;
        public Calibration Cal = new();                       // value->% mapping (persisted)
        public (int v, int pct)[] Curve = Array.Empty<(int, int)>();  // built from Cal
        public double Sm = -1;          // EMA state (smoothed raw value)
        public int LastRaw;             // last raw value (so a cap change can re-render)
        public bool InMuteZone;         // latched mute-dead-zone state (see Render)
        public int LastApplied = -1;    // last desired integer volume % (deadband)
        // Resolves overlapping output/app/category assignments. The fader with
        // the greatest sequence is the one the user acted on most recently.
        public long DriveSequence;
        // Logical changes are coalesced here before publishing a complete desired
        // target map. AudioController applies a global limit after category fan-out,
        // so this per-slider state can never multiply into an OS-call flood.
        public int VolPending = -1;     // latest % awaiting publication, -1 = none
        public long VolLastWrite;       // tick of the last desired-state publication

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
        // the on-screen bar with the mouse or by hotkeys. Value is its last
        // position (0..100). HkUp/HkDown/HkMute are its global hotkey bindings and
        // Step is the per-press percentage nudge.
        public bool IsVirtual { get; set; }
        public int Value { get; set; } = 50;
        public Hotkey HkUp { get; set; } = new();
        public Hotkey HkDown { get; set; } = new();
        public Hotkey HkMute { get; set; } = new();
        public int Step { get; set; } = 5;
        // Mute survives a restart: Value is 0 while muted, PreMute is the level
        // the mute hotkey restores.
        public bool Muted { get; set; }
        public int PreMute { get; set; } = 50;
    }

    // Everything remembered for one physical fader unit (its sliders, in order).
    sealed class DeviceProfile
    {
        public string Name { get; set; } = "";
        public List<SliderConfig> Sliders { get; set; } = new();
    }

    // A live on-screen section: one connected device (or the virtual home). Its
    // sliders are the same Axis objects that appear, concatenated, in _sliders.
    // Groups render top-to-bottom with a small header (device name + status).
    sealed class FaderGroup
    {
        public string Key = "";       // device key, or VirtualKey
        public string Name = "";      // header text
        public bool IsVirtual;
        public bool Connected = true; // physical: a reader is open; drives the header dot
        public List<Axis> Sliders = new();
        // Header parts, rebuilt each layout and re-themed in place on theme change.
        public Control? Header;
        public Label? HeaderDot;
        public Label? HeaderName;
        public RoundedButton? HeaderBtn;
        public void ClearHeaderRefs() { Header = null; HeaderDot = null; HeaderName = null; HeaderBtn = null; }
    }

    // One open HID device being read on its own background thread. Keyed by device
    // key in _readers; the thread self-removes on disconnect.
    sealed class Reader
    {
        public string Key = "";
        public string Name = "";
        public Thread Thread = null!;
        public HidStream? Stream;
        public volatile bool Stop;
        // Reused producer/consumer buffers. HID reports can arrive thousands of
        // times per second; keeping these per reader avoids one managed allocation
        // for every report while still letting the UI consume only the newest.
        public readonly object PendingLock = new();
        public readonly int[] PendingAxes = new int[MaxAxes];
        public readonly int[] SnapshotAxes = new int[MaxAxes];
        public int PendingCount;
        public volatile bool HasPending;
    }

    sealed class Settings
    {
        public int SchemaVersion { get; set; } = CurrentSettingsSchema;
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
        public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Ask;
        public bool SoftTakeover { get; set; } = true;
        // App-key -> unix seconds last seen with a live session (for pruning).
        public Dictionary<string, long> AppSeen { get; set; } = new();
        // Explicit per-device monitoring overrides. By default a unit is monitored
        // iff it exposes our exact fader report or matches our VID; a device that
        // merely shares the vendor HID page (a Steam Controller puck, say) is left
        // alone. IgnoredDevices force a matching unit OFF; MonitoredDevices opt a
        // page-only unit IN. Keyed by device key (VID:PID[:serial]).
        public List<string> IgnoredDevices { get; set; } = new();
        public List<string> MonitoredDevices { get; set; } = new();

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
    DeviceProfile? _legacyProfile;
    // On-screen groups (connected devices + the virtual home), top-to-bottom.
    readonly List<FaderGroup> _groups = new();
    // Latest raw axes per device key (independent of slider mapping) — the setup
    // wizard reads a device's array to detect which fader is being moved. UI thread
    // only.
    readonly Dictionary<string, int[]> _rawByDevice = new();

    // Per-device monitoring overrides (see Settings.IgnoredDevices /
    // MonitoredDevices). _ignored/_allowed are the UI-thread working sets edited by
    // the Devices dialog; the *Snap copies are immutable snapshots the HID
    // discovery + reader threads read. Publish fresh snapshots (never mutate in
    // place) so those threads always see a consistent pair.
    HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    volatile HashSet<string> _ignoredSnap = new(StringComparer.OrdinalIgnoreCase);
    volatile HashSet<string> _allowedSnap = new(StringComparer.OrdinalIgnoreCase);

    void PublishDeviceSets()
    {
        _ignoredSnap = new HashSet<string>(_ignored, StringComparer.OrdinalIgnoreCase);
        _allowedSnap = new HashSet<string>(_allowed, StringComparer.OrdinalIgnoreCase);
    }

    // Default monitoring decision for a candidate the user hasn't overridden: take
    // units that speak our exact fader report or carry our VID; leave page-only
    // strangers alone.
    static bool DefaultMonitor(bool byUsage, bool ourVid) => byUsage || ourVid;

    // Whether discovery should open a candidate, honouring explicit overrides.
    bool ShouldMonitor(string key, bool byUsage, bool ourVid)
        => _allowedSnap.Contains(key) || (!_ignoredSnap.Contains(key) && DefaultMonitor(byUsage, ourVid));

    // App-volume tracking. _knownApps is every app ever seen in the mixer
    // (persisted, keyed by executable identity with a process-name fallback);
    // _liveApps is supplied by the
    // event-driven AudioController; the target combos list
    // both outputs and known apps.
    readonly Dictionary<string, string> _knownApps = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _liveApps = new();   // apps with a session right now (i.e. in the mixer)
    // App-key -> extracted 16px exe icon (null once = couldn't extract, retried
    // up to IconTryMax times). Kept for the app lifetime.
    readonly Dictionary<string, Image?> _appIcons = new(StringComparer.OrdinalIgnoreCase);
    // App-key -> failed extraction attempts, so an inaccessible exe (elevated
    // process, missing file) isn't re-tried on every 1s poll forever.
    readonly Dictionary<string, int> _iconTries = new(StringComparer.OrdinalIgnoreCase);
    const int IconTryMax = 3;
    // App-key -> unix seconds the app last had a live session (persisted).
    // PruneKnownApps drops long-unseen, unreferenced apps at startup so the
    // known-apps list and icon cache don't grow forever.
    readonly Dictionary<string, long> _appSeen = new(StringComparer.OrdinalIgnoreCase);
    List<Category> _categories = new();
    AudioController? _audio;
    System.Windows.Forms.Timer? _audioStats;
    string? _audioStatsBaseTitle;
    readonly object _audioSnapshotLock = new();
    IReadOnlyList<AudioController.AppSnapshot>? _pendingAudioSnapshot;
    bool _audioSnapshotQueued;
    bool _audioFaultShown;
    long _nextDriveSequence;

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
    const int CurrentSettingsSchema = 3;
    bool _settingsFaultShown;

    // ---- controls ---------------------------------------------------------

    readonly RoundedButton _btnRefresh = new() { Text = "Refresh", AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0) };
    readonly RoundedButton _btnDevices = new() { Text = "Devices", AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(8, 0, 0, 0) };
    readonly RoundedButton _btnOptions = new() { Text = "Options", AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(8, 0, 0, 0) };
    readonly Label _status = new() { Text = "Starting…", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label _statusDot = new() { Text = "●", AutoSize = true, Font = UiFonts.Get(8f), Margin = new Padding(0, 3, 6, 0) };

    // Slider cards are built dynamically into _sliderHost (one row each); it lives
    // inside _scroll, a custom scroll viewport that sizes the cards around its own
    // thumb (the native AutoScroll bar overlaid the cards' right edge).
    TableLayoutPanel _sliderHost = null!, _footer = null!;
    RoundedScrollPanel _scroll = null!;
    // Empty-state card shown (in place of slider rows) when there are no faders.
    CardPanel? _emptyCard;
    Label? _emptyTitle, _emptySub;
    RoundedButton? _emptyAdd;

    readonly MMDeviceEnumerator _enum = new();
    DeviceNotify? _notify;
    // Coalesces bursts of device notifications into a single refresh.
    readonly System.Windows.Forms.Timer _deviceDebounce = new() { Interval = 250 };
    int _deviceRefreshQueued;
    // Applies the newest axes from each reader at a fixed ~60 Hz, so a device
    // that spams reports drives at most one update per tick instead of per report.
    readonly System.Windows.Forms.Timer _faderPump = new() { Interval = 16 };
    readonly System.Windows.Forms.Timer _trayUpdate = new() { Interval = 250 };
    volatile bool _pumpActive;
    int _pumpStartQueued;

    // Global keyboard hook (observes keys, never swallows) + the smoothing ramp
    // that eases virtual faders toward their hotkey-set target.
    readonly KeyboardHook _hook = new();
    readonly System.Windows.Forms.Timer _vramp = new() { Interval = 20 };
    // Every vk with any hotkey binding — a snapshot the hook thread reads so
    // unbound keys (i.e. all normal typing) bail without touching the UI thread.
    volatile int[] _boundVks = Array.Empty<int>();

    Axis _left = null!, _right = null!;
    // Every on-screen slider across all groups, in display order (the flat
    // concatenation of _groups). Rebuilt by RelayoutGroups; most per-slider
    // machinery (theme, hotkeys, session render) iterates this.
    Axis[] _sliders = Array.Empty<Axis>();

    // HID discovery + one reader thread per open device.
    readonly object _readersLock = new();
    readonly Dictionary<string, Reader> _readers = new(StringComparer.OrdinalIgnoreCase);
    volatile Reader[] _readerSnapshot = Array.Empty<Reader>();
    readonly AutoResetEvent _discoveryWake = new(false);
    EventHandler<DeviceListChangedEventArgs>? _hidChanged;
    Thread? _discoveryThread;
    long _hidReportCount;
    long _desiredSnapshotCount;
    volatile bool _run;
    bool _loadingSettings;
    bool _calibrating;   // true while the calibration dialog is open (don't drive devices)
    string _connText = "Starting…";   // last dongle-connection status text
    bool _connected;                  // dongle currently connected

    readonly NotifyIcon _tray = new() { Text = "ZMK Volume Fader", Icon = LoadAppIcon() };
    bool _exiting;
    bool _trayHintShown;   // one-time "still running in the tray" balloon
    string? _pendingTrayText, _lastTrayText;
    readonly object _statusPostLock = new();
    string? _lastStatusText;
    bool? _lastStatusConnected;
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
        // Leak-isolation runs show their mode so results can't be misattributed.
        var _diag = Program.DiagText();
        if (_diag.Length != 0) Text += $" {_diag}";
        Icon = LoadAppIcon();
        Font = UiFonts.Get(9.75f);
        ClientSize = new Size(460, 364);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // Start with no faders: the slider count is per-device (a connecting unit
        // seeds its defaults; the setup dialog builds an arbitrary set). Until then
        // the host shows the empty-state "Add fader" card. _left/_right are
        // vestigial (only ever assigned, never read).
        _sliders = Array.Empty<Axis>();
        _left = _right = null!;
        ClientSize = new Size(ClientSize.Width, WindowHeightFor(_sliders.Length));

        // Slider cards stack in _sliderHost, which scrolls inside _scroll; the
        // footer stays pinned below.
        _sliderHost = new TableLayoutPanel { ColumnCount = 1, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
        _sliderHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scroll = new RoundedScrollPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
        _scroll.SetContent(_sliderHost);
        PopulateSliderHost();

        _footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) };
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _btnRefresh.Click += (_, _) => LoadDevices();
        _btnDevices.Click += (_, _) => OpenDevices();
        _btnOptions.Click += (_, _) => OpenOptions();
        _tip.SetToolTip(_btnRefresh, "Re-scan audio devices and apps");
        _tip.SetToolTip(_btnDevices, "Choose which fader devices to monitor or ignore");
        _tip.SetToolTip(_btnOptions, "Calibration, sliders, categories, and preferences");
        var leftBtns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        leftBtns.Controls.Add(_btnRefresh);
        leftBtns.Controls.Add(_btnDevices);
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
        root.Controls.Add(_scroll, 0, 0);
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

        _vramp.Tick += (_, _) => VrampTick();
        _faderPump.Tick += (_, _) => FaderPumpTick();
        _trayUpdate.Tick += (_, _) => FlushTrayText();
        Load += (_, _) => { ApplyTheme(CurrentTheme()); LoadDevices(); LoadSettings(); PruneKnownApps(); EnsureVirtualGroup(); RelayoutGroups(); LoadCachedIcons(); PopulateCombos(); FitWindowHeight(); StartAudio(); StartHid(); RegisterDeviceNotifications(); StartHotkeys(); };
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
        // Group headers add height too (one per non-empty group).
        foreach (var g in _groups)
            if (g.Sliders.Count > 0 && g.Header is { } h)
                total += h.PreferredSize.Height + h.Margin.Vertical;
        // No faders: size to the empty-state card (treat it like one slider card).
        if (_sliders.Length == 0 && _emptyCard is { } ec)
        {
            if (ec.Controls.Count > 0 && ec.Controls[0] is TableLayoutPanel inner)
                ec.Height = inner.PreferredSize.Height + ec.Padding.Vertical + LogicalToDeviceUnits(2);
            total += ec.Height + ec.Margin.Vertical;
        }
        int chrome = _footer.PreferredSize.Height + LogicalToDeviceUnits(14) * 2 + LogicalToDeviceUnits(12);
        int want = total + chrome;
        int minH = LogicalToDeviceUnits(300);
        // Keep cap >= min: Math.Clamp throws when max < min (short screen + high DPI).
        int cap = Math.Max(minH, Math.Min(LogicalToDeviceUnits(760),
            Screen.FromControl(this).WorkingArea.Height - LogicalToDeviceUnits(80)));
        // Force width to the DPI-scaled design width too — belt-and-braces in case
        // the framework auto-scale didn't already widen it.
        ClientSize = new Size(LogicalToDeviceUnits(460), Math.Clamp(want, minH, cap));
        _scroll.Reflow();   // resize the card column around the (custom) scrollbar
    }

    // (Re)build the slider host with one row per slider. Called on construction
    // and whenever the slider set changes.
    void PopulateSliderHost()
    {
        _sliderHost.SuspendLayout();
        _sliderHost.Controls.Clear();
        _sliderHost.RowStyles.Clear();
        // The empty card and group headers are rebuilt each time; dispose the old
        // ones (Controls.Clear doesn't) so rebuilds don't leak them.
        if (_emptyCard is { IsDisposed: false } oldEmpty) { ClearTips(oldEmpty); oldEmpty.Dispose(); }
        _emptyCard = null; _emptyTitle = _emptySub = null; _emptyAdd = null;
        foreach (var g in _groups)
            if (g.Header is { IsDisposed: false } h) { ClearTips(h); h.Dispose(); g.ClearHeaderRefs(); }

        // Flatten to a single column of rows: a header then each card, per group.
        var rows = new List<Control>();
        if (_sliders.Length == 0)
        {
            rows.Add(BuildEmptyCard());
        }
        else
        {
            bool first = true;
            foreach (var g in _groups)
            {
                if (g.Sliders.Count == 0) continue;   // no header for an empty group
                rows.Add(BuildGroupHeader(g, first));
                first = false;
                foreach (var s in g.Sliders) rows.Add(s.Card);
            }
        }

        _sliderHost.RowCount = rows.Count;
        for (int i = 0; i < rows.Count; i++)
        {
            _sliderHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _sliderHost.Controls.Add(rows[i], 0, i);
        }
        _sliderHost.ResumeLayout();
        _scroll?.Reflow();
    }

    // Empty-state card (shown when there are no faders): a short message plus an
    // accent "Add fader" button that opens setup with the Add buttons pulsing.
    CardPanel BuildEmptyCard()
    {
        var card = new CardPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 168, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 14, 16, 16) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _emptyTitle = new Label { Text = "No faders yet", AutoSize = true, Anchor = AnchorStyles.None, Font = UiFonts.Get(13f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };
        _emptySub = new Label { Text = "Add a physical or virtual fader to get started.", AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 12) };
        _emptyAdd = new RoundedButton { Text = "Add fader", AutoSize = true, Anchor = AnchorStyles.None, Padding = new Padding(16, 7, 16, 7), Margin = new Padding(0, 0, 0, 4) };
        _emptyAdd.Click += (_, _) => RunSetupWizard(VirtualKey, pulse: true);
        _tip.SetToolTip(_emptyAdd, "Set up your faders");
        t.Controls.Add(_emptyTitle, 0, 0);
        t.Controls.Add(_emptySub, 0, 1);
        t.Controls.Add(_emptyAdd, 0, 2);
        card.Controls.Add(t);
        _emptyCard = card;
        return card;
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
        var pct = new Label { Text = "—", AutoSize = true, Anchor = AnchorStyles.Right, Font = UiFonts.Get(15f) };
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
        var tabFont = UiFonts.Get(8.25f);
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
        // Physical faders show a Max % cap. Virtual faders keep the frequent Mute
        // action visible and put reset/hotkeys/remove into a compact More menu.
        axis.Mute = MuteButton(axis);
        if (isVirtual)
        {
            axis.More = MoreButton(axis);
            var vbtns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Anchor = AnchorStyles.Right, Margin = new Padding(10, 0, 0, 0) };
            vbtns.Controls.Add(axis.Mute);
            vbtns.Controls.Add(axis.More);
            t.Controls.Add(vbtns, 1, 3);
        }
        else
        {
            var controls = MaxCap(limit);
            axis.Mute.Margin = new Padding(8, 0, 0, 0);
            controls.Controls.Add(axis.Mute);
            t.Controls.Add(controls, 1, 3);
        }
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
            bar.AccessibleName = $"{name} level";
            bar.AccessibleRole = AccessibleRole.Slider;
            _tip.SetToolTip(bar, "Drag to set the volume (or focus it and use the arrow keys)");
        }
        else
        {
            _tip.SetToolTip(limit, "Maximum volume this fader can reach");
            limit.ValueChanged += (_, _) => OnLimitChanged(axis);
        }
        return axis;
    }

    RoundedButton MuteButton(Axis axis)
    {
        var b = new RoundedButton
        {
            Text = "Mute", AutoSize = true, Font = UiFonts.Get(8.25f),
            Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 0, 4, 0),
            Anchor = AnchorStyles.None, Radius = 7, AccessibleName = "Mute fader",
        };
        b.Click += (_, _) => ToggleMute(axis);
        _tip.SetToolTip(b, "Mute this fader's target; click again to restore it");
        return b;
    }

    RoundedButton MoreButton(Axis axis)
    {
        var b = new RoundedButton
        {
            Text = "More ▾", AutoSize = true, Font = UiFonts.Get(8.25f),
            Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0),
            Anchor = AnchorStyles.None, Radius = 7, AccessibleName = "More fader actions",
        };
        b.Click += (_, _) => ShowVirtualActions(axis, b);
        _tip.SetToolTip(b, "Reset, configure hotkeys, or remove this virtual fader");
        return b;
    }

    void ShowVirtualActions(Axis axis, Control owner)
    {
        var menu = new ContextMenuStrip
        {
            Font = UiFonts.Get(9f), BackColor = _theme.CtlBg, ForeColor = _theme.Text,
            ShowImageMargin = false, ShowCheckMargin = false,
            Padding = new Padding(2),
        };
        var reset = menu.Items.Add("Reset to 100%");
        reset.Enabled = axis.VMuted || axis.VTarget < 100 || axis.VCur < 99.5;
        reset.Click += (_, _) => ResetVirtual(axis);
        menu.Items.Add("Configure hotkeys…", null, (_, _) => OpenHotkeys(axis));
        menu.Items.Add(new ToolStripSeparator());
        var remove = menu.Items.Add("Remove fader…");
        remove.ForeColor = _theme.Dark ? Color.FromArgb(0xFF, 0x9A, 0x91) : Color.FromArgb(0xA8, 0x2E, 0x2E);
        remove.Click += (_, _) => RemoveSlider(axis);
        foreach (ToolStripItem item in menu.Items)
        {
            item.BackColor = _theme.CtlBg;
            item.Padding = new Padding(8, 4, 12, 4);
        }
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(owner, new Point(owner.Width, owner.Height), ToolStripDropDownDirection.BelowLeft);
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
            Bounds = new Rectangle(pt.X, pt.Y - 1, LogicalToDeviceUnits(190), a.Name.Height + 4),
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
        TakeTargetOwnership(a);
        ApplyActive(a);
        ArmPickup(a);
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

            foreach (var vb in new[] { s.Mute, s.More })
                if (vb is { } btn)
                {
                    btn.BackColor = t.CtlBg; btn.ForeColor = t.Text;
                    btn.FlatAppearance.BorderColor = t.CtlBorder; btn.Surround = t.Card; btn.Invalidate();
                }

            StyleTabs(s);
        }
        foreach (var btn in new[] { _btnRefresh, _btnDevices, _btnOptions })
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
        // Group headers: recolour after WalkLabels, which greyed their labels.
        foreach (var g in _groups) RethemeHeader(g, t);

        // Empty-state card (present only when there are no faders). Set after
        // WalkLabels so the title keeps its primary colour.
        if (_emptyCard is { } ec)
        {
            ec.BackColor = t.Card;
            if (_emptyTitle is { } et) et.ForeColor = t.Text;
            if (_emptySub is { } es) es.ForeColor = t.Subtle;
            if (_emptyAdd is { } ea)
            {
                ea.BackColor = t.Accent; ea.ForeColor = AccentText();
                ea.FlatAppearance.BorderColor = t.Accent; ea.Surround = t.Card; ea.Invalidate();
            }
        }

        // Theme the custom scrollbar thumb (shown when sliders overflow the window).
        _scroll.ThumbColor = t.CtlBorder;
        _scroll.ThumbHoverColor = t.Subtle;
        _scroll.Invalidate();

        SetTitleBarDark(t.Dark);
        UpdateConflictHints();
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
            if (!hi && item is AppItem { Live: false }) fg = _theme.Subtle;
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
        else if (m.Msg == Program.WM_SHOWME)   // a second instance launched — surface this one
            RestoreFromTray();
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
            // Honour the configured close behaviour (Options → "On close");
            // Ask is the default.
            if (_closeBehavior == CloseBehavior.Tray)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
            if (_closeBehavior == CloseBehavior.Ask)
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
        }

        StopHid();       // stops discovery/readers and unblocks pending HID reads
        // Stop new device callbacks first, then the debounce they feed.
        if (_notify != null)
        {
            try { _enum.UnregisterEndpointNotificationCallback(_notify); } catch { }
            _notify = null;
        }
        _deviceDebounce.Stop();
        _deviceDebounce.Dispose();
        _vramp.Stop();
        _vramp.Dispose();
        _faderPump.Stop();
        _faderPump.Dispose();
        _trayUpdate.Stop();
        _trayUpdate.Dispose();
        _hook.Dispose();   // unhook the global keyboard hook
        _audioStats?.Dispose();
        _audioStats = null;
        _audio?.Dispose(); // releases all Core Audio COM objects on their MTA owner
        _audio = null;
        _present.Clear();
        try { _enum.Dispose(); } catch { }
        _tip.Dispose();
        foreach (var image in _appIcons.Values.Where(i => i != null).Distinct()) image!.Dispose();
        _appIcons.Clear();
        var trayIcon = _tray.Icon;
        var trayMenu = _tray.ContextMenuStrip;
        var formIcon = Icon;
        _tray.Icon = null;
        _tray.ContextMenuStrip = null;
        Icon = null;
        _tray.Visible = false;
        _tray.Dispose();
        trayMenu?.Dispose();
        trayIcon?.Dispose();
        formIcon?.Dispose();
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
        // The UI keeps only immutable endpoint metadata. AudioController owns the
        // long-lived MMDevice objects; these short enumeration wrappers are always
        // released immediately after their ID/name has been copied.
        var items = new List<DeviceItem>();
        try
        {
            foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { items.Add(new DeviceItem { Id = d.ID, Name = d.FriendlyName }); }
                catch { }
                finally { try { d.Dispose(); } catch { } }
            }
        }
        catch (Exception ex)
        {
            Program.LogRateLimited("audio-device-list", ex, "Refreshing audio devices");
            return;
        }
        _present.Clear();
        foreach (var it in items.OrderBy(d => d.Name)) _present[it.Id] = it;

        PopulateCombos();
        _audio?.RefreshEndpoints();
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
        UpdateConflictHints();
    }

    HashSet<string> ConfiguredTargetKeys(Axis a)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!IsFaderConnected(a)) return keys;
        if (a.Target == TargetKind.Output)
        {
            if (Resolve(a) is string id) keys.Add($"output:{id}");
        }
        else if (a.Target == TargetKind.App)
        {
            if (a.AppKey != null) keys.Add($"app:{a.AppKey}");
        }
        else if (a.CategoryName == UnassignedCategory)
        {
            foreach (string key in _knownApps.Keys.Where(k =>
                         !_categories.Any(c => c.AppKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                         && !_sliders.Any(s => s != a && IsFaderConnected(s)
                             && s.Target == TargetKind.App
                             && string.Equals(s.AppKey, k, StringComparison.OrdinalIgnoreCase))))
                keys.Add($"app:{key}");
        }
        else if (_categories.FirstOrDefault(c => c.Name == a.CategoryName) is { } category)
            foreach (string key in category.AppKeys) keys.Add($"app:{key}");
        return keys;
    }

    void UpdateConflictHints()
    {
        var targets = _sliders.ToDictionary(a => a, ConfiguredTargetKeys);
        foreach (var a in _sliders)
        {
            var conflicts = _sliders.Where(other => other != a && targets[a].Overlaps(targets[other]))
                .Select(other => other.Name.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bool conflict = conflicts.Length > 0;
            a.Combo.BorderColor = conflict ? Color.FromArgb(0xF0, 0x8A, 0x3C) : _theme.CtlBorder;
            a.Combo.Invalidate();
            _tip.SetToolTip(a.Combo, conflict
                ? $"Also controlled by {string.Join(", ", conflicts)}. The most recently moved fader wins."
                : "Pick what this fader controls");
        }
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
                    .Select(k => new AppItem
                    {
                        Key = k,
                        Name = _knownApps.TryGetValue(k, out var nm) ? nm : k,
                        Live = _liveApps.Contains(k),
                    })
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
            _deviceDebounce.Tick += (_, _) =>
            {
                _deviceDebounce.Stop();
                Interlocked.Exchange(ref _deviceRefreshQueued, 0);
                LoadDevices();
            };
            _notify = new DeviceNotify(OnAudioDevicesChanged);
            _enum.RegisterEndpointNotificationCallback(_notify);
        }
        catch (Exception ex) { Program.LogRateLimited("audio-device-notify", ex, "Registering audio device notifications"); }
    }

    // An endpoint appeared/vanished/changed state. Callbacks arrive on a COM
    // thread and often in bursts (e.g. one unplug raises several), so kick a
    // short debounce on the UI thread and do a single LoadDevices when it settles.
    void OnAudioDevicesChanged()
    {
        if (Interlocked.Exchange(ref _deviceRefreshQueued, 1) != 0) return;
        SafeUi(() =>
        {
            _deviceDebounce.Stop();
            _deviceDebounce.Start();
        });
    }

    // All outputs we could rank, including unplugged/disabled ones, for the editor.
    IReadOnlyList<OutputPref> AllKnownOutputs()
    {
        var result = new List<OutputPref>();
        foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Render,
                     DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled))
        {
            try { result.Add(new OutputPref { Id = d.ID, Name = d.FriendlyName }); }
            catch { }
            finally { try { d.Dispose(); } catch { } }
        }
        return result.OrderBy(o => o.Name).ToList();
    }

    // ---- app volume (audio sessions) --------------------------------------

    void StartAudio()
    {
        _audio = new AudioController(QueueAudioSnapshot, ex =>
        {
            Program.LogRateLimited("audio-worker", ex, "Core Audio worker restarted");
            SafeUi(() =>
            {
                if (_audioFaultShown) return;
                _audioFaultShown = true;
                _tray.ShowBalloonTip(4000, "ZMK Volume Fader",
                    "Windows audio control was interrupted and is reconnecting.", ToolTipIcon.Warning);
            });
        });
        ArmAllPhysicalPickups();
        PublishDesiredVolumes();
        if (Program.DiagAudioStats)
        {
            _audioStatsBaseTitle = Text;
            _audioStats = new System.Windows.Forms.Timer { Interval = 5_000 };
            _audioStats.Tick += (_, _) =>
            {
                if (_audio != null)
                    Text = $"{_audioStatsBaseTitle} [sets endpoint={_audio.EndpointSetCalls} session={_audio.SessionSetCalls}]";
            };
            _audioStats.Start();
        }
    }

    // Session bursts can arrive in clusters when a browser/game starts. Keep only
    // the newest immutable snapshot and post at most one UI callback at a time.
    void QueueAudioSnapshot(IReadOnlyList<AudioController.AppSnapshot> snapshot)
    {
        lock (_audioSnapshotLock)
        {
            _pendingAudioSnapshot = snapshot;
            if (_audioSnapshotQueued) return;
            _audioSnapshotQueued = true;
        }
        SafeUi(DrainAudioSnapshot);
    }

    void DrainAudioSnapshot()
    {
        IReadOnlyList<AudioController.AppSnapshot>? snapshot;
        lock (_audioSnapshotLock)
        {
            snapshot = _pendingAudioSnapshot;
            _pendingAudioSnapshot = null;
            _audioSnapshotQueued = false;
        }
        if (snapshot != null) ApplyAudioSnapshot(snapshot);
    }

    // Start the global keyboard hook. It lives on its own thread (so a busy UI
    // can never delay system-wide key delivery — Windows silently drops a
    // low-level hook whose callback stalls); its handler bails immediately for
    // keys with no binding, and marshals real matches to the UI thread.
    void StartHotkeys()
    {
        _hook.KeyDown += (vk, ctrl, alt, shift, win) =>
        {
            var bound = _boundVks;   // volatile snapshot
            bool maybe = false;
            for (int i = 0; i < bound.Length; i++)
                if (bound[i] == vk) { maybe = true; break; }
            if (!maybe || !IsHandleCreated) return;
            try { BeginInvoke(() => OnGlobalKey(vk, ctrl, alt, shift, win)); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        };
        _hook.InstallFailed += () =>
        {
            try
            {
                BeginInvoke(() => _tray.ShowBalloonTip(3000, "ZMK Volume Fader",
                    "Global hotkeys couldn't be enabled.", ToolTipIcon.Warning));
            }
            catch { }
        };
        _hook.Install();
    }

    // Rebuild the hook thread's bound-key snapshot. Call whenever bindings can
    // change (profile load, slider rebuild, hotkey dialog save).
    void UpdateHotkeySnapshot() =>
        _boundVks = _sliders.Where(s => s.IsVirtual)
            .SelectMany(s => new[] { s.HkUp, s.HkDown, s.HkMute })
            .Where(h => h.IsBound)
            .Select(h => h.Vk)
            .Distinct()
            .ToArray();

    // Called only when the controller's live app set changes. Friendly-name and
    // icon work therefore happens on session arrival, never on a timer.
    void ApplyAudioSnapshot(IReadOnlyList<AudioController.AppSnapshot> snapshot)
    {
        bool namesChanged = false;
        bool iconsChanged = false;
        bool identitiesChanged = false;
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var app in snapshot)
        {
            string key = app.Key;
            if (!key.Equals(app.LegacyKey, StringComparison.OrdinalIgnoreCase))
                identitiesChanged |= MigrateAppIdentity(app.LegacyKey, key);
            _appSeen[key] = nowSec;
            if (key == SystemAppKey)
            {
                if (!_knownApps.ContainsKey(key))
                {
                    _knownApps[key] = "System sounds";
                    namesChanged = true;
                }
                continue;
            }

            bool nameKnown = _knownApps.ContainsKey(key);
            bool wantIcon = (!_appIcons.TryGetValue(key, out var have) || have == null || have.Width < IconStore)
                && _iconTries.GetValueOrDefault(key) < IconTryMax;
            if (nameKnown && !wantIcon) continue;

            ResolveAppDetails(app.Pid, out var name, out var exePath);
            if (string.IsNullOrEmpty(name)) name = _knownApps.GetValueOrDefault(key, key);
            if (!_knownApps.TryGetValue(key, out var previous) || previous != name)
            {
                _knownApps[key] = name;
                namesChanged = true;
            }
            if (wantIcon)
            {
                var loaded = LoadExeIcon(exePath);
                if (loaded != null)
                {
                    if (_appIcons.TryGetValue(key, out var old) && old != null && !ReferenceEquals(old, loaded))
                        old.Dispose();
                    _appIcons[key] = loaded;
                    iconsChanged = true;
                    SaveIconFile(key, loaded);
                    _iconTries.Remove(key);
                }
                else
                {
                    _appIcons.TryAdd(key, null);
                    _iconTries[key] = _iconTries.GetValueOrDefault(key) + 1;
                }
            }
        }

        var previousLive = _liveApps;
        var live = new HashSet<string>(snapshot.Select(a => a.Key), StringComparer.OrdinalIgnoreCase);
        bool liveChanged = !live.SetEquals(_liveApps);
        _liveApps = live;
        if (namesChanged || identitiesChanged || liveChanged)
        {
            if (namesChanged || identitiesChanged) SaveSettings();
            PopulateCombos();
        }
        else if (iconsChanged)
            foreach (var s in _sliders) s.Combo.Invalidate();

        if (liveChanged)
        {
            var added = live.Where(k => !previousLive.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var a in _sliders.Where(a => !a.IsVirtual && TargetIncludesAny(a, added)))
                ArmPickup(a, publish: false);
        }

        UpdateAllSliderStates();
        PublishDesiredVolumes();
    }

    // Upgrade legacy process-name assignments to an executable identity when a
    // live session gives us one. Existing categories, faders, icons, and history
    // follow the migration automatically.
    bool MigrateAppIdentity(string oldKey, string newKey)
    {
        if (oldKey.Equals(newKey, StringComparison.OrdinalIgnoreCase)) return false;
        bool changed = false;

        if (_knownApps.Remove(oldKey, out string? oldName))
        {
            _knownApps.TryAdd(newKey, oldName);
            changed = true;
        }
        if (_appSeen.Remove(oldKey, out long oldSeen))
        {
            _appSeen[newKey] = Math.Max(oldSeen, _appSeen.GetValueOrDefault(newKey));
            changed = true;
        }
        if (_iconTries.Remove(oldKey, out int tries))
            _iconTries[newKey] = Math.Max(tries, _iconTries.GetValueOrDefault(newKey));
        if (_appIcons.Remove(oldKey, out var oldIcon))
        {
            bool transferred = false;
            if (!_appIcons.TryGetValue(newKey, out var current) || current == null)
            { _appIcons[newKey] = oldIcon; transferred = true; }
            else if (oldIcon != null && !ReferenceEquals(oldIcon, current))
                oldIcon.Dispose();
            if (transferred && oldIcon != null) SaveIconFile(newKey, oldIcon);
            changed = true;
        }

        foreach (var category in _categories)
        {
            bool replaced = false;
            for (int i = 0; i < category.AppKeys.Count; i++)
                if (category.AppKeys[i].Equals(oldKey, StringComparison.OrdinalIgnoreCase))
                { category.AppKeys[i] = newKey; replaced = true; }
            if (!replaced) continue;
            category.AppKeys = category.AppKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            changed = true;
        }
        foreach (var axis in _sliders)
            if (axis.AppKey?.Equals(oldKey, StringComparison.OrdinalIgnoreCase) == true)
            { axis.AppKey = newKey; changed = true; }
        foreach (var profile in _devices.Values)
            foreach (var config in profile.Sliders)
                if (config.AppKey?.Equals(oldKey, StringComparison.OrdinalIgnoreCase) == true)
                { config.AppKey = newKey; changed = true; }

        string oldIconFile = IconFile(oldKey);
        if (!oldIconFile.Equals(IconFile(newKey), StringComparison.OrdinalIgnoreCase))
            try { File.Delete(oldIconFile); } catch { }
        return changed;
    }

    // The friendly display name (exe FileDescription, else process name) and exe
    // path for icon extraction. Reads MainModule, so call sparingly (see above).
    static void ResolveAppDetails(int pid, out string name, out string? exePath)
    {
        name = "";
        exePath = null;
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
        }
        catch { }
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
    static string IconFile(string key)
    {
        if (key.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            return Path.Combine(IconDir, $"exe-{hash}.png");
        }
        return Path.Combine(IconDir,
            string.Concat(key.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 ? c : '_')) + ".png");
    }

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

    // Drop known apps that haven't had a live session in ~60 days and aren't
    // referenced by any category or fader target (in any device profile), so the
    // app list and the icon cache don't grow forever. Apps without a timestamp
    // (settings from an older build) get one now — a fresh grace period.
    void PruneKnownApps()
    {
        const long MaxAgeSec = 60L * 24 * 3600;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var referenced = new HashSet<string>(
            _categories.SelectMany(c => c.AppKeys)
                .Concat(_devices.Values.SelectMany(p => p.Sliders)
                    .Where(cfg => cfg.AppKey != null).Select(cfg => cfg.AppKey!)));
        foreach (var key in _knownApps.Keys.ToArray())
        {
            if (key == SystemAppKey || referenced.Contains(key)) continue;
            if (!_appSeen.TryGetValue(key, out var seen)) { _appSeen[key] = now; continue; }
            if (now - seen <= MaxAgeSec) continue;
            _knownApps.Remove(key);
            _appSeen.Remove(key);
            try { File.Delete(IconFile(key)); } catch { }
        }
        // Timestamps for apps no longer known are dead weight.
        foreach (var key in _appSeen.Keys.ToArray())
            if (!_knownApps.ContainsKey(key)) _appSeen.Remove(key);
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

    bool TargetIncludesAny(Axis a, HashSet<string> appKeys)
    {
        if (a.Target == TargetKind.App) return a.AppKey != null && appKeys.Contains(a.AppKey);
        if (a.Target != TargetKind.Category) return false;
        if (a.CategoryName == UnassignedCategory) return UnassignedKeys().Any(appKeys.Contains);
        return _categories.FirstOrDefault(c => c.Name == a.CategoryName)?.AppKeys.Any(appKeys.Contains) == true;
    }

    void PickupTargetKeys(Axis a, out string[] endpoints, out string[] apps)
    {
        endpoints = Array.Empty<string>();
        apps = Array.Empty<string>();
        if (a.Target == TargetKind.Output)
        {
            if (Resolve(a) is string id) endpoints = new[] { id };
        }
        else if (a.Target == TargetKind.App)
        {
            if (a.AppKey != null && _liveApps.Contains(a.AppKey)) apps = new[] { a.AppKey };
        }
        else if (a.CategoryName == UnassignedCategory)
            apps = UnassignedKeys().ToArray();
        else
            apps = _categories.FirstOrDefault(c => c.Name == a.CategoryName)?.AppKeys
                .Where(_liveApps.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                ?? Array.Empty<string>();
    }

    void ArmAllPhysicalPickups()
    {
        foreach (var a in _sliders.Where(a => !a.IsVirtual)) ArmPickup(a, publish: false);
    }

    void ArmPickup(Axis a, bool publish = true)
    {
        if (!_softTakeover)
        {
            CancelPickup(a);
            return;
        }
        if (a.IsVirtual || a.VMuted || _audio == null || !IsFaderConnected(a)) return;
        PickupTargetKeys(a, out var endpoints, out var apps);
        if (endpoints.Length == 0 && apps.Length == 0) { CancelPickup(a); return; }

        int generation = ++a.PickupGeneration;
        a.PickupArmed = true;
        a.PickupReady = false;
        a.PickupPosition = null;
        if (!Program.DiagNoDraw)
        {
            a.Pct.Text = "Syncing…";
            a.Pct.ForeColor = _theme.Accent;
        }
        if (publish) PublishDesiredVolumes();

        if (_audio.RequestCurrentVolumes(endpoints, apps, snapshot => SafeUi(() =>
            CompletePickup(a, generation, endpoints, apps, snapshot)))) return;
        CancelPickup(a);
    }

    void CompletePickup(Axis a, int generation, string[] endpoints, string[] apps,
        AudioController.VolumeSnapshot snapshot)
    {
        if (a.PickupGeneration != generation || !a.PickupArmed) return;
        var values = endpoints.Where(snapshot.Endpoints.ContainsKey).Select(id => snapshot.Endpoints[id])
            .Concat(apps.Where(snapshot.Apps.ContainsKey).Select(key => snapshot.Apps[key]))
            .ToArray();
        if (values.Length == 0)
        {
            CancelPickup(a);
            a.LastApplied = -1;
            Render(a);
            return;
        }
        int currentLevel = Math.Clamp((int)Math.Round(values.Average() * 100), 0, 100);
        // A configured cap can make the current Windows level unreachable. In
        // that case, taking control at the fader's maximum is the safest handoff.
        a.PickupTarget = Math.Min(currentLevel, (int)a.Limit.Value);
        a.PickupReady = true;
        a.PickupPosition = null;
        a.LastApplied = a.PickupTarget;
        Render(a);
    }

    void CancelPickup(Axis a)
    {
        a.PickupGeneration++;
        a.PickupArmed = false;
        a.PickupReady = false;
        a.PickupPosition = null;
        a.Bar.PickupPosition = null;
        _tip.SetToolTip(a.Bar, a.IsVirtual
            ? "Drag to set the volume (or focus it and use the arrow keys)"
            : "Physical fader position");
    }

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
            ArmPickup(a);
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
            a.LastApplied = -1;
            TakeTargetOwnership(a);
            ApplyActive(a);
            ArmPickup(a);
            SaveSettings();
            return;
        }
        if (item is CategoryItem cat)
        {
            a.Target = TargetKind.Category;
            a.CategoryName = cat.Name;
            a.LastApplied = -1;
            TakeTargetOwnership(a);
            ApplyActive(a);
            ArmPickup(a);
            SaveSettings();
            return;
        }
        // Output pick. Picking the auto target (the device the ranking would
        // choose) clears the override; any other pick becomes an override.
        a.Target = TargetKind.Output;
        string? picked = (item as DeviceItem)?.Id;
        a.OverrideId = (picked != null && picked == AutoTarget(a)) ? null : picked;
        TakeTargetOwnership(a);
        ApplyActive(a);
        ArmPickup(a);
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
        Render(a, takeOwnership: true);
    }

    // The user dragged a virtual fader. Drag is direct (no smoothing ramp — you're
    // already in fine control): set target and current together. Persist only when
    // the drag ends (committed) to avoid a write per pixel.
    void OnVirtualSet(Axis a, int value, bool committed)
    {
        a.VTarget = value;
        a.VCur = value;
        a.VMuted = false;
        UpdateMuteButton(a);
        Render(a, takeOwnership: true);
        if (committed && !_loadingSettings) SaveSettings();
    }

    // A bound hotkey fired. Runs on the UI thread (the hook is installed there),
    // so touch sliders directly — but keep it quick; it's in the global key path.
    void OnGlobalKey(int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        foreach (var a in _sliders)
        {
            if (!a.IsVirtual) continue;
            if (a.HkUp.Matches(vk, ctrl, alt, shift, win)) NudgeVirtual(a, a.Step);
            else if (a.HkDown.Matches(vk, ctrl, alt, shift, win)) NudgeVirtual(a, -a.Step);
            else if (a.HkMute.Matches(vk, ctrl, alt, shift, win)) ToggleMute(a);
        }
    }

    // Move a virtual fader's target by delta%; the ramp glides the applied level
    // to it, so holding the key (OS auto-repeat) chases smoothly without spiking.
    void NudgeVirtual(Axis a, int delta)
    {
        a.VMuted = false;
        UpdateMuteButton(a);
        a.VTarget = Math.Clamp(a.VTarget + delta, 0, 100);
        EnsureRamp();
    }

    void ToggleMute(Axis a)
    {
        CancelPickup(a);
        if (a.IsVirtual)
        {
            if (a.VMuted) { a.VTarget = a.VPreMute; a.VMuted = false; }
            else { a.VPreMute = a.VTarget; a.VTarget = 0; a.VMuted = true; }
            EnsureRamp();
        }
        else
        {
            if (!a.VMuted) a.VPreMute = Math.Max(0, a.LastApplied);
            a.VMuted = !a.VMuted;
            a.LastApplied = -1;
            Render(a, takeOwnership: true);
            if (!_loadingSettings) SaveSettings();
        }
        UpdateMuteButton(a);
    }

    void ResetVirtual(Axis a)
    {
        if (!a.IsVirtual) return;
        a.VMuted = false;
        UpdateMuteButton(a);
        a.VTarget = 100;
        a.VCur = 100;
        Render(a, takeOwnership: true);
        if (!_loadingSettings) SaveSettings();
    }

    void UpdateMuteButton(Axis a)
    {
        if (a.Mute == null) return;
        a.Mute.Text = a.VMuted ? "Unmute" : "Mute";
        a.Mute.AccessibleName = a.VMuted ? "Unmute fader" : "Mute fader";
    }

    void EnsureRamp() { if (!_vramp.Enabled) _vramp.Start(); }

    // Ease each virtual fader's current level toward its target; drive volume as it
    // moves; stop and persist once everything has settled.
    void VrampTick()
    {
        bool moving = false;
        foreach (var a in _sliders)
        {
            if (!a.IsVirtual) continue;
            double diff = a.VTarget - a.VCur;
            if (Math.Abs(diff) < 0.5)
            {
                if (a.VCur != a.VTarget) { a.VCur = a.VTarget; Render(a, takeOwnership: true); }
                continue;
            }
            a.VCur += diff * 0.35;   // exponential ease-out
            Render(a, takeOwnership: true);
            moving = true;
        }
        if (!moving)
        {
            _vramp.Stop();
            if (!_loadingSettings) SaveSettings();   // persist settled levels once
        }
    }

    // Open the per-fader hotkey assignment dialog and store the result. Hotkey
    // dispatch is suspended while it's open (Discord-style) so pressing an
    // already-bound key to rebind it can't nudge a volume mid-capture; the other
    // faders' bindings ride along so the dialog can warn about conflicts.
    void OpenHotkeys(Axis a)
    {
        var others = _sliders.Where(s => s.IsVirtual && s != a)
            .SelectMany(s => new (string, Hotkey)[]
                { (s.Name.Text, s.HkUp), (s.Name.Text, s.HkDown), (s.Name.Text, s.HkMute) })
            .Where(o => o.Item2.IsBound)
            .ToList();
        _hook.Suspend = true;
        try
        {
            using var dlg = new HotkeyDialog(_theme, a.Name.Text, a.HkUp, a.HkDown, a.HkMute, a.Step, others);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                a.HkUp = dlg.Up; a.HkDown = dlg.Down; a.HkMute = dlg.Mute; a.Step = dlg.Step;
                SaveSettings();
                UpdateHotkeySnapshot();
            }
        }
        finally { _hook.Suspend = false; }
    }

    // Delete one virtual fader: rebuild the layout from the remaining sliders.
    void RemoveSlider(Axis a)
    {
        if (MessageBox.Show(this, $"Remove the virtual fader “{a.Name.Text}”?",
                "ZMK Volume Fader", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            != DialogResult.Yes) return;
        // Rebuild just the fader's own group, leaving other groups intact.
        if (_groups.FirstOrDefault(g => g.Key == a.GroupKey) is not { } g) return;
        var configs = g.Sliders.Where(s => s != a).Select(ToConfig).ToList();
        BuildGroupCards(g, configs);
        RelayoutGroups();
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
        Value = s.VTarget,
        HkUp = s.HkUp,
        HkDown = s.HkDown,
        HkMute = s.HkMute,
        Step = s.Step,
        Muted = s.VMuted,
        PreMute = s.VPreMute,
    };

    // Copy a config's virtual-fader state (level, hotkeys, step) onto a slider.
    static void LoadVirtual(Axis a, SliderConfig c)
    {
        a.VTarget = c.Value;
        a.VCur = c.Value;
        a.VMuted = c.Muted;
        // Restore the pre-mute level so unmuting after a restart still works.
        a.VPreMute = c.Muted ? Math.Clamp(c.PreMute, 0, 100) : c.Value;
        a.HkUp = c.HkUp ?? new();
        a.HkDown = c.HkDown ?? new();
        a.HkMute = c.HkMute ?? new();
        a.Step = c.Step <= 0 ? 5 : c.Step;
        if (a.IsVirtual) a.Bar.Value = c.Value;
    }

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
        _loadingSettings = true;
        try
        {
            // Prefer the multi-slider file; fall back to the release build's
            // settings.json to migrate an existing setup on first run.
            string? path = File.Exists(SettingsPath) ? SettingsPath
                : File.Exists(LegacySettingsPath) ? LegacySettingsPath : null;
            if (path == null) return;
            Settings? s = null;
            Exception? readError = null;
            string? loadedFrom = null;
            string[] candidates = path == SettingsPath
                ? new[] { path, SettingsPath + ".bak" }
                : new[] { path };
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(candidate));
                    if (s != null) { loadedFrom = candidate; break; }
                }
                catch (Exception ex) { readError = ex; }
            }
            if (s == null)
            {
                // Unreadable (corrupt, truncated, or locked mid-scan): keep a copy
                // for recovery — otherwise the next SaveSettings would overwrite
                // every profile with the empty defaults we're falling back to.
                try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
                if (readError != null) Program.Log(readError, "Loading settings");
                return;
            }
            if (loadedFrom != path)
            {
                // Preserve the broken primary for inspection, then restore the
                // known-good backup so the next atomic save does not rotate the
                // corrupt file over that backup.
                try
                {
                    File.Copy(path, path + ".bad", overwrite: true);
                    File.Copy(loadedFrom!, path, overwrite: true);
                }
                catch (Exception ex) { Program.Log(ex, "Restoring settings backup"); }
            }
            if (s.SchemaVersion > CurrentSettingsSchema)
            {
                Program.Log(new InvalidDataException(
                    $"Settings schema {s.SchemaVersion} is newer than supported schema {CurrentSettingsSchema}."));
                return;
            }

            _deviceMax = s.DeviceMax != null ? new(s.DeviceMax) : new();
            _devices = s.Devices != null ? new(s.Devices) : new();
            _knownApps.Clear();
            if (s.KnownApps != null) foreach (var kv in s.KnownApps) _knownApps[kv.Key] = kv.Value;
            _appSeen.Clear();
            if (s.AppSeen != null) foreach (var kv in s.AppSeen) _appSeen[kv.Key] = kv.Value;
            _ignored = new(s.IgnoredDevices ?? new(), StringComparer.OrdinalIgnoreCase);
            _allowed = new(s.MonitoredDevices ?? new(), StringComparer.OrdinalIgnoreCase);
            PublishDeviceSets();
            _categories = s.Categories ?? new();
            _themeMode = s.ThemeMode;
            _closeBehavior = s.CloseBehavior;
            _softTakeover = s.SoftTakeover;

            // No per-device profiles yet but old flat settings present -> build a
            // seed profile to apply to the first device that connects.
            if (_devices.Count == 0 && (s.LeftOutputs != null || s.LeftDeviceId != null || s.LeftCal != null))
                _legacyProfile = LegacyToProfile(s);

            ApplyTheme(CurrentTheme());
        }
        catch (Exception ex) { Program.Log(ex, "Applying settings"); }
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
            // Snapshot every live group into its own device profile. Disconnected
            // devices keep their existing profile in _devices untouched.
            foreach (var g in _groups)
                _devices[g.Key] = new DeviceProfile { Name = g.Name, Sliders = g.Sliders.Select(ToConfig).ToList() };

            var s = new Settings { SchemaVersion = CurrentSettingsSchema, Devices = _devices, DeviceMax = _deviceMax, KnownApps = new(_knownApps), AppSeen = new(_appSeen), IgnoredDevices = _ignored.ToList(), MonitoredDevices = _allowed.ToList(), Categories = _categories, ThemeMode = _themeMode, CloseBehavior = _closeBehavior, SoftTakeover = _softTakeover };
            Directory.CreateDirectory(SettingsDir);
            // Write to a temp file then swap it in, so a crash mid-write can't
            // leave a half-written (corrupt) file behind.
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            if (File.Exists(SettingsPath))
            {
                try { File.Replace(tmp, SettingsPath, SettingsPath + ".bak", ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(SettingsPath, SettingsPath + ".bak", overwrite: true);
                    File.Move(tmp, SettingsPath, overwrite: true);
                }
            }
            else File.Move(tmp, SettingsPath);
        }
        catch (Exception ex)
        {
            Program.LogRateLimited("settings-save", ex, "Saving settings");
            SafeUi(() =>
            {
                if (_settingsFaultShown) return;
                _settingsFaultShown = true;
                _tray.ShowBalloonTip(4000, "ZMK Volume Fader",
                    "Settings could not be saved. Details were written to error.log.", ToolTipIcon.Warning);
            });
        }
    }

    // ---- per-device profiles ----------------------------------------------

    static string DeviceKey(HidDevice d)
    {
        string? serial = null;
        try { serial = d.GetSerialNumber(); } catch { }
        string vp = $"{d.VendorID:X4}:{d.ProductID:X4}";
        return string.IsNullOrWhiteSpace(serial) ? vp : $"{vp}:{serial}";
    }

    // From a reader thread: a device connected. Add (or un-grey) its group.
    void OnDeviceConnected(string key, string name) => SafeUi(() => AddOrConnectGroup(key, name));

    // From a reader thread: a device dropped. Keep its group on screen (frozen at
    // its last levels) but grey the header dot — a brief USB/BLE blip shouldn't
    // flash the layout. The group is only removed when the user ignores it.
    void OnDeviceDisconnected(string key) => SafeUi(() => MarkGroupConnection(key, false));

    // Post an action to the UI thread, swallowing the shutdown race where the
    // handle is destroyed between the check and the post.
    void SafeUi(Action a)
    {
        if (!IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    void AddOrConnectGroup(string key, string name)
    {
        if (_groups.FirstOrDefault(g => g.Key == key) is { } existing)
        {
            bool reconnected = !existing.Connected;
            existing.Connected = true;
            if (!string.IsNullOrWhiteSpace(name)) { existing.Name = name; if (existing.HeaderName != null) existing.HeaderName.Text = name; }
            RethemeHeader(existing, CurrentTheme());
            UpdateAggregateStatus();
            if (reconnected)
            {
                foreach (var a in existing.Sliders) ArmPickup(a, publish: false);
                PublishDesiredVolumes();
            }
            return;
        }
        bool freshDefault = false;
        if (!_devices.TryGetValue(key, out var profile))
        {
            // First time we've seen this unit: seed from migrated settings, else a
            // default two-slider layout the setup wizard can refine.
            if (_legacyProfile != null) { profile = _legacyProfile; _legacyProfile = null; }
            else { profile = new DeviceProfile { Sliders = DefaultSliders() }; freshDefault = true; }
            profile.Name = name;
            _devices[key] = profile;
        }
        if (string.IsNullOrEmpty(profile.Name)) profile.Name = name;
        var group = new FaderGroup { Key = key, Name = profile.Name, IsVirtual = false, Connected = true };
        _groups.Add(group);
        BuildGroupCards(group, profile.Sliders);
        RelayoutGroups();
        SaveSettings();
        UpdateAggregateStatus();
        foreach (var a in group.Sliders) ArmPickup(a, publish: false);
        PublishDesiredVolumes();
        if (freshDefault) PromptSetup(group.Key, name);
    }

    void MarkGroupConnection(string key, bool connected)
    {
        if (_groups.FirstOrDefault(g => g.Key == key) is not { } g) return;
        if (g.Connected == connected) return;
        g.Connected = connected;
        if (connected)
            foreach (var a in g.Sliders) ArmPickup(a, publish: false);
        else
            foreach (var a in g.Sliders) CancelPickup(a);
        RethemeHeader(g, CurrentTheme());
        UpdateAggregateStatus();
        UpdateAllSliderStates();
        PublishDesiredVolumes();
    }

    // Drop a group entirely (user ignored the device): persist its final state,
    // dispose its cards, and re-lay out. Falls back to the empty card when the
    // last group goes.
    void RemoveGroup(string key)
    {
        if (_groups.FirstOrDefault(g => g.Key == key) is not { } g) return;
        _devices[key] = new DeviceProfile { Name = g.Name, Sliders = g.Sliders.Select(ToConfig).ToList() };
        foreach (var s in g.Sliders) { ClearTips(s.Card); s.Card.Dispose(); }
        _groups.Remove(g);
        _rawByDevice.Remove(key);
        RelayoutGroups();
        SaveSettings();
    }

    static List<SliderConfig> DefaultSliders() => new()
    {
        new SliderConfig { AxisIndex = 0, Label = "Left fader" },
        new SliderConfig { AxisIndex = 1, Label = "Right fader" },
    };

    // Show the saved virtual-fader home (if it has any faders) as its own group so
    // people without hardware — or alongside it — keep their draggable faders.
    void EnsureVirtualGroup()
    {
        if (_groups.Any(g => g.Key == VirtualKey)) return;
        if (_devices.TryGetValue(VirtualKey, out var vp) && vp.Sliders.Count > 0)
        {
            var g = new FaderGroup { Key = VirtualKey, Name = "Virtual faders", IsVirtual = true, Connected = true };
            _groups.Add(g);
            BuildGroupCards(g, vp.Sliders);
        }
    }

    // Ensure a group object exists for a key (used by the setup wizard when adding
    // faders to the virtual home, or a device whose group isn't built yet).
    FaderGroup EnsureGroup(string key)
    {
        if (_groups.FirstOrDefault(g => g.Key == key) is { } g) return g;
        bool virt = key == VirtualKey;
        string name = virt ? "Virtual faders"
            : _devices.TryGetValue(key, out var p) && !string.IsNullOrEmpty(p.Name) ? p.Name : key;
        g = new FaderGroup { Key = key, Name = name, IsVirtual = virt, Connected = true };
        _groups.Add(g);
        return g;
    }

    // Build (or rebuild) one group's Axis cards from its slider configs, tagging
    // each with the group's key so device axes route to the right sliders. Disposes
    // the group's previous cards.
    void BuildGroupCards(FaderGroup g, List<SliderConfig> configs)
    {
        var old = g.Sliders;
        g.Sliders = configs.Select((c, i) =>
        {
            var a = BuildSlider(c.AxisIndex, string.IsNullOrEmpty(c.Label) ? $"Fader {i + 1}" : c.Label, c.IsVirtual);
            a.GroupKey = g.Key;
            a.Prefs = ClonePrefs(c.Outputs);
            a.OverrideId = c.OverrideId;
            a.Target = c.Target;
            a.AppKey = c.AppKey;
            a.CategoryName = c.CategoryName;
            LoadVirtual(a, c);
            UpdateMuteButton(a);
            if (c.Target != TargetKind.Output) { _loadingSettings = true; a.Limit.Value = ClampLimit(c.Max); _loadingSettings = false; }
            ApplyCalibration(a, c.Cal);
            return a;
        }).ToList();
        foreach (var s in old) { ClearTips(s.Card); s.Card.Dispose(); }
    }

    // Re-lay out the whole host after any group change: reorder groups (physical
    // first, virtual home last), rebuild the flat _sliders view, rebuild the host
    // (headers + cards), and refresh theme/size/combos/hotkeys.
    void RelayoutGroups()
    {
        _groups.Sort((a, b) =>
            a.IsVirtual != b.IsVirtual ? (a.IsVirtual ? 1 : -1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _sliders = _groups.SelectMany(g => g.Sliders).ToArray();
        _left = _sliders.Length > 0 ? _sliders[0] : null!;
        _right = _sliders.Length > 1 ? _sliders[1] : _left;

        PopulateSliderHost();
        ApplyTheme(CurrentTheme());
        FitWindowHeight();
        LoadDevices();   // repopulate combos + re-pick active outputs across all cards
        UpdateHotkeySnapshot();
        ArmAllPhysicalPickups();
        PublishDesiredVolumes();
    }

    // Build a group's header row: a status dot, the device/section name, and a
    // Set-up (physical) or Add-fader (virtual) button. References are stashed on
    // the group so ApplyTheme can recolour them in place without a relayout.
    Control BuildGroupHeader(FaderGroup g, bool first)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, AutoSize = true, BackColor = Color.Transparent,
            Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(2, first ? 0 : 12, 2, 4)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var dot = new Label { Text = "●", AutoSize = true, Font = UiFonts.Get(7f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };
        var name = new Label { Text = g.Name, AutoSize = true, Font = UiFonts.Get(9.75f, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 0, 0) };
        var btn = new RoundedButton { Text = g.IsVirtual ? "Add fader" : "Configure", AutoSize = true, Padding = new Padding(10, 3, 10, 3), Anchor = AnchorStyles.Right, Margin = new Padding(8, 0, 0, 0) };
        btn.Click += (_, _) => RunSetupWizard(g.Key, pulse: g.IsVirtual && g.Sliders.Count == 0);
        _tip.SetToolTip(btn, g.IsVirtual ? "Add or arrange virtual faders" : "Map this device's faders and capture their range");

        row.Controls.Add(dot, 0, 0);
        row.Controls.Add(name, 1, 0);
        row.Controls.Add(btn, 2, 0);

        g.Header = row; g.HeaderDot = dot; g.HeaderName = name; g.HeaderBtn = btn;
        RethemeHeader(g, CurrentTheme());
        return row;
    }

    // Recolour a group header for the current theme. The dot is accent while the
    // device is connected, subtle once it drops (virtual home: always subtle).
    void RethemeHeader(FaderGroup g, Theme t)
    {
        if (g.HeaderDot is { } dot) dot.ForeColor = !g.IsVirtual && g.Connected ? t.Accent : t.Subtle;
        if (g.HeaderName is { } nm) nm.ForeColor = t.Text;
        if (g.HeaderBtn is { } btn)
        {
            btn.BackColor = t.CtlBg; btn.ForeColor = t.Text;
            btn.FlatAppearance.BorderColor = t.CtlBorder; btn.Surround = t.Window; btn.Invalidate();
        }
    }

    // Unregister every tooltip under a control tree (see BuildGroupCards).
    void ClearTips(Control root)
    {
        _tip.SetToolTip(root, null);
        foreach (Control c in root.Controls) ClearTips(c);
    }

    // Open the fader setup dialog for one group: a reorderable list seeded from
    // that group's faders, with inline physical capture and add-virtual. Physical
    // capture watches the group's own device axes, so setting up one device never
    // sees another's movement. On Done, rebuild just that group. The empty-state
    // card and the virtual header pass VirtualKey (its capture reads nothing —
    // physical faders are mapped from their own device's "Set up" button).
    void RunSetupWizard(string groupKey, bool pulse = false)
    {
        var group = _groups.FirstOrDefault(g => g.Key == groupKey);
        var seedFrom = group?.Sliders ?? new List<Axis>();
        var existing = seedFrom.Select((s, i) => new SetupDialog.Item
        {
            Kind = s.IsVirtual ? SetupDialog.ItemKind.Virtual : SetupDialog.ItemKind.Physical,
            Axis = s.AxisIndex,
            Min = s.Cal.Min,
            Max = s.Cal.Max,
            SourceIndex = i,
            Label = s.Name.Text,
        }).ToList();

        bool prev = _calibrating;
        _calibrating = true;   // don't drive outputs mid-sweep
        using (var dlg = new SetupDialog(_theme, () => RawFor(groupKey), existing, pulse))
        {
            var result = dlg.ShowDialog(this);
            _calibrating = prev;
            if (result == DialogResult.OK)
            {
                var configs = dlg.Result.Select((item, i) =>
                {
                    // The name typed in the dialog, or a positional default if blank.
                    string label = string.IsNullOrWhiteSpace(item.Label) ? $"Fader {i + 1}" : item.Label.Trim();
                    // Seeded rows reuse the existing slider's full config so its
                    // target/cap/calibration survive a reorder; the (possibly edited)
                    // name overrides.
                    if (item.SourceIndex is int si && si >= 0 && si < seedFrom.Count)
                    {
                        var cfg = ToConfig(seedFrom[si]);
                        cfg.Label = label;
                        return cfg;
                    }
                    return item.Kind == SetupDialog.ItemKind.Physical
                        ? new SliderConfig
                        {
                            AxisIndex = item.Axis,
                            Label = label,
                            Cal = new Calibration { Min = item.Min, Max = item.Max, Taper = TaperKind.Linear },
                        }
                        : new SliderConfig { IsVirtual = true, AxisIndex = -1, Label = label, Value = 50 };
                }).ToList();
                var g = EnsureGroup(groupKey);
                BuildGroupCards(g, configs);
                RelayoutGroups();   // 0 configs collapses the group (empty card if it was the last)
                SaveSettings();
            }
        }
        foreach (var s in _sliders) { s.LastApplied = -1; ApplyActive(s); }
    }

    // The setup wizard's raw-axis feed for a group: a snapshot of that device's
    // latest axes (zeros for the virtual home, which has no hardware to capture).
    int[] RawFor(string key) => _rawByDevice.TryGetValue(key, out var r) ? (int[])r.Clone() : new int[MaxAxes];

    // Which group the Options → "Set up faders" button edits (it isn't group-aware
    // — each group's own header has a Set-up button too): a connected device first,
    // else any device, else the virtual home.
    string DefaultSetupGroupKey()
        => _groups.FirstOrDefault(g => !g.IsVirtual && g.Connected)?.Key
        ?? _groups.FirstOrDefault(g => !g.IsVirtual)?.Key
        ?? VirtualKey;

    void PromptSetup(string key, string name)
    {
        var r = MessageBox.Show(this,
            $"New fader device “{name}” detected.\n\nRun setup to map its faders? " +
            "You'll move each fader fully bottom-to-top so the app can detect it and capture its range.",
            "ZMK Volume Fader", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes) RunSetupWizard(key);
    }

    static int ClampLimit(int pct) => Math.Clamp(pct, 1, 100);

    // ---- device manager ---------------------------------------------------

    // Build a friendly one-liner for a candidate: VID:PID, serial, and whether it
    // exposes our exact fader report or merely shares the vendor page.
    static string DeviceDetail(FaderCandidate c)
    {
        string s = $"{c.Vid:X4}:{c.Pid:X4}";
        if (!string.IsNullOrWhiteSpace(c.Serial)) s += $"  ·  SN {c.Serial}";
        s += c.ByUsage ? "  ·  fader report" : "  ·  shares the vendor HID page";
        return s;
    }

    // Show the Devices dialog: every detected fader-page unit (plus any the user
    // explicitly overrode, so a choice can be reversed while unplugged) with a
    // monitor/ignore toggle. On Save, fold each row's choice back into the
    // override sets (only where it differs from the default) and reconcile live
    // readers/groups so a change takes effect at once.
    void OpenDevices()
    {
        var rows = new List<DevicesDialog.Row>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var open = ConnectedKeys();
        foreach (var c in EnumerateCandidates())
        {
            seen.Add(c.Key);
            bool auto = DefaultMonitor(c.ByUsage, c.OurVid);
            rows.Add(new DevicesDialog.Row
            {
                Key = c.Key,
                Name = string.IsNullOrWhiteSpace(c.Name) ? "(unnamed HID device)" : c.Name,
                Detail = DeviceDetail(c),
                OurVid = c.OurVid,
                Connected = open.Contains(c.Key),
                AutoDefault = auto,
                Monitored = ShouldMonitor(c.Key, c.ByUsage, c.OurVid),
            });
        }
        // Overridden units that aren't plugged in right now — still list them so a
        // choice can be reversed. An ignored one defaults to "would monitor" so the
        // ignore persists if left unticked; an opted-in one to "would not".
        foreach (var key in _ignored.Concat(_allowed))
        {
            if (!seen.Add(key)) continue;
            bool allowed = _allowed.Contains(key);
            string name = _devices.TryGetValue(key, out var p) && !string.IsNullOrWhiteSpace(p.Name) ? p.Name : (allowed ? "Opted-in device" : "Ignored device");
            rows.Add(new DevicesDialog.Row { Key = key, Name = name, Detail = $"{key}  ·  not connected", AutoDefault = !allowed, Monitored = allowed });
        }
        rows = rows
            .OrderByDescending(r => r.Connected)
            .ThenByDescending(r => r.OurVid)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var dlg = new DevicesDialog(_theme, rows);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // Apply per-row deltas: clear any prior override for the shown key, then
        // record one only when the choice differs from the default. Keys not shown
        // (overridden + unplugged and not surfaced) keep their entries untouched.
        foreach (var r in rows)
        {
            _ignored.Remove(r.Key);
            _allowed.Remove(r.Key);
            if (r.Monitored == r.AutoDefault) continue;
            if (r.Monitored) _allowed.Add(r.Key); else _ignored.Add(r.Key);
        }
        PublishDeviceSets();
        SaveSettings();
        _discoveryWake.Set();

        // Reconcile live state: stop reading and drop the group for any device the
        // user just un-ticked. Newly opted-in units are opened by the next
        // discovery scan (~1.5 s), which adds their group automatically.
        foreach (var r in rows)
            if (!r.Monitored && _groups.Any(g => g.Key == r.Key))
            {
                StopReader(r.Key);
                RemoveGroup(r.Key);
            }
    }

    // Signal one device's reader to stop and close its stream.
    void StopReader(string key)
    {
        Reader? r;
        lock (_readersLock) _readers.TryGetValue(key, out r);
        if (r == null) return;
        r.Stop = true;
        try { r.Stream?.Close(); } catch { }
    }

    // Device keys currently open for reading.
    HashSet<string> ConnectedKeys()
    {
        lock (_readersLock) return new HashSet<string>(_readers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // ---- options ----------------------------------------------------------

    void OpenOptions()
    {
        var cals = _sliders.Select(s => s.Cal.Clone()).ToArray();
        var raws = _sliders.Select(s => (Func<int>)(() => s.LastRaw)).ToArray();
        var outs = _sliders.Select(s => ClonePrefs(s.Prefs)).ToArray();
        var labels = _sliders.Select(s => s.Name.Text).ToArray();
        var cats = _categories.Select(c => new Category { Name = c.Name, AppKeys = new(c.AppKeys) }).ToList();
        var virtuals = _sliders.Select(s => s.IsVirtual).ToArray();
        using var dlg = new OptionsDialog(_theme, _themeMode, GetStartWithWindows(), _closeBehavior, _softTakeover,
            cals, raws, outs, labels, AllKnownOutputs(), _present.Keys.ToArray(), cats, _knownApps, _appIcons, virtuals, _liveApps);
        _calibrating = true;                 // stop driving devices while sweeping
        var result = dlg.ShowDialog(this);
        _calibrating = false;
        if (dlg.DiagnosticsRequested) { OpenDiagnostics(); return; }
        if (dlg.ExportRequested) { ExportSettings(); return; }
        if (dlg.ImportRequested) { ImportSettings(); return; }
        if (result == DialogResult.OK)
        {
            for (int i = 0; i < _sliders.Length; i++)
            {
                ApplyCalibration(_sliders[i], dlg.Cals[i]);
                ApplyOutputs(_sliders[i], dlg.Outputs[i]);
            }
            _categories = dlg.Categories;
            // Follow renames so sliders (and every saved device profile) that
            // point at a renamed category stay attached instead of going dead.
            if (dlg.CategoryRenames.Count > 0)
            {
                foreach (var s in _sliders)
                    if (s.CategoryName != null && dlg.CategoryRenames.TryGetValue(s.CategoryName, out var nn))
                        s.CategoryName = nn;
                foreach (var p in _devices.Values)
                    foreach (var cfg in p.Sliders)
                        if (cfg.CategoryName != null && dlg.CategoryRenames.TryGetValue(cfg.CategoryName, out var nn))
                            cfg.CategoryName = nn;
            }
            _themeMode = dlg.SelectedTheme;
            _closeBehavior = dlg.SelectedClose;
            _softTakeover = dlg.SoftTakeover;
            ApplyTheme(CurrentTheme());
            SetStartWithWindows(dlg.StartWithWindows);
            SaveSettings();
        }
        // Re-push (categories/outputs may have changed, so repopulate combos too).
        // Runs before any requested setup so the combos are fresh even if the
        // setup dialog is then cancelled.
        foreach (var s in _sliders) s.LastApplied = -1;
        PopulateCombos();
        if (_softTakeover) ArmAllPhysicalPickups();
        else foreach (var s in _sliders) CancelPickup(s);
        PublishDesiredVolumes();
        if (result == DialogResult.OK && dlg.SetupRequested) RunSetupWizard(DefaultSetupGroupKey());
    }

    void OpenDiagnostics()
    {
        using var dialog = new DiagnosticsDialog(_theme, BuildDiagnosticReport);
        dialog.ShowDialog(this);
    }

    string BuildDiagnosticReport()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var readers = _readerSnapshot;
        var sb = new StringBuilder();
        sb.AppendLine("ZMK Volume Fader diagnostics");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        sb.AppendLine($"Build: {VersionText()}");
        sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Process uptime: {DateTime.UtcNow - Program.StartedAtUtc:g}");
        sb.AppendLine();
        sb.AppendLine("Process resources");
        sb.AppendLine($"  Working set: {process.WorkingSet64 / 1024d / 1024d:F1} MB");
        sb.AppendLine($"  Private memory: {process.PrivateMemorySize64 / 1024d / 1024d:F1} MB");
        sb.AppendLine($"  Managed heap: {GC.GetTotalMemory(false) / 1024d / 1024d:F1} MB");
        sb.AppendLine($"  Handles: {process.HandleCount}");
        sb.AppendLine($"  Threads: {process.Threads.Count}");
        sb.AppendLine();
        sb.AppendLine("HID");
        sb.AppendLine($"  Open readers: {readers.Length}");
        sb.AppendLine($"  Reports received: {Interlocked.Read(ref _hidReportCount)}");
        foreach (var reader in readers) sb.AppendLine($"  Device: {reader.Name} ({reader.Key})");
        sb.AppendLine();
        sb.AppendLine("Core Audio");
        sb.AppendLine($"  Endpoints: {_audio?.EndpointCount ?? 0}");
        sb.AppendLine($"  Sessions: {_audio?.SessionCount ?? 0}");
        sb.AppendLine($"  Live app keys: {_liveApps.Count}");
        sb.AppendLine($"  Endpoint setters: {_audio?.EndpointSetCalls ?? 0}");
        sb.AppendLine($"  Session setters: {_audio?.SessionSetCalls ?? 0}");
        sb.AppendLine($"  Desired snapshots: {Interlocked.Read(ref _desiredSnapshotCount)}");
        sb.AppendLine();
        sb.AppendLine("Faders");
        sb.AppendLine($"  Total: {_sliders.Length}");
        sb.AppendLine($"  Soft takeover enabled: {_softTakeover}");
        sb.AppendLine($"  Soft takeover armed: {_sliders.Count(a => a.PickupArmed)}");
        sb.AppendLine($"  Muted: {_sliders.Count(a => a.VMuted)}");
        foreach (var a in _sliders)
            sb.AppendLine($"  {a.Name.Text}: {(a.IsVirtual ? "virtual" : $"axis {a.AxisIndex + 1}")}, {a.Target}, {a.LastApplied}%");
        sb.AppendLine();
        sb.AppendLine($"Settings: {SettingsPath}");
        sb.AppendLine($"Error log: {Program.ErrorLogPath} ({(File.Exists(Program.ErrorLogPath) ? "present" : "not present")})");
        return sb.ToString();
    }

    void ExportSettings()
    {
        SaveSettings();
        using var dialog = new SaveFileDialog
        {
            Title = "Export ZMK Volume Fader settings",
            Filter = "ZMK Volume Fader settings (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"ZmkVolumeFader-settings-{DateTime.Now:yyyyMMdd}.json",
            AddExtension = true,
            DefaultExt = "json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.Copy(SettingsPath, dialog.FileName, overwrite: true);
            MessageBox.Show(this, "Settings exported successfully.", "Export settings",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Program.Log(ex, "Exporting settings");
            MessageBox.Show(this, $"Settings could not be exported.\n\n{ex.Message}", "Export settings",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ImportSettings()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import ZMK Volume Fader settings",
            Filter = "ZMK Volume Fader settings (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var imported = JsonSerializer.Deserialize<Settings>(File.ReadAllText(dialog.FileName));
            if (imported == null) throw new InvalidDataException("The selected file does not contain settings.");
            if (imported.SchemaVersion > CurrentSettingsSchema)
                throw new InvalidDataException($"Settings schema {imported.SchemaVersion} is newer than this app supports ({CurrentSettingsSchema}).");
            if (MessageBox.Show(this,
                    "Importing will replace the current configuration and restart the app. Continue?",
                    "Import settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Directory.CreateDirectory(SettingsDir);
            string temp = SettingsPath + ".import";
            File.Copy(dialog.FileName, temp, overwrite: true);
            if (File.Exists(SettingsPath))
                File.Replace(temp, SettingsPath, SettingsPath + ".pre-import.bak", ignoreMetadataErrors: true);
            else
                File.Move(temp, SettingsPath);

            _exiting = true;
            Application.Restart();
        }
        catch (Exception ex)
        {
            Program.Log(ex, "Importing settings");
            MessageBox.Show(this, $"Settings could not be imported.\n\n{ex.Message}", "Import settings",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

    // All usages in a device's report descriptor, or null when it couldn't be
    // read (device busy/inaccessible) — distinct from "read fine, not ours" so a
    // transient failure doesn't permanently rule a device out below.
    static SortedSet<uint>? TryUsages(HidDevice d)
    {
        var usages = new SortedSet<uint>();
        try
        {
            foreach (var item in d.GetReportDescriptor().DeviceItems)
                foreach (var u in item.Usages.GetAllValues())
                    usages.Add(u);
            return usages;
        }
        catch { return null; }
    }

    // Per-device classification, cached by DevicePath. Discovery runs continuously
    // (every 1.5 s), and parsing a report descriptor (TryUsages) plus reading a
    // serial (DeviceKey) both open the device and allocate — doing it for every
    // HID device on every scan churns a lot of garbage that, at rest with no GC
    // pressure, visibly inflates the working set. Each device is inspected once;
    // the cache is pruned to currently-present paths so it stays bounded.
    // Discovery-thread only (no locking needed).
    sealed class DevInfo { public bool Match; public bool ByUsage; public bool OurVid; public string Key = ""; }
    readonly Dictionary<string, DevInfo> _devInfo = new();

    // Inspect a device once. null = transient read failure (don't cache — retry
    // next scan); Match=false = definitively not on our page; Match=true carries
    // its identity key + how it matched.
    static DevInfo? Classify(HidDevice d)
    {
        var us = TryUsages(d);
        if (us == null) return null;
        bool byUsage = us.Contains(FaderUsage);
        if (!byUsage && !us.Any(u => (u >> 16) == 0xFF00)) return new DevInfo { Match = false };
        return new DevInfo { Match = true, ByUsage = byUsage, OurVid = d.VendorID == VID, Key = DeviceKey(d) };
    }

    // Every fader-page device discovery should open right now: one per device
    // identity, filtered by ShouldMonitor. Matching keys on our vendor fader usage
    // (0xFF000001) rather than VID/PID lets the same unit be found over USB or BLE
    // (HID-over-GATT can assign a different product id). A page-only stranger is
    // skipped unless the user opted it in; an ignored unit is always skipped.
    List<(HidDevice Dev, string Key)> FindFaders()
    {
        var result = new List<(HidDevice, string)>();
        var present = new HashSet<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            string path = d.DevicePath;
            present.Add(path);
            if (!_devInfo.TryGetValue(path, out var info))
            {
                info = Classify(d);
                if (info == null) continue;   // transient — retry next scan, uncached
                _devInfo[path] = info;
            }
            if (!info.Match) continue;
            if (!seen.Add(info.Key)) continue;   // one reader per identity
            if (!ShouldMonitor(info.Key, info.ByUsage, info.OurVid)) continue;
            result.Add((d, info.Key));
        }
        // Forget devices that have gone away so the cache can't grow unbounded (and
        // a reconnect re-inspects).
        if (_devInfo.Count > present.Count)
            foreach (var stale in _devInfo.Keys.Where(k => !present.Contains(k)).ToList())
                _devInfo.Remove(stale);
        return result;
    }

    // A fader-page HID device the Devices dialog can monitor or ignore. Populated
    // by a full descriptor scan (unlike discovery's cached hot path) so the list
    // is always accurate when the dialog opens.
    internal sealed class FaderCandidate
    {
        public string Key = "";      // DeviceKey (VID:PID[:serial]) — the monitor/ignore identity
        public string Name = "";     // product name, best effort
        public int Vid, Pid;
        public string? Serial;
        public bool OurVid;          // matches our VID (0x1D50)
        public bool ByUsage;         // exposes our exact fader usage (vs only the 0xFF00 page)
    }

    // Every HID device currently on our vendor page, one row per identity. The
    // Devices dialog unions this with the persisted ignore set so a device can be
    // re-enabled even while unplugged.
    static List<FaderCandidate> EnumerateCandidates()
    {
        var list = new List<FaderCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            var us = TryUsages(d);
            if (us == null) continue;
            bool byUsage = us.Contains(FaderUsage);
            if (!byUsage && !us.Any(u => (u >> 16) == 0xFF00)) continue;
            string key = DeviceKey(d);
            if (!seen.Add(key)) continue;   // collapse the same unit seen twice (e.g. USB + BLE)
            string name; try { name = d.GetProductName(); } catch { name = ""; }
            string? serial; try { serial = d.GetSerialNumber(); } catch { serial = null; }
            list.Add(new FaderCandidate { Key = key, Name = name, Vid = d.VendorID, Pid = d.ProductID, Serial = serial, OurVid = d.VendorID == VID, ByUsage = byUsage });
        }
        return list;
    }

    // A discovery thread wakes on HidSharp device changes (plus a slow safety
    // retry) and opens monitored devices; each gets its own blocking reader thread.
    void StartHid()
    {
        _run = true;
        UpdateAggregateStatus();
        if (Program.DiagSynth) RequestFaderPump();
        _hidChanged = (_, _) =>
        {
            try { _discoveryWake.Set(); }
            catch (ObjectDisposedException) { } // notification already in flight at shutdown
        };
        DeviceList.Local.Changed += _hidChanged;
        _discoveryThread = new Thread(DiscoveryLoop) { IsBackground = true, Name = "fader-discovery" };
        _discoveryThread.Start();
    }

    void DiscoveryLoop()
    {
        while (_run)
        {
            try
            {
                foreach (var (dev, key) in FindFaders())
                {
                    lock (_readersLock) if (_readers.ContainsKey(key)) continue;
                    if (!dev.TryOpen(out HidStream stream)) continue;
                    StartReader(key, dev, stream);
                }
            }
            catch (Exception ex) { Program.LogRateLimited("hid-discovery", ex, "Discovering fader devices"); }
            // DeviceList.Changed wakes this immediately for plug/unplug. The slow
            // fallback covers a missed OS notification or a transient open failure
            // without continuously enumerating every HID device on the machine.
            _discoveryWake.WaitOne(_readerSnapshot.Length == 0 ? 10_000 : 60_000);
        }
    }

    void PublishReaderSnapshotLocked() => _readerSnapshot = _readers.Values.ToArray();

    void StartReader(string key, HidDevice dev, HidStream stream)
    {
        var reader = new Reader { Key = key, Stream = stream };
        try { reader.Name = dev.GetProductName(); } catch { reader.Name = "ZMK keyboard"; }
        int reportLen; try { reportLen = dev.GetMaxInputReportLength(); } catch { reportLen = 64; }
        lock (_readersLock)
        {
            _readers[key] = reader;
            PublishReaderSnapshotLocked();
        }
        reader.Thread = new Thread(() => ReaderLoop(reader, stream, reportLen)) { IsBackground = true, Name = $"fader-hid:{key}" };
        OnDeviceConnected(key, reader.Name);
        reader.Thread.Start();
    }

    void ReaderLoop(Reader reader, HidStream stream, int reportLen)
    {
        try
        {
            using (stream)
            {
                // Block until a report arrives. Cancellation is by closing the
                // stream (StopReader / StopReaders), which unblocks the read. A
                // *finite* ReadTimeout made HidSharp poll the device continuously,
                // waking the CPU out of idle hundreds of thousands of times a
                // second; a real-time power-monitoring ETW consumer couldn't drain
                // that, and its kernel reply queue ballooned to many GB of paged +
                // nonpaged pool (PoolMon tags EtwD / Etwr). Blocking cleanly avoids
                // the poll entirely.
                stream.ReadTimeout = System.Threading.Timeout.Infinite;
                var buf = new byte[reportLen];
                while (_run && !reader.Stop)
                {
                    // Dropped from monitoring in the Devices dialog while live:
                    // release it (the group is removed there separately).
                    if (_ignoredSnap.Contains(reader.Key) && !_allowedSnap.Contains(reader.Key)) break;
                    int n;
                    try { n = stream.Read(buf, 0, buf.Length); }
                    catch { break; }   // stream closed (cancel) or device gone
                    if (n > 0) Interlocked.Increment(ref _hidReportCount);
                    // Leak isolation: consume the report and do nothing — if the
                    // kernel-pool leak still runs in this mode, the HID read path
                    // alone is the trigger and nothing downstream matters.
                    if (Program.DiagSink) continue;
                    // Report id 2: up to eight 16-bit LE axes at bytes 1.. (two
                    // bytes each), then a button byte. Read whatever axes are
                    // present so any number of sliders (1..8) can be driven.
                    if (n >= 3 && buf[0] == 0x02)
                    {
                        int count = Math.Min(MaxAxes, (n - 1) / 2);
                        lock (reader.PendingLock)
                        {
                            for (int i = 0; i < count; i++)
                                reader.PendingAxes[i] = ReadAxis(buf, 1 + 2 * i);
                            reader.PendingCount = count;
                            reader.HasPending = true;
                        }
                        RequestFaderPump();
                    }
                }
            }
        }
        catch (Exception ex) { Program.LogRateLimited($"hid-reader:{reader.Key}", ex, "Reading a fader device"); }
        finally
        {
            lock (_readersLock)
            {
                if (_readers.TryGetValue(reader.Key, out var cur) && cur == reader) _readers.Remove(reader.Key);
                PublishReaderSnapshotLocked();
            }
            OnDeviceDisconnected(reader.Key);
            UpdateAggregateStatus();
            if (_run) _discoveryWake.Set();
        }
    }

    void StopHid()
    {
        _run = false;
        if (_hidChanged != null)
        {
            try { DeviceList.Local.Changed -= _hidChanged; } catch { }
            _hidChanged = null;
        }
        var readers = _readerSnapshot;
        StopReaders();
        _discoveryWake.Set();
        bool discoveryStopped = _discoveryThread?.Join(2_000) ?? true;
        _discoveryThread = null;

        foreach (var reader in readers) reader.Thread.Join(1_000);
        if (discoveryStopped) _discoveryWake.Dispose();
        try { HidSharp.Utility.HidSharpLibrary.ManualShutdown().WaitOne(2_000); }
        catch (Exception ex) { Program.LogRateLimited("hid-shutdown", ex, "Stopping HidSharp"); }
    }

    // Signal every reader to stop and close its stream (unblocking any pending
    // read) — called on shutdown so background threads exit promptly.
    void StopReaders()
    {
        var snapshot = _readerSnapshot;
        foreach (var r in snapshot) { r.Stop = true; try { r.Stream?.Close(); } catch { } }
    }

    // Footer status from the set of open readers: names when few, a count when many.
    void UpdateAggregateStatus()
    {
        var names = _readerSnapshot.Select(r => r.Name).ToList();
        if (names.Count == 0) { SetStatus("No fader device — plug one in…", false); return; }
        if (names.Count == 1) { SetStatus($"Connected · {names[0]}", true); return; }
        if (names.Count <= 3) { SetStatus($"Connected · {string.Join(", ", names)}", true); return; }
        SetStatus($"Connected · {names.Count} devices", true);
    }

    static int ReadAxis(byte[] b, int i) => (short)(b[i] | (b[i + 1] << 8));

    // UI thread, ~60 Hz: apply the newest axes each reader has stashed. Coalescing
    // here means a device spamming reports drives at most one update per tick
    // instead of marshaling + filtering + driving on every single report.
    void FaderPumpTick()
    {
        if (!IsHandleCreated) return;
        foreach (var r in _readerSnapshot)
        {
            int count;
            lock (r.PendingLock)
            {
                if (!r.HasPending) continue;
                count = r.PendingCount;
                Array.Copy(r.PendingAxes, r.SnapshotAxes, count);
                r.HasPending = false;
            }
            // Synth isolation: drain real reports but let the synthetic motion
            // below be the only thing driving the sliders.
            if (Program.DiagSynth) continue;
            if (!_rawByDevice.TryGetValue(r.Key, out var raw) || raw.Length != MaxAxes)
                _rawByDevice[r.Key] = raw = new int[MaxAxes];
            for (int i = 0; i < count; i++) raw[i] = r.SnapshotAxes[i];
            foreach (var s in _sliders)
                if (s.GroupKey == r.Key && s.AxisIndex >= 0 && s.AxisIndex < count)
                    ApplyAxis(s, r.SnapshotAxes[s.AxisIndex]);
        }
        if (Program.DiagSynth) SynthTick();
        FlushPendingVolumes();
        if (!Program.DiagSynth && !PumpHasWork())
        {
            _faderPump.Stop();
            _pumpActive = false;
            // Close the race where a reader published immediately before the flag
            // changed but saw the pump as active and therefore did not queue a wake.
            if (PumpHasWork()) RequestFaderPump();
        }
    }

    // Wake the UI pump only when work exists. At rest there is no permanent 60 Hz
    // WinForms timer wakeup; report floods are still coalesced by Reader.Pending.
    void RequestFaderPump()
    {
        if (_pumpActive || Interlocked.Exchange(ref _pumpStartQueued, 1) != 0) return;
        SafeUi(() =>
        {
            Interlocked.Exchange(ref _pumpStartQueued, 0);
            if (_pumpActive) return;
            _pumpActive = true;
            _faderPump.Start();
        });
    }

    bool PumpHasWork()
    {
        if (_sliders.Any(a => a.VolPending >= 0)) return true;
        return _readerSnapshot.Any(r => r.HasPending);
    }

    // Synthetic fader motion (--diag-synth): sweep every physical slider through
    // a 2..30% triangle (~1 s per cycle) injected below the HID layer, so the
    // draw / tray / volume channels run at full per-tick rate with the hardware
    // untouched. Attributes the kernel-pool leak without hand-riding a fader.
    double _synthPhase;
    void SynthTick()
    {
        _synthPhase += _faderPump.Interval / 1000.0;
        double t = _synthPhase % 1.0;
        double pct = 2 + (t < 0.5 ? t * 2 : (1 - t) * 2) * 28;
        foreach (var s in _sliders)
            if (!s.IsVirtual && s.AxisIndex >= 0 && s.Curve.Length > 0)
                ApplyAxis(s, (int)Math.Round(Calibration.InvEval(s.Curve, pct)));
    }

    // A raw jump beyond this (mV) is real movement, not noise — track it exactly.
    // Wiper noise is ~±15 mV; the firmware's rest band is 30 mV.
    const int SnapBand = 60;
    // The mute dead zone unlatches only this far (mV) above its threshold, so
    // boundary noise can't toggle the mute.
    const int MuteExitBand = 15;

    void ApplyAxis(Axis a, int raw)
    {
        a.LastRaw = raw;

        // EMA smooth (16-bit value is finer but noisier, ~+/-15 mV) — but only
        // within the noise band. Reports arrive per *change* (plus a slow
        // heartbeat at rest), so easing a big jump would leave Sm far from the
        // final value when the reports stop: a fast pull to the end then crept
        // toward 0% at heartbeat rate and could stall inside the hysteresis
        // band. Movement past the noise floor snaps; smoothing only ever
        // filters idle flicker.
        a.Sm = a.Sm < 0 || Math.Abs(raw - a.Sm) > SnapBand
            ? raw
            : a.Sm * 0.85 + raw * 0.15;
        Render(a, takeOwnership: true);
    }

    bool IsFaderConnected(Axis a) =>
        a.IsVirtual || _groups.FirstOrDefault(g => g.Key == a.GroupKey)?.Connected == true;

    void TakeTargetOwnership(Axis a) => a.DriveSequence = ++_nextDriveSequence;

    // Is this slider actually driving something right now? False if the unit is
    // unplugged, an output target isn't present, or an app/category target has no
    // live session. Drives the muted (greyed) look on the fader bar.
    bool Drivable(Axis a)
    {
        // Virtual faders don't depend on the dongle, so a disconnect doesn't grey
        // them; they still grey when their app/category target isn't playing.
        if (!IsFaderConnected(a)) return false;
        switch (a.Target)
        {
            case TargetKind.App:
                return a.AppKey != null && _liveApps.Contains(a.AppKey);
            case TargetKind.Category:
                if (a.CategoryName == UnassignedCategory) return UnassignedKeys().Any();
                var c = _categories.FirstOrDefault(x => x.Name == a.CategoryName);
                return c != null && c.AppKeys.Any(_liveApps.Contains);
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

    void Render(Axis a, bool takeOwnership = false)
    {
        // no-draw isolation: the muted tint / % color are paint work too.
        if (!Program.DiagNoDraw) UpdateSliderState(a);
        if (!a.IsVirtual && a.Sm < 0) return;

        // Virtual faders drag straight to the final volume (no taper curve, no cap);
        // physical faders map their smoothed raw value through the calibration curve.
        double faderPct = a.IsVirtual ? a.VCur : Calibration.Eval(a.Curve, (int)Math.Round(a.Sm));
        // Mute dead zone (physical): gate on the *instantaneous* raw reading so
        // crossing the threshold mutes on that very report — the smoothed value
        // lags on a slow pull and left the output hovering at 1% until it caught
        // up. Latched with an exit band (threshold + noise) so wiper jitter at
        // the boundary can't flicker the mute on and off.
        if (!a.IsVirtual)
        {
            if (a.Cal.MuteRaw > 0)
            {
                if (a.LastRaw < a.Cal.MuteRaw) a.InMuteZone = true;
                else if (a.LastRaw > a.Cal.MuteRaw + MuteExitBand) a.InMuteZone = false;
            }
            else a.InMuteZone = false;
            if (a.InMuteZone) faderPct = 0;
        }
        int cap = a.IsVirtual ? 100 : (int)a.Limit.Value;
        double pf = Math.Clamp(faderPct * cap / 100.0, 0, 100);

        if (!a.IsVirtual && a.VMuted) pf = 0;

        if (!a.IsVirtual && a.PickupArmed && !a.VMuted)
        {
            int position = (int)Math.Round(pf);
            a.Bar.PickupPosition = position;
            if (!a.PickupReady)
            {
                if (!Program.DiagNoDraw)
                {
                    a.Pct.Text = "Syncing…";
                    a.Pct.ForeColor = _theme.Accent;
                }
                return;
            }

            int? previous = a.PickupPosition;
            a.PickupPosition = position;
            bool reached = PickupLogic.HasReached(previous, position, a.PickupTarget);
            if (!Program.DiagNoDraw)
            {
                a.Bar.Value = a.PickupTarget;
                a.Pct.Text = $"Pickup {a.PickupTarget}%";
                a.Pct.ForeColor = _theme.Accent;
                _tip.SetToolTip(a.Bar, $"Move the physical fader to {a.PickupTarget}% to take control");
            }
            if (!reached) return;

            CancelPickup(a);
            a.LastApplied = -1;
        }

        // Hysteresis: hold the current integer % until pf moves > Hyst off it.
        int applied = a.LastApplied < 0 || Math.Abs(pf - a.LastApplied) > Hyst
            ? (int)Math.Round(pf)
            : a.LastApplied;

        if (!Program.DiagNoDraw)
        {
            a.Bar.Value = applied;
            a.Pct.Text = $"{applied}%";
        }

        if (applied == a.LastApplied) return;
        if (takeOwnership) TakeTargetOwnership(a);
        a.LastApplied = applied;
        if (!Program.DiagNoDraw && !Program.DiagNoTray) UpdateTrayText();
        if (_calibrating) return;   // visualize, but don't drive anything while calibrating
        if (Program.DiagNoVolume) return;   // leak isolation: never send a volume set

        // Queue a logical change. The audio worker coalesces complete desired maps
        // and limits actual Windows calls after app/category session fan-out.
        a.VolPending = applied;
        RequestFaderPump();
    }

    // Minimum gap between desired-state publications per slider (~20/s). Actual
    // Core Audio calls have a separate global ceiling in AudioController.
    const int MinVolWriteMs = 50;

    // Publish each slider's newest desired value once it leaves this window. The
    // pump stays awake until the final settled value has been handed off.
    void FlushPendingVolumes()
    {
        long now = Environment.TickCount64;
        bool changed = false;
        foreach (var a in _sliders)
        {
            if (a.VolPending < 0 || now - a.VolLastWrite < MinVolWriteMs) continue;
            a.VolPending = -1;
            a.VolLastWrite = now;
            changed = true;
        }
        if (changed) PublishDesiredVolumes();
    }


    // Build one complete logical target map. Replacing the previous map clears
    // stale targets after reassignment/removal. Dictionary assignment preserves
    // the existing "last fader wins" behavior when target groups overlap.
    void PublishDesiredVolumes()
    {
        if (_audio == null) return;
        var endpoints = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var apps = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        if (!Program.DiagNoVolume)
        {
            // Older owners are written first; the most recently manipulated
            // fader wins if two logical targets overlap.
            foreach (var a in _sliders.OrderBy(a => a.DriveSequence))
            {
                if (a.LastApplied < 0 || !IsFaderConnected(a) || a.PickupArmed) continue;
                float scalar = a.LastApplied / 100f;
                if (a.Target == TargetKind.Output)
                {
                    string? id = Resolve(a);
                    if (id != null) endpoints[id] = scalar;
                }
                else if (a.Target == TargetKind.App)
                {
                    if (a.AppKey != null && _liveApps.Contains(a.AppKey)) apps[a.AppKey] = scalar;
                }
                else if (a.CategoryName == UnassignedCategory)
                {
                    foreach (var key in UnassignedKeys()) apps[key] = scalar;
                }
                else
                {
                    var category = _categories.FirstOrDefault(c => c.Name == a.CategoryName);
                    if (category != null)
                        foreach (var key in category.AppKeys.Where(_liveApps.Contains)) apps[key] = scalar;
                }
            }
        }
        _audio.SetDesiredVolumes(endpoints, apps);
        Interlocked.Increment(ref _desiredSnapshotCount);
    }

    void SetStatus(string text, bool connected)
    {
        lock (_statusPostLock)
        {
            if (_lastStatusText == text && _lastStatusConnected == connected) return;
            _lastStatusText = text;
            _lastStatusConnected = connected;
        }
        SafeUi(() =>
        {
            _connText = text;
            _connected = connected;
            RefreshStatus();
        });
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
        if (Program.DiagNoTray) return;
        string body = _connected
            ? string.Join(" · ", _sliders.Select(s => $"{s.Bar.Value}%"))
            : "Disconnected";
        string t = $"ZMK Volume Fader\n{body}";
        t = t.Length <= 63 ? t : t[..63];
        if (t == _pendingTrayText || t == _lastTrayText) return;
        _pendingTrayText = t;
        if (!_trayUpdate.Enabled) _trayUpdate.Start();
    }

    void FlushTrayText()
    {
        string? text = _pendingTrayText;
        _pendingTrayText = null;
        _trayUpdate.Stop();
        if (text == null || text == _lastTrayText) return;
        _tray.Text = text;
        _lastTrayText = text;
    }
}
