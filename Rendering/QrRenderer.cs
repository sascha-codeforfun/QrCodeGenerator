using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using QrCodeGenerator.Util;

namespace QrCodeGenerator.Rendering;

/// <summary>QR encoding plus center-graphic compositing for PNG and SVG output.</summary>
internal static class QrRenderer
{
    // ==================== BASE CODES ====================

    public static QRCodeData CreateQrData(string? payload, QRCodeGenerator.ECCLevel ecc)
    {
        string text = payload?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("Please enter a URL or some text first.");
        return new QRCodeGenerator().CreateQrCode(text, ecc);
    }

    public static string GenerateSvg(string? payload, QRCodeGenerator.ECCLevel ecc, string darkHex, string lightHex)
    {
        using QRCodeData data = CreateQrData(payload, ecc);
        var svgQr = new SvgQRCode(data);
        return svgQr.GetGraphic(10, darkHex, lightHex);
    }

    // ==================== IMAGE LOGO ====================

    public static byte[] OverlayLogoOnPng(byte[] qrPngBytes, string logoPath, double sizePercent)
    {
        Guard.RequireFile(logoPath, "center image");
        BitmapSource qr = Bitmaps.FromBytes(qrPngBytes);
        BitmapSource logo = Bitmaps.FromFile(logoPath);

        double w = qr.PixelWidth;
        double h = qr.PixelHeight;
        double box = Math.Min(w, h) * (sizePercent / 100.0);
        double pad = box * 0.12;
        double x = (w - box) / 2.0;
        double y = (h - box) / 2.0;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(qr, new Rect(0, 0, w, h));
            var bg = new Rect(x - pad, y - pad, box + 2 * pad, box + 2 * pad);
            dc.DrawRoundedRectangle(Brushes.White, null, bg, pad, pad);
            dc.DrawImage(logo, new Rect(x, y, box, box));
        }

        return Render(visual, w, h);
    }

    public static string EmbedLogoInSvg(string svg, string logoPath, double sizePercent)
    {
        Guard.RequireFile(logoPath, "center image");
        (double w, double h) = Svg.ReadDimensions(svg);

        double box = Math.Min(w, h) * (sizePercent / 100.0);
        double pad = box * 0.12;
        double x = (w - box) / 2.0;
        double y = (h - box) / 2.0;

        string mime = GuessImageMime(logoPath);
        string base64 = Convert.ToBase64String(File.ReadAllBytes(logoPath));

        string overlay =
            $"<rect x=\"{Svg.Num(x - pad)}\" y=\"{Svg.Num(y - pad)}\" width=\"{Svg.Num(box + 2 * pad)}\" " +
            $"height=\"{Svg.Num(box + 2 * pad)}\" rx=\"{Svg.Num(pad)}\" ry=\"{Svg.Num(pad)}\" fill=\"#FFFFFF\"/>" +
            $"<image x=\"{Svg.Num(x)}\" y=\"{Svg.Num(y)}\" width=\"{Svg.Num(box)}\" height=\"{Svg.Num(box)}\" " +
            $"preserveAspectRatio=\"xMidYMid meet\" " +
            $"xlink:href=\"data:{mime};base64,{base64}\" " +
            $"href=\"data:{mime};base64,{base64}\"/>";

        if (!svg.Contains("xmlns:xlink"))
        {
            svg = System.Text.RegularExpressions.Regex.Replace(
                svg, "<svg\\b",
                "<svg xmlns:xlink=\"http://www.w3.org/1999/xlink\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return Svg.InsertBeforeClose(svg, overlay, "logo");
    }

    // ==================== FONT GLYPH ====================

    public static byte[] OverlayGlyphOnPng(
        byte[] qrPngBytes, string fontPath, string text, string glyphHex, string? glyphBgHex, double sizePercent)
    {
        BitmapSource qr = Bitmaps.FromBytes(qrPngBytes);
        double w = qr.PixelWidth;
        double h = qr.PixelHeight;
        double box = Math.Min(w, h) * (sizePercent / 100.0);
        double pad = box * 0.12;
        double x = (w - box) / 2.0;
        double y = (h - box) / 2.0;

        PathGeometry glyph = GlyphFonts.BuildGeometry(fontPath, text);
        Matrix fit = FitMatrix(glyph.Bounds, x, y, box);
        var brush = new SolidColorBrush(ColorHex.Parse(glyphHex));
        brush.Freeze();

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(qr, new Rect(0, 0, w, h));
            if (glyphBgHex != null)
            {
                var padBrush = new SolidColorBrush(ColorHex.Parse(glyphBgHex));
                padBrush.Freeze();
                var bg = new Rect(x - pad, y - pad, box + 2 * pad, box + 2 * pad);
                dc.DrawRoundedRectangle(padBrush, null, bg, pad, pad);
            }
            dc.PushTransform(new MatrixTransform(fit));
            dc.DrawGeometry(brush, null, glyph);
            dc.Pop();
        }

        return Render(visual, w, h);
    }

    public static string EmbedGlyphInSvg(
        string svg, string fontPath, string text, string glyphHex, string? glyphBgHex, double sizePercent)
    {
        (double w, double h) = Svg.ReadDimensions(svg);
        double box = Math.Min(w, h) * (sizePercent / 100.0);
        double pad = box * 0.12;
        double x = (w - box) / 2.0;
        double y = (h - box) / 2.0;

        PathGeometry glyph = GlyphFonts.BuildGeometry(fontPath, text);
        Matrix fit = FitMatrix(glyph.Bounds, x, y, box);
        string pathData = Svg.PathData(glyph);

        string padRect = glyphBgHex == null
            ? string.Empty
            : $"<rect x=\"{Svg.Num(x - pad)}\" y=\"{Svg.Num(y - pad)}\" width=\"{Svg.Num(box + 2 * pad)}\" " +
              $"height=\"{Svg.Num(box + 2 * pad)}\" rx=\"{Svg.Num(pad)}\" ry=\"{Svg.Num(pad)}\" fill=\"{glyphBgHex}\"/>";

        string overlay =
            padRect +
            $"<g transform=\"matrix({Svg.Num(fit.M11)} {Svg.Num(fit.M12)} {Svg.Num(fit.M21)} {Svg.Num(fit.M22)} " +
            $"{Svg.Num(fit.OffsetX)} {Svg.Num(fit.OffsetY)})\">" +
            $"<path d=\"{pathData}\" fill=\"{glyphHex}\" fill-rule=\"nonzero\"/></g>";

        return Svg.InsertBeforeClose(svg, overlay, "glyph");
    }

    // ==================== HELPERS ====================

    /// <summary>Matrix that scales/centers a geometry's bounds into a target square, preserving aspect.</summary>
    private static Matrix FitMatrix(Rect bounds, double boxX, double boxY, double boxSize)
    {
        double scale = boxSize / Math.Max(bounds.Width, bounds.Height);
        double contentW = bounds.Width * scale;
        double contentH = bounds.Height * scale;
        double offsetX = boxX + (boxSize - contentW) / 2.0;
        double offsetY = boxY + (boxSize - contentH) / 2.0;
        return new Matrix(scale, 0, 0, scale,
            offsetX - bounds.X * scale,
            offsetY - bounds.Y * scale);
    }

    private static byte[] Render(DrawingVisual visual, double w, double h)
    {
        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return Bitmaps.EncodePng(rtb);
    }

    private static string GuessImageMime(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
    }
}
