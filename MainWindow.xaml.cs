using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QRCoder;

namespace QrCodeGenerator;

public partial class MainWindow : Window
{
    private string? _logoPath;
    private string? _fontPath;

    // URL-builder schema state
    private UrlSchemaJson? _schema;
    private readonly List<ParamInput> _paramInputs = new();

    public MainWindow()
    {
        InitializeComponent();
        TryAutoLoadSchema();
    }

    // ==================== URL BUILDER (schema-driven) ====================

    private void LoadSchemaButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON schema (*.json)|*.json",
            Title = "Load a URL parameter schema"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            LoadSchemaFromFile(dialog.FileName);
            StatusText.Text = $"Loaded schema: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            ShowError($"Could not load schema: {ex.Message}");
        }
    }

    private void BuildUrlButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UrlTextBox.Text = ComposeUrl();
            StatusText.Text = "URL built from parameters.";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void AddToBatchButton_Click(object sender, RoutedEventArgs e)
    {
        string url = (UrlTextBox.Text ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            StatusText.Text = "Nothing to add — the URL box is empty.";
            return;
        }

        string[] existingLines = (BatchUrlsTextBox.Text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // Guard against duplicates (e.g. an accidental double-click).
        foreach (string line in existingLines)
        {
            if (string.Equals(line.Trim(), url, StringComparison.Ordinal))
            {
                StatusText.Text = "Already in the Batch → ZIP list — not added again.";
                return;
            }
        }

        string existing = BatchUrlsTextBox.Text ?? string.Empty;
        if (existing.Length > 0 && !existing.EndsWith("\n") && !existing.EndsWith("\r"))
            existing += Environment.NewLine;

        BatchUrlsTextBox.Text = existing + url + Environment.NewLine;
        BatchUrlsTextBox.CaretIndex = BatchUrlsTextBox.Text.Length;

        StatusText.Text = $"Added to the Batch → ZIP list ({existingLines.Length + 1} URL(s) queued).";
    }

    private void TryAutoLoadSchema()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "url-schema.json");
            if (File.Exists(path))
                LoadSchemaFromFile(path);
        }
        catch
        {
            // Ignore problems auto-loading at startup; the user can load one manually.
        }
    }

    private void LoadSchemaFromFile(string path)
    {
        string json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        UrlSchemaJson schema = JsonSerializer.Deserialize<UrlSchemaJson>(json, options)
            ?? throw new InvalidOperationException("The schema file is empty or invalid.");

        _schema = schema;
        RebuildParamUi();

        int count = _paramInputs.Count;
        string baseInfo = $"{(schema.Domain ?? "").TrimEnd('/')}/{(schema.Prefix ?? "").Trim('/')}".TrimEnd('/');
        SchemaInfoText.Text = $"{Path.GetFileName(path)}  •  {baseInfo}  •  {count} parameter(s)";

        // Pre-fill the URL box from the defaults.
        UrlTextBox.Text = ComposeUrl();
    }

    private void RebuildParamUi()
    {
        ParamsPanel.Children.Clear();
        _paramInputs.Clear();

        if (_schema?.Parameters == null) return;

        var labelBrush = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));

        foreach (UrlParamJson p in _schema.Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;

            var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(p.Label) ? p.Name : p.Label,
                Foreground = labelBrush,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3),
            };
            wrap.Children.Add(label);

            List<string> defaults = p.ResolveDefaultValues();

            if (defaults.Count >= 2)
            {
                // Multiple values -> dropdown, first entry pre-selected.
                var combo = new ComboBox { Padding = new Thickness(8, 6, 8, 6) };
                foreach (string option in defaults) combo.Items.Add(option);
                combo.SelectedIndex = 0;

                wrap.Children.Add(combo);
                _paramInputs.Add(new ParamInput(p, () => combo.SelectedItem as string ?? string.Empty));
            }
            else
            {
                // One value (or none) -> editable textbox, pre-filled.
                string initial = defaults.Count == 1 ? defaults[0] : string.Empty;
                var box = new TextBox { Text = initial };

                wrap.Children.Add(box);
                _paramInputs.Add(new ParamInput(p, () => box.Text ?? string.Empty));
            }

            ParamsPanel.Children.Add(wrap);
        }
    }

    private string ComposeUrl()
    {
        if (_schema == null)
            throw new InvalidOperationException("Load a parameter schema first (click \"Load schema…\").");

        string domain = (_schema.Domain ?? string.Empty).Trim();
        if (domain.Length == 0)
            throw new InvalidOperationException("The schema has no \"domain\".");
        domain = domain.TrimEnd('/');

        string prefix = (_schema.Prefix ?? string.Empty).Trim().Trim('/');
        string basePart = prefix.Length == 0 ? domain : domain + "/" + prefix;

        var pairs = new List<string>();
        foreach (ParamInput input in _paramInputs)
        {
            string name = input.Def.Name ?? string.Empty;
            if (name.Length == 0) continue;

            string value = input.GetValue();
            bool omitIfEmpty = input.Def.OmitIfEmpty ?? true;
            if (value.Length == 0 && omitIfEmpty) continue;

            pairs.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
        }

        return pairs.Count == 0 ? basePart : basePart + "?" + string.Join("&", pairs);
    }

    // ==================== SINGLE TAB ====================

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            byte[] pngBytes = BuildPng();
            QrImage.Source = LoadBitmap(pngBytes);
            PlaceholderText.Visibility = Visibility.Collapsed;
            SavePngButton.IsEnabled = true;
            SaveSvgButton.IsEnabled = true;
            StatusText.Text = "Preview generated.";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SavePngButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = "qrcode.png" };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllBytes(dialog.FileName, BuildPng());
                StatusText.Text = $"Saved PNG to {dialog.FileName}";
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void SaveSvgButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { Filter = "SVG image (*.svg)|*.svg", FileName = "qrcode.svg" };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, BuildSvg(), new UTF8Encoding(false));
                StatusText.Text = $"Saved SVG to {dialog.FileName}";
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void BrowseLogoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Title = "Choose a center image"
        };
        if (dialog.ShowDialog() == true)
        {
            _logoPath = dialog.FileName;
            LogoPathTextBox.Text = dialog.FileName;
            LogoPathTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            StatusText.Text = "Tip: with a center image, pick High (H) error correction so the code still scans.";
        }
    }

    private void ClearLogoButton_Click(object sender, RoutedEventArgs e)
    {
        _logoPath = null;
        LogoPathTextBox.Text = "No image selected";
        LogoPathTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    }

    private void CenterModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Named elements may not exist yet during initial XAML load.
        if (ImagePanel == null || FontPanel == null || SizePanel == null) return;

        int mode = CenterModeComboBox.SelectedIndex;
        ImagePanel.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
        FontPanel.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        SizePanel.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;

        UpdateGlyphPreview();
    }

    private void BrowseFontButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Font files (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc",
            Title = "Choose a font file"
        };
        if (dialog.ShowDialog() == true)
        {
            _fontPath = dialog.FileName;
            FontPathTextBox.Text = dialog.FileName;
            FontPathTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            StatusText.Text = "Tip: use High (H) error correction with a center glyph so the code still scans.";
            UpdateGlyphPreview();
        }
    }

    private void ClearFontButton_Click(object sender, RoutedEventArgs e)
    {
        _fontPath = null;
        FontPathTextBox.Text = "No font selected";
        FontPathTextBox.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        UpdateGlyphPreview();
    }

    private void GlyphInput_Changed(object sender, TextChangedEventArgs e) => UpdateGlyphPreview();

    /// <summary>Renders a small live vector preview of the current glyph(s) next to the input.</summary>
    private void UpdateGlyphPreview()
    {
        if (GlyphPreviewImage == null) return; // not yet built during XAML init

        if (CenterMode != 2 || string.IsNullOrEmpty(_fontPath)
            || string.IsNullOrWhiteSpace(FontCharsTextBox.Text))
        {
            GlyphPreviewImage.Source = null;
            return;
        }

        try
        {
            PathGeometry geo = BuildGlyphGeometry(_fontPath!, FontCharsTextBox.Text);
            geo.FillRule = FillRule.Nonzero;

            Brush brush;
            try { brush = new SolidColorBrush(ParseColor(GlyphColorTextBox.Text)); }
            catch { brush = Brushes.Black; }

            var drawing = new GeometryDrawing(brush, null, geo);
            var image = new DrawingImage(drawing);
            image.Freeze();
            GlyphPreviewImage.Source = image;
        }
        catch
        {
            // Invalid font / no matching glyph / whitespace: just clear the swatch, no dialog.
            GlyphPreviewImage.Source = null;
        }
    }

    private int CenterMode => CenterModeComboBox?.SelectedIndex ?? 0;

    private bool HasImageInput => !string.IsNullOrEmpty(_logoPath);

    private bool HasGlyphInput =>
        !string.IsNullOrEmpty(_fontPath) && !string.IsNullOrWhiteSpace(FontCharsTextBox.Text);

    private byte[] BuildPng()
    {
        byte[] baseBytes;
        using (QRCodeData data = CreateQrData(UrlTextBox.Text, GetEccLevel()))
        {
            var png = new PngByteQRCode(data);
            baseBytes = png.GetGraphic(
                GetPixelsPerModule(),
                HexToRgba(DarkColorTextBox.Text),
                HexToRgba(LightColorTextBox.Text));
        }

        if (CenterMode == 1 && HasImageInput)
            return OverlayLogoOnPng(baseBytes, _logoPath!, GetLogoSizePercent());
        if (CenterMode == 2 && HasGlyphInput)
            return OverlayGlyphOnPng(baseBytes, _fontPath!, FontCharsTextBox.Text,
                NormalizeHex(GlyphColorTextBox.Text), GetLogoSizePercent());
        return baseBytes;
    }

    private string BuildSvg()
    {
        string svg = GenerateSvg(
            UrlTextBox.Text,
            GetEccLevel(),
            NormalizeHex(DarkColorTextBox.Text),
            NormalizeHex(LightColorTextBox.Text));

        if (CenterMode == 1 && HasImageInput)
            return EmbedLogoInSvg(svg, _logoPath!, GetLogoSizePercent());
        if (CenterMode == 2 && HasGlyphInput)
            return EmbedGlyphInSvg(svg, _fontPath!, FontCharsTextBox.Text,
                NormalizeHex(GlyphColorTextBox.Text), GetLogoSizePercent());
        return svg;
    }

    // ==================== BATCH TAB ====================

    private void GenerateZipButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string[] urls = (BatchUrlsTextBox.Text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var payloads = new List<string>();
            foreach (string line in urls)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) payloads.Add(trimmed);
            }

            if (payloads.Count == 0)
            {
                BatchStatusText.Text = "Paste at least one URL first.";
                return;
            }

            var dialog = new SaveFileDialog { Filter = "ZIP archive (*.zip)|*.zip", FileName = "qrcodes.zip" };
            if (dialog.ShowDialog() != true) return;

            QRCodeGenerator.ECCLevel ecc = GetEccLevel();
            string darkHex = NormalizeHex(DarkColorTextBox.Text);
            string lightHex = NormalizeHex(LightColorTextBox.Text);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int written = 0;
            var failures = new List<string>();

            using (var fs = new FileStream(dialog.FileName, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (string payload in payloads)
                {
                    try
                    {
                        string svg = GenerateSvg(payload, ecc, darkHex, lightHex);
                        string entryName = UniqueName(BuildSvgFileName(payload), usedNames) + ".svg";

                        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                        writer.Write(svg);
                        written++;
                    }
                    catch (Exception exItem)
                    {
                        failures.Add($"{payload} — {exItem.Message}");
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append($"Wrote {written} SVG file(s) to {dialog.FileName}.");
            if (failures.Count > 0)
            {
                sb.Append($"  Skipped {failures.Count}:");
                foreach (string f in failures) sb.Append($"\n  • {f}");
            }
            BatchStatusText.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            BatchStatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "QR Code Generator", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==================== FILENAME SANITIZATION ====================

    // Small set of common two-part public suffixes so e.g. "example.co.uk"
    // is not mistaken for the subdomain "example" on the TLD "co.uk".
    private static readonly HashSet<string> TwoLevelTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk", "net.uk", "sch.uk", "ltd.uk", "plc.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "id.au",
        "co.jp", "or.jp", "ne.jp", "ac.jp", "go.jp",
        "co.nz", "org.nz", "govt.nz", "ac.nz",
        "co.za", "org.za",
        "com.br", "com.cn", "com.mx", "com.tr", "com.sg", "com.hk", "com.tw", "com.ar",
        "co.in", "co.kr", "co.id", "co.th", "com.my",
    };

    /// <summary>
    /// Builds a filename (without extension) from a URL:
    /// drops the protocol, drops the subdomain (keeps the registrable domain),
    /// lowercases, then replaces every character that is not a-z or 0-9 with '_'.
    /// </summary>
    public static string BuildSvgFileName(string url)
    {
        string combined;
        string working = url.Trim();
        if (!working.Contains("://")) working = "http://" + working;

        if (Uri.TryCreate(working, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            string domain = RegistrableDomain(uri.Host);
            string rest = uri.AbsolutePath + uri.Query + uri.Fragment;
            combined = domain + rest;
        }
        else
        {
            // Non-http input: just strip a leading scheme and use the remainder.
            combined = Regex.Replace(url.Trim(), "^[a-zA-Z][a-zA-Z0-9+.-]*://", "");
        }

        // Every non a-z / 0-9 character (path and querystring included) -> underscore.
        string slug = Regex.Replace(combined.ToLowerInvariant(), "[^a-z0-9]", "_");
        slug = slug.Trim('_');

        if (slug.Length == 0) slug = "qr";
        if (slug.Length > 120) slug = slug.Substring(0, 120).Trim('_');
        return slug;
    }

    private static string RegistrableDomain(string host)
    {
        if (string.IsNullOrEmpty(host)) return host;

        // Leave IP literals untouched.
        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;

        string[] labels = host.Split('.');
        if (labels.Length <= 2) return host;

        string lastTwo = labels[^2] + "." + labels[^1];
        if (TwoLevelTlds.Contains(lastTwo) && labels.Length >= 3)
            return labels[^3] + "." + lastTwo;

        return lastTwo;
    }

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        string candidate = baseName;
        int i = 2;
        while (!used.Add(candidate))
        {
            candidate = baseName + "_" + i;
            i++;
        }
        return candidate;
    }

    // ==================== SHARED QR BUILDING ====================

    private static QRCodeData CreateQrData(string? payload, QRCodeGenerator.ECCLevel ecc)
    {
        string text = payload?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("Please enter a URL or some text first.");
        return new QRCodeGenerator().CreateQrCode(text, ecc);
    }

    private static string GenerateSvg(string? payload, QRCodeGenerator.ECCLevel ecc, string darkHex, string lightHex)
    {
        using QRCodeData data = CreateQrData(payload, ecc);
        var svgQr = new SvgQRCode(data);
        return svgQr.GetGraphic(10, darkHex, lightHex);
    }

    // ==================== LOGO COMPOSITING (PNG) ====================

    private static byte[] OverlayLogoOnPng(byte[] qrPngBytes, string logoPath, double sizePercent)
    {
        RequireFile(logoPath, "center image");
        BitmapSource qr = LoadBitmap(qrPngBytes);
        BitmapSource logo = LoadBitmapFromFile(logoPath);

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

        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ==================== LOGO COMPOSITING (SVG) ====================

    private static string EmbedLogoInSvg(string svg, string logoPath, double sizePercent)
    {
        RequireFile(logoPath, "center image");
        (double w, double h) = ReadSvgDimensions(svg);

        double box = Math.Min(w, h) * (sizePercent / 100.0);
        double pad = box * 0.12;
        double x = (w - box) / 2.0;
        double y = (h - box) / 2.0;

        string mime = GuessImageMime(logoPath);
        string base64 = Convert.ToBase64String(File.ReadAllBytes(logoPath));

        string overlay =
            $"<rect x=\"{F(x - pad)}\" y=\"{F(y - pad)}\" width=\"{F(box + 2 * pad)}\" " +
            $"height=\"{F(box + 2 * pad)}\" rx=\"{F(pad)}\" ry=\"{F(pad)}\" fill=\"#FFFFFF\"/>" +
            $"<image x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(box)}\" height=\"{F(box)}\" " +
            $"preserveAspectRatio=\"xMidYMid meet\" " +
            $"xlink:href=\"data:{mime};base64,{base64}\" " +
            $"href=\"data:{mime};base64,{base64}\"/>";

        if (!svg.Contains("xmlns:xlink"))
        {
            svg = Regex.Replace(
                svg, "<svg\\b",
                "<svg xmlns:xlink=\"http://www.w3.org/1999/xlink\"",
                RegexOptions.IgnoreCase);
        }

        int closeIdx = svg.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0)
            throw new InvalidOperationException("Could not parse the generated SVG to insert the logo.");

        return svg.Substring(0, closeIdx) + overlay + svg.Substring(closeIdx);
    }

    private static (double width, double height) ReadSvgDimensions(string svg)
    {
        Match wM = Regex.Match(svg, "\\bwidth=\"([\\d.]+)(?:px)?\"", RegexOptions.IgnoreCase);
        Match hM = Regex.Match(svg, "\\bheight=\"([\\d.]+)(?:px)?\"", RegexOptions.IgnoreCase);
        if (wM.Success && hM.Success)
            return (ParseInv(wM.Groups[1].Value), ParseInv(hM.Groups[1].Value));

        Match vb = Regex.Match(
            svg, "viewBox=\"[\\d.]+\\s+[\\d.]+\\s+([\\d.]+)\\s+([\\d.]+)\"",
            RegexOptions.IgnoreCase);
        if (vb.Success)
            return (ParseInv(vb.Groups[1].Value), ParseInv(vb.Groups[2].Value));

        throw new InvalidOperationException("Could not determine SVG dimensions for logo placement.");
    }

    // ==================== CENTER GLYPH (vector from font) ====================

    /// <summary>
    /// Extracts a single PathGeometry for the given character(s) from a TTF/OTF font,
    /// laid out left-to-right using the font's advance widths.
    /// </summary>
    private static PathGeometry BuildGlyphGeometry(string fontPath, string text)
    {
        RequireFile(fontPath, "font file");

        // Try the original path first.
        if (TryLoadGlyphTypeface(fontPath, out GlyphTypeface gtf))
            return BuildGlyphGeometry(gtf, text);

        // Fallback: some drives/paths (e.g. a font at the root of a secondary drive)
        // trip up WPF's font URI resolver and it throws internally. Copy the file to a
        // temp location on the system drive and load from there. Keep the temp file
        // alive until the outline is fully extracted, then clean it up.
        string temp = Path.Combine(
            Path.GetTempPath(),
            "qrgen_" + Guid.NewGuid().ToString("N") + Path.GetExtension(fontPath));
        try
        {
            File.Copy(fontPath, temp, overwrite: true);
            if (TryLoadGlyphTypeface(temp, out GlyphTypeface gtfTemp))
                return BuildGlyphGeometry(gtfTemp, text);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // fall through to the friendly error below
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }

        throw new InvalidOperationException(
            "Couldn't read that font. Make sure it's an intact TTF/OTF/TTC file " +
            "(WOFF/WOFF2 are not supported — convert them first).");
    }

    /// <summary>Extracts the combined outline for the given characters from a loaded typeface.</summary>
    private static PathGeometry BuildGlyphGeometry(GlyphTypeface gtf, string text)
    {
        const double emSize = 1000.0;
        var group = new GeometryGroup();
        double advance = 0;
        int placed = 0;

        foreach (char ch in text)
        {
            if (!gtf.CharacterToGlyphMap.TryGetValue(ch, out ushort glyphIndex))
                continue;

            Geometry outline = gtf.GetGlyphOutline(glyphIndex, emSize, emSize);
            // Wrap rather than set outline.Transform directly: the returned geometry may be frozen.
            var positioned = new GeometryGroup { Transform = new TranslateTransform(advance, 0) };
            positioned.Children.Add(outline);
            group.Children.Add(positioned);

            if (gtf.AdvanceWidths.TryGetValue(glyphIndex, out double aw))
                advance += aw * emSize;
            placed++;
        }

        if (placed == 0)
            throw new InvalidOperationException("None of those characters exist in the chosen font.");

        PathGeometry combined = PathGeometry.CreateFromGeometry(group);
        Rect b = combined.Bounds;
        if (b.IsEmpty || b.Width <= 0 || b.Height <= 0)
            throw new InvalidOperationException(
                "Those characters produced no visible outline (e.g. only whitespace).");

        return combined;
    }

    /// <summary>
    /// Tries to load a glyph typeface without throwing: first via the forgiving
    /// Fonts.GetTypefaces path, then via the (fragile) direct constructor.
    /// </summary>
    private static bool TryLoadGlyphTypeface(string fontPath, out GlyphTypeface gtf)
    {
        gtf = null!;

        Uri fontUri;
        try { fontUri = new Uri(fontPath, UriKind.Absolute); }
        catch { return false; }

        try
        {
            foreach (Typeface tf in Fonts.GetTypefaces(fontUri))
                if (tf.TryGetGlyphTypeface(out GlyphTypeface g)) { gtf = g; return true; }
        }
        catch
        {
            // fall through to direct construction
        }

        try { gtf = new GlyphTypeface(fontUri); return true; }
        catch { return false; }
    }

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

    private static byte[] OverlayGlyphOnPng(
        byte[] qrPngBytes, string fontPath, string text, string glyphHex, double sizePercent)
    {
        BitmapSource qr = LoadBitmap(qrPngBytes);
        double w = qr.PixelWidth;
        double h = qr.PixelHeight;
        double box = Math.Min(w, h) * (sizePercent / 100.0);
        double pad = box * 0.12;
        double x = (w - box) / 2.0;
        double y = (h - box) / 2.0;

        PathGeometry glyph = BuildGlyphGeometry(fontPath, text);
        Matrix fit = FitMatrix(glyph.Bounds, x, y, box);
        var brush = new SolidColorBrush(ParseColor(glyphHex));
        brush.Freeze();

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(qr, new Rect(0, 0, w, h));
            var bg = new Rect(x - pad, y - pad, box + 2 * pad, box + 2 * pad);
            dc.DrawRoundedRectangle(Brushes.White, null, bg, pad, pad);
            dc.PushTransform(new MatrixTransform(fit));
            dc.DrawGeometry(brush, null, glyph);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static string EmbedGlyphInSvg(
        string svg, string fontPath, string text, string glyphHex, double sizePercent)
    {
        (double w, double h) = ReadSvgDimensions(svg);
        double box = Math.Min(w, h) * (sizePercent / 100.0);
        double pad = box * 0.12;
        double x = (w - box) / 2.0;
        double y = (h - box) / 2.0;

        PathGeometry glyph = BuildGlyphGeometry(fontPath, text);
        Matrix fit = FitMatrix(glyph.Bounds, x, y, box);
        string pathData = GeometryToSvgPath(glyph);

        string overlay =
            $"<rect x=\"{F(x - pad)}\" y=\"{F(y - pad)}\" width=\"{F(box + 2 * pad)}\" " +
            $"height=\"{F(box + 2 * pad)}\" rx=\"{F(pad)}\" ry=\"{F(pad)}\" fill=\"#FFFFFF\"/>" +
            $"<g transform=\"matrix({F(fit.M11)} {F(fit.M12)} {F(fit.M21)} {F(fit.M22)} " +
            $"{F(fit.OffsetX)} {F(fit.OffsetY)})\">" +
            $"<path d=\"{pathData}\" fill=\"{glyphHex}\" fill-rule=\"nonzero\"/></g>";

        int closeIdx = svg.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0)
            throw new InvalidOperationException("Could not parse the generated SVG to insert the glyph.");

        return svg.Substring(0, closeIdx) + overlay + svg.Substring(closeIdx);
    }

    /// <summary>Converts a PathGeometry to an SVG path 'd' string, preserving curves.</summary>
    private static string GeometryToSvgPath(PathGeometry geometry)
    {
        var sb = new StringBuilder();

        foreach (PathFigure figure in geometry.Figures)
        {
            sb.Append('M').Append(Pt(figure.StartPoint));

            foreach (PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment ls:
                        sb.Append('L').Append(Pt(ls.Point));
                        break;
                    case PolyLineSegment pls:
                        foreach (var p in pls.Points) sb.Append('L').Append(Pt(p));
                        break;
                    case BezierSegment bs:
                        sb.Append('C').Append(Pt(bs.Point1)).Append(Pt(bs.Point2)).Append(Pt(bs.Point3));
                        break;
                    case PolyBezierSegment pbs:
                        for (int i = 0; i + 2 < pbs.Points.Count; i += 3)
                            sb.Append('C').Append(Pt(pbs.Points[i])).Append(Pt(pbs.Points[i + 1])).Append(Pt(pbs.Points[i + 2]));
                        break;
                    case QuadraticBezierSegment qs:
                        sb.Append('Q').Append(Pt(qs.Point1)).Append(Pt(qs.Point2));
                        break;
                    case PolyQuadraticBezierSegment pqs:
                        for (int i = 0; i + 1 < pqs.Points.Count; i += 2)
                            sb.Append('Q').Append(Pt(pqs.Points[i])).Append(Pt(pqs.Points[i + 1]));
                        break;
                    case ArcSegment arc:
                        sb.Append('A')
                          .Append(F(arc.Size.Width)).Append(' ').Append(F(arc.Size.Height)).Append(' ')
                          .Append(F(arc.RotationAngle)).Append(' ')
                          .Append(arc.IsLargeArc ? '1' : '0').Append(' ')
                          .Append(arc.SweepDirection == SweepDirection.Clockwise ? '1' : '0').Append(' ')
                          .Append(Pt(arc.Point));
                        break;
                }
            }

            if (figure.IsClosed) sb.Append('Z');
        }

        return sb.ToString();
    }

    private static string Pt(Point p) =>
        F(p.X) + " " + F(p.Y) + " ";

    private static Color ParseColor(string hex)
    {
        string h = NormalizeHex(hex).TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(h.Substring(0, 2), 16),
            Convert.ToByte(h.Substring(2, 2), 16),
            Convert.ToByte(h.Substring(4, 2), 16));
    }

    // ==================== HELPERS ====================

    private QRCodeGenerator.ECCLevel GetEccLevel() => EccComboBox.SelectedIndex switch
    {
        0 => QRCodeGenerator.ECCLevel.L,
        1 => QRCodeGenerator.ECCLevel.M,
        3 => QRCodeGenerator.ECCLevel.H,
        _ => QRCodeGenerator.ECCLevel.Q,
    };

    private int GetPixelsPerModule()
    {
        if (!int.TryParse(PixelsPerModuleTextBox.Text, out int value) || value < 1 || value > 100)
            throw new InvalidOperationException("Pixels per module must be a whole number between 1 and 100.");
        return value;
    }

    private double GetLogoSizePercent()
    {
        if (!double.TryParse(LogoSizeTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
            && !double.TryParse(LogoSizeTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            throw new InvalidOperationException("Logo size must be a number.");
        if (value < 5 || value > 40)
            throw new InvalidOperationException("Logo size should be between 5% and 40% so the code stays scannable.");
        return value;
    }

    private static void RequireFile(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(
                $"The selected {what} no longer exists — it may have been moved, renamed, or deleted. " +
                "Pick it again with Browse….");
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

    private static string NormalizeHex(string? hex)
    {
        hex = (hex ?? string.Empty).Trim();
        if (!hex.StartsWith("#")) hex = "#" + hex;
        if (!Regex.IsMatch(hex, "^#([0-9a-fA-F]{6})$"))
            throw new InvalidOperationException($"'{hex}' is not a valid 6-digit hex color (e.g. #000000).");
        return hex.ToUpperInvariant();
    }

    private static byte[] HexToRgba(string? hex)
    {
        string normalized = NormalizeHex(hex).TrimStart('#');
        byte r = Convert.ToByte(normalized.Substring(0, 2), 16);
        byte g = Convert.ToByte(normalized.Substring(2, 2), 16);
        byte b = Convert.ToByte(normalized.Substring(4, 2), 16);
        return new byte[] { r, g, b, 255 };
    }

    private static BitmapImage LoadBitmap(byte[] pngBytes)
    {
        using var ms = new MemoryStream(pngBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapImage LoadBitmapFromFile(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static double ParseInv(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void ShowError(string message)
    {
        StatusText.Text = message;
        MessageBox.Show(message, "QR Code Generator", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

// ==================== URL SCHEMA MODEL ====================

/// <summary>Root of the url-schema.json file.</summary>
public sealed class UrlSchemaJson
{
    /// <summary>Base domain, e.g. "https://example.com".</summary>
    public string? Domain { get; set; }

    /// <summary>Optional path after the domain, e.g. "landing" or "campaign/spring".</summary>
    public string? Prefix { get; set; }

    /// <summary>Ordered list of query parameters shown as inputs.</summary>
    public List<UrlParamJson>? Parameters { get; set; }
}

/// <summary>A single query parameter definition.</summary>
public sealed class UrlParamJson
{
    /// <summary>Query key (e.g. "utm_source"). Required.</summary>
    public string? Name { get; set; }

    /// <summary>Friendly label for the input; falls back to Name when omitted.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Pre-fill value(s). A single value (string/number/bool) renders as a textbox;
    /// a JSON array of values renders as a dropdown with the first entry pre-selected.
    /// </summary>
    public JsonElement? Default { get; set; }

    /// <summary>When true (the default), the parameter is left out of the URL if its value is empty.</summary>
    public bool? OmitIfEmpty { get; set; }

    /// <summary>Flattens <see cref="Default"/> to a list of strings (empty, one, or many).</summary>
    public List<string> ResolveDefaultValues()
    {
        var values = new List<string>();
        if (Default is not JsonElement el) return values;

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in el.EnumerateArray())
                values.Add(ScalarToString(item));
        }
        else
        {
            values.Add(ScalarToString(el));
        }
        return values;
    }

    private static string ScalarToString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? string.Empty,
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => e.GetRawText(),
    };
}

/// <summary>Binds a parameter definition to a UI control's current value.</summary>
internal sealed class ParamInput
{
    public UrlParamJson Def { get; }
    public Func<string> GetValue { get; }

    public ParamInput(UrlParamJson def, Func<string> getValue)
    {
        Def = def;
        GetValue = getValue;
    }
}
