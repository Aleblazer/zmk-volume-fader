using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

static class RoundGfx
{
    public static GraphicsPath Round(RectangleF r, float rad)
    {
        float d = rad * 2;
        var p = new GraphicsPath();
        if (d <= 0 || r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>Flat button with anti-aliased rounded corners. The true corners are
/// painted with <see cref="Surround"/> (the container colour) so they blend in
/// without needing a jagged clip Region. Lightens on hover, darkens on press.</summary>
internal sealed class RoundedButton : Button
{
    public Color Surround { get; set; } = Color.Black;
    public int Radius { get; set; } = 8;
    bool _hover, _down;

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Surround);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = RoundGfx.Round(r, Radius);

        var fill = BackColor;
        if (_down) fill = RoundGfx.Blend(fill, Color.Black, 0.14f);
        else if (_hover) fill = RoundGfx.Blend(fill, Color.White, 0.10f);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);

        var border = FlatAppearance.BorderColor;
        if (border.A > 0 && border != BackColor)
            using (var p = new Pen(border, 1f)) g.DrawPath(p, path);

        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>DropDownList combo whose closed box is custom-painted as a rounded,
/// themed rect with a chevron (the popup list stays native, themed via
/// SetWindowTheme). Items can still be owner-drawn via the DrawItem event.</summary>
internal sealed class RoundedComboBox : ComboBox
{
    public Color Surround { get; set; } = Color.Black;
    public Color BoxColor { get; set; } = Color.White;
    public Color BorderColor { get; set; } = Color.Gray;
    public Color ChevronColor { get; set; } = Color.Gray;
    public int Radius { get; set; } = 8;
    // Shown (in PlaceholderColor) when nothing is selected.
    public string? Placeholder { get; set; }
    public Color PlaceholderColor { get; set; } = Color.Gray;

    const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc; public bool fErase; public RECT rcPaint; public bool fRestore;
        public bool fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

    public RoundedComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
        if (m.Msg == WM_PAINT)
        {
            IntPtr hdc = BeginPaint(Handle, out var ps);
            try
            {
                // Paint straight to the (opaque) DC so ClearType text stays crisp;
                // an ARGB back-buffer makes dark-on-light text look bold/fringed.
                using var g = Graphics.FromHdc(hdc);
                PaintClosed(g);
            }
            finally { EndPaint(Handle, ref ps); }
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    void PaintClosed(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Surround);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = RoundGfx.Round(r, Radius))
        {
            using (var b = new SolidBrush(BoxColor)) g.FillPath(b, path);
            using (var p = new Pen(BorderColor, 1f)) g.DrawPath(p, path);
        }

        string text = GetItemText(SelectedItem) ?? string.Empty;
        Color textColor = ForeColor;
        if (SelectedIndex < 0 && string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(Placeholder))
        {
            text = Placeholder!;
            textColor = PlaceholderColor;
        }
        var textRect = new Rectangle(9, 0, Width - 9 - 22, Height);
        TextRenderer.DrawText(g, text, Font, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        float cxv = Width - 15, cyv = Height / 2f;
        using var cp = new Pen(ChevronColor, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(cp, new[] { new PointF(cxv - 4, cyv - 2), new PointF(cxv, cyv + 2.5f), new PointF(cxv + 4, cyv - 2) });
    }
}
