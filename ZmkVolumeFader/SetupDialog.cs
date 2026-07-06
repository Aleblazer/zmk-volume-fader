using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Guided per-device setup. The user moves each fader fully bottom-to-top; the
/// dialog watches all HID axes, binds the one that actually travels to the next
/// slider, and captures its min/max. "No more faders" finishes. <see cref="Result"/>
/// is the ordered list of (axis, min, max); the owner turns it into slider configs.
/// </summary>
sealed class SetupDialog : Form
{
    public List<(int Axis, int Min, int Max)> Result { get; } = new();

    readonly MainForm.Theme _t;
    readonly Func<int[]> _rawAxes;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 40 };

    const int MaxAxes = 6;
    const int TravelThreshold = 1500;   // mV of sweep before we count a fader as "moved"

    readonly HashSet<int> _used = new();
    readonly int[] _min = new int[MaxAxes];
    readonly int[] _max = new int[MaxAxes];
    bool _haveBaseline;
    int _candidate = -1;

    readonly Label _title = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 30, Margin = new Padding(0, 0, 0, 6), Font = new Font("Segoe UI", 12.5f, FontStyle.Bold) };
    readonly Label _detail = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 84, Margin = new Padding(0) };
    readonly RoundedButton _next, _finish, _cancel;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public SetupDialog(MainForm.Theme t, Func<int[]> rawAxes)
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        _t = t;
        _rawAxes = rawAxes;

        Text = "Set Up Sliders";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 220);
        BackColor = _t.Window;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // title
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // detail
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // spacer
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // buttons

        _title.ForeColor = _t.Text;
        _detail.ForeColor = _t.Subtle;
        root.Controls.Add(_title, 0, 0);
        root.Controls.Add(_detail, 0, 1);

        var btnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        _next = MakeButton("Next fader ▶", accent: true);
        _next.Click += (_, _) => AcceptCandidate();
        _finish = MakeButton("No more faders", accent: false);
        _finish.Click += (_, _) => Finish();
        _cancel = MakeButton("Cancel", accent: false);
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        btnRow.Controls.Add(_next);
        btnRow.Controls.Add(_finish);
        btnRow.Controls.Add(_cancel);
        root.Controls.Add(btnRow, 0, 3);

        Controls.Add(root);
        CancelButton = _cancel;

        StartStep();
        _tick.Tick += (_, _) => Poll();
        Load += (_, _) => { ApplyDark(); _tick.Start(); };
        FormClosing += (_, _) => _tick.Stop();
    }

    int Captured => Result.Count;

    void StartStep()
    {
        _haveBaseline = false;
        _candidate = -1;
        _next.Enabled = false;
        UpdateText();
    }

    void Poll()
    {
        var axes = _rawAxes();
        if (axes.Length < MaxAxes) return;   // wait for a full report

        if (!_haveBaseline)
        {
            for (int a = 0; a < MaxAxes; a++) _min[a] = _max[a] = axes[a];
            _haveBaseline = true;
        }

        int best = -1, bestTravel = 0;
        for (int a = 0; a < MaxAxes; a++)
        {
            if (_used.Contains(a)) continue;
            if (axes[a] < _min[a]) _min[a] = axes[a];
            if (axes[a] > _max[a]) _max[a] = axes[a];
            int travel = _max[a] - _min[a];
            if (travel > bestTravel) { bestTravel = travel; best = a; }
        }

        _candidate = bestTravel >= TravelThreshold ? best : -1;
        _next.Enabled = _candidate >= 0;
        UpdateText();
    }

    void UpdateText()
    {
        int n = Captured + 1;
        _title.Text = $"Fader {n}";
        _detail.Text = _candidate >= 0
            ? $"Detected on axis {_candidate + 1} — range {_min[_candidate]}–{_max[_candidate]} mV.\n\n" +
              "Sweep it fully bottom→top to capture both ends, then “Next fader”.\n" +
              "Or “No more faders” if that was the last one."
            : $"Move fader {n} fully from bottom to top.\n\n" +
              (Captured > 0 ? $"{Captured} captured so far.  " : "") +
              "Click “No more faders” once you've done them all.";
    }

    void AcceptCandidate()
    {
        if (_candidate < 0) return;
        Result.Add((_candidate, _min[_candidate], _max[_candidate]));
        _used.Add(_candidate);
        if (_used.Count >= MaxAxes) { Finish(); return; }
        StartStep();
    }

    void Finish()
    {
        DialogResult = Result.Count > 0 ? DialogResult.OK : DialogResult.Cancel;
        Close();
    }

    RoundedButton MakeButton(string text, bool accent)
    {
        var b = new RoundedButton { Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(6, 0, 0, 0), Surround = _t.Window };
        if (accent) { b.BackColor = _t.Accent; b.ForeColor = AccentText(); b.FlatAppearance.BorderColor = _t.Accent; }
        else { b.BackColor = _t.CtlBg; b.ForeColor = _t.Text; b.FlatAppearance.BorderColor = _t.CtlBorder; }
        return b;
    }

    Color AccentText()
    {
        var a = _t.Accent;
        double lum = (0.299 * a.R + 0.587 * a.G + 0.114 * a.B) / 255.0;
        return lum > 0.55 ? Color.FromArgb(0x10, 0x18, 0x12) : Color.White;
    }

    void ApplyDark()
    {
        int v = _t.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
    }
}
