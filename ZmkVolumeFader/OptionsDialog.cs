using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Modal options dialog. A General section toggles "start with Windows" and the
/// light/dark/auto theme; below it, each fader records its raw min/max and picks
/// the taper preset that matches the pot, with a live preview as you move it.
/// The owner reads back <see cref="SelectedTheme"/>, <see cref="StartWithWindows"/>,
/// and the edited <see cref="LeftCal"/>/<see cref="RightCal"/> on a Save result.
/// </summary>
sealed class OptionsDialog : Form
{
    public Calibration LeftCal => _cal[0];
    public Calibration RightCal => _cal[1];
    public bool StartWithWindows => _startup.Checked;
    public ThemeMode SelectedTheme => (ThemeMode)Math.Clamp(_themeCombo.SelectedIndex, 0, 2);

    readonly Calibration[] _cal;
    readonly Func<int>[] _raw;
    readonly MainForm.Theme _t;

    readonly Label[] _rawLbl = new Label[2];
    readonly Label[] _rangeLbl = new Label[2];
    readonly Label[] _previewLbl = new Label[2];
    readonly Button[] _recordBtn = new Button[2];
    readonly ComboBox[] _taper = new ComboBox[2];
    readonly MainForm.FaderBar[] _bar = new MainForm.FaderBar[2];
    readonly bool[] _recording = new bool[2];

    readonly CheckBox _startup = new() { Text = "Start with Windows", AutoSize = true, FlatStyle = FlatStyle.Standard, Margin = new Padding(0, 0, 0, 0) };
    readonly ComboBox _themeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 50 };
    readonly ToolTip _tip = new();

    static readonly string[] TaperItems = { "Linear pot", "Audio pot", "Straight" };
    static readonly string[] ThemeItems = { "Auto (follow Windows)", "Light", "Dark" };

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public OptionsDialog(MainForm.Theme theme, ThemeMode themeMode, bool startWithWindows,
        Calibration left, Calibration right, Func<int> rawL, Func<int> rawR)
    {
        _t = theme;
        _cal = new[] { left, right };
        _raw = new[] { rawL, rawR };

        Text = "Options";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 652);
        BackColor = _t.Window;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 6; r++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildGeneral(themeMode, startWithWindows), 0, 0);
        root.Controls.Add(new Label
        {
            Text = "Calibration — hit Record, sweep the fader fully end-to-end, then stop. Then pick the taper that matches your pot. The preview updates as you move it.",
            AutoSize = true, MaximumSize = new Size(398, 0), ForeColor = _t.Subtle, Margin = new Padding(2, 4, 2, 10),
        }, 0, 1);
        root.Controls.Add(BuildFader(0, "Left fader"), 0, 2);
        root.Controls.Add(BuildFader(1, "Right fader"), 0, 3);

        var btnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        var save = MakeButton("Save", accent: true);
        save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = MakeButton("Cancel", accent: false);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        btnRow.Controls.Add(save);
        btnRow.Controls.Add(cancel);
        root.Controls.Add(btnRow, 0, 4);

        root.Controls.Add(BuildAbout(), 0, 5);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        _tick.Tick += (_, _) => { Tick(0); Tick(1); };
        Load += (_, _) => { ApplyDark(); _tick.Start(); };
        FormClosing += (_, _) => _tick.Stop();
    }

    Control BuildAbout()
    {
        var ver = GetType().Assembly.GetName().Version;
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 2; r++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        t.Controls.Add(new Label
        {
            Text = (ver is null ? "ZMK Volume Fader" : $"ZMK Volume Fader  v{ver.Major}.{ver.Minor}.{ver.Build}")
                 + "\nVibecoded by Aleblazer of Split Logic Keyboards",
            AutoSize = false, Dock = DockStyle.Fill, Height = 34, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = _t.Subtle, Font = new Font("Segoe UI", 8.25f), Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);

        var icons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.None, BackColor = Color.Transparent, Margin = new Padding(0) };
        var gh = new LinkIcon("https://github.com/Aleblazer/zmk-volume-fader", Icons.Path(Icons.GitHub))
        {
            Width = 24, Height = 24, Margin = new Padding(6, 0, 6, 0),
            BackColor = _t.Window, IconColor = _t.Subtle, HoverColor = _t.Text,
        };
        _tip.SetToolTip(gh, "View the project on GitHub");
        icons.Controls.Add(gh);

        var logo = Icons.LoadEmbedded("splitlogic.png");
        if (logo != null)
        {
            var sl = new LinkIcon("https://www.splitlogic.xyz", logo)
            {
                Height = 24, Width = (int)Math.Round(24.0 * logo.Width / logo.Height),
                Margin = new Padding(6, 0, 6, 0), BackColor = _t.Window,
            };
            _tip.SetToolTip(sl, "Split Logic Keyboards — splitlogic.xyz");
            icons.Controls.Add(sl);
        }

        t.Controls.Add(icons, 0, 1);
        return t;
    }

    Panel BuildGeneral(ThemeMode mode, bool startup)
    {
        var card = new Panel { Width = 398, Height = 90, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(12), BackColor = _t.Card };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 2; r++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _startup.ForeColor = _t.Text;
        _startup.BackColor = Color.Transparent;
        _startup.Checked = startup;
        t.Controls.Add(_startup, 0, 0);

        var themeRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        themeRow.Controls.Add(new Label { Text = "Theme", AutoSize = true, ForeColor = _t.Subtle, Margin = new Padding(0, 6, 8, 0) });
        _themeCombo.BackColor = _t.CtlBg;
        _themeCombo.ForeColor = _t.Text;
        _themeCombo.Items.AddRange(ThemeItems);
        _themeCombo.SelectedIndex = Math.Clamp((int)mode, 0, ThemeItems.Length - 1);
        themeRow.Controls.Add(_themeCombo);
        t.Controls.Add(themeRow, 0, 1);

        card.Controls.Add(t);
        return card;
    }

    Panel BuildFader(int i, string name)
    {
        int idx = i;
        var card = new Panel { Width = 398, Height = 164, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12), BackColor = _t.Card };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int r = 0; r < 4; r++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        t.Controls.Add(new Label { Text = name, AutoSize = true, ForeColor = _t.Subtle, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 0) }, 0, 0);
        _rawLbl[i] = new Label { Text = "raw —", AutoSize = true, ForeColor = _t.Text, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 11f) };
        t.Controls.Add(_rawLbl[i], 1, 0);

        _rangeLbl[i] = new Label { Text = "range —", AutoSize = true, ForeColor = _t.Text, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 0, 0) };
        _recordBtn[i] = MakeButton("Record", false);
        _recordBtn[i].Click += (_, _) => ToggleRecord(idx);
        t.Controls.Add(_rangeLbl[i], 0, 1);
        t.Controls.Add(_recordBtn[i], 1, 1);

        var taperPanel = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 4) };
        taperPanel.Controls.Add(new Label { Text = "Taper", AutoSize = true, ForeColor = _t.Subtle, Margin = new Padding(0, 6, 8, 0) });
        _taper[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, BackColor = _t.CtlBg, ForeColor = _t.Text };
        _taper[i].Items.AddRange(TaperItems);
        _taper[i].SelectedIndex = Math.Clamp((int)_cal[i].Taper, 0, TaperItems.Length - 1);
        _cal[i].Taper = (TaperKind)_taper[i].SelectedIndex;   // normalize any stale value
        _taper[i].SelectedIndexChanged += (_, _) => OnTaperChanged(idx);
        taperPanel.Controls.Add(_taper[i]);
        t.Controls.Add(taperPanel, 0, 2);
        t.SetColumnSpan(taperPanel, 2);

        _bar[i] = new MainForm.FaderBar
        {
            Height = 22, ShowTicks = false, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 8, 8, 2),
            Track = _t.Inset, Fill = _t.Accent, BackColor = _t.Card, Tick = _t.Subtle,
            Knob = _t.Dark ? Color.FromArgb(0xE6, 0xE8, 0xEB) : Color.White,
            KnobEdge = _t.Dark ? Color.FromArgb(0x0E, 0x10, 0x14) : Color.FromArgb(0xC2, 0xC6, 0xCC),
        };
        _previewLbl[i] = new Label { Text = "0%", AutoSize = true, ForeColor = _t.Accent, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 11f) };
        t.Controls.Add(_bar[i], 0, 3);
        t.Controls.Add(_previewLbl[i], 1, 3);

        card.Controls.Add(t);
        return card;
    }

    Button MakeButton(string text, bool accent)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 5, 10, 5), Margin = new Padding(6, 0, 0, 0) };
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

    void ToggleRecord(int i)
    {
        _recording[i] = !_recording[i];
        if (_recording[i])
        {
            _cal[i].Min = int.MaxValue;
            _cal[i].Max = int.MinValue;
            _recordBtn[i].Text = "Stop";
        }
        else
        {
            if (_cal[i].Min > _cal[i].Max) { _cal[i].Min = 0; _cal[i].Max = 3250; }  // nothing swept
            _recordBtn[i].Text = "Record";
        }
    }

    void OnTaperChanged(int i) => _cal[i].Taper = (TaperKind)_taper[i].SelectedIndex;

    void Tick(int i)
    {
        int v = _raw[i]();
        _rawLbl[i].Text = $"raw {v}";
        if (_recording[i])
        {
            if (v < _cal[i].Min) _cal[i].Min = v;
            if (v > _cal[i].Max) _cal[i].Max = v;
        }
        _rangeLbl[i].Text = _cal[i].Min <= _cal[i].Max ? $"range {_cal[i].Min} – {_cal[i].Max}" : "range —";

        int p = Math.Clamp((int)Math.Round(Calibration.Eval(_cal[i].BuildCurve(), v)), 0, 100);
        _previewLbl[i].Text = $"{p}%";
        _bar[i].Value = p;
    }

    void ApplyDark()
    {
        int v = _t.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
        foreach (var c in _taper)
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, _t.Dark ? "DarkMode_CFD" : null, null);
        if (_themeCombo.IsHandleCreated) SetWindowTheme(_themeCombo.Handle, _t.Dark ? "DarkMode_CFD" : null, null);
    }
}
