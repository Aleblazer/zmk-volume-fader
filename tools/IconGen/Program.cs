using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

// Generates a multi-size .ico: the ZMK monogram (white, bold) on a green
// diagonal gradient rounded square — the app/window/tray icon.
// Usage: dotnet run --project tools/IconGen -- <output.ico>

string outPath = args.Length > 0 ? args[0] : "app.ico";

// Green diagonal gradient (top-left -> bottom-right).
Color top = ColorTranslator.FromHtml("#46E07A");   // bright green
Color bot = ColorTranslator.FromHtml("#0C8A52");   // emerald

// BMP/DIB frames (not PNG) so the .ico loads on every runtime, including
// .NET Framework's Icon.ToBitmap. 128 is the largest; Explorer upscales for 256.
int[] sizes = { 128, 64, 48, 32, 24, 16 };
var frames = new List<byte[]>();

foreach (int s in sizes)
{
    using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        var rect = new RectangleF(0, 0, s, s);
        float radius = s * 0.22f;
        using (var path = RoundedRect(rect, radius))
        using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), top, bot))
            g.FillPath(brush, path);

        // Fit "ZMK" to ~78% of the width.
        const string text = "ZMK";
        using var fam = new FontFamily("Segoe UI");
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        float target = s * 0.78f;
        float emSize = s * 0.42f;
        for (int i = 0; i < 10; i++)
        {
            using var probe = new Font(fam, emSize, FontStyle.Bold, GraphicsUnit.Pixel);
            float w = g.MeasureString(text, probe).Width;
            if (w <= target) break;
            emSize *= target / w;
        }
        using var font = new Font(fam, emSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        g.DrawString(text, font, white, rect, fmt);
    }

    frames.Add(BmpFrame(bmp));
}

WriteIco(outPath, sizes, frames);

// Sanity-check that the .ico loads and resolves a small frame.
using (var probe = new Icon(outPath))
using (var probe16 = new Icon(outPath, new Size(16, 16)))
using (var bm = probe.ToBitmap())
    Console.WriteLine($"Wrote {outPath}: {frames.Count} frames, base {probe.Width}x{probe.Height}, " +
                      $"16px frame {probe16.Width}px, ToBitmap {bm.Width}x{bm.Height}");

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    float d = radius * 2;
    var p = new GraphicsPath();
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

// Encode a bitmap as an uncompressed icon frame: BITMAPINFOHEADER (doubled
// height for the XOR image + AND mask), 32bpp BGRA bottom-up, then a zeroed
// 1bpp AND mask (alpha carries transparency on modern Windows).
static byte[] BmpFrame(Bitmap bmp)
{
    int w = bmp.Width, h = bmp.Height;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int stride = Math.Abs(data.Stride);
    byte[] px = new byte[stride * h];
    for (int y = 0; y < h; y++)
        Marshal.Copy(data.Scan0 + y * data.Stride, px, y * stride, stride);
    bmp.UnlockBits(data);

    using var ms = new MemoryStream();
    using var w2 = new BinaryWriter(ms);
    w2.Write(40);              // biSize
    w2.Write(w);               // biWidth
    w2.Write(h * 2);           // biHeight (XOR + AND)
    w2.Write((short)1);        // biPlanes
    w2.Write((short)32);       // biBitCount
    w2.Write(0);               // biCompression (BI_RGB)
    w2.Write(0);               // biSizeImage
    w2.Write(0); w2.Write(0);  // pels-per-meter x/y
    w2.Write(0); w2.Write(0);  // clrUsed / clrImportant
    for (int y = h - 1; y >= 0; y--)   // XOR pixels, bottom-up
        w2.Write(px, y * stride, w * 4);
    byte[] andRow = new byte[((w + 31) / 32) * 4];   // 1bpp, all-zero = opaque
    for (int y = 0; y < h; y++)
        w2.Write(andRow);
    return ms.ToArray();
}

// Assemble the .ico container (ICONDIR + entries + frame data).
static void WriteIco(string path, int[] sizes, List<byte[]> frames)
{
    using var fs = new FileStream(path, FileMode.Create);
    using var w = new BinaryWriter(fs);
    w.Write((short)0);              // reserved
    w.Write((short)1);              // type: icon
    w.Write((short)frames.Count);   // image count

    int offset = 6 + 16 * frames.Count;
    for (int i = 0; i < frames.Count; i++)
    {
        int s = sizes[i];
        w.Write((byte)(s >= 256 ? 0 : s)); // width  (0 => 256)
        w.Write((byte)(s >= 256 ? 0 : s)); // height
        w.Write((byte)0);                  // palette
        w.Write((byte)0);                  // reserved
        w.Write((short)1);                 // color planes
        w.Write((short)32);                // bits per pixel
        w.Write(frames[i].Length);         // bytes in resource
        w.Write(offset);                   // offset
        offset += frames[i].Length;
    }
    foreach (var f in frames) w.Write(f);
}
