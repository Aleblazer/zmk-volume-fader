using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Modal calibration dialog. Per fader: record the raw min/max by sweeping, then
/// pick the taper preset that matches the pot. A live preview shows the resulting
/// volume as you move it. The edited <see cref="LeftCal"/>/<see cref="RightCal"/>
/// calibrations are read back by the owner on a Save result.
/// </summary>
sealed class CalibrationDialog : Form
{
    public Calibration LeftCal => _cal[0];
    public Calibration RightCal => _cal[1];

    readonly Calibration[] _cal;
    readonly Func<int>[] _raw;
    readonly MainForm.Theme _t;

    readonly Label[] _rawLbl = new Label[2];
    readonly Label[] _rangeLbl = new Label[2];
    readonly Label[] _previewLbl = new Label[2];
    readonly Button[] _recordBtn = new Button[2];
    readonly ComboBox[] _taper = new ComboBox[2];
    readonly Panel[] _barFill = new Panel[2];
    readonly bool[] _recording = new bool[2];

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 50 };

    static readonly string[] TaperItems = { "Linear pot", "Audio pot", "Straight" };

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public CalibrationDialog(MainForm.Theme theme, Calibration left, Calibration right, Func<int> rawL, Func<int> rawR)
    {
        _t = theme;
        _cal = new[] { left, right };
        _raw = new[] { rawL, rawR };

        Text = "Calibrate faders";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 460);
        BackColor = _t.Window;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 4; r++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            Text = "Hit Record, sweep the fader fully end-to-end, then stop. Then pick the taper that matches your pot. The preview updates as you move it.",
            AutoSize = true, MaximumSize = new Size(398, 0), ForeColor = _t.Subtle, Margin = new Padding(2, 0, 2, 10),
        }, 0, 0);
        root.Controls.Add(BuildFader(0, "Left fader"), 0, 1);
        root.Controls.Add(BuildFader(1, "Right fader"), 0, 2);

        var btnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        var save = MakeButton("Save", accent: true);
        save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = MakeButton("Cancel", accent: false);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        btnRow.Controls.Add(save);
        btnRow.Controls.Add(cancel);
        root.Controls.Add(btnRow, 0, 3);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        _tick.Tick += (_, _) => { Tick(0); Tick(1); };
        Load += (_, _) => { ApplyDark(); _tick.Start(); };
        FormClosing += (_, _) => _tick.Stop();
    }

    Panel BuildFader(int i, string name)
    {
        int idx = i;
        var card = new Panel { Width = 398, Height = 150, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12), BackColor = _t.Card };

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

        var barBg = new Panel { Height = 8, BackColor = _t.Inset, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 12, 8, 2) };
        _barFill[i] = new Panel { Dock = DockStyle.Left, Width = 0, BackColor = _t.Accent };
        barBg.Controls.Add(_barFill[i]);
        _previewLbl[i] = new Label { Text = "0%", AutoSize = true, ForeColor = _t.Accent, Anchor = AnchorStyles.Right, Font = new Font("Segoe UI", 11f) };
        t.Controls.Add(barBg, 0, 3);
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
        if (_barFill[i].Parent is { } bg) _barFill[i].Width = bg.ClientSize.Width * p / 100;
    }

    void ApplyDark()
    {
        int v = _t.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
        foreach (var c in _taper)
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, _t.Dark ? "DarkMode_CFD" : null, null);
    }
}
