using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Guided curve-capture wizard for one fader. Step 1 captures the raw min/max.
/// Step 2 records two instructed steady sweeps (bottom→top, then top→bottom);
/// each Start→Stop is a known-direction traversal, time-normalized and averaged
/// (ends pinned to min/max) into the curve. Step 3 lets you test it live.
/// On Save it writes Min/Max/Taper/CustomPoints into the supplied calibration.
/// </summary>
sealed class CurveCaptureDialog : Form
{
    enum Step { Ends, Sweep, Review }

    const int N = 21;                            // resample resolution per sweep
    static readonly bool[] PassUp = { true, false };  // pass directions: up, then down

    readonly MainForm.Theme _t;
    readonly Func<int> _raw;
    readonly Calibration _target;

    Step _step = Step.Ends;
    int _min, _max;
    bool _minSet, _maxSet;

    int _passIndex;
    bool _recording;
    readonly List<int> _passSamples = new();
    readonly List<int[]> _passes = new();
    CurvePoint[]? _built;

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 40 };

    Label _instr = null!, _rawLbl = null!, _stepInfo = null!, _previewLbl = null!;
    Panel _barFill = null!;
    Button _btnMin = null!, _btnMax = null!, _btnStartStop = null!, _btnNext = null!, _btnSave = null!, _btnRedo = null!;
    Panel _pEnds = null!, _pSweep = null!, _pReview = null!;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public CurveCaptureDialog(MainForm.Theme theme, string faderName, Func<int> raw, Calibration target)
    {
        _t = theme;
        _raw = raw;
        _target = target;
        _min = target.Min;
        _max = target.Max;

        Text = $"Measure curve — {faderName}";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 290);
        BackColor = _t.Window;

        _instr = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 64, ForeColor = _t.Subtle, Padding = new Padding(14, 12, 14, 4) };
        _rawLbl = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 30, ForeColor = _t.Text, Font = new Font("Segoe UI", 13f), TextAlign = ContentAlignment.MiddleCenter, Text = "raw —" };

        var barBg = new Panel { Dock = DockStyle.Top, Height = 8, Margin = new Padding(0), BackColor = _t.Inset };
        _barFill = new Panel { Dock = DockStyle.Left, Width = 0, BackColor = _t.Accent };
        barBg.Controls.Add(_barFill);
        var barWrap = new Panel { Dock = DockStyle.Top, Height = 16, Padding = new Padding(14, 4, 14, 4), BackColor = Color.Transparent };
        barWrap.Controls.Add(barBg);

        // Step panels (one shown at a time).
        _pEnds = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(14, 8, 14, 8), BackColor = Color.Transparent };
        _btnMin = MakeButton("Capture min", false);
        _btnMax = MakeButton("Capture max", false);
        _btnMin.Click += (_, _) => { _min = _raw(); _minSet = true; UpdateEndsInfo(); };
        _btnMax.Click += (_, _) => { _max = _raw(); _maxSet = true; UpdateEndsInfo(); };
        _stepInfo = new Label { AutoSize = true, ForeColor = _t.Text, Location = new Point(14, 46) };
        var endsRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Location = new Point(12, 6) };
        endsRow.Controls.Add(_btnMin);
        endsRow.Controls.Add(_btnMax);
        _pEnds.Controls.Add(endsRow);
        _pEnds.Controls.Add(_stepInfo);

        _pSweep = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(14, 8, 14, 8), BackColor = Color.Transparent, Visible = false };
        _btnStartStop = MakeButton("Start", true);
        _btnStartStop.Location = new Point(14, 8);
        _btnStartStop.Click += (_, _) => ToggleRecord();
        _pSweep.Controls.Add(_btnStartStop);

        _pReview = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(14, 8, 14, 8), BackColor = Color.Transparent, Visible = false };
        _previewLbl = new Label { AutoSize = true, ForeColor = _t.Accent, Font = new Font("Segoe UI", 14f), Location = new Point(14, 8) };
        _pReview.Controls.Add(_previewLbl);

        // Bottom button row.
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 6, 10, 6), BackColor = Color.Transparent };
        var cancel = MakeButton("Cancel", false);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _btnNext = MakeButton("Next", true);
        _btnNext.Click += (_, _) => GoToSweep();
        _btnSave = MakeButton("Save", true);
        _btnSave.Click += (_, _) => Save();
        _btnRedo = MakeButton("Redo sweeps", false);
        _btnRedo.Click += (_, _) => GoToSweep();
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(_btnNext);
        bottom.Controls.Add(_btnSave);
        bottom.Controls.Add(_btnRedo);

        // Dock order: last added Top sits highest, so add bottom-up.
        Controls.Add(_pReview);
        Controls.Add(_pSweep);
        Controls.Add(_pEnds);
        Controls.Add(barWrap);
        Controls.Add(_rawLbl);
        Controls.Add(_instr);
        Controls.Add(bottom);

        CancelButton = cancel;
        _tick.Tick += (_, _) => Tick();
        Load += (_, _) => { SetTitleBarDark(); _tick.Start(); UpdateStep(); UpdateEndsInfo(); };
        FormClosing += (_, _) => _tick.Stop();
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

    void Tick()
    {
        int v = _raw();
        _rawLbl.Text = $"raw {v}";
        if (_minSet && _maxSet && _max > _min)
        {
            int frac = (int)(100L * (v - _min) / (_max - _min));
            frac = Math.Clamp(frac, 0, 100);
            if (_barFill.Parent is { } bg) _barFill.Width = bg.ClientSize.Width * frac / 100;
        }
        if (_recording) _passSamples.Add(v);
        if (_step == Step.Review && _built is { Length: >= 2 })
        {
            var curve = new (int, int)[_built.Length];
            for (int i = 0; i < curve.Length; i++) curve[i] = (_built[i].V, _built[i].Pct);
            _previewLbl.Text = $"{Math.Clamp((int)Math.Round(Calibration.Eval(curve, v)), 0, 100)}%   (move the fader to test)";
        }
    }

    void UpdateEndsInfo()
    {
        _stepInfo.Text = $"min {(_minSet ? _min.ToString() : "—")}      max {(_maxSet ? _max.ToString() : "—")}";
        _btnNext.Enabled = _minSet && _maxSet && _max > _min;
    }

    void GoToSweep()
    {
        if (_min >= _max) { (_min, _max) = (Math.Min(_min, _max), Math.Max(_min, _max)); }
        _passes.Clear();
        _passSamples.Clear();
        _passIndex = 0;
        _recording = false;
        _btnStartStop.Text = "Start";
        _step = Step.Sweep;
        UpdateStep();
    }

    void ToggleRecord()
    {
        if (!_recording)
        {
            _passSamples.Clear();
            _recording = true;
            _btnStartStop.Text = "Stop";
        }
        else
        {
            _recording = false;
            _btnStartStop.Text = "Start";
            FinishPass();
        }
    }

    void FinishPass()
    {
        int span = _passSamples.Count == 0 ? 0 : _passSamples.Max() - _passSamples.Min();
        if (_passSamples.Count < 4 || span < (_max - _min) / 2)
        {
            MessageBox.Show(this, "That pass didn't cover the full travel. Move to the end and sweep all the way across.",
                "Try again", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;   // repeat the same pass
        }

        var list = new List<int>(_passSamples);
        if (!PassUp[_passIndex]) list.Reverse();   // orient every pass low -> high
        var arr = new int[N];
        for (int j = 0; j < N; j++)
            arr[j] = list[Math.Clamp((int)Math.Round(j / (N - 1.0) * (list.Count - 1)), 0, list.Count - 1)];
        _passes.Add(arr);

        _passIndex++;
        if (_passIndex >= PassUp.Length) { BuildCurve(); _step = Step.Review; UpdateStep(); }
        else UpdateStep();
    }

    void BuildCurve()
    {
        var avg = new double[N];
        foreach (var p in _passes)
            for (int j = 0; j < N; j++) avg[j] += p[j];
        for (int j = 0; j < N; j++) avg[j] /= _passes.Count;

        avg[0] = _min;
        avg[N - 1] = _max;
        for (int j = 1; j < N; j++) avg[j] = Math.Max(avg[j], avg[j - 1]);   // monotonic

        var pts = new List<CurvePoint>();
        for (int j = 0; j < N; j += 2)   // ~11 evenly-spaced points
            pts.Add(new CurvePoint((int)Math.Round(avg[j]), (int)Math.Round(j / (N - 1.0) * 100)));
        _built = pts.ToArray();
    }

    void Save()
    {
        if (_built is { Length: >= 2 })
        {
            _target.Min = _min;
            _target.Max = _max;
            _target.Taper = TaperKind.Custom;
            _target.CustomPoints = _built;
            DialogResult = DialogResult.OK;
        }
        Close();
    }

    void UpdateStep()
    {
        _pEnds.Visible = _step == Step.Ends;
        _pSweep.Visible = _step == Step.Sweep;
        _pReview.Visible = _step == Step.Review;
        _btnNext.Visible = _step == Step.Ends;
        _btnSave.Visible = _step == Step.Review;
        _btnRedo.Visible = _step == Step.Review;

        _instr.Text = _step switch
        {
            Step.Ends => "Step 1 of 2 — Move the fader fully to the BOTTOM and click Capture min, then fully to the TOP and click Capture max.",
            Step.Sweep => "Step 2 of 2 — " +
                (PassUp[_passIndex]
                    ? "Put the fader at the BOTTOM. Click Start, sweep STEADILY up to the top, then click Stop."
                    : "Now put it at the TOP. Click Start, sweep STEADILY down to the bottom, then click Stop.") +
                $"  (pass {_passIndex + 1} of {PassUp.Length})",
            _ => "Done — move the fader to test the captured curve. Save to keep it, or redo the sweeps.",
        };
    }

    void SetTitleBarDark()
    {
        int v = _t.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
    }
}
