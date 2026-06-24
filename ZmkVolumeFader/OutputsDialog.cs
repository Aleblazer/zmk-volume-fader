using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ZmkVolumeFader;

/// <summary>
/// Ranked output editor reached from Options ("Set Default Outputs…"). Each fader
/// gets an ordered list of outputs; the app drives the highest one that's plugged
/// in and switches automatically as devices come and go. Reorder with the arrows,
/// remove with ✕, and add from the picker (which lists every known output,
/// including ones that aren't currently connected). The owner reads <see cref="Left"/>
/// and <see cref="Right"/> back on a Save result.
/// </summary>
sealed class OutputsDialog : Form
{
    public List<OutputPref> LeftOutputs { get; private set; }
    public List<OutputPref> RightOutputs { get; private set; }

    readonly MainForm.Theme _t;
    IReadOnlyList<OutputPref> _known;
    HashSet<string> _present;
    readonly MMDeviceEnumerator _enum = new();

    readonly ListBox[] _list = new ListBox[2];
    readonly RoundedComboBox[] _add = new RoundedComboBox[2];
    readonly RoundedButton[] _addBtn = new RoundedButton[2];

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public OutputsDialog(MainForm.Theme t, List<OutputPref> left, List<OutputPref> right,
        IReadOnlyList<OutputPref> known, IEnumerable<string> presentIds)
    {
        _t = t;
        LeftOutputs = Clone(left);
        RightOutputs = Clone(right);
        _known = known;
        _present = new HashSet<string>(presentIds);

        Text = "Set Default Outputs";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(430, 604);
        BackColor = _t.Window;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 4; r++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            Text = "Each fader drives the top output that's plugged in. If it's unplugged the app drops to the next one, and takes it back over when it returns. Use the arrows to rank them.",
            AutoSize = true, MaximumSize = new Size(398, 0), ForeColor = _t.Subtle, Margin = new Padding(2, 0, 2, 10),
        }, 0, 0);
        root.Controls.Add(BuildFader(0, "Left fader"), 0, 1);
        root.Controls.Add(BuildFader(1, "Right fader"), 0, 2);

        // Footer: Refresh on the left, Save/Cancel on the right.
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 16, 0, 0) };
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
        root.Controls.Add(footer, 0, 3);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        // Fill in friendly names for any entries remembered while disconnected.
        ReconcileNames(0);
        ReconcileNames(1);

        Load += (_, _) =>
        {
            ApplyDark();
            // Fit the dialog snugly to its content. Done here (not in the ctor) so
            // it runs after DPI/font scaling, otherwise the form ends up taller
            // than its content and leaves dead space below the footer.
            int h = root.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
            ClientSize = new Size(ClientSize.Width, h);
        };
        FormClosed += (_, _) => _enum.Dispose();
    }

    static List<OutputPref> Clone(IEnumerable<OutputPref> src) =>
        src.Select(p => new OutputPref { Id = p.Id, Name = p.Name }).ToList();

    Panel BuildFader(int idx, string name)
    {
        var card = new Panel { Width = 398, Height = 232, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12), BackColor = _t.Card };

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
        foreach (var p in (idx == 0 ? LeftOutputs : RightOutputs)) lb.Items.Add(p);
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

    // Repopulate a fader's add-picker with known outputs not already in its list.
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
    // pickers and the "(not connected)" markers are current, and fill in friendly
    // names for entries that were remembered while their device was disconnected.
    void RefreshDevices()
    {
        try
        {
            _known = _enum.EnumerateAudioEndPoints(DataFlow.Render,
                    DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled)
                .Select(d => new OutputPref { Id = d.ID, Name = d.FriendlyName })
                .OrderBy(o => o.Name)
                .ToList();
            _present = _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => d.ID).ToHashSet();
        }
        catch { return; }

        ReconcileNames(0);
        ReconcileNames(1);
        RefreshAdd(0);
        RefreshAdd(1);
    }

    // Update list entries' display names from the known-device list (keeps a
    // sensible label even for a device that was added while unplugged).
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
        LeftOutputs = _list[0].Items.Cast<OutputPref>().ToList();
        RightOutputs = _list[1].Items.Cast<OutputPref>().ToList();
    }

    // surround fills the rounded corners — pass the colour of the container the
    // button sits on (card vs. window) so the corners blend in instead of showing
    // grey bits.
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
