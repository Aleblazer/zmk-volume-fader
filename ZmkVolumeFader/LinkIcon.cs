using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace ZmkVolumeFader;

/// <summary>
/// A small clickable icon that opens a URL. Renders either a themed vector
/// (an SVG-style path scaled from its viewBox) or a raster image, with a hand
/// cursor and a hover tint.
/// </summary>
sealed class LinkIcon : Control
{
    readonly GraphicsPath? _vector;
    readonly float _viewBox;
    readonly Image? _image;
    readonly string _url;
    bool _hover;

    public Color IconColor { get; set; } = Color.Gray;
    public Color HoverColor { get; set; } = Color.White;

    public LinkIcon(string url, GraphicsPath vector, float viewBox = 24f)
    {
        _url = url; _vector = vector; _viewBox = viewBox;
        Init();
    }

    public LinkIcon(string url, Image image)
    {
        _url = url; _image = image;
        Init();
    }

    void Init()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Click += (_, _) => Open(_url);
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (Width < 2 || Height < 2) return;

        if (_vector != null)
        {
            float s = Math.Min(Width, Height) / _viewBox;
            var st = g.Save();
            g.TranslateTransform((Width - _viewBox * s) / 2f, (Height - _viewBox * s) / 2f);
            g.ScaleTransform(s, s);
            using var b = new SolidBrush(_hover ? HoverColor : IconColor);
            g.FillPath(b, _vector);
            g.Restore(st);
        }
        else if (_image != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float s = Math.Min(Width / (float)_image.Width, Height / (float)_image.Height);
            float w = _image.Width * s, h = _image.Height * s;
            g.DrawImage(_image, (Width - w) / 2f, (Height - h) / 2f, w, h);
        }
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — nothing useful to do */ }
    }
}

/// <summary>Vector icon paths (SVG "d" data) and a minimal path parser.</summary>
static class Icons
{
    // GitHub "mark" (simple-icons), 24x24 viewBox, all cubic beziers.
    public const string GitHub =
        "M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12";

    // Load an embedded image resource by filename suffix (e.g. "splitlogic.png").
    // Returns an independent Bitmap so the resource stream can be released.
    public static Image? LoadEmbedded(string endsWith)
    {
        var asm = typeof(Icons).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var tmp = Image.FromStream(s);
        return new Bitmap(tmp);
    }

    public static GraphicsPath Path(string d)
    {
        var p = new GraphicsPath();
        int i = 0, n = d.Length;
        float cx = 0, cy = 0, sx = 0, sy = 0;
        char cmd = ' ';

        float Num()
        {
            while (i < n && (d[i] == ' ' || d[i] == ',' || d[i] == '\n' || d[i] == '\t')) i++;
            int s = i;
            if (i < n && (d[i] == '-' || d[i] == '+')) i++;
            while (i < n && char.IsDigit(d[i])) i++;
            if (i < n && d[i] == '.') { i++; while (i < n && char.IsDigit(d[i])) i++; }
            if (i < n && (d[i] == 'e' || d[i] == 'E')) { i++; if (i < n && (d[i] == '-' || d[i] == '+')) i++; while (i < n && char.IsDigit(d[i])) i++; }
            return float.Parse(d.Substring(s, i - s), CultureInfo.InvariantCulture);
        }

        bool PeekNum()
        {
            int j = i;
            while (j < n && (d[j] == ' ' || d[j] == ',' || d[j] == '\n' || d[j] == '\t')) j++;
            return j < n && (char.IsDigit(d[j]) || d[j] == '-' || d[j] == '+' || d[j] == '.');
        }

        while (i < n)
        {
            if (char.IsLetter(d[i])) { cmd = d[i]; i++; }
            switch (cmd)
            {
                case 'M': cx = Num(); cy = Num(); sx = cx; sy = cy; p.StartFigure(); cmd = 'L'; break;
                case 'm': cx += Num(); cy += Num(); sx = cx; sy = cy; p.StartFigure(); cmd = 'l'; break;
                case 'L': { float x = Num(), y = Num(); p.AddLine(cx, cy, x, y); cx = x; cy = y; } break;
                case 'l': { float x = cx + Num(), y = cy + Num(); p.AddLine(cx, cy, x, y); cx = x; cy = y; } break;
                case 'H': { float x = Num(); p.AddLine(cx, cy, x, cy); cx = x; } break;
                case 'h': { float x = cx + Num(); p.AddLine(cx, cy, x, cy); cx = x; } break;
                case 'V': { float y = Num(); p.AddLine(cx, cy, cx, y); cy = y; } break;
                case 'v': { float y = cy + Num(); p.AddLine(cx, cy, cx, y); cy = y; } break;
                case 'C': { float x1 = Num(), y1 = Num(), x2 = Num(), y2 = Num(), x = Num(), y = Num(); p.AddBezier(cx, cy, x1, y1, x2, y2, x, y); cx = x; cy = y; } break;
                case 'c': { float x1 = cx + Num(), y1 = cy + Num(), x2 = cx + Num(), y2 = cy + Num(), x = cx + Num(), y = cy + Num(); p.AddBezier(cx, cy, x1, y1, x2, y2, x, y); cx = x; cy = y; } break;
                case 'Z': case 'z': p.CloseFigure(); cx = sx; cy = sy; break;
                default: if (!PeekNum()) i++; break;   // skip anything unexpected
            }
        }
        return p;
    }
}
