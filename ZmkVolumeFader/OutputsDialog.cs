using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ZmkVolumeFader;

/// <summary>
/// Ranked output editor reached from Options ("Set Default Outputs…"). Each slider
/// gets an ordered list of outputs; the app drives the highest one that's plugged
/// in and switches automatically as devices come and go. Reorder with the arrows,
/// remove with ✕, and add from the picker (which lists every known output,
/// including ones not currently connected). The list scrolls for many sliders. The
/// owner reads <see cref="Result"/> (one list per slider) back on a Save result.
/// </summary>
sealed class OutputsDialog : Form
{
    public List<OutputPref>[] Result { get; private set; }

    readonly int _n;
    readonly string[] _labels;
    readonly MainForm.Theme _t;
    IReadOnlyList<OutputPref> _known;
    HashSet<string> _present;
    readonly MMDeviceEnumerator _enum = new();
    RoundedScrollPanel _scroll = null!;

    readonly ListBox[] _list;
    readonly RoundedComboBox[] _add;
    readonly RoundedButton[] _addBtn;

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public OutputsDialog(MainForm.Theme t, string[] labels, List<OutputPref>[] outs,
        IReadOnlyList<OutputPref> known, IEnumerable<string> presentIds)
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        _t = t;
        _n = outs.Length;
        _labels = labels;
        Result = outs.Select(Clone).ToArray();
        _known = known;
        _present = new HashSet<string>(presentIds);
        _list = new ListBox[_n]; _add = new RoundedComboBox[_n]; _addBtn = new RoundedButton[_n];

        Text = "Set Default Outputs";
        Font = UiFonts.Get(9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 640);
        BackColor = _t.Window;

        int rows = 1 + _n;
        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = rows, BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < rows; r++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            Text = "Each slider drives the top output that's plugged in. If it's unplugged the app drops to the next one, and takes it back over when it returns. Use the arrows to rank them.",
            AutoSize = true, MaximumSize = new Size(392, 0), ForeColor = _t.Subtle, Margin = new Padding(2, 0, 2, 10),
        }, 0, 0);
        for (int i = 0; i < _n; i++) root.Controls.Add(BuildFader(i, _labels[i]), 0, 1 + i);

        _scroll = new RoundedScrollPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(14, 14, 14, 0),
            ThumbColor = _t.CtlBorder, ThumbHoverColor = _t.Subtle,
        };
        _scroll.SetContent(root);

        // Footer: Refresh on the left, Save/Cancel on the right — pinned below.
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(14, 10, 14, 12) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var refresh = MakeButton("Refresh outputs", accent: false);
        refresh.Anchor = AnchorStyles.Left; refresh.Margin = new Padding(0);
        refresh.Click += (_, _) => RefreshDevices();
        footer.Controls.Add(refresh, 0, 0);
        var rightBtns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, BackColor = Color.Transparent, Margin = new Padding(0) };
        var save = MakeButton("Save", accent: true);
        save.Click += (_, _) => { CommitResults(); DialogResult = DialogResult.OK; Close(); };
        var cancel = MakeButton("Cancel", accent: false);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        rightBtns.Controls.Add(save);
        rightBtns.Controls.Add(cancel);
        footer.Controls.Add(rightBtns, 1, 0);

        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(_scroll, 0, 0);
        outer.Controls.Add(footer, 0, 1);
        Controls.Add(outer);

        AcceptButton = save;
        CancelButton = cancel;

        for (int i = 0; i < _n; i++) ReconcileNames(i);

        Load += (_, _) =>
        {
            ApplyDark();
            // Fit to content up to a cap (scrolls beyond); done here so it runs
            // after DPI/font scaling.
            int content = root.PreferredSize.Height + LogicalToDeviceUnits(14);
            int foot = footer.PreferredSize.Height;
            // Force the DPI-scaled width — auto-scale doesn't reliably widen a
            // FixedDialog, so the content would otherwise clip at 125%+. Height
            // is clamped to the screen too (720 logical = 1440 device at 200%);
            // cap stays >= min or Math.Clamp throws.
            int minH = LogicalToDeviceUnits(300);
            int cap = Math.Max(minH, Math.Min(LogicalToDeviceUnits(720),
                Screen.FromControl(this).WorkingArea.Height - LogicalToDeviceUnits(80)));
            ClientSize = new Size(LogicalToDeviceUnits(430), Math.Clamp(content + foot, minH, cap));
        };
        FormClosed += (_, _) => _enum.Dispose();
    }

    static List<OutputPref> Clone(IEnumerable<OutputPref> src) =>
        src.Select(p => new OutputPref { Id = p.Id, Name = p.Name }).ToList();

    Panel BuildFader(int idx, string name)
    {
        var card = new Panel { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Height = 232, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12), BackColor = _t.Card };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // header
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 132)); // list + reorder buttons
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // add row

        var header = new Label { Text = name, AutoSize = true, ForeColor = _t.Subtle, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 6) };
        t.Controls.Add(header, 0, 0);
        t.SetColumnSpan(header, 2);

        var lb = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 22,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            BackColor = _t.CtlBg,
            ForeColor = _t.Text,
            Margin = new Padding(0, 0, 8, 0),
        };
        lb.DrawItem += (s, e) => DrawListItem(lb, e);
        foreach (var p in Result[idx]) lb.Items.Add(p);
        _list[idx] = lb;
        t.Controls.Add(lb, 0, 1);

        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Anchor = AnchorStyles.Top, Margin = new Padding(0) };
        var up = MakeButton("▲", accent: false, surround: _t.Card); up.Margin = new Padding(0, 0, 0, 6); up.Click += (_, _) => MoveItem(idx, -1);
        var dn = MakeButton("▼", accent: false, surround: _t.Card); dn.Margin = new Padding(0, 0, 0, 6); dn.Click += (_, _) => MoveItem(idx, +1);
        var rm = MakeButton("✕", accent: false, surround: _t.Card); rm.Margin = new Padding(0); rm.Click += (_, _) => Remove(idx);
        foreach (var b in new[] { up, dn, rm }) { b.AutoSize = false; b.Size = new Size(40, 30); }
        col.Controls.Add(up); col.Controls.Add(dn); col.Controls.Add(rm);
        t.Controls.Add(col, 1, 1);

        // Combo stretches to fill the row; Add stays pinned at the right so it
        // can't be clipped at any scaling. Spans both columns of the card table.
        var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var add = new RoundedComboBox
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = _t.CtlBg, ForeColor = _t.Text,
            Surround = _t.Card, BoxColor = _t.CtlBg, BorderColor = _t.CtlBorder, ChevronColor = _t.Subtle,
            Margin = new Padding(0, 0, 8, 0),
        };
        _add[idx] = add;
        var addBtn = MakeButton("Add", accent: false, surround: _t.Card);
        addBtn.Anchor = AnchorStyles.Right;
        addBtn.Click += (_, _) => Add(idx);
        _addBtn[idx] = addBtn;
        addRow.Controls.Add(add, 0, 0);
        addRow.Controls.Add(addBtn, 1, 0);
        t.Controls.Add(addRow, 0, 2);
        t.SetColumnSpan(addRow, 2);

        card.Controls.Add(t);
        RefreshAdd(idx);
        return card;
    }

    void DrawListItem(ListBox lb, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= lb.Items.Count) { e.DrawBackground(); return; }
        var p = (OutputPref)lb.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        bool connected = _present.Contains(p.Id);

        Color bg = sel ? _t.Accent : _t.CtlBg;
        using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);

        Color fg = sel ? AccentText() : (connected ? _t.Text : _t.Subtle);
        string text = $"{e.Index + 1}.   {p.Name}" + (connected ? "" : "    (not connected)");
        var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, lb.Font, r, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    void MoveItem(int idx, int dir)
    {
        var lb = _list[idx];
        int i = lb.SelectedIndex, j = i + dir;
        if (i < 0 || j < 0 || j >= lb.Items.Count) return;
        var item = lb.Items[i];
        lb.Items.RemoveAt(i);
        lb.Items.Insert(j, item);
        lb.SelectedIndex = j;
        lb.Invalidate();
    }

    void Remove(int idx)
    {
        var lb = _list[idx];
        int i = lb.SelectedIndex;
        if (i < 0) return;
        lb.Items.RemoveAt(i);
        if (lb.Items.Count > 0) lb.SelectedIndex = Math.Min(i, lb.Items.Count - 1);
        lb.Invalidate();
        RefreshAdd(idx);
    }

    void Add(int idx)
    {
        var lb = _list[idx];
        if (_add[idx].SelectedItem is OutputPref k)
        {
            lb.Items.Add(new OutputPref { Id = k.Id, Name = k.Name });
            lb.SelectedIndex = lb.Items.Count - 1;
            lb.Invalidate();
            RefreshAdd(idx);
        }
    }

    // Repopulate a slider's add-picker with known outputs not already in its list.
    void RefreshAdd(int idx)
    {
        var lb = _list[idx];
        var have = lb.Items.Cast<OutputPref>().Select(p => p.Id).ToHashSet();
        var combo = _add[idx];
        combo.BeginUpdate();
        combo.Items.Clear();
        foreach (var k in _known)
            if (!have.Contains(k.Id)) combo.Items.Add(k);
        combo.EndUpdate();
        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
        _addBtn[idx].Enabled = combo.Items.Count > 0;
        combo.Invalidate();
    }

    // Re-scan the system's outputs (e.g. after plugging something in) so the add
    // pickers and the "(not connected)" markers are current.
    void RefreshDevices()
    {
        try
        {
            var known = new List<OutputPref>();
            foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Render,
                         DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled))
            {
                try { known.Add(new OutputPref { Id = d.ID, Name = d.FriendlyName }); }
                finally { try { d.Dispose(); } catch { } }
            }
            var present = new HashSet<string>();
            foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { present.Add(d.ID); }
                finally { try { d.Dispose(); } catch { } }
            }
            _known = known.OrderBy(o => o.Name).ToList();
            _present = present;
        }
        catch { return; }

        for (int i = 0; i < _n; i++) { ReconcileNames(i); RefreshAdd(i); }
    }

    // Update a slider's list entries' display names from the known-device list.
    void ReconcileNames(int idx)
    {
        var lb = _list[idx];
        for (int i = 0; i < lb.Items.Count; i++)
        {
            var p = (OutputPref)lb.Items[i];
            var k = _known.FirstOrDefault(x => x.Id == p.Id);
            if (k != null && !string.IsNullOrEmpty(k.Name)) p.Name = k.Name;
        }
        lb.Invalidate();
    }

    void CommitResults()
    {
        for (int i = 0; i < _n; i++) Result[i] = _list[i].Items.Cast<OutputPref>().ToList();
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

    void ApplyDark()
    {
        int v = _t.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
        foreach (var lb in _list)
            if (lb.IsHandleCreated) SetWindowTheme(lb.Handle, _t.Dark ? "DarkMode_Explorer" : null, null);
        foreach (var c in _add)
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, _t.Dark ? "DarkMode_CFD" : null, null);
    }
}
