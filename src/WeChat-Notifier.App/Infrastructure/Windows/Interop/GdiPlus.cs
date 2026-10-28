using System.Runtime.InteropServices;

namespace WeChat_Notifier.Infrastructure.Windows.Interop;

/// <summary>仅为透明气泡绘制文字/图标；直接调用 Windows GDI+，不依赖 WPF 或 System.Drawing。</summary>
internal static unsafe class GdiPlus
{
    private static nuint _token;
    private static readonly Guid PngEncoder = new("557cf406-1a04-11d3-9a73-0000f81ef32e");
    private const int PremultipliedArgb = 0xE200B;

    internal static void EnsureStarted()
    {
        if (_token != 0) return;
        var input = new StartupInput { Version = 1 };
        Check(GdiplusStartup(out _token, ref input, 0));
    }

    internal static void Shutdown()
    {
        if (_token == 0) return;
        GdiplusShutdown(_token);
        _token = 0;
    }

    internal static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"气泡绘制失败（GDI+ {status}）。");
    }

    internal static void Draw(byte[] pixels, int width, int height, Action<nint> draw)
    {
        EnsureStarted();
        fixed (byte* data = pixels)
        {
            Check(GdipCreateBitmapFromScan0(width, height, width * 4, PremultipliedArgb, data, out var image));
            try
            {
                Check(GdipGetImageGraphicsContext(image, out var graphics));
                try
                {
                    Check(GdipSetSmoothingMode(graphics, 4));
                    Check(GdipSetTextRenderingHint(graphics, 4));
                    draw(graphics);
                }
                finally { GdipDeleteGraphics(graphics); }
            }
            finally { GdipDisposeImage(image); }
        }
    }

    internal static float MeasureTextHeight(string text, float width, float size, float maxHeight)
    {
        if (text.Length == 0) return 0;
        float height = 0;
        Draw(new byte[16], 2, 2, graphics =>
        {
            WithFont(size, false, font =>
            {
                var rect = new RectF(0, 0, width, maxHeight);
                Check(GdipMeasureString(graphics, text, text.Length, font, ref rect, 0, out var bounds, out _, out _));
                height = Math.Min(maxHeight, bounds.Height);
            });
        });
        return height;
    }

    internal static void Text(nint graphics, string text, RectF rect, float size, bool bold, uint argb, bool noWrap = false)
    {
        if (text.Length == 0) return;
        WithFont(size, bold, font =>
        {
            Check(GdipCreateSolidFill(argb, out var brush));
            Check(GdipCreateStringFormat(0, 0, out var format));
            try
            {
                Check(GdipSetStringFormatFlags(format, 0x2000 | (noWrap ? 0x1000 : 0)));
                Check(GdipSetStringFormatTrimming(format, 3));
                Check(GdipDrawString(graphics, text, text.Length, font, ref rect, format, brush));
            }
            finally { GdipDeleteStringFormat(format); GdipDeleteBrush(brush); }
        });
    }

    private static void WithFont(float size, bool bold, Action<nint> draw)
    {
        Check(GdipCreateFontFamilyFromName("Segoe UI", 0, out var family));
        try
        {
            Check(GdipCreateFont(family, size, bold ? 1 : 0, 2, out var font));
            try { draw(font); }
            finally { GdipDeleteFont(font); }
        }
        finally { GdipDeleteFontFamily(family); }
    }

    internal static void Ellipse(nint graphics, RectF rect, uint color)
    {
        Check(GdipCreateSolidFill(color, out var brush));
        try { Check(GdipFillEllipse(graphics, brush, rect.X, rect.Y, rect.Width, rect.Height)); }
        finally { GdipDeleteBrush(brush); }
    }

    internal static void Line(nint graphics, float x1, float y1, float x2, float y2, float width, uint color)
    {
        Check(GdipCreatePen1(color, width, 2, out var pen));
        try { Check(GdipDrawLine(graphics, pen, x1, y1, x2, y2)); }
        finally { GdipDeletePen(pen); }
    }

    internal static void Image(nint graphics, string path, RectF rect)
    {
        Check(GdipLoadImageFromFile(path, out var image));
        try { Check(GdipDrawImageRect(graphics, image, rect.X, rect.Y, rect.Width, rect.Height)); }
        finally { GdipDisposeImage(image); }
    }

    internal static nint CreateIcon(byte[] pixels, int width, int height)
    {
        EnsureStarted();
        fixed (byte* data = pixels)
        {
            Check(GdipCreateBitmapFromScan0(width, height, width * 4, PremultipliedArgb, data, out var image));
            try
            {
                Check(GdipCreateHICONFromBitmap(image, out var icon));
                return icon;
            }
            finally { GdipDisposeImage(image); }
        }
    }

    internal static void SavePixels(byte[] pixels, int width, int height, string path)
    {
        EnsureStarted();
        fixed (byte* data = pixels)
        {
            Check(GdipCreateBitmapFromScan0(width, height, width * 4, PremultipliedArgb, data, out var image));
            try { var encoder = PngEncoder; Check(GdipSaveImageToFile(image, path, ref encoder, 0)); }
            finally { GdipDisposeImage(image); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint Version;
        public nint Callback;
        public int SuppressThread, SuppressCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectF(float x, float y, float width, float height)
    {
        public float X = x, Y = y, Width = width, Height = height;
    }

    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out nuint token, ref StartupInput input, nint output);
    [DllImport("gdiplus.dll")] private static extern void GdiplusShutdown(nuint token);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, byte* data, out nint bitmap);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageGraphicsContext(nint image, out nint graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(nint graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(nint image);
    [DllImport("gdiplus.dll")] private static extern int GdipSetSmoothingMode(nint graphics, int mode);
    [DllImport("gdiplus.dll")] private static extern int GdipSetTextRenderingHint(nint graphics, int hint);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipCreateFontFamilyFromName(string name, nint collection, out nint family);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFontFamily(nint family);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateFont(nint family, float size, int style, int unit, out nint font);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFont(nint font);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateSolidFill(uint color, out nint brush);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteBrush(nint brush);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateStringFormat(int attributes, ushort language, out nint format);
    [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatFlags(nint format, int flags);
    [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatTrimming(nint format, int trimming);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteStringFormat(nint format);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipDrawString(nint graphics, string text, int length, nint font, ref RectF rect, nint format, nint brush);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipMeasureString(nint graphics, string text, int length, nint font, ref RectF rect, nint format, out RectF bounds, out int fitted, out int lines);
    [DllImport("gdiplus.dll")] private static extern int GdipFillEllipse(nint graphics, nint brush, float x, float y, float width, float height);
    [DllImport("gdiplus.dll")] private static extern int GdipCreatePen1(uint color, float width, int unit, out nint pen);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePen(nint pen);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipLoadImageFromFile(string path, out nint image);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawImageRect(nint graphics, nint image, float x, float y, float width, float height);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipCreateHICONFromBitmap(nint bitmap, out nint hicon);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipSaveImageToFile(nint image, string path, ref Guid encoder, nint parameters);
}
