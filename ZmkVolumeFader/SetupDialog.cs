using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Fader setup as a reorderable list. Seeded from the current faders, the dialog
/// lets you add physical faders (captured live by sweeping the hardware), add
/// virtual faders, reorder with ▲/▼, and remove with ✕. "Done" returns the list
/// order in <see cref="Result"/>; the owner turns each item into a slider config,
/// reusing an existing slider's settings when the item carries a SourceIndex.
/// </summary>
sealed class SetupDialog : Form
{
    public enum ItemKind { Physical, Virtual }

    public sealed class Item
    {
        public ItemKind Kind;
        public string DeviceKey = "";
        public string DeviceName = "";
        public int Axis = -1;      // physical: captured HID axis index; -1 for virtual
        public int Min, Max;       // physical: captured raw calibration range (mV)
        public int? SourceIndex;   // index into the `existing` list this row came from; null if newly added in this dialog
        public string Label = "";  // display name
    }

    public sealed class DeviceOption
    {
        public string Key = "";
        public string Name = "";
        public bool Connected;
        public override string ToString() => $"{Name} — {(Connected ? "connected" : "offline")}";
    }

    public List<Item> Result { get; } = new();

    readonly MainForm.Theme _t;
    readonly Func<string, int[]> _rawAxes;
    readonly IReadOnlyList<DeviceOption> _devices;
    readonly Action<string?>? _deviceSelected;
    readonly Func<string, bool>? _sourceConnected;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 40 };

    const int MaxAxes = 8;   // hid-io vendor report carries up to eight 16-bit axes
    const int TravelThreshold = 1500;   // mV of sweep before we count a fader as "moved"
    const int W = 560, H = 440;

    // The working list (seeded from `existing`); Result is this order on Done.
    readonly List<Item> _items = new();

    enum Mode { List, Capture }
    Mode _mode = Mode.List;

    // Capture state (only meaningful in Mode.Capture).
    readonly int[] _min = new int[MaxAxes];
    readonly int[] _max = new int[MaxAxes];
    readonly HashSet<string> _usedAxes = new(StringComparer.OrdinalIgnoreCase);
    bool _haveBaseline;
    int _candidate = -1;
    int _editingIndex = -1;

    // --- controls ---
    readonly Label _title = new() { AutoSize = false, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Font = UiFonts.Get(12.5f, FontStyle.Bold) };
    // Same themed scroll viewport as the main window (native AutoScroll would
    // paint a squared scrollbar and ignore the wheel unless focused).
    readonly RoundedScrollPanel _listScroll = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    readonly TableLayoutPanel _listInner;
    readonly TableLayoutPanel _capturePanel;
    readonly Label _captureDetail = new() { AutoSize = false, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
    readonly Label _captureHead = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 26, Font = UiFonts.Get(10.5f, FontStyle.Bold) };
    readonly Label _validation = new() { AutoSize = true, Visible = false, Margin = new Padding(0, 4, 0, 4) };
    readonly ComboBox _captureDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    readonly FlowLayoutPanel _listButtons;
    readonly FlowLayoutPanel _captureButtons;
    readonly RoundedButton _addPhysical, _addVirtual, _done, _cancel, _capture, _captureCancel;

    // Pulse animation for the two Add buttons (draws attention when asked).
    readonly System.Windows.Forms.Timer? _pulse;
    bool _pulseState;
    int _pulseTicks;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);

    public SetupDialog(MainForm.Theme t, Func<string, int[]> rawAxes, IReadOnlyList<DeviceOption> devices,
        IReadOnlyList<Item> existing, Action<string?>? deviceSelected = null,
        Func<string, bool>? sourceConnected = null, bool pulseAddButtons = false)
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        _t = t;
        _rawAxes = rawAxes;
        _devices = devices;
        _deviceSelected = deviceSelected;
        _sourceConnected = sourceConnected;

        // Clone the seed so reorder/remove never mutates the caller's list.
        foreach (var it in existing)
            _items.Add(new Item { Kind = it.Kind, DeviceKey = it.DeviceKey, DeviceName = it.DeviceName, Axis = it.Axis, Min = it.Min, Max = it.Max, SourceIndex = it.SourceIndex, Label = it.Label });

        Text = "Fader Layout";
        Font = UiFonts.Get(9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(W, H);
        BackColor = _t.Window;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // title
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // content (list or capture)
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons

        _title.ForeColor = _t.Text;
        root.Controls.Add(_title, 0, 0);

        // Scrolling list of faders.
        _listInner = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Color.Transparent, Margin = new Padding(0) };
        _listInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _listScroll.ThumbColor = _t.CtlBorder;
        _listScroll.ThumbHoverColor = _t.Subtle;
        _listScroll.SetContent(_listInner);

        // Capture panel (shown while adding a physical fader).
        _capturePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Visible = false };
        _capturePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _capturePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _capturePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _capturePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _captureHead.Text = "Add physical fader";
        _captureHead.ForeColor = _t.Text;
        _captureDetail.ForeColor = _t.Subtle;
        _captureDevice.BackColor = _t.CtlBg;
        _captureDevice.ForeColor = _t.Text;
        _captureDevice.Margin = new Padding(0, 6, 0, 4);
        foreach (var device in _devices) _captureDevice.Items.Add(device);
        if (_captureDevice.Items.Count > 0) _captureDevice.SelectedIndex = 0;
        _captureDevice.SelectedIndexChanged += (_, _) =>
        {
            ResetCapture();
            if (_captureDevice.SelectedItem is DeviceOption selected) _deviceSelected?.Invoke(selected.Key);
        };
        _capturePanel.Controls.Add(_captureHead, 0, 0);
        _capturePanel.Controls.Add(_captureDevice, 0, 1);
        _capturePanel.Controls.Add(_captureDetail, 0, 2);

        // Both content panels share the content cell; only one is visible at a time.
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        content.Controls.Add(_listScroll);
        content.Controls.Add(_capturePanel);
        root.Controls.Add(content, 0, 1);

        // Bottom buttons: list mode (Add physical / Add virtual / Done / Cancel).
        _listButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        _addPhysical = MakeButton("Add physical fader", accent: true);
        // An offline remembered device can be connected after entering capture;
        // keep the action available whenever there is at least one known source.
        _addPhysical.Enabled = _devices.Count > 0;
        _addPhysical.Click += (_, _) => { StopPulse(); EnterCapture(); };
        _addVirtual = MakeButton("Add virtual fader", accent: false);
        _addVirtual.Click += (_, _) => { StopPulse(); AddVirtual(); };
        _done = MakeButton("Done", accent: true);
        _done.Click += (_, _) => { StopPulse(); Finish(); };
        _cancel = MakeButton("Cancel", accent: false);
        _cancel.Click += (_, _) => { StopPulse(); DialogResult = DialogResult.Cancel; Close(); };
        _listButtons.Controls.AddRange(new Control[] { _addPhysical, _addVirtual, _done, _cancel });

        // Bottom buttons: capture mode (Capture / Cancel).
        _captureButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0), Visible = false };
        _capture = MakeButton("Capture", accent: true);
        _capture.Enabled = false;
        _capture.Click += (_, _) => CaptureCandidate();
        _captureCancel = MakeButton("Cancel", accent: false);
        _captureCancel.Click += (_, _) => ExitCapture();
        _captureButtons.Controls.AddRange(new Control[] { _capture, _captureCancel });

        _validation.ForeColor = Color.FromArgb(0xF0, 0x8A, 0x3C);
        var btnHost = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        btnHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        btnHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        btnHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        btnHost.Controls.Add(_validation, 0, 0);
        var modeButtons = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent };
        modeButtons.Controls.Add(_listButtons);
        modeButtons.Controls.Add(_captureButtons);
        btnHost.Controls.Add(modeButtons, 0, 1);
        root.Controls.Add(btnHost, 0, 2);

        Controls.Add(root);
        CancelButton = _cancel;

        if (pulseAddButtons) _pulse = new System.Windows.Forms.Timer { Interval = 450 };

        RebuildRows();
        UpdateTitle();
        _tick.Tick += (_, _) => Poll();
        // Force the DPI-scaled size — auto-scale doesn't reliably resize a
        // FixedDialog, so the content would clip at 125%+.
        Load += (_, _) =>
        {
            ApplyDark();
            // Height is additionally clamped to the screen (the list scrolls).
            ClientSize = new Size(LogicalToDeviceUnits(W),
                Math.Min(LogicalToDeviceUnits(H),
                    Math.Max(LogicalToDeviceUnits(300),
                        Screen.FromControl(this).WorkingArea.Height - LogicalToDeviceUnits(80))));
            StartPulse();
        };
        FormClosing += (_, _) => { _tick.Stop(); _pulse?.Stop(); };
        FormClosed += (_, _) => { _tick.Dispose(); _pulse?.Dispose(); };
    }

    // ---- list mode --------------------------------------------------------

    void UpdateTitle() => _title.Text = _mode == Mode.Capture
        ? (_editingIndex >= 0 ? "Change physical source" : "Add physical fader")
        : "Your faders — mix devices, change sources, reorder, or remove.";

    // (Re)build the list rows from _items, one per fader (or a hint when empty).
    void RebuildRows()
    {
        _listInner.SuspendLayout();
        var removed = _listInner.Controls.Cast<Control>().ToArray();
        _listInner.Controls.Clear();
        foreach (var control in removed) control.Dispose();
        _listInner.RowStyles.Clear();
        int rowH = LogicalToDeviceUnits(58), gap = LogicalToDeviceUnits(8);
        if (_items.Count == 0)
        {
            _listInner.RowCount = 1;
            _listInner.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH + gap));
            var hint = new Label
            {
                AutoSize = false, Height = rowH, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = _t.Subtle, Margin = new Padding(0, 0, 0, gap),
                Text = "No faders yet — add a physical or virtual fader below.",
            };
            _listInner.Controls.Add(hint, 0, 0);
        }
        else
        {
            _listInner.RowCount = _items.Count;
            for (int i = 0; i < _items.Count; i++)
            {
                _listInner.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH + gap));
                _listInner.Controls.Add(BuildRow(i, rowH, gap), 0, i);
            }
        }
        _listInner.ResumeLayout();
        _listScroll.Reflow();
        UpdateValidation();
    }

    Panel BuildRow(int index, int rowH, int gap)
    {
        var it = _items[index];
        var row = new Panel
        {
            Height = rowH, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, 0, 0, gap), Padding = new Padding(14, 6, 10, 6), BackColor = _t.Card,
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, BackColor = Color.Transparent };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int c = 0; c < 4; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        // Editable name; blank falls back to a default (see RunSetupWizard).
        var name = new TextBox
        {
            Text = it.Label,
            PlaceholderText = it.Kind == ItemKind.Virtual ? "Virtual fader" : "Physical fader",
            BorderStyle = BorderStyle.FixedSingle, BackColor = _t.CtlBg, ForeColor = _t.Text,
            Font = UiFonts.Get(9.75f),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, Margin = new Padding(0, 0, 6, 1),
        };
        name.TextChanged += (_, _) => it.Label = name.Text;
        var sub = new Label { Text = Subtitle(it), AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft, ForeColor = _t.Subtle };
        grid.Controls.Add(name, 0, 0);
        grid.Controls.Add(sub, 0, 1);

        if (it.Kind == ItemKind.Physical)
        {
            var source = SourceButton();
            source.Click += (_, _) => { StopPulse(); EnterCapture(index); };
            grid.Controls.Add(source, 1, 0); grid.SetRowSpan(source, 2);
        }
        var up = SmallButton("▲", "Move fader up");
        up.Enabled = index > 0;
        up.Click += (_, _) => { StopPulse(); Swap(index, index - 1); };
        var down = SmallButton("▼", "Move fader down");
        down.Enabled = index < _items.Count - 1;
        down.Click += (_, _) => { StopPulse(); Swap(index, index + 1); };
        var remove = SmallButton("✕", "Remove fader");
        remove.Click += (_, _) => { StopPulse(); _items.RemoveAt(index); RebuildRows(); };
        grid.Controls.Add(up, 2, 0); grid.SetRowSpan(up, 2);
        grid.Controls.Add(down, 3, 0); grid.SetRowSpan(down, 2);
        grid.Controls.Add(remove, 4, 0); grid.SetRowSpan(remove, 2);

        row.Controls.Add(grid);
        return row;
    }

    string Subtitle(Item it)
    {
        if (it.Kind == ItemKind.Virtual) return "Virtual · dragged in the app";
        var device = _devices.FirstOrDefault(d => d.Key.Equals(it.DeviceKey, StringComparison.OrdinalIgnoreCase));
        string state = device == null ? "unavailable" : device.Connected ? "connected" : "offline";
        return $"{it.DeviceName} · axis {it.Axis + 1} · {state} · {it.Min}–{it.Max} mV";
    }

    void Swap(int a, int b)
    {
        if (a < 0 || b < 0 || a >= _items.Count || b >= _items.Count) return;
        (_items[a], _items[b]) = (_items[b], _items[a]);
        RebuildRows();
    }

    void AddVirtual()
    {
        _items.Add(new Item { Kind = ItemKind.Virtual, Axis = -1, Label = "" });
        RebuildRows();
    }

    void Finish()
    {
        if (!UpdateValidation()) return;
        Result.Clear();
        Result.AddRange(_items);   // may be empty — the owner handles a 0-fader layout
        DialogResult = DialogResult.OK;
        Close();
    }

    // ---- capture mode -----------------------------------------------------

    void EnterCapture(int editingIndex = -1)
    {
        _editingIndex = editingIndex;
        _mode = Mode.Capture;
        if (_editingIndex >= 0 && _editingIndex < _items.Count)
        {
            string key = _items[_editingIndex].DeviceKey;
            for (int i = 0; i < _captureDevice.Items.Count; i++)
                if (_captureDevice.Items[i] is DeviceOption option
                    && option.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                { _captureDevice.SelectedIndex = i; break; }
        }
        if (_captureDevice.SelectedItem is DeviceOption selected) _deviceSelected?.Invoke(selected.Key);
        // Exclude axes already bound to a physical fader in the current list.
        _usedAxes.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            if (i != _editingIndex && it.Kind == ItemKind.Physical && it.Axis >= 0)
                _usedAxes.Add(Binding(it.DeviceKey, it.Axis));
        }
        ResetCapture();

        _capture.Enabled = false;
        _listScroll.Visible = false;
        _listButtons.Visible = false;
        _capturePanel.Visible = true;
        _captureButtons.Visible = true;
        _captureHead.Text = _editingIndex >= 0 ? "Change physical source" : "Add physical fader";
        _capture.Text = _editingIndex >= 0 ? "Use source" : "Capture";
        UpdateTitle();
        UpdateCaptureText();
        _tick.Start();
    }

    void ExitCapture()
    {
        _tick.Stop();
        _deviceSelected?.Invoke(null);
        _editingIndex = -1;
        _mode = Mode.List;
        _capturePanel.Visible = false;
        _captureButtons.Visible = false;
        _listScroll.Visible = true;
        _listButtons.Visible = true;
        RebuildRows();
        _listScroll.Reflow();
        UpdateTitle();
    }

    void Poll()
    {
        if (_mode != Mode.Capture) return;   // one timer; only capture mode reads axes
        if (_captureDevice.SelectedItem is not DeviceOption device) return;
        if (_sourceConnected != null)
        {
            bool connected = _sourceConnected(device.Key);
            if (device.Connected != connected)
            {
                device.Connected = connected;
                _captureDevice.Invalidate();
                UpdateCaptureText();
            }
        }
        if (!device.Connected) return;
        var axes = _rawAxes(device.Key);
        if (axes.Length < MaxAxes)
        {
            _captureDetail.Text = "Waiting for the selected device to begin reporting…";
            return;
        }

        if (!_haveBaseline)
        {
            for (int a = 0; a < MaxAxes; a++) _min[a] = _max[a] = axes[a];
            _haveBaseline = true;
        }

        int best = -1, bestTravel = 0;
        for (int a = 0; a < MaxAxes; a++)
        {
            if (_usedAxes.Contains(Binding(device.Key, a))) continue;
            if (axes[a] < _min[a]) _min[a] = axes[a];
            if (axes[a] > _max[a]) _max[a] = axes[a];
            int travel = _max[a] - _min[a];
            if (travel > bestTravel) { bestTravel = travel; best = a; }
        }

        _candidate = bestTravel >= TravelThreshold ? best : -1;
        _capture.Enabled = _candidate >= 0;
        UpdateCaptureText();
    }

    void UpdateCaptureText() => _captureDetail.Text = _candidate >= 0
        ? $"Detected on axis {_candidate + 1} — range {_min[_candidate]}–{_max[_candidate]} mV.\n\n" +
          (_editingIndex >= 0 ? "Sweep it fully, then use this source." : "Sweep it fully, then Capture.")
        : _captureDevice.Items.Count == 0
            ? "No compatible fader devices are currently detected."
            : _captureDevice.SelectedItem is DeviceOption { Connected: false }
                ? "This source is offline. Connect it or choose another connected device."
            : "Choose a device, then move one fader fully from bottom to top.";

    void CaptureCandidate()
    {
        if (_candidate < 0 || _captureDevice.SelectedItem is not DeviceOption device) return;
        var captured = new Item
        {
            Kind = ItemKind.Physical,
            DeviceKey = device.Key,
            DeviceName = device.Name,
            Axis = _candidate,
            Min = _min[_candidate],
            Max = _max[_candidate],
            Label = "",
        };
        if (_editingIndex >= 0 && _editingIndex < _items.Count)
        {
            captured.SourceIndex = _items[_editingIndex].SourceIndex;
            captured.Label = _items[_editingIndex].Label;
            _items[_editingIndex] = captured;
        }
        else _items.Add(captured);
        ExitCapture();
    }

    void ResetCapture()
    {
        _haveBaseline = false;
        _candidate = -1;
        _capture.Enabled = false;
        UpdateCaptureText();
    }

    static string Binding(string deviceKey, int axis) => FaderLayoutLogic.BindingKey(deviceKey, axis);

    bool UpdateValidation()
    {
        var duplicates = FaderLayoutLogic.FindDuplicates(_items
            .Where(item => item.Kind == ItemKind.Physical)
            .Select(item => new PhysicalBinding(item.DeviceKey, item.Axis)));
        if (duplicates.Count == 0)
        {
            _validation.Visible = false;
            _validation.Text = "";
            _done.Enabled = true;
            return true;
        }
        var duplicate = duplicates[0];
        string name = _devices.FirstOrDefault(d => d.Key.Equals(duplicate.DeviceKey,
            StringComparison.OrdinalIgnoreCase))?.Name ?? duplicate.DeviceKey;
        _validation.Text = $"Duplicate source: {name}, axis {duplicate.Axis + 1}. Each physical source can be used once.";
        _validation.Visible = true;
        _done.Enabled = false;
        return false;
    }

    // ---- pulse ------------------------------------------------------------

    void StartPulse()
    {
        if (_pulse == null) return;
        _pulse.Tick += (_, _) =>
        {
            // Stop after ~6s; guard against a closed form.
            if (IsDisposed || !IsHandleCreated || ++_pulseTicks > 13) { StopPulse(); return; }
            _pulseState = !_pulseState;
            if (_pulseState)
            {
                _addPhysical.BackColor = RoundGfx.Blend(_t.Accent, Color.White, 0.30f);
                _addVirtual.BackColor = _t.Accent;
                _addVirtual.ForeColor = AccentText();
                _addVirtual.FlatAppearance.BorderColor = _t.Accent;
            }
            else RestoreAddButtons();
            _addPhysical.Invalidate();
            _addVirtual.Invalidate();
        };
        _pulse.Start();
    }

    void StopPulse()
    {
        if (_pulse == null || !_pulse.Enabled) return;
        _pulse.Stop();
        RestoreAddButtons();
        _addPhysical.Invalidate();
        _addVirtual.Invalidate();
    }

    void RestoreAddButtons()
    {
        _addPhysical.BackColor = _t.Accent;
        _addPhysical.ForeColor = AccentText();
        _addPhysical.FlatAppearance.BorderColor = _t.Accent;
        _addVirtual.BackColor = _t.CtlBg;
        _addVirtual.ForeColor = _t.Text;
        _addVirtual.FlatAppearance.BorderColor = _t.CtlBorder;
    }

    // ---- shared helpers ---------------------------------------------------

    RoundedButton SmallButton(string glyph, string accessibleName)
    {
        var b = new RoundedButton
        {
            Text = glyph, AutoSize = false, Radius = 7, Anchor = AnchorStyles.None,
            Size = new Size(LogicalToDeviceUnits(34), LogicalToDeviceUnits(32)),
            Margin = new Padding(4, 0, 0, 0), Surround = _t.Card,
            BackColor = _t.CtlBg, ForeColor = _t.Text,
            AccessibleName = accessibleName,
        };
        b.FlatAppearance.BorderColor = _t.CtlBorder;
        return b;
    }

    RoundedButton SourceButton()
    {
        var b = new RoundedButton
        {
            Text = "Source…", AutoSize = false, Radius = 7, Anchor = AnchorStyles.None,
            Size = new Size(LogicalToDeviceUnits(70), LogicalToDeviceUnits(32)),
            Margin = new Padding(4, 0, 0, 0), Surround = _t.Card,
            BackColor = _t.CtlBg, ForeColor = _t.Text,
            AccessibleName = "Change physical fader source",
        };
        b.FlatAppearance.BorderColor = _t.CtlBorder;
        return b;
    }

    RoundedButton MakeButton(string text, bool accent)
    {
        var b = new RoundedButton { Text = text, AutoSize = true, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0, 0, 8, 0), Surround = _t.Window };
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
        if (_captureDevice.IsHandleCreated)
            SetWindowTheme(_captureDevice.Handle, _t.Dark ? "DarkMode_CFD" : null, null);
    }
}
