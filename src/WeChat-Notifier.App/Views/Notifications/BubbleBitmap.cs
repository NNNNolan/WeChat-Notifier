using WeChat_Notifier.Configuration;
using WeChat_Notifier.Domain;
using WeChat_Notifier.Infrastructure.Windows.Interop;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier.Views.Notifications;

/// <summary>原气泡的布局、两层投影和颜色，输出一个预乘 BGRA 位图。</summary>
internal static class BubbleBitmap
{
    public const double ShadowPad = 18;

    public static int Height(AppConfig config, SessionInfo session, double scale)
    {
        var available = (float)((config.Width - 30 - (config.ShowAvatar ? 52 : 0)) * scale);
        var body = GdiPlus.MeasureTextHeight(session.Preview.Trim(), available, (float)(12.5 * scale),
            (float)(config.MaxBodyLines * 17.5 * scale));
        return (int)Math.Ceiling((27 + Math.Max(config.ShowAvatar ? 40 : 0, 22 + body / scale)) * scale);
    }

    public static byte[] Compose(AppConfig config, SessionInfo session, double scale, byte[] glass,
        int width, int height, bool lightText, bool hovered)
    {
        var pad = (int)Math.Round(ShadowPad * scale);
        var outerWidth = width + pad * 2;
        var outerHeight = height + pad * 2;
        var pixels = new byte[outerWidth * outerHeight * 4];
        ComposeInto(config, session, scale, glass, width, height, lightText, hovered, pixels);
        return pixels;
    }

    public static void ComposeInto(AppConfig config, SessionInfo session, double scale, byte[] glass,
        int width, int height, bool lightText, bool hovered, byte[] pixels)
    {
        var pad = (int)Math.Round(ShadowPad * scale);
        var outerWidth = width + pad * 2;
        var outerHeight = height + pad * 2;
        if (glass.Length != checked(width * height * 4) || pixels.Length != checked(outerWidth * outerHeight * 4))
            throw new ArgumentException("气泡位图缓冲区尺寸无效。");
        Array.Clear(pixels);
        var elevation = config.ShadowElevation;
        if (elevation > 0)
        {
            for (var y = 0; y < outerHeight; y++)
            for (var x = 0; x < outerWidth; x++)
            {
                double Layer(double opacity, double blur, double offset)
                {
                    var distance = Distance(x - pad + 0.5, y - pad - offset * elevation * scale + 0.5,
                        width, height, config.CornerRadius * scale);
                    if (distance < 0) return opacity * elevation;
                    var sigma = Math.Max(0.5, blur * elevation * scale / 2);
                    return Math.Min(1, opacity * elevation) * Math.Exp(-distance * distance / (2 * sigma * sigma));
                }
                var alpha = 1 - (1 - Layer(0.06, 8, 2)) * (1 - Layer(0.02, 2, 1));
                pixels[(y * outerWidth + x) * 4 + 3] = (byte)Math.Round(alpha * 255);
            }
        }
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var source = (y * width + x) * 4;
            var target = ((y + pad) * outerWidth + x + pad) * 4;
            var alpha = glass[source + 3];
            pixels[target] = glass[source];
            pixels[target + 1] = glass[source + 1];
            pixels[target + 2] = glass[source + 2];
            pixels[target + 3] = (byte)(alpha + pixels[target + 3] * (255 - alpha) / 255);
        }

        var s = (float)scale;
        var p = (float)pad;
        GdiPlus.Draw(pixels, outerWidth, outerHeight, graphics =>
        {
            var textX = p + 15 * s;
            if (config.ShowAvatar)
            {
                var rect = new GdiPlus.RectF(textX, p + 13 * s, 40 * s, 40 * s);
                GdiPlus.Image(graphics, IconLoader.ImagePath, rect);
                textX += 52 * s;
            }
            var available = p + width - 15 * s - textX;
            GdiPlus.Text(graphics, session.Title, new(textX, p + 12 * s, available - 68 * s, 21 * s),
                14 * s, true, lightText ? 0xFFFFFFFF : 0xFF141418, true);
            GdiPlus.Text(graphics, session.Time, new(p + width - 78 * s, p + 14 * s, 45 * s, 18 * s),
                11 * s, false, lightText ? 0x80FFFFFF : 0x801A1A1E, true);
            GdiPlus.Text(graphics, session.Preview.Trim(),
                new(textX, p + 35 * s, available, Math.Min(height - 45 * s, config.MaxBodyLines * 17.5f * s)),
                12.5f * s, false, lightText ? 0xE6FFFFFF : 0xDF1A1A1E);
            var closeX = p + width - 34 * s;
            var closeY = p + 8 * s;
            GdiPlus.Ellipse(graphics, new(closeX, closeY, 26 * s, 26 * s), hovered ? 0x66000000u : 0x4D000000u);
            GdiPlus.Line(graphics, closeX + 9 * s, closeY + 9 * s, closeX + 17 * s, closeY + 17 * s, 1.8f * s, 0xFFFFFFFF);
            GdiPlus.Line(graphics, closeX + 17 * s, closeY + 9 * s, closeX + 9 * s, closeY + 17 * s, 1.8f * s, 0xFFFFFFFF);
        });
    }

    public static byte[] Solid(AppConfig config, int width, int height, double scale)
    {
        var pixels = new byte[width * height * 4];
        SolidInto(config, width, height, scale, pixels);
        return pixels;
    }

    public static void SolidInto(AppConfig config, int width, int height, double scale, byte[] pixels)
    {
        if (pixels.Length != checked(width * height * 4))
            throw new ArgumentException("气泡背景缓冲区尺寸无效。");
        var dark = config.Theme == "Dark";
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var distance = Distance(x + 0.5, y + 0.5, width, height, config.CornerRadius * scale);
            var alpha = Math.Clamp(0.5 - distance, 0, 1) * config.BackgroundOpacity;
            var index = (y * width + x) * 4;
            pixels[index] = (byte)((dark ? 30 : 252) * alpha);
            pixels[index + 1] = (byte)((dark ? 27 : 250) * alpha);
            pixels[index + 2] = (byte)((dark ? 27 : 250) * alpha);
            pixels[index + 3] = (byte)(255 * alpha);
        }
    }

    private static double Distance(double x, double y, double width, double height, double radius)
    {
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        var dx = Math.Abs(x - width / 2) - width / 2 + radius;
        var dy = Math.Abs(y - height / 2) - height / 2 + radius;
        return Math.Min(Math.Max(dx, dy), 0) + Math.Sqrt(Math.Pow(Math.Max(dx, 0), 2) + Math.Pow(Math.Max(dy, 0), 2)) - radius;
    }
}
