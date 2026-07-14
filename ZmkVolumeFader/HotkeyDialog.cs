using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Per-virtual-fader hotkey assignment: bind Volume Up / Down / Mute to global
/// keys and set the per-press step %. Click a field, then press the chord to
/// capture it (Esc cancels, Delete clears). Bindings aren't swallowed at runtime
/// (see <see cref="KeyboardHook"/>); a bare common key is allowed but hinted
/// against, since it would fire during normal typing. F13–F24 are ideal.
/// </summary>
sealed class HotkeyDialog : Form
{
    public Hotkey Up => _up;
    public Hotkey Down => _down;
    public Hotkey Mute => _mute;
    public int Step => (int)_step.Value;

    enum Field { None, Up, Down, Mute }

    readonly MainForm.Theme _t;
    Hotkey _up, _down, _mute;
    Field _listening = Field.None;
    // Other virtual faders' bindings, for cross-fader conflict notices.
    readonly IReadOnlyList<(string Fader, Hotkey Hk)> _others;

    readonly RoundedButton _upBtn, _downBtn, _muteBtn;
    readonly MainForm.Stepper _step;
    readonly Label _hint;
    readonly TableLayoutPanel _root;
    readonly FlowLayoutPanel _btnRow;

    const int W = 400, H = 320;
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    public HotkeyDialog(MainForm.Theme t, string faderName, Hotkey up, Hotkey down, Hotkey mute, int step,
        IReadOnlyList<(string Fader, Hotkey Hk)>? others = null)
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        _t = t;
        _others = others ?? Array.Empty<(string, Hotkey)>();
        _up = Clone(up); _down = Clone(down); _mute = Clone(mute);

        Text = "Hotkeys";
        Font = UiFonts.Get(9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(W, H);
        BackColor = _t.Window;

        var root = _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 7, Padding = new Padding(16), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int r = 0; r < 7; r++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label { Text = $"Hotkeys — {faderName}", AutoSize = true, ForeColor = _t.Text, Font = UiFonts.Get(12.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4) };
        root.Controls.Add(title, 0, 0); root.SetColumnSpan(title, 3);

        var intro = new Label { Text = "Click a field, then press the key combo. F13–F24 work great with no modifier.", AutoSize = true, MaximumSize = new Size(W - 40, 0), ForeColor = _t.Subtle, Margin = new Padding(0, 0, 0, 10) };
        root.Controls.Add(intro, 0, 1); root.SetColumnSpan(intro, 3);

        _upBtn = AddRow(root, 2, "Volume up", Field.Up);
        _downBtn = AddRow(root, 3, "Volume down", Field.Down);
        _muteBtn = AddRow(root, 4, "Mute (toggle)", Field.Mute);

        // Step row.
        root.Controls.Add(new Label { Text = "Step", AutoSize = true, ForeColor = _t.Subtle, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, 5);
        var stepRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0) };
        _step = new MainForm.Stepper { Minimum = 1, Maximum = 25, Value = Math.Clamp(step, 1, 25) };
        stepRow.Controls.Add(_step);
        stepRow.Controls.Add(new Label { Text = "% per press", AutoSize = true, ForeColor = _t.Subtle, Margin = new Padding(6, 5, 0, 0) });
        root.Controls.Add(stepRow, 1, 5); root.SetColumnSpan(stepRow, 2);

        // Amber warning tone; reads on both light and dark backgrounds.
        _hint = new Label { AutoSize = true, MaximumSize = new Size(W - 40, 0), ForeColor = Color.FromArgb(0xD9, 0x8A, 0x28), Visible = false, Margin = new Padding(0, 10, 0, 0) };
        root.Controls.Add(_hint, 0, 6); root.SetColumnSpan(_hint, 3);

        // Bottom buttons, pinned below the grid.
        var btnRow = _btnRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent, Padding = new Padding(16, 8, 16, 12) };
        var save = MakeButton("Save", accent: true);
        save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = MakeButton("Cancel", accent: false);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        btnRow.Controls.Add(save);
        btnRow.Controls.Add(cancel);

        Controls.Add(root);
        Controls.Add(btnRow);
        AcceptButton = save;
        CancelButton = cancel;   // Esc closes when not capturing (see ProcessCmdKey)

        RefreshButtons();
        UpdateHint();

        Load += (_, _) =>
        {
            ApplyDark();
            _step.BackColor = _t.CtlBg; _step.ForeColor = _t.Text; _step.BorderColor = _t.CtlBorder; _step.ChevronColor = _t.Subtle; _step.Surround = _t.Window; _step.SizeToFont();
            FitHeight();
        };
    }

    RoundedButton AddRow(TableLayoutPanel root, int row, string label, Field field)
    {
        root.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = _t.Subtle, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, row);
        var btn = new RoundedButton
        {
            AutoSize = false, Height = 30, Anchor = AnchorStyles.Left | AnchorStyles.Right, Radius = 7,
            Margin = new Padding(0, 4, 0, 4), Surround = _t.Window,
            BackColor = _t.CtlBg, ForeColor = _t.Text,
        };
        btn.FlatAppearance.BorderColor = _t.CtlBorder;
        btn.Click += (_, _) => StartListening(field, btn);
        root.Controls.Add(btn, 1, row);

        var clear = new RoundedButton
        {
            Text = "✕", AutoSize = false, Size = new Size(30, 30), Radius = 7, Anchor = AnchorStyles.None,
            Margin = new Padding(6, 4, 0, 4), Surround = _t.Window, BackColor = _t.CtlBg, ForeColor = _t.Text,
            AccessibleName = $"Clear {label} binding",
        };
        clear.FlatAppearance.BorderColor = _t.CtlBorder;
        clear.Click += (_, _) => { Set(field, new Hotkey()); _listening = Field.None; RefreshButtons(); UpdateHint(); };
        root.Controls.Add(clear, 2, row);
        return btn;
    }

    void StartListening(Field f, RoundedButton btn)
    {
        _listening = f;
        RefreshButtons();
        btn.Text = "Press keys…  (Esc to cancel)";
        btn.FlatAppearance.BorderColor = _t.Accent;
        btn.Invalidate();
        btn.Focus();
    }

    // Capture in ProcessCmdKey (not the KeyDown event) so we also see dialog-nav
    // keys — Tab/Enter/arrows/Esc — which WinForms would otherwise consume before
    // KeyDown fires. While listening we swallow the chord and record it.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_listening == Field.None) return base.ProcessCmdKey(ref msg, keyData);

        Keys code = keyData & Keys.KeyCode;
        if (code == Keys.Escape) { _listening = Field.None; RefreshButtons(); return true; }
        if (code is Keys.Back or Keys.Delete) { Set(_listening, new Hotkey()); _listening = Field.None; RefreshButtons(); UpdateHint(); return true; }
        if (IsModifierOnly(code)) return true;   // consume; wait for the real key

        Set(_listening, new Hotkey
        {
            Vk = (int)code,
            Ctrl = (keyData & Keys.Control) != 0,
            Alt = (keyData & Keys.Alt) != 0,
            Shift = (keyData & Keys.Shift) != 0,
            Win = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0,
        });
        _listening = Field.None;
        RefreshButtons();
        UpdateHint();
        return true;
    }

    static bool IsModifierOnly(Keys k) => k is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
        or Keys.LWin or Keys.RWin;

    void Set(Field f, Hotkey h)
    {
        switch (f) { case Field.Up: _up = h; break; case Field.Down: _down = h; break; case Field.Mute: _mute = h; break; }
    }

    void RefreshButtons()
    {
        SetBtn(_upBtn, _up);
        SetBtn(_downBtn, _down);
        SetBtn(_muteBtn, _mute);
    }

    void SetBtn(RoundedButton b, Hotkey h)
    {
        b.Text = h.ToString();
        b.ForeColor = h.IsBound ? _t.Text : _t.Subtle;
        b.FlatAppearance.BorderColor = _t.CtlBorder;
        b.Invalidate();
    }

    void UpdateHint()
    {
        var lines = new List<string>();
        if (_up.IsBareCommonKey || _down.IsBareCommonKey || _mute.IsBareCommonKey)
            lines.Add("⚠ A bare everyday key will trigger whenever you press it normally. " +
                      "Use F13–F24 or add a modifier (Ctrl/Alt/Shift/Win).");
        if (_up.SameAs(_down) || _up.SameAs(_mute) || _down.SameAs(_mute))
            lines.Add("⚠ The same key is bound to two actions here — only the first " +
                      "(Up, then Down, then Mute) will fire.");
        foreach (var (fader, hk) in _others)
        {
            var mine = new[] { _up, _down, _mute }.FirstOrDefault(m => m.SameAs(hk));
            if (mine != null)
            {
                lines.Add($"Note: {mine} is also bound on “{fader}” — both faders will respond.");
                break;
            }
        }
        if (IsMediaKey(_up) || IsMediaKey(_down) || IsMediaKey(_mute))
            lines.Add("Note: media keys keep their normal Windows function too (they aren't swallowed).");

        _hint.Visible = lines.Count > 0;
        _hint.Text = string.Join("\n\n", lines);
        FitHeight();   // warnings can wrap to several lines
    }

    static bool IsMediaKey(Hotkey h) => h.IsBound && h.Vk >= 0xAD && h.Vk <= 0xB3;

    // Content-fit height (warnings grow the dialog), capped to the screen.
    void FitHeight()
    {
        if (!IsHandleCreated) return;
        int want = _root.PreferredSize.Height + _btnRow.PreferredSize.Height;
        int cap = Math.Max(LogicalToDeviceUnits(H),
            Screen.FromControl(this).WorkingArea.Height - LogicalToDeviceUnits(80));
        ClientSize = new Size(LogicalToDeviceUnits(W),
            Math.Min(Math.Max(LogicalToDeviceUnits(H), want), cap));
    }

    static Hotkey Clone(Hotkey h) => new() { Vk = h.Vk, Ctrl = h.Ctrl, Alt = h.Alt, Shift = h.Shift, Win = h.Win };

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
