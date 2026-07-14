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

    // Old name -> new name for every pre-existing category renamed here, filled
    // on Save. The owner re-points fader targets so they follow the rename.
    public Dictionary<string, string> Renamed { get; } = new();

    readonly MainForm.Theme _t;
    readonly (string Key, string Name, bool Live)[] _apps;   // all seen apps, sorted by name
    readonly List<(string Key, string Name, bool Live)> _filteredApps = new();
    readonly IReadOnlyDictionary<string, Image?>? _appIcons;
    // Each clone's name when the dialog opened (keys are the Result objects).
    readonly Dictionary<Category, string> _origNames = new();
    int _lastCat = -1;   // previously-selected index, for commit-on-selection-change

    readonly TextBox _name = new() { BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox _search = new() { BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Search apps…" };
    readonly ListBox _catList = new() { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
    readonly ListBox _appList = new() { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string? subApp, string? subId);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public CategoriesDialog(MainForm.Theme t, List<Category> categories, IReadOnlyDictionary<string, string> knownApps,
        IReadOnlyDictionary<string, Image?>? appIcons = null, IReadOnlySet<string>? liveApps = null)
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        _t = t;
        _appIcons = appIcons;
        Result = categories.Select(c => new Category { Name = c.Name, AppKeys = new(c.AppKeys) }).ToList();
        foreach (var c in Result) _origNames[c] = c.Name;
        _apps = knownApps.Select(kv => (kv.Key, kv.Value, liveApps?.Contains(kv.Key) == true))
            .OrderBy(a => a.Value, StringComparer.OrdinalIgnoreCase).ToArray();
        _filteredApps.AddRange(_apps);

        Text = "Manage Categories";
        Font = UiFonts.Get(9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 540);
        BackColor = _t.Window;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(14), BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // name row
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // new/rename/delete
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116)); // categories list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // "apps in category"
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // app search
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

        _search.BackColor = _t.CtlBg;
        _search.ForeColor = _t.Text;
        _search.Dock = DockStyle.Fill;
        _search.Margin = new Padding(0, 0, 0, 6);
        _search.TextChanged += (_, _) => RebuildAppFilter();
        root.Controls.Add(_search, 0, 4);

        _appList.BackColor = _t.CtlBg; _appList.ForeColor = _t.Text; _appList.Dock = DockStyle.Fill; _appList.Margin = new Padding(0);
        _appList.DrawItem += (s, e) => DrawApp(e);
        _appList.MouseDown += (s, e) => ToggleApp(_appList.IndexFromPoint(e.Location));
        foreach (var a in _filteredApps) _appList.Items.Add(AppText(a));
        root.Controls.Add(_appList, 0, 5);

        var btnRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        var save = MakeButton("Save", true); save.Click += (_, _) => { CommitName(); CollectRenames(); DialogResult = DialogResult.OK; Close(); };
        var cancel = MakeButton("Cancel", false); cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        btnRow.Controls.Add(save); btnRow.Controls.Add(cancel);
        root.Controls.Add(btnRow, 0, 6);

        Controls.Add(root);
        CancelButton = cancel;

        if (Result.Count > 0) _catList.SelectedIndex = 0; else OnCatSelected();
        Load += (_, _) =>
        {
            ApplyDark();
            // Force the DPI-scaled size — auto-scale doesn't reliably resize a
            // FixedDialog, so at 125%+ the content would clip against a ~440px window.
            ClientSize = new Size(LogicalToDeviceUnits(440),
                Math.Min(LogicalToDeviceUnits(540),
                    Math.Max(LogicalToDeviceUnits(300),
                        Screen.FromControl(this).WorkingArea.Height - LogicalToDeviceUnits(80))));
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
        // Indices shift under the removed row — a stale _lastCat would let the
        // selection-change commit rename the *next* category to the deleted
        // one's name.
        _lastCat = -1;
        _catList.Items.RemoveAt(i);
        if (_catList.Items.Count > 0) _catList.SelectedIndex = Math.Min(i, _catList.Items.Count - 1);
        else OnCatSelected();
    }

    string UniqueName(string baseName, Category? self = null)
    {
        string n = baseName;
        int k = 2;
        while (Result.Any(c => c != self && string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))) n = $"{baseName} {k++}";
        return n;
    }

    void OnCatSelected()
    {
        // Commit any name typed for the previously-selected category first, so
        // clicking another category doesn't silently discard the rename.
        CommitNameTo(_lastCat);
        _lastCat = _catList.SelectedIndex;
        _name.Text = Selected?.Name ?? "";
        _appList.Invalidate();
    }

    // Apply the name box to the selected category (on rename / selection change / save).
    void CommitName() => CommitNameTo(_catList.SelectedIndex);

    void CommitNameTo(int index)
    {
        if (index < 0 || index >= Result.Count) return;
        var c = Result[index];
        // '#' marks built-in sentinels (System Sounds, Everything Else) — a
        // user category can't start with it or it would collide with them.
        var nm = _name.Text.Trim().TrimStart('#').Trim();
        if (nm.Length == 0 || nm == c.Name) return;
        c.Name = UniqueName(nm, c);
        _catList.Invalidate();
    }

    // Old->new names of pre-existing categories whose name changed (see Renamed).
    void CollectRenames()
    {
        Renamed.Clear();
        foreach (var c in Result)
            if (_origNames.TryGetValue(c, out var old) && old != c.Name)
                Renamed[old] = c.Name;
    }

    void ToggleApp(int index)
    {
        if (index < 0 || Selected is not { } c) return;
        string key = _filteredApps[index].Key;
        int removed = c.AppKeys.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) c.AppKeys.Add(key);
        _appList.Invalidate();
    }

    void RebuildAppFilter()
    {
        string query = _search.Text.Trim();
        _filteredApps.Clear();
        _filteredApps.AddRange(query.Length == 0 ? _apps : _apps.Where(a =>
            a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || a.Key.Contains(query, StringComparison.OrdinalIgnoreCase)));
        _appList.BeginUpdate();
        _appList.Items.Clear();
        foreach (var app in _filteredApps) _appList.Items.Add(AppText(app));
        _appList.EndUpdate();
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
        if (e.Index >= _filteredApps.Count) return;
        var app = _filteredApps[e.Index];
        bool member = Selected != null && Selected.AppKeys.Contains(app.Key, StringComparer.OrdinalIgnoreCase);
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? _t.Accent : _t.CtlBg)) e.Graphics.FillRectangle(b, e.Bounds);
        Color fg = sel ? AccentText() : app.Live ? _t.Text : _t.Subtle;
        // Checkbox glyph in a fixed lead column.
        var boxRect = new Rectangle(e.Bounds.X + LogicalToDeviceUnits(6), e.Bounds.Y, LogicalToDeviceUnits(20), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, member ? "☑" : "☐", _appList.Font, boxRect, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        int left = boxRect.Right + LogicalToDeviceUnits(2);
        // App icon, if we have one.
        if (_appIcons != null && _appIcons.TryGetValue(app.Key, out var img) && img != null)
        {
            int isz = LogicalToDeviceUnits(18);
            var ir = new Rectangle(left, e.Bounds.Y + (e.Bounds.Height - isz) / 2, isz, isz);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(img, ir);
            left = ir.Right + LogicalToDeviceUnits(5);
        }
        var r = new Rectangle(left, e.Bounds.Y, e.Bounds.Right - left - 2, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, AppText(app), _appList.Font, r, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    static string AppText((string Key, string Name, bool Live) app) =>
        app.Live ? app.Name : $"{app.Name}  (not running)";

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
