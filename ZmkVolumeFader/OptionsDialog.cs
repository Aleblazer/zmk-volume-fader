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
    public List<Category> Categories => _categories;
    public bool StartWithWindows => _startup.Checked;
    public ThemeMode SelectedTheme => (ThemeMode)Math.Clamp(_themeCombo.SelectedIndex, 0, 2);
    public CloseBehavior SelectedClose => (CloseBehavior)Math.Clamp(_closeCombo.SelectedIndex, 0, 2);
    // Old -> new names for categories renamed in this Options session (possibly
    // across several visits to Manage Categories); the owner re-points fader
    // targets on Save so a rename doesn't detach them.
    public Dictionary<string, string> CategoryRenames { get; } = new();
    // Set when the user clicks "Set up sliders…"; the owner runs the wizard.
    public bool SetupRequested { get; private set; }

    readonly int _n;
    readonly Calibration[] _cal;
    readonly Func<int>[] _raw;
    readonly List<OutputPref>[] _outputs;
    List<Category> _categories;
    readonly IReadOnlyDictionary<string, string> _knownApps;
    readonly IReadOnlyDictionary<string, Image?>? _appIcons;
    readonly string[] _labels;
    readonly IReadOnlyList<OutputPref> _known;
    readonly string[] _presentIds;
    readonly MainForm.Theme _t;

    readonly Label[] _rawLbl, _rangeLbl, _previewLbl;
    readonly RoundedButton[] _recordBtn;
    readonly RoundedComboBox[] _taper;
    readonly MainForm.Stepper?[] _muteStep;   // physical faders' mute dead zone
    readonly MainForm.FaderBar[] _bar;
    readonly bool[] _recording;
    readonly bool[] _virtual;   // per slider: virtual faders have no calibration
    readonly int[] _lastRaw;
    readonly (int v, int pct)[][] _previewCurves;
    readonly (int Min, int Max, TaperKind Taper)[] _previewCurveSpecs;
    readonly bool[] _previewCurveValid;

    readonly CheckBox _startup = new() { Text = "Start with Windows", AutoSize = true, FlatStyle = FlatStyle.Standard, Margin = new Padding(0, 0, 0, 0) };
    readonly RoundedComboBox _themeCombo = new() { Width = 190 };
    readonly RoundedComboBox _closeCombo = new() { Width = 190 };

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 50 };
    readonly ToolTip _tip = new();
    TableLayoutPanel _root = null!;
    FlowLayoutPanel _btnRow = null!;
    RoundedScrollPanel _scroll = null!;

    static readonly string[] TaperItems = { "Linear pot", "Audio pot", "Straight" };
    static readonly string[] ThemeItems = { "Auto (follow Windows)", "Light", "Dark" };
    static readonly string[] CloseItems = { "Ask every time", "Minimize to tray", "Exit the app" };

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public OptionsDialog(MainForm.Theme theme, ThemeMode themeMode, bool startWithWindows, CloseBehavior closeBehavior,
        Calibration[] cals, Func<int>[] raws, List<OutputPref>[] outs, string[] labels,
        IReadOnlyList<OutputPref> known, IEnumerable<string> presentIds,
        List<Category> categories, IReadOnlyDictionary<string, string> knownApps,
        IReadOnlyDictionary<string, Image?>? appIcons = null, bool[]? virtuals = null)
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        _appIcons = appIcons;
        _t = theme;
        _n = cals.Length;
        _cal = cals;
        _raw = raws;
        _outputs = outs;
        _categories = categories;
        _knownApps = knownApps;
        _labels = labels;
        _known = known;
        _presentIds = presentIds.ToArray();
        _rawLbl = new Label[_n]; _rangeLbl = new Label[_n]; _previewLbl = new Label[_n];
        _recordBtn = new RoundedButton[_n]; _taper = new RoundedComboBox[_n];
        _muteStep = new MainForm.Stepper?[_n];
        _bar = new MainForm.FaderBar[_n]; _recording = new bool[_n];
        _virtual = virtuals ?? new bool[_n];
        _lastRaw = Enumerable.Repeat(int.MinValue, _n).ToArray();
        _previewCurves = new (int v, int pct)[_n][];
        _previewCurveSpecs = new (int, int, TaperKind)[_n];
        _previewCurveValid = new bool[_n];

        Text = "Options";
        Font = UiFonts.Get(9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 700);
        BackColor = _t.Window;

        // Scrollable content (General + [calibration label] + N faders + About),
        // with Save/Cancel pinned below. The calibration blurb is skipped when
        // every fader is virtual (nothing to calibrate).
        bool anyPhysical = _virtual.Any(v => !v);
        int rows = 2 + _n + (anyPhysical ? 1 : 0);
        _root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = rows, BackColor = Color.Transparent };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < rows; r++) _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        int row = 0;
        _root.Controls.Add(BuildGeneral(themeMode, startWithWindows, closeBehavior), 0, row++);
        if (anyPhysical)
            _root.Controls.Add(new Label
            {
                Text = "Calibration — hit Record, sweep the fader fully end-to-end, then stop. Then pick the taper that matches your pot. The preview updates as you move it.",
                AutoSize = true, MaximumSize = new Size(392, 0), ForeColor = _t.Subtle, Margin = new Padding(2, 4, 2, 10),
            }, 0, row++);
        for (int i = 0; i < _n; i++)
            _root.Controls.Add(_virtual[i] ? BuildVirtualFader(_labels[i]) : BuildFader(i, _labels[i]), 0, row++);
        _root.Controls.Add(BuildAbout(), 0, row++);

        _scroll = new RoundedScrollPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(14, 14, 14, 14),
            ThumbColor = _t.CtlBorder, ThumbHoverColor = _t.Subtle,
        };
        _scroll.SetContent(_root);

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
        outer.Controls.Add(_scroll, 0, 0);
        outer.Controls.Add(_btnRow, 0, 1);
        Controls.Add(outer);

        AcceptButton = save;
        CancelButton = cancel;

        _tick.Tick += (_, _) => { for (int i = 0; i < _n; i++) Tick(i); };
        Load += (_, _) =>
        {
            ApplyDark();
            foreach (var ms in _muteStep) ms?.SizeToFont();   // DPI-fit before measuring
            FitHeight();
            _tick.Start();
        };
        FormClosing += (_, _) => _tick.Stop();
        FormClosed += (_, _) => { _tick.Dispose(); _tip.Dispose(); };
    }

    // Size the dialog to its content up to a cap; the fader list scrolls beyond.
    // Width is forced to the DPI-scaled design width — the framework auto-scale
    // doesn't reliably widen a FixedDialog, so at 125%+ the content grew taller
    // and wider while the window stayed ~430px and clipped the right-hand buttons.
    void FitHeight()
    {
        int content = _root.PreferredSize.Height + LogicalToDeviceUnits(28);      // scroll top+bottom padding
        int buttons = _btnRow.PreferredSize.Height;
        int minH = LogicalToDeviceUnits(300);
        // Also clamp to the screen: 720 logical is 1440 device px at 200% and
        // would push Save/Cancel off a shorter display. Keep cap >= min or
        // Math.Clamp throws.
        int cap = Math.Max(minH, Math.Min(LogicalToDeviceUnits(720),
            Screen.FromControl(this).WorkingArea.Height - LogicalToDeviceUnits(80)));
        ClientSize = new Size(LogicalToDeviceUnits(430),
            Math.Clamp(content + buttons, minH, cap));
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
            // AutoSize so both lines are always fully shown — a fixed height clipped
            // the second line at 125%+ scaling. Anchor.None centres it in the cell.
            AutoSize = true, Anchor = AnchorStyles.None, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = _t.Subtle, Font = UiFonts.Get(8.25f), Margin = new Padding(0, 0, 0, 4),
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

    Panel BuildGeneral(ThemeMode mode, bool startup, CloseBehavior close)
    {
        // Auto-size so the wrapped button row (Set up / Set Default Outputs /
        // Manage Categories) is never clipped at any scaling.
        var card = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(12), BackColor = _t.Card };

        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 4; r++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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

        // What the window's X button does (ask / tray / exit).
        var closeRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        closeRow.Controls.Add(new Label { Text = "On close", AutoSize = true, ForeColor = _t.Subtle, Margin = new Padding(0, 6, 8, 0) });
        _closeCombo.BackColor = _t.CtlBg;
        _closeCombo.ForeColor = _t.Text;
        _closeCombo.Surround = _t.Card; _closeCombo.BoxColor = _t.CtlBg; _closeCombo.BorderColor = _t.CtlBorder; _closeCombo.ChevronColor = _t.Subtle;
        _closeCombo.Items.AddRange(CloseItems);
        _closeCombo.SelectedIndex = Math.Clamp((int)close, 0, CloseItems.Length - 1);
        closeRow.Controls.Add(_closeCombo);
        t.Controls.Add(closeRow, 0, 2);

        // Setup wizard, ranked-output editor, and category editor. A 2-column
        // table (not a wrapping FlowLayoutPanel — that mis-sizes its height inside
        // an auto-size parent) keeps the card tight to its content.
        var btnGrid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        btnGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        btnGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var setupBtn = MakeButton("Set up sliders…", accent: false, surround: _t.Card);
        setupBtn.Margin = new Padding(0, 0, 6, 6);
        setupBtn.Click += (_, _) => { SetupRequested = true; DialogResult = DialogResult.OK; Close(); };
        var outBtn = MakeButton("Set Default Outputs…", accent: false, surround: _t.Card);
        outBtn.Margin = new Padding(0, 0, 0, 6);
        outBtn.Click += (_, _) => OpenOutputs();
        var catBtn = MakeButton("Manage Categories…", accent: false, surround: _t.Card);
        catBtn.Margin = new Padding(0, 0, 0, 0);
        catBtn.Click += (_, _) => OpenCategories();
        btnGrid.Controls.Add(setupBtn, 0, 0);
        btnGrid.Controls.Add(outBtn, 1, 0);
        btnGrid.Controls.Add(catBtn, 0, 1);
        t.Controls.Add(btnGrid, 0, 3);

        card.Controls.Add(t);
        return card;
    }

    void OpenOutputs()
    {
        using var dlg = new OutputsDialog(_t, _labels, _outputs, _known, _presentIds);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            for (int i = 0; i < _n; i++) _outputs[i] = dlg.Result[i];
    }

    void OpenCategories()
    {
        using var dlg = new CategoriesDialog(_t, _categories, _knownApps, _appIcons);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _categories = dlg.Result;
        // Merge into the session's rename map, chaining across repeat visits
        // (A→B on the first visit then B→C on the second must yield A→C).
        foreach (var kv in dlg.Renamed)
        {
            foreach (var key in CategoryRenames.Where(p => p.Value == kv.Key).Select(p => p.Key).ToList())
                CategoryRenames[key] = kv.Value;
            CategoryRenames.TryAdd(kv.Key, kv.Value);
        }
    }

    // Virtual faders have no calibration (no raw/range/Record/taper); show a
    // compact card that just names it and explains it's dragged in the app.
    Panel BuildVirtualFader(string name)
    {
        var card = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12), BackColor = _t.Card };
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 2; r++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = name, AutoSize = true, ForeColor = _t.Text, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 0) }, 0, 0);
        t.Controls.Add(new Label { Text = "Virtual fader — set by dragging it in the app. No calibration needed.", AutoSize = true, MaximumSize = new Size(392, 0), ForeColor = _t.Subtle, Margin = new Padding(0, 4, 0, 0) }, 0, 1);
        card.Controls.Add(t);
        return card;
    }

    Panel BuildFader(int i, string name)
    {
        int idx = i;
        // Auto-size (like the virtual/General cards) so the added mute row can't
        // clip at any scaling.
        var card = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12), BackColor = _t.Card };

        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 5, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int r = 0; r < 5; r++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        t.Controls.Add(new Label { Text = name, AutoSize = true, ForeColor = _t.Subtle, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 0) }, 0, 0);
        _rawLbl[i] = new Label { Text = "raw —", AutoSize = true, ForeColor = _t.Text, Anchor = AnchorStyles.Right, Font = UiFonts.Get(11f) };
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

        // Mute dead zone: force 0% output while the raw reading sits below this
        // value (a mixer-style mute detent; also stops a wiper resting a few mV
        // above the calibrated Min from hovering at 1%).
        var mutePanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) };
        mutePanel.Controls.Add(new Label { Text = "Mute below raw", AutoSize = true, ForeColor = _t.Subtle, Margin = new Padding(0, 6, 8, 0) });
        var ms = new MainForm.Stepper
        {
            Minimum = 0, Maximum = 300, Value = Math.Clamp(_cal[i].MuteRaw, 0, 300),
            BackColor = _t.CtlBg, ForeColor = _t.Text, BorderColor = _t.CtlBorder,
            ChevronColor = _t.Subtle, Surround = _t.Card,
        };
        ms.ValueChanged += (_, _) => { _cal[idx].MuteRaw = ms.Value; _lastRaw[idx] = int.MinValue; };
        _muteStep[i] = ms;
        mutePanel.Controls.Add(ms);
        mutePanel.Controls.Add(new Label { Text = "(0 = off)", AutoSize = true, ForeColor = _t.Subtle, Margin = new Padding(6, 6, 0, 0) });
        _tip.SetToolTip(ms, "Force 0% volume while the raw reading is below this value");
        t.Controls.Add(mutePanel, 0, 3);
        t.SetColumnSpan(mutePanel, 2);

        _bar[i] = new MainForm.FaderBar
        {
            Height = 22, ShowTicks = false, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 8, 8, 2),
            Track = _t.Inset, Fill = _t.Accent, BackColor = _t.Card, Tick = _t.Subtle,
            Knob = _t.Dark ? Color.FromArgb(0xE6, 0xE8, 0xEB) : Color.White,
            KnobEdge = _t.Dark ? Color.FromArgb(0x0E, 0x10, 0x14) : Color.FromArgb(0xC2, 0xC6, 0xCC),
        };
        _previewLbl[i] = new Label { Text = "0%", AutoSize = true, ForeColor = _t.Accent, Anchor = AnchorStyles.Right, Font = UiFonts.Get(11f) };
        t.Controls.Add(_bar[i], 0, 4);
        t.Controls.Add(_previewLbl[i], 1, 4);

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
        _lastRaw[i] = int.MinValue;
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

    void OnTaperChanged(int i)
    {
        _cal[i].Taper = (TaperKind)_taper[i].SelectedIndex;
        _lastRaw[i] = int.MinValue;
    }

    void Tick(int i)
    {
        if (_virtual[i]) return;   // no calibration controls on a virtual fader
        int v = _raw[i]();
        var spec = (_cal[i].Min, _cal[i].Max, _cal[i].Taper);
        bool curveChanged = !_previewCurveValid[i] || _previewCurveSpecs[i] != spec;
        if (v == _lastRaw[i] && !curveChanged) return;
        _lastRaw[i] = v;
        _rawLbl[i].Text = $"raw {v}";
        if (_recording[i])
        {
            if (v < _cal[i].Min) _cal[i].Min = v;
            if (v > _cal[i].Max) _cal[i].Max = v;
        }
        _rangeLbl[i].Text = _cal[i].Min <= _cal[i].Max ? $"range {_cal[i].Min} – {_cal[i].Max}" : "range —";

        spec = (_cal[i].Min, _cal[i].Max, _cal[i].Taper);
        if (!_previewCurveValid[i] || _previewCurveSpecs[i] != spec)
        {
            _previewCurves[i] = _cal[i].BuildCurve();
            _previewCurveSpecs[i] = spec;
            _previewCurveValid[i] = true;
        }
        int p = Math.Clamp((int)Math.Round(Calibration.Eval(_previewCurves[i], v)), 0, 100);
        if (_cal[i].MuteRaw > 0 && v < _cal[i].MuteRaw) p = 0;   // preview the mute dead zone
        _previewLbl[i].Text = $"{p}%";
        _bar[i].Value = p;
    }

    void ApplyDark()
    {
        int v = _t.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
        foreach (var c in _taper)
            if (c is { IsHandleCreated: true }) SetWindowTheme(c.Handle, _t.Dark ? "DarkMode_CFD" : null, null);
        if (_themeCombo.IsHandleCreated) SetWindowTheme(_themeCombo.Handle, _t.Dark ? "DarkMode_CFD" : null, null);
        if (_closeCombo.IsHandleCreated) SetWindowTheme(_closeCombo.Handle, _t.Dark ? "DarkMode_CFD" : null, null);
    }
}
