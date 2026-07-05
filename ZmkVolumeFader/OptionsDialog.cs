using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Modal options dialog. A General section toggles "start with Windows" and the
/// light/dark/auto theme; below it, each slider records its raw min/max and picks
/// the taper preset that matches the pot, with a live preview as you move it. The
/// fader list scrolls for many sliders; Save/Cancel stay pinned. The owner reads
/// back <see cref="SelectedTheme"/>, <see cref="StartWithWindows"/>, and the edited
/// <see cref="Cals"/>/<see cref="Outputs"/> on a Save result.
/// </summary>
sealed class OptionsDialog : Form
{
    public Calibration[] Cals => _cal;
    public List<OutputPref>[] Outputs => _outputs;
    public bool StartWithWindows => _startup.Checked;
    public ThemeMode SelectedTheme => (ThemeMode)Math.Clamp(_themeCombo.SelectedIndex, 0, 2);
    // Set when the user clicks "Set up sliders…"; the owner runs the wizard.
    public bool SetupRequested { get; private set; }

    readonly int _n;
    readonly Calibration[] _cal;
    readonly Func<int>[] _raw;
    readonly List<OutputPref>[] _outputs;
    readonly string[] _labels;
    readonly IReadOnlyList<OutputPref> _known;
    readonly string[] _presentIds;
    readonly MainForm.Theme _t;

    readonly Label[] _rawLbl, _rangeLbl, _previewLbl;
    readonly RoundedButton[] _recordBtn;
    readonly RoundedComboBox[] _taper;
    readonly MainForm.FaderBar[] _bar;
    readonly bool[] _recording;

    readonly CheckBox _startup = new() { Text = "Start with Windows", AutoSize = true, FlatStyle = FlatStyle.Standard, Margin = new Padding(0, 0, 0, 0) };
    readonly RoundedComboBox _themeCombo = new() { Width = 190 };

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 50 };
    readonly ToolTip _tip = new();
    TableLayoutPanel _root = null!;
    FlowLayoutPanel _btnRow = null!;

    static readonly string[] TaperItems = { "Linear pot", "Audio pot", "Straight" };
    static readonly string[] ThemeItems = { "Auto (follow Windows)", "Light", "Dark" };

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public OptionsDialog(MainForm.Theme theme, ThemeMode themeMode, bool startWithWindows,
        Calibration[] cals, Func<int>[] raws, List<OutputPref>[] outs, string[] labels,
        IReadOnlyList<OutputPref> known, IEnumerable<string> presentIds)
    {
        _t = theme;
        _n = cals.Length;
        _cal = cals;
        _raw = raws;
        _outputs = outs;
        _labels = labels;
        _known = known;
        _presentIds = presentIds.ToArray();
        _rawLbl = new Label[_n]; _rangeLbl = new Label[_n]; _previewLbl = new Label[_n];
        _recordBtn = new RoundedButton[_n]; _taper = new RoundedComboBox[_n];
        _bar = new MainForm.FaderBar[_n]; _recording = new bool[_n];

        Text = "Options";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 700);
        BackColor = _t.Window;

        // Scrollable content (General + calibration label + N faders + About),
        // with Save/Cancel pinned below.
        int rows = 3 + _n;
        _root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 1, RowCount = rows, BackColor = Color.Transparent };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < rows; r++) _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _root.Controls.Add(BuildGeneral(themeMode, startWithWindows), 0, 0);
        _root.Controls.Add(new Label
        {
            Text = "Calibration — hit Record, sweep the fader fully end-to-end, then stop. Then pick the taper that matches your pot. The preview updates as you move it.",
            AutoSize = true, MaximumSize = new Size(392, 0), ForeColor = _t.Subtle, Margin = new Padding(2, 4, 2, 10),
        }, 0, 1);
        for (int i = 0; i < _n; i++) _root.Controls.Add(BuildFader(i, _labels[i]), 0, 2 + i);
        _root.Controls.Add(BuildAbout(), 0, 2 + _n);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(14, 14, 14, 0) };
        scroll.Controls.Add(_root);

        _btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent, Padding = new Padding(14, 8, 14, 12) };
        var save = MakeButton("Save", accent: true);
        save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = MakeButton("Cancel", accent: false);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _btnRow.Controls.Add(save);
        _btnRow.Controls.Add(cancel);

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(scroll, 0, 0);
        outer.Controls.Add(_btnRow, 0, 1);
        Controls.Add(outer);

        AcceptButton = save;
        CancelButton = cancel;

        _tick.Tick += (_, _) => { for (int i = 0; i < _n; i++) Tick(i); };
        Load += (_, _) => { ApplyDark(); FitHeight(); _tick.Start(); };
        FormClosing += (_, _) => _tick.Stop();
    }

    // Size the dialog to its content up to a cap; the fader list scrolls beyond.
    void FitHeight()
    {
        int content = _root.PreferredSize.Height + 14;      // scroll padding
        int buttons = _btnRow.PreferredSize.Height;
        ClientSize = new Size(ClientSize.Width, Math.Clamp(content + buttons, 300, 720));
    }

    Control BuildAbout()
    {
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 2; r++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        t.Controls.Add(new Label
        {
            Text = $"ZMK Volume Fader  {MainForm.VersionText()}"
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
        var card = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 132, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(12), BackColor = _t.Card };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 3; r++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _startup.ForeColor = _t.Text;
        _startup.BackColor = Color.Transparent;
        _startup.Checked = startup;
        t.Controls.Add(_startup, 0, 0);

        var themeRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        themeRow.Controls.Add(new Label { Text = "Theme", AutoSize = true, ForeColor = _t.Subtle, Margin = new Padding(0, 6, 8, 0) });
        _themeCombo.BackColor = _t.CtlBg;
        _themeCombo.ForeColor = _t.Text;
        _themeCombo.Surround = _t.Card; _themeCombo.BoxColor = _t.CtlBg; _themeCombo.BorderColor = _t.CtlBorder; _themeCombo.ChevronColor = _t.Subtle;
        _themeCombo.Items.AddRange(ThemeItems);
        _themeCombo.SelectedIndex = Math.Clamp((int)mode, 0, ThemeItems.Length - 1);
        themeRow.Controls.Add(_themeCombo);
        t.Controls.Add(themeRow, 0, 1);

        // Setup wizard + the ranked output-fallback editor, side by side.
        var btnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        var setupBtn = MakeButton("Set up sliders…", accent: false, surround: _t.Card);
        setupBtn.Margin = new Padding(0);
        setupBtn.Click += (_, _) => { SetupRequested = true; DialogResult = DialogResult.OK; Close(); };
        var outBtn = MakeButton("Set Default Outputs…", accent: false, surround: _t.Card);
        outBtn.Margin = new Padding(8, 0, 0, 0);
        outBtn.Click += (_, _) => OpenOutputs();
        btnRow.Controls.Add(setupBtn);
        btnRow.Controls.Add(outBtn);
        t.Controls.Add(btnRow, 0, 2);

        card.Controls.Add(t);
        return card;
    }

    void OpenOutputs()
    {
        using var dlg = new OutputsDialog(_t, _labels, _outputs, _known, _presentIds);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            for (int i = 0; i < _n; i++) _outputs[i] = dlg.Result[i];
    }

    Panel BuildFader(int i, string name)
    {
        int idx = i;
        var card = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 164, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12), BackColor = _t.Card };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int r = 0; r < 4; r++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        t.Controls.Add(new Label { Text = name, AutoSize = true, ForeColor = _t.Subtle, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 0) }, 0, 0);
        _rawLbl[i] = new Label { Text = "raw —", AutoSize = true, ForeColor = _t.Text, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 11f) };
        t.Controls.Add(_rawLbl[i], 1, 0);

        _rangeLbl[i] = new Label { Text = "range —", AutoSize = true, ForeColor = _t.Text, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 0, 0) };
        _recordBtn[i] = MakeButton("Record", false, _t.Card);
        _recordBtn[i].Click += (_, _) => ToggleRecord(idx);
        t.Controls.Add(_rangeLbl[i], 0, 1);
        t.Controls.Add(_recordBtn[i], 1, 1);

        var taperPanel = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 4) };
        taperPanel.Controls.Add(new Label { Text = "Taper", AutoSize = true, ForeColor = _t.Subtle, Margin = new Padding(0, 6, 8, 0) });
        _taper[i] = new RoundedComboBox { Width = 150, BackColor = _t.CtlBg, ForeColor = _t.Text, Surround = _t.Card, BoxColor = _t.CtlBg, BorderColor = _t.CtlBorder, ChevronColor = _t.Subtle };
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

    RoundedButton MakeButton(string text, bool accent, Color? surround = null)
    {
        var b = new RoundedButton { Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(6, 0, 0, 0), Surround = surround ?? _t.Window };
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
