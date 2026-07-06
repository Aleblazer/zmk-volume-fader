using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Manage app categories (reached from Options). Create/rename/delete categories,
/// and tick which seen apps belong to the selected one. A category slider moves
/// every app in the group together. The owner reads <see cref="Result"/> on Save.
/// </summary>
sealed class CategoriesDialog : Form
{
    public List<Category> Result { get; private set; }

    readonly MainForm.Theme _t;
    readonly (string Key, string Name)[] _apps;   // all seen apps, sorted by name
    readonly IReadOnlyDictionary<string, Image?>? _appIcons;

    readonly TextBox _name = new() { BorderStyle = BorderStyle.FixedSingle };
    readonly ListBox _catList = new() { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
    readonly ListBox _appList = new() { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public CategoriesDialog(MainForm.Theme t, List<Category> categories, IReadOnlyDictionary<string, string> knownApps,
        IReadOnlyDictionary<string, Image?>? appIcons = null)
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        _t = t;
        _appIcons = appIcons;
        Result = categories.Select(c => new Category { Name = c.Name, AppKeys = new(c.AppKeys) }).ToList();
        _apps = knownApps.Select(kv => (kv.Key, kv.Value)).OrderBy(a => a.Value, StringComparer.OrdinalIgnoreCase).ToArray();

        Text = "Manage Categories";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 540);
        BackColor = _t.Window;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // name row
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // new/rename/delete
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116)); // categories list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // "apps in category"
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // apps list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // save/cancel

        var nameRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.Controls.Add(new Label { Text = "Name", AutoSize = true, ForeColor = _t.Subtle, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        _name.BackColor = _t.CtlBg; _name.ForeColor = _t.Text; _name.Anchor = AnchorStyles.Left | AnchorStyles.Right; _name.Margin = new Padding(0, 3, 0, 3);
        nameRow.Controls.Add(_name, 1, 0);
        root.Controls.Add(nameRow, 0, 0);

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        var add = MakeButton("New", false); add.Margin = new Padding(0, 0, 6, 0); add.Click += (_, _) => NewCategory();
        var rename = MakeButton("Rename", false); rename.Margin = new Padding(0, 0, 6, 0); rename.Click += (_, _) => RenameCategory();
        var del = MakeButton("Delete", false); del.Click += (_, _) => DeleteCategory();
        actions.Controls.Add(add); actions.Controls.Add(rename); actions.Controls.Add(del);
        root.Controls.Add(actions, 0, 1);

        _catList.BackColor = _t.CtlBg; _catList.ForeColor = _t.Text; _catList.Dock = DockStyle.Fill; _catList.Margin = new Padding(0, 0, 0, 8);
        _catList.DrawItem += (s, e) => DrawCat(e);
        _catList.SelectedIndexChanged += (_, _) => OnCatSelected();
        foreach (var c in Result) _catList.Items.Add(c);
        root.Controls.Add(_catList, 0, 2);

        root.Controls.Add(new Label { Text = "Apps in this category — click to toggle:", AutoSize = true, ForeColor = _t.Subtle, Margin = new Padding(0, 0, 0, 4) }, 0, 3);

        _appList.BackColor = _t.CtlBg; _appList.ForeColor = _t.Text; _appList.Dock = DockStyle.Fill; _appList.Margin = new Padding(0);
        _appList.DrawItem += (s, e) => DrawApp(e);
        _appList.MouseDown += (s, e) => ToggleApp(_appList.IndexFromPoint(e.Location));
        foreach (var a in _apps) _appList.Items.Add(a.Name);
        root.Controls.Add(_appList, 0, 4);

        var btnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        var save = MakeButton("Save", true); save.Click += (_, _) => { CommitName(); DialogResult = DialogResult.OK; Close(); };
        var cancel = MakeButton("Cancel", false); cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        btnRow.Controls.Add(save); btnRow.Controls.Add(cancel);
        root.Controls.Add(btnRow, 0, 5);

        Controls.Add(root);
        CancelButton = cancel;

        if (Result.Count > 0) _catList.SelectedIndex = 0; else OnCatSelected();
        Load += (_, _) =>
        {
            ApplyDark();
            // Row height tracks the DPI-scaled font so text isn't clipped at 125%+.
            _catList.ItemHeight = _catList.Font.Height + LogicalToDeviceUnits(8);
            _appList.ItemHeight = _appList.Font.Height + LogicalToDeviceUnits(8);
        };
    }

    Category? Selected => _catList.SelectedIndex >= 0 ? Result[_catList.SelectedIndex] : null;

    void NewCategory()
    {
        CommitName();
        var c = new Category { Name = UniqueName("New category") };
        Result.Add(c);
        _catList.Items.Add(c);
        _catList.SelectedIndex = _catList.Items.Count - 1;
        _name.Focus();
        _name.SelectAll();
    }

    void RenameCategory()
    {
        if (Selected == null) return;
        CommitName();
        _name.Focus();
        _name.SelectAll();
    }

    void DeleteCategory()
    {
        int i = _catList.SelectedIndex;
        if (i < 0) return;
        Result.RemoveAt(i);
        _catList.Items.RemoveAt(i);
        if (_catList.Items.Count > 0) _catList.SelectedIndex = Math.Min(i, _catList.Items.Count - 1);
        else OnCatSelected();
    }

    string UniqueName(string baseName)
    {
        string n = baseName;
        int k = 2;
        while (Result.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))) n = $"{baseName} {k++}";
        return n;
    }

    void OnCatSelected()
    {
        _name.Text = Selected?.Name ?? "";
        _appList.Invalidate();
    }

    // Apply the name box to the selected category (on rename / focus change / save).
    void CommitName()
    {
        if (Selected is not { } c) return;
        var nm = _name.Text.Trim();
        if (nm.Length == 0 || nm == c.Name) return;
        c.Name = UniqueName(nm);
        _catList.Invalidate();
    }

    void ToggleApp(int index)
    {
        if (index < 0 || Selected is not { } c) return;
        string key = _apps[index].Key;
        if (!c.AppKeys.Remove(key)) c.AppKeys.Add(key);
        _appList.Invalidate();
    }

    void DrawCat(DrawItemEventArgs e)
    {
        if (e.Index < 0) { e.DrawBackground(); return; }
        var c = (Category)_catList.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? _t.Accent : _t.CtlBg)) e.Graphics.FillRectangle(b, e.Bounds);
        Color fg = sel ? AccentText() : _t.Text;
        var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, $"{c.Name}  ({c.AppKeys.Count})", _catList.Font, r, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    void DrawApp(DrawItemEventArgs e)
    {
        if (e.Index < 0) { e.DrawBackground(); return; }
        bool member = Selected != null && Selected.AppKeys.Contains(_apps[e.Index].Key);
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? _t.Accent : _t.CtlBg)) e.Graphics.FillRectangle(b, e.Bounds);
        Color fg = sel ? AccentText() : _t.Text;
        // Checkbox glyph in a fixed lead column.
        var boxRect = new Rectangle(e.Bounds.X + LogicalToDeviceUnits(6), e.Bounds.Y, LogicalToDeviceUnits(20), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, member ? "☑" : "☐", _appList.Font, boxRect, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        int left = boxRect.Right + LogicalToDeviceUnits(2);
        // App icon, if we have one.
        if (_appIcons != null && _appIcons.TryGetValue(_apps[e.Index].Key, out var img) && img != null)
        {
            int isz = LogicalToDeviceUnits(18);
            var ir = new Rectangle(left, e.Bounds.Y + (e.Bounds.Height - isz) / 2, isz, isz);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(img, ir);
            left = ir.Right + LogicalToDeviceUnits(5);
        }
        var r = new Rectangle(left, e.Bounds.Y, e.Bounds.Right - left - 2, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, _apps[e.Index].Name, _appList.Font, r, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
        foreach (var lb in new[] { _catList, _appList })
            if (lb.IsHandleCreated) SetWindowTheme(lb.Handle, _t.Dark ? "DarkMode_Explorer" : null, null);
    }
}
