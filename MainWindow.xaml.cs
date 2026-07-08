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
    private bool _previewDark;

    // URL-builder schema state
    private UrlSchemaJson? _schema;
    private readonly List<ParamInput> _paramInputs = new();

    public MainWindow()
    {
        InitializeComponent();
        TryAutoLoadSchema();
        InitSwatches();
        UpdateGlyphSummary();
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

        var labelBrush = (Brush)FindResource("Text");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        bool leftUsed = false;   // is column 0 of the current row occupied?

        void EnsureRow(int r)
        {
            while (grid.RowDefinitions.Count <= r)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        foreach (UrlParamJson p in _schema.Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;

            List<string> defaults = p.ResolveDefaultValues();
            bool wide = p.Wide == true || IsWideByContent(defaults);
            FrameworkElement field = BuildParamField(p, defaults, labelBrush);

            if (wide)
            {
                if (leftUsed) { row++; leftUsed = false; }   // move to a fresh row
                EnsureRow(row);
                Grid.SetRow(field, row);
                Grid.SetColumn(field, 0);
                Grid.SetColumnSpan(field, 3);
                grid.Children.Add(field);
                row++;
            }
            else if (!leftUsed)
            {
                EnsureRow(row);
                Grid.SetRow(field, row);
                Grid.SetColumn(field, 0);
                grid.Children.Add(field);
                leftUsed = true;
            }
            else
            {
                EnsureRow(row);
                Grid.SetRow(field, row);
                Grid.SetColumn(field, 2);
                grid.Children.Add(field);
                leftUsed = false;
                row++;
            }
        }

        ParamsPanel.Children.Add(grid);
    }

    /// <summary>Long values (or a long dropdown option) get the full width so they don't truncate.</summary>
    private static bool IsWideByContent(List<string> defaults)
    {
        foreach (string d in defaults)
            if (d.Length > 40) return true;
        return false;
    }

    /// <summary>Builds one parameter's label + input (textbox or dropdown) and registers its value getter.</summary>
    private FrameworkElement BuildParamField(UrlParamJson p, List<string> defaults, Brush labelBrush)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(p.Label) ? p.Name : p.Label,
            Foreground = labelBrush,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 3),
        };
        wrap.Children.Add(label);

        if (defaults.Count >= 2)
        {
            var combo = new ComboBox();
            foreach (string option in defaults) combo.Items.Add(option);
            combo.SelectedIndex = 0;
            wrap.Children.Add(combo);
            _paramInputs.Add(new ParamInput(p, () => combo.SelectedItem as string ?? string.Empty));
        }
        else
        {
            string initial = defaults.Count == 1 ? defaults[0] : string.Empty;
            var box = new TextBox { Text = initial };
            wrap.Children.Add(box);
            _paramInputs.Add(new ParamInput(p, () => box.Text ?? string.Empty));
        }

        return wrap;
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

    /// <summary>Regenerates the preview, keeping the last good image on any error (no popup).</summary>
    private void RegeneratePreviewSafe()
    {
        if (QrImage == null) return;   // still initializing
        try
        {
            byte[] pngBytes = BuildPng();
            QrImage.Source = LoadBitmap(pngBytes);
            PlaceholderText.Visibility = Visibility.Collapsed;
            SavePngButton.IsEnabled = true;
            SaveSvgButton.IsEnabled = true;
            if (StatusText.Text.StartsWith("Can't preview", StringComparison.Ordinal))
                StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            // Keep the last good preview; just a quiet hint, no popup.
            StatusText.Text = "Can't preview yet: " + ex.Message;
        }
    }

    private void AutoRegen_LostFocus(object sender, RoutedEventArgs e) => RegeneratePreviewSafe();

    private void EccComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RegeneratePreviewSafe();

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
            LogoPathTextBox.Foreground = (Brush)FindResource("Text");
            StatusText.Text = "Tip: with a center image, pick High (H) error correction so the code still scans.";
        }
    }

    private void ClearLogoButton_Click(object sender, RoutedEventArgs e)
    {
        _logoPath = null;
        LogoPathTextBox.Text = "No image selected";
        LogoPathTextBox.Foreground = (Brush)FindResource("TextDim");
    }

    private void CenterModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Named elements may not exist yet during initial XAML load.
        if (ImagePanel == null || FontPanel == null || SizePanel == null) return;

        int mode = CenterModeComboBox.SelectedIndex;
        ImagePanel.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
        FontPanel.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        SizePanel.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;

        UpdateGlyphSummary();
        UpdateGlyphPreview();
        RegeneratePreviewSafe();
    }

    private void ChooseFontGlyphButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var chooser = new GlyphPickerWindow(_fontPath, FontCharsTextBox.Text) { Owner = this };
            if (chooser.ShowDialog() == true)
            {
                _fontPath = chooser.ResultFontPath;
                FontCharsTextBox.Text = chooser.ResultChars;   // triggers preview + swatch
                if (!string.IsNullOrEmpty(_fontPath))
                    StatusText.Text = "Tip: use High (H) error correction with a center glyph so the code still scans.";
                UpdateGlyphSummary();
                UpdateGlyphPreview();
                RegeneratePreviewSafe();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void UpdateGlyphSummary()
    {
        if (GlyphSummaryText == null) return;

        if (string.IsNullOrEmpty(_fontPath))
        {
            GlyphSummaryText.Text = "No font or glyph chosen";
            return;
        }

        string font = Path.GetFileName(_fontPath);
        string chars = FontCharsTextBox.Text ?? string.Empty;
        GlyphSummaryText.Text = chars.Length > 0 ? $"{font} · \u201c{chars}\u201d" : $"{font} · (no glyph)";
    }

    private void GlyphInput_Changed(object sender, TextChangedEventArgs e)
    {
        SetSwatchFill(GlyphSwatch, GlyphColorTextBox?.Text ?? string.Empty);
        UpdateGlyphPreview();
    }

    // ==================== COLOR PICKER ====================

    private void ColorHex_Changed(object sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, DarkColorTextBox)) SetSwatchFill(DarkSwatch, DarkColorTextBox.Text);
        else if (ReferenceEquals(sender, LightColorTextBox)) SetSwatchFill(LightSwatch, LightColorTextBox.Text);
        else if (ReferenceEquals(sender, GlyphBgColorTextBox)) SetSwatchFill(GlyphBgSwatch, GlyphBgColorTextBox.Text);
    }

    private void SetSwatchFill(Button? swatch, string hex)
    {
        if (swatch == null) return;
        try { swatch.Background = new SolidColorBrush(ParseColor(hex)); }
        catch { swatch.Background = (Brush)FindResource("Card"); }
    }

    private void InitSwatches()
    {
        SetSwatchFill(DarkSwatch, DarkColorTextBox.Text);
        SetSwatchFill(LightSwatch, LightColorTextBox.Text);
        SetSwatchFill(GlyphSwatch, GlyphColorTextBox.Text);
        SetSwatchFill(GlyphBgSwatch, GlyphBgColorTextBox.Text);
    }

    private void DarkSwatch_Click(object sender, RoutedEventArgs e)
        => OpenColorPicker(DarkColorTextBox, LightColorTextBox.Text, "light color");

    private void LightSwatch_Click(object sender, RoutedEventArgs e)
        => OpenColorPicker(LightColorTextBox, DarkColorTextBox.Text, "dark color");

    private void GlyphSwatch_Click(object sender, RoutedEventArgs e)
    {
        // Glyph readability is glyph-color vs its background: the pad, or (when transparent)
        // the light modules the glyph sits on.
        bool transparent = GlyphBgNoneCheck?.IsChecked == true;
        string pairHex = transparent ? LightColorTextBox.Text : GlyphBgColorTextBox.Text;
        string pairLabel = transparent ? "light modules" : "glyph background";
        OpenColorPicker(GlyphColorTextBox, pairHex, pairLabel);
    }

    private void GlyphBgSwatch_Click(object sender, RoutedEventArgs e)
        => OpenColorPicker(GlyphBgColorTextBox, DarkColorTextBox.Text, "dark modules");

    private void GlyphBgNone_Changed(object sender, RoutedEventArgs e)
    {
        if (GlyphBgColorTextBox == null) return;   // still initializing
        bool none = GlyphBgNoneCheck.IsChecked == true;
        GlyphBgColorTextBox.IsEnabled = !none;
        GlyphBgSwatch.IsEnabled = !none;
        RegeneratePreviewSafe();
    }

    private void OpenColorPicker(TextBox target, string? pairHex, string? pairLabel)
    {
        var picker = new ColorPickerWindow(target.Text, pairHex, pairLabel) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedHex != null)
        {
            target.Text = picker.SelectedHex;   // triggers TextChanged → validation, swatch
            RegeneratePreviewSafe();
        }
    }

    private void PreviewBgButton_Click(object sender, RoutedEventArgs e)
    {
        _previewDark = !_previewDark;
        PreviewArea.Background = _previewDark ? (Brush)FindResource("Bg") : Brushes.White;
        PreviewBgButton.Content = _previewDark ? "Preview BG: dark" : "Preview BG: white";
        PlaceholderText.Foreground = _previewDark
            ? (Brush)FindResource("TextDim")
            : new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    }

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
                NormalizeHex(GlyphColorTextBox.Text), GetGlyphBgHex(), GetLogoSizePercent());
        return baseBytes;
    }

    private string BuildSvg()
    {
        string svg = GenerateSvg(
            UrlTextBox.Text,
            GetEccLevel(),
            NormalizeHex(DarkColorTextBox.Text),
            NormalizeHex(LightColorTextBox.Text));

        return ApplyCenterGraphicToSvg(svg);
    }

    /// <summary>Applies the current Single-tab center graphic (image or glyph) to an SVG, if any.</summary>
    private string ApplyCenterGraphicToSvg(string svg)
    {
        if (CenterMode == 1 && HasImageInput)
            return EmbedLogoInSvg(svg, _logoPath!, GetLogoSizePercent());
        if (CenterMode == 2 && HasGlyphInput)
            return EmbedGlyphInSvg(svg, _fontPath!, FontCharsTextBox.Text,
                NormalizeHex(GlyphColorTextBox.Text), GetGlyphBgHex(), GetLogoSizePercent());
        return svg;
    }

    /// <summary>Glyph background color, or null when the "none" (transparent) toggle is on.</summary>
    private string? GetGlyphBgHex()
        => GlyphBgNoneCheck?.IsChecked == true ? null : NormalizeHex(GlyphBgColorTextBox.Text);

    /// <summary>Throws with a clear message if the active center-graphic config is invalid.</summary>
    private void ValidateCenterGraphicConfig()
    {
        if (CenterMode == 1 && HasImageInput)
        {
            RequireFile(_logoPath!, "center image");
            _ = GetLogoSizePercent();
        }
        else if (CenterMode == 2 && HasGlyphInput)
        {
            RequireFile(_fontPath!, "font file");
            _ = NormalizeHex(GlyphColorTextBox.Text);
            if (GlyphBgNoneCheck?.IsChecked != true) _ = NormalizeHex(GlyphBgColorTextBox.Text);
            _ = GetLogoSizePercent();
        }
    }

    // ==================== BATCH TAB ====================

    private void GenerateZipButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateBatchSummary();

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

            // Fail fast (before prompting for a save location) if the center graphic is misconfigured.
            ValidateCenterGraphicConfig();

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
                        svg = ApplyCenterGraphicToSvg(svg);
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

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ignore SelectionChanged bubbling up from inner ComboBoxes/ListBoxes.
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        UpdateBatchSummary();
    }

    /// <summary>Refreshes the read-only summary of Single-tab settings shown on the Batch tab.</summary>
    private void UpdateBatchSummary()
    {
        if (BatchAppliedText == null) return;

        var parts = new List<string>
        {
            $"Error correction: {EccLabel()}",
            $"Dark {DarkColorTextBox.Text}",
            $"Light {LightColorTextBox.Text}",
        };

        if (CenterMode == 1 && HasImageInput)
            parts.Add($"Center: image \u201c{Path.GetFileName(_logoPath)}\u201d @ {ImageSizeTextBox.Text}%");
        else if (CenterMode == 2 && HasGlyphInput)
            parts.Add($"Center: glyph \u201c{FontCharsTextBox.Text}\u201d from " +
                      $"{Path.GetFileName(_fontPath)} @ {LogoSizeTextBox.Text}% ({GlyphColorTextBox.Text})");
        else
            parts.Add("Center: none");

        BatchAppliedText.Text = string.Join("   \u00b7   ", parts);
    }

    private string EccLabel() => EccComboBox.SelectedIndex switch
    {
        0 => "L \u2014 Low (7%)",
        1 => "M \u2014 Medium (15%)",
        3 => "H \u2014 High (30%)",
        _ => "Q \u2014 Quartile (25%)",
    };

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
        using LoadedFont lf = LoadFontRobust(fontPath);
        return BuildGlyphGeometry(lf.Typeface, text);
    }

    /// <summary>
    /// Loads a font's glyph typeface robustly and keeps any temp copy alive until disposed.
    /// Tries the original path first; if WPF's resolver trips (e.g. a font at the root of a
    /// secondary drive), copies the file to the system-drive temp folder and loads from there.
    /// </summary>
    internal static LoadedFont LoadFontRobust(string fontPath)
    {
        RequireFile(fontPath, "font file");

        if (TryLoadGlyphTypeface(fontPath, out GlyphTypeface gtf))
            return new LoadedFont(gtf, null);

        string temp = Path.Combine(
            Path.GetTempPath(),
            "qrgen_" + Guid.NewGuid().ToString("N") + Path.GetExtension(fontPath));
        try
        {
            File.Copy(fontPath, temp, overwrite: true);
            if (TryLoadGlyphTypeface(temp, out GlyphTypeface gtfTemp))
                return new LoadedFont(gtfTemp, temp);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // fall through to the friendly error below
        }

        try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }

        throw new InvalidOperationException(
            "Couldn't read that font. Make sure it's an intact TTF/OTF/TTC file " +
            "(WOFF/WOFF2 are not supported — convert them first).");
    }

    /// <summary>A loaded glyph typeface plus any temp file to clean up when done.</summary>
    internal sealed class LoadedFont : IDisposable
    {
        public GlyphTypeface Typeface { get; }
        private readonly string? _tempFile;

        public LoadedFont(GlyphTypeface typeface, string? tempFile)
        {
            Typeface = typeface;
            _tempFile = tempFile;
        }

        public void Dispose()
        {
            if (_tempFile == null) return;
            try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { /* best effort */ }
        }
    }

    /// <summary>Extracts the combined outline for the given characters from a loaded typeface.</summary>
    internal static PathGeometry BuildGlyphGeometry(GlyphTypeface gtf, string text)
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
    /// Tries to load a glyph typeface without throwing. Loads the specific file directly
    /// first (GlyphTypeface(Uri) reads the exact file), then falls back to Fonts.GetTypefaces.
    /// Direct-first matters: GetTypefaces consults WPF's installed-font cache and, for a file
    /// in C:\Windows\Fonts, can return glyphs from a different / previously loaded font.
    /// </summary>
    private static bool TryLoadGlyphTypeface(string fontPath, out GlyphTypeface gtf)
    {
        gtf = null!;

        Uri fontUri;
        try { fontUri = new Uri(fontPath, UriKind.Absolute); }
        catch { return false; }

        // Accurate: reads exactly this file.
        try { gtf = new GlyphTypeface(fontUri); return true; }
        catch { /* can NRE on some paths (e.g. secondary-drive root); fall through */ }

        // Fallback: forgiving, but cache-prone for installed fonts.
        try
        {
            foreach (Typeface tf in Fonts.GetTypefaces(fontUri))
                if (tf.TryGetGlyphTypeface(out GlyphTypeface g)) { gtf = g; return true; }
        }
        catch
        {
            // fall through
        }

        return false;
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
        byte[] qrPngBytes, string fontPath, string text, string glyphHex, string? glyphBgHex, double sizePercent)
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
            if (glyphBgHex != null)
            {
                var padBrush = new SolidColorBrush(ParseColor(glyphBgHex));
                padBrush.Freeze();
                var bg = new Rect(x - pad, y - pad, box + 2 * pad, box + 2 * pad);
                dc.DrawRoundedRectangle(padBrush, null, bg, pad, pad);
            }
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
        string svg, string fontPath, string text, string glyphHex, string? glyphBgHex, double sizePercent)
    {
        (double w, double h) = ReadSvgDimensions(svg);
        double box = Math.Min(w, h) * (sizePercent / 100.0);
        double pad = box * 0.12;
        double x = (w - box) / 2.0;
        double y = (h - box) / 2.0;

        PathGeometry glyph = BuildGlyphGeometry(fontPath, text);
        Matrix fit = FitMatrix(glyph.Bounds, x, y, box);
        string pathData = GeometryToSvgPath(glyph);

        string padRect = glyphBgHex == null
            ? string.Empty
            : $"<rect x=\"{F(x - pad)}\" y=\"{F(y - pad)}\" width=\"{F(box + 2 * pad)}\" " +
              $"height=\"{F(box + 2 * pad)}\" rx=\"{F(pad)}\" ry=\"{F(pad)}\" fill=\"{glyphBgHex}\"/>";

        string overlay =
            padRect +
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
        TextBox src = CenterMode == 2 ? LogoSizeTextBox : ImageSizeTextBox;
        if (!double.TryParse(src.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
            && !double.TryParse(src.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
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

    /// <summary>Force this field to span the full width of the two-column layout.</summary>
    public bool? Wide { get; set; }

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
