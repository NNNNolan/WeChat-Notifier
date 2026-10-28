using System.Runtime.InteropServices;
using WeChat_Notifier.Infrastructure.Logging;
using WeChat_Notifier.Infrastructure.Windows;

namespace WeChat_Notifier.Views.Notifications;

/// <summary>
/// CPU port of the liquid-glass shader used by the mikcb Flutter app
/// (`liquid_glass_widgets` 0.30.2, shaders/liquid_glass_final_render.frag +
/// liquid_glass_geometry_blended.frag + render.glsl).
///
/// Same maths, same order, same constants:
///   geometry  — rounded-rect SDF, SDF-gradient surface normal, hemisphere height
///   refraction— refract(incident, normal, 1/n) x (height + 8*thickness) / |z|
///   dispersion— R/G/B sampled at 1±(aberration/2) x displacement
///   tint      — iOS 26 luminosity-preserving applyGlassColor (white is achromatic,
///               so it reduces to a straight mix toward white at glassColor.a)
///   adaptive  — mix(1.2, 0.8, backdropLuma) modulates saturation and tint
///   edge      — directional specular (pow 1.5 lobe) + Fresnel rim ramp
///
/// mikcb passes edgeAbsorption = 0, ambientRim = 0, whitenStrength = 0, so the
/// meniscus-darkening, extra rim ring and legibility veil all drop out.
///
/// Doing it on the CPU is viable because a toast is tiny: a 630x180 physical
/// region is ~113k pixels, a few milliseconds of work, once per bubble.
/// </summary>
public static class LiquidGlassRenderer
{
    // render.glsl
    private static readonly double[] LumaWeights = { 0.299, 0.587, 0.114 };

    public sealed class Settings
    {
        public double Thickness = 30;            // logical px, mikcb: 30
        public double Blur = 3;                  // mikcb: 3
        public double RefractiveIndex = 1.59;    // mikcb: 1.59
        public double ChromaticAberration = 0.3; // mikcb: 0.3
        public double LightIntensity = 0.6;      // mikcb: 0.6
        public double AmbientStrength = 1.0;     // mikcb: 1
        public double Saturation = 0.7;          // mikcb: 0.7
        public double Tint = 0.24;               // glassColor = 0x3DFFFFFF -> white @ 24%
        public double RefractScale = 1.0;        // uRefractScale
        public double LightAngleDeg = 135;       // Apple iOS 26 upper-left
        public double CornerRadiusDip = 16;
        public double Dpr = 1.0;
    }

    /// <summary>
    /// Captures the desktop behind <paramref name="rect"/> and renders the glass
    /// into a premultiplied BGRA bitmap sized to that rect.
    /// <paramref name="interiorLuma"/> reports the average brightness of the
    /// material so the caller can choose dark or light text.
    /// </summary>
    /// <summary>Grabs the desktop into <paramref name="buffer"/>, resizing it if needed.</summary>
    public static bool Capture(int left, int top, int w, int h, ref byte[] buffer)
    {
        if (buffer.Length != w * h * 4) buffer = new byte[w * h * 4];
        if (!TryCapture(left, top, w, h, buffer))
        {
            Log.Warn("liquid glass: backdrop capture failed");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Runs the shader maths for one frame. <paramref name="raw"/> is left
    /// untouched so the caller can diff consecutive captures cheaply and skip
    /// frames where nothing behind the bubble moved.
    /// </summary>
    public static double RenderInto(byte[] raw, byte[] work, byte[] dst, int w, int h, Settings s)
    {
        if (w < 2 || h < 2 || raw.Length != checked(w * h * 4)
            || work.Length != raw.Length || dst.Length != raw.Length)
            throw new ArgumentException("玻璃渲染缓冲区尺寸无效。");
        Buffer.BlockCopy(raw, 0, work, 0, raw.Length);
        BlurInPlace(work, w, h, s.Blur);   // the package frosts before the shader sees it
        return RenderCore(work, dst, w, h, s);
    }

    /// <summary>The per-pixel loop. Returns the interior mean luminance.</summary>
    private static double RenderCore(byte[] src, byte[] dst, int w, int h, Settings s)
    {
        var thickness = Math.Max(0.001, s.Thickness * s.Dpr / 3.0);   // uThickness * scale
        var halfW = w / 2.0;
        var halfH = h / 2.0;
        var radius = Math.Min(s.CornerRadiusDip * s.Dpr, Math.Min(halfW, halfH));
        var smoothing = 1.5 * Math.Max(1.0, s.Dpr);
        var eta = 1.0 / Math.Max(0.001, s.RefractiveIndex);

        // The package builds the light vector as Offset(cos(a), -sin(a)) — see
        // liquid_glass_render_object.dart. The negated sine is what puts Apple's
        // 135 degree default in the UPPER left; without it the highlight lands
        // bottom-left instead.
        var lightRad = s.LightAngleDeg * Math.PI / 180.0;
        var lx = Math.Cos(lightRad);
        var ly = -Math.Sin(lightRad);

        double lumaSum = 0;
        var lumaCount = 0;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var px = x + 0.5 - halfW;
                var py = y + 0.5 - halfH;

                // ---- geometry pass: rounded-rect SDF ------------------------
                var (sd, gx, gy) = RoundedRectSdf(px, py, halfW, halfH, radius);

                // gradient magnitude normalisation (the SDF is already Euclidean
                // for a rounded rect, so |grad| == 1 and this is a no-op guard)
                var gradMag = Math.Sqrt(gx * gx + gy * gy);
                var sdN = gradMag > 0.1 ? sd / gradMag : sd;

                var alpha = SmoothStep(smoothing * 0.5, -smoothing * 0.5, sdN);
                var o = (y * w + x) * 4;
                if (alpha < 0.01 || sdN >= 0.0)
                {
                    dst[o] = dst[o + 1] = dst[o + 2] = dst[o + 3] = 0;
                    continue;
                }

                var nCos = Math.Max(thickness + sdN, 0.0) / thickness;
                var nSin = Math.Sqrt(Math.Max(0.0, 1.0 - nCos * nCos));

                var nx0 = gx * nCos;
                var ny0 = gy * nCos;
                var lenN = Math.Sqrt(nx0 * nx0 + ny0 * ny0 + nSin * nSin);
                var nx = nx0 / lenN;
                var ny = ny0 / lenN;
                var nz = nSin / lenN;

                var xx = thickness + sdN;
                var sqrtTerm = Math.Sqrt(Math.Max(0.0, thickness * thickness - xx * xx));
                var height = sdN < -thickness ? thickness : sqrtTerm;
                var normalizedHeight = Math.Clamp(height / thickness, 0.0, 1.0);

                // ---- refraction --------------------------------------------
                var dotNI = -nz;
                var kk = 1.0 - eta * eta * (1.0 - dotNI * dotNI);
                double rx = 0, ry = 0, rz = -eta;
                if (kk >= 0)
                {
                    var k = Math.Sqrt(kk);
                    var f = eta * dotNI + k;
                    rx = -f * nx;
                    ry = -f * ny;
                    rz = -eta - f * nz;
                }

                var refractLen = (height + thickness * 8.0) / Math.Max(0.001, Math.Abs(rz));
                var dispX = rx * refractLen * s.RefractScale;
                var dispY = ry * refractLen * s.RefractScale;

                double r, g, b;

                if (gx * gx + gy * gy < 1e-6 || (Math.Abs(dispX) < 0.01 && Math.Abs(dispY) < 0.01))
                {
                    // flat interior: single tap
                    Sample(src, w, h, x + dispX, y + dispY, out r, out g, out b);
                }
                else
                {
                    var ds = s.ChromaticAberration * 0.5;
                    Sample(src, w, h, x + dispX * (1.0 + ds), y + dispY * (1.0 + ds), out r, out _, out _);
                    Sample(src, w, h, x + dispX, y + dispY, out _, out g, out _);
                    Sample(src, w, h, x + dispX * (1.0 - ds), y + dispY * (1.0 - ds), out _, out _, out b);
                }

                // ---- applyGlassColor (white is achromatic -> directMix) -----
                var tint = s.Tint;
                r += (1.0 - r) * tint;
                g += (1.0 - g) * tint;
                b += (1.0 - b) * tint;

                // ---- content-adaptive strength -----------------------------
                var backdropLuma = r * LumaWeights[0] + g * LumaWeights[1] + b * LumaWeights[2];
                var adaptive = 1.2 + (0.8 - 1.2) * backdropLuma;

                // ---- applySaturation ---------------------------------------
                var sat = s.Saturation * adaptive;
                var lum = r * LumaWeights[0] + g * LumaWeights[1] + b * LumaWeights[2];
                r = lum + (r - lum) * sat;
                g = lum + (g - lum) * sat;
                b = lum + (b - lum) * sat;

                // ---- adaptive tint re-blend --------------------------------
                var tintDelta = tint * 0.12 * (adaptive - 1.0);
                r += (1.0 - r) * tintDelta;
                g += (1.0 - g) * tintDelta;
                b += (1.0 - b) * tintDelta;

                // ---- edge lighting -----------------------------------------
                var baseScale = 40.0 * Math.Max(0.1, s.Dpr / 3.0);
                var thicknessScale = Math.Clamp(baseScale / thickness, 1.0, 4.0);
                var edgeThreshold = 0.8 + (0.5 - 0.8) * (1.0 / thicknessScale);
                var edgeFactor = 1.0 - SmoothStep(0.0, edgeThreshold, normalizedHeight);

                if (edgeFactor > 0.01)
                {
                    var reach = Math.Max(Math.Sqrt(nx * nx + ny * ny), 1e-4);
                    var ax = nx / reach;
                    var ay = ny / reach;

                    var mainLight = Math.Max(0.0, ax * lx + ay * ly);
                    var oppositeLight = Math.Max(0.0, ax * -lx + ay * -ly);
                    var totalInfluence = mainLight + oppositeLight * 0.8;

                    var directional = totalInfluence * Math.Sqrt(totalInfluence) * s.LightIntensity * 3.0;
                    var ambient = s.AmbientStrength * 0.5;

                    var brightnessRaw = (directional + ambient) * edgeFactor * thicknessScale * 0.8;
                    var brightness = brightnessRaw / (1.0 + brightnessRaw);

                    HighlightColor(r, g, b, 1.0, out var hr, out var hg, out var hb);
                    r += (hr - r) * brightness;
                    g += (hg - g) * brightness;
                    b += (hb - b) * brightness;
                }

                // ---- Fresnel rim -------------------------------------------
                var fresnel = (1.0 - nz) * edgeFactor * 0.12 * 1.0;
                r = Math.Clamp(r + fresnel, 0.0, 1.0);
                g = Math.Clamp(g + fresnel, 0.0, 1.0);
                b = Math.Clamp(b + fresnel, 0.0, 1.0);

                // ---- premultiplied output ----------------------------------
                dst[o] = ClampByte(b * alpha);
                dst[o + 1] = ClampByte(g * alpha);
                dst[o + 2] = ClampByte(r * alpha);
                dst[o + 3] = ClampByte(alpha);

                // interior-only average, for choosing the text colour
                if (normalizedHeight > 0.25)
                {
                    lumaSum += backdropLuma;
                    lumaCount++;
                }
            }
        }

        return lumaCount > 0 ? lumaSum / lumaCount : 0.5;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>sdf.glsl sdfRRect — negative inside, Euclidean.</summary>
    private static (double Sd, double Gx, double Gy) RoundedRectSdf(
        double px, double py, double halfW, double halfH, double r)
    {
        r = Math.Min(r, Math.Min(halfW, halfH));
        var qx = Math.Abs(px) - halfW + r;
        var qy = Math.Abs(py) - halfH + r;
        var outsideX = Math.Max(qx, 0.0);
        var outsideY = Math.Max(qy, 0.0);
        var sd = Math.Min(Math.Max(qx, qy), 0.0)
               + Math.Sqrt(outsideX * outsideX + outsideY * outsideY)
               - r;

        // analytic gradient == normalised direction, so |grad| is 1 everywhere
        double gx, gy;
        if (qx > 0 && qy > 0)
        {
            var len = Math.Sqrt(qx * qx + qy * qy);
            gx = len > 1e-9 ? qx / len : 0;
            gy = len > 1e-9 ? qy / len : 0;
        }
        else if (qx > qy) { gx = 1; gy = 0; }
        else { gx = 0; gy = 1; }

        gx *= Math.Sign(px) == 0 ? 1 : Math.Sign(px);
        gy *= Math.Sign(py) == 0 ? 1 : Math.Sign(py);
        return (sd, gx, gy);
    }

    private static void HighlightColor(double r, double g, double b, double target,
        out double hr, out double hg, out double hb)
    {
        var luma = r * LumaWeights[0] + g * LumaWeights[1] + b * LumaWeights[2];
        var maxC = Math.Max(r, Math.Max(g, b));

        var l = luma * 2.5;
        var lumFactor = l / (1.0 + l);
        var sat = maxC * 2.5;
        var satFactor = sat / (1.0 + sat);
        var influence = lumFactor * satFactor;

        var denom = Math.Max(luma, 0.001);
        var tr = r / denom * target;
        var tg = g / denom * target;
        var tb = b / denom * target;

        hr = target + (tr - target) * influence;
        hg = target + (tg - target) * influence;
        hb = target + (tb - target) * influence;
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        if (Math.Abs(edge1 - edge0) < 1e-9) return x < edge0 ? 0 : 1;
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static byte ClampByte(double v) => v <= 0 ? (byte)0 : (v >= 1 ? (byte)255 : (byte)(v * 255.0 + 0.5));

    /// <summary>Bilinear sample of the captured backdrop, edges clamped.</summary>
    private static void Sample(byte[] src, int w, int h, double x, double y,
        out double r, out double g, out double b)
    {
        var fx = Math.Clamp(x - 0.5, 0, w - 1.001);
        var fy = Math.Clamp(y - 0.5, 0, h - 1.001);
        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var tx = fx - x0;
        var ty = fy - y0;

        var i00 = (y0 * w + x0) * 4;
        var i10 = (y0 * w + x1) * 4;
        var i01 = (y1 * w + x0) * 4;
        var i11 = (y1 * w + x1) * 4;

        double Mix(int ch) =>
            (src[i00 + ch] * (1 - tx) + src[i10 + ch] * tx) * (1 - ty) +
            (src[i01 + ch] * (1 - tx) + src[i11 + ch] * tx) * ty;

        b = Mix(0) / 255.0;
        g = Mix(1) / 255.0;
        r = Mix(2) / 255.0;
    }

    /// <summary>Separable box blur standing in for Flutter's ImageFilter.blur sigma.</summary>
    private static void BlurInPlace(byte[] px, int w, int h, double sigma)
    {
        if (sigma <= 0.01) return;
        var radius = Math.Max(1, (int)Math.Round(sigma));
        var passes = 2;
        var tmp = new byte[px.Length];

        for (var p = 0; p < passes; p++)
        {
            var win = radius * 2 + 1;
            for (var y = 0; y < h; y++)
            {
                var row = y * w * 4;
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (var k = -radius; k <= radius; k++) sum += px[row + Clamp(k, w) * 4 + c];
                    for (var x = 0; x < w; x++)
                    {
                        tmp[row + x * 4 + c] = (byte)(sum / win);
                        sum -= px[row + Clamp(x - radius, w) * 4 + c];
                        sum += px[row + Clamp(x + radius + 1, w) * 4 + c];
                    }
                }
            }
            for (var x = 0; x < w; x++)
            {
                var col = x * 4;
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (var k = -radius; k <= radius; k++) sum += tmp[Clamp(k, h) * w * 4 + col + c];
                    for (var y = 0; y < h; y++)
                    {
                        px[y * w * 4 + col + c] = (byte)(sum / win);
                        sum -= tmp[Clamp(y - radius, h) * w * 4 + col + c];
                        sum += tmp[Clamp(y + radius + 1, h) * w * 4 + col + c];
                    }
                }
            }
        }
    }

    private static int Clamp(int v, int max) => v < 0 ? 0 : (v >= max ? max - 1 : v);

    // ------------------------------------------------------------------ capture

    private const int SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;

    private static bool TryCapture(int left, int top, int width, int height, byte[] bgra)
    {
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return false;

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            dib = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
            old = SelectObject(memDc, dib);

            if (!BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SRCCOPY)) return false;

            Marshal.Copy(bits, bgra, 0, bgra.Length);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("backdrop capture failed: " + ex.Message);
            return false;
        }
        finally
        {
            if (memDc != IntPtr.Zero && old != IntPtr.Zero) SelectObject(memDc, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int rop);
}
