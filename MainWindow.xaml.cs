using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using HueThemeKit;
using QRCoder;
using QrCodeGenerator.Dialogs;
using QrCodeGenerator.Models;
using QrCodeGenerator.Rendering;
using QrCodeGenerator.Util;

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
        HueTitleBar.Attach(this);

        // The header's version block: the app version, and under it the SDK that built the exe
        // and the runtime packed into it, so any copy shows which .NET it carries.
        VersionText.Text = HueVersion.Text;
        SdkText.Text = "built with SDK " + (HueVersion.Sdk ?? "unknown");
        RuntimeText.Text = "runtime " + HueVersion.Runtime;

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
            FrameworkElement field = BuildParamField(p, defaults);

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
    private FrameworkElement BuildParamField(UrlParamJson p, List<string> defaults)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        // A field label like the ones in the markup: the theme's Label style. The text sits in a
        // TextBlock so an underscore in a schema name ("utm_source") is not read as an access key.
        var label = new Label
        {
            Content = new TextBlock { Text = string.IsNullOrWhiteSpace(p.Label) ? p.Name : p.Label },
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
            QrImage.Source = Bitmaps.FromBytes(pngBytes);
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
            QrImage.Source = Bitmaps.FromBytes(pngBytes);
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
            LogoPathTextBox.SetResourceReference(ForegroundProperty, "Brush.Text");
            StatusText.Text = "Tip: with a center image, pick High (H) error correction so the code still scans.";
        }
    }

    private void ClearLogoButton_Click(object sender, RoutedEventArgs e)
    {
        _logoPath = null;
        LogoPathTextBox.Text = "No image selected";
        LogoPathTextBox.SetResourceReference(ForegroundProperty, "Brush.TextDim");
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
        // The swatch shows the colour typed next to it: a content colour, not a theme colour.
        try { swatch.Background = new SolidColorBrush(ColorHex.Parse(hex)); }
        catch { swatch.SetResourceReference(BackgroundProperty, "Brush.Surface"); }
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
        // Dark is the window's own colour; white is paper (Content.Paper in App.xaml), which is
        // content and stays white in every theme.
        PreviewArea.SetResourceReference(Border.BackgroundProperty, _previewDark ? "Brush.Base" : "Content.Paper");
        PreviewBgButton.Content = _previewDark ? "Preview BG: dark" : "Preview BG: white";
        PlaceholderText.SetResourceReference(TextBlock.ForegroundProperty,
            _previewDark ? "Brush.TextDim" : "Content.PaperHint");
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
            PathGeometry geo = GlyphFonts.BuildGeometry(_fontPath!, FontCharsTextBox.Text);
            geo.FillRule = FillRule.Nonzero;

            Brush brush;
            // The glyph in the colour it will have in the code (content); black until that is valid.
            try { brush = new SolidColorBrush(ColorHex.Parse(GlyphColorTextBox.Text)); }
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
        using (QRCodeData data = QrRenderer.CreateQrData(UrlTextBox.Text, GetEccLevel()))
        {
            var png = new PngByteQRCode(data);
            baseBytes = png.GetGraphic(
                GetPixelsPerModule(),
                ColorHex.ToRgba(DarkColorTextBox.Text),
                ColorHex.ToRgba(LightColorTextBox.Text));
        }

        if (CenterMode == 1 && HasImageInput)
            return QrRenderer.OverlayLogoOnPng(baseBytes, _logoPath!, GetLogoSizePercent());
        if (CenterMode == 2 && HasGlyphInput)
            return QrRenderer.OverlayGlyphOnPng(baseBytes, _fontPath!, FontCharsTextBox.Text,
                ColorHex.Normalize(GlyphColorTextBox.Text), GetGlyphBgHex(), GetLogoSizePercent());
        return baseBytes;
    }

    private string BuildSvg()
    {
        string svg = QrRenderer.GenerateSvg(
            UrlTextBox.Text,
            GetEccLevel(),
            ColorHex.Normalize(DarkColorTextBox.Text),
            ColorHex.Normalize(LightColorTextBox.Text));

        return ApplyCenterGraphicToSvg(svg);
    }

    /// <summary>Applies the current Single-tab center graphic (image or glyph) to an SVG, if any.</summary>
    private string ApplyCenterGraphicToSvg(string svg)
    {
        if (CenterMode == 1 && HasImageInput)
            return QrRenderer.EmbedLogoInSvg(svg, _logoPath!, GetLogoSizePercent());
        if (CenterMode == 2 && HasGlyphInput)
            return QrRenderer.EmbedGlyphInSvg(svg, _fontPath!, FontCharsTextBox.Text,
                ColorHex.Normalize(GlyphColorTextBox.Text), GetGlyphBgHex(), GetLogoSizePercent());
        return svg;
    }

    /// <summary>Glyph background color, or null when the "none" (transparent) toggle is on.</summary>
    private string? GetGlyphBgHex()
        => GlyphBgNoneCheck?.IsChecked == true ? null : ColorHex.Normalize(GlyphBgColorTextBox.Text);

    /// <summary>Throws with a clear message if the active center-graphic config is invalid.</summary>
    private void ValidateCenterGraphicConfig()
    {
        if (CenterMode == 1 && HasImageInput)
        {
            Guard.RequireFile(_logoPath!, "center image");
            _ = GetLogoSizePercent();
        }
        else if (CenterMode == 2 && HasGlyphInput)
        {
            Guard.RequireFile(_fontPath!, "font file");
            _ = ColorHex.Normalize(GlyphColorTextBox.Text);
            if (GlyphBgNoneCheck?.IsChecked != true) _ = ColorHex.Normalize(GlyphBgColorTextBox.Text);
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
            string darkHex = ColorHex.Normalize(DarkColorTextBox.Text);
            string lightHex = ColorHex.Normalize(LightColorTextBox.Text);

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
                        string svg = QrRenderer.GenerateSvg(payload, ecc, darkHex, lightHex);
                        svg = ApplyCenterGraphicToSvg(svg);
                        string entryName = FileNameSanitizer.UniqueName(FileNameSanitizer.BuildSvgFileName(payload), usedNames) + ".svg";

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
            HueMessage.Show(IsLoaded ? this : null, "QR Code Generator", ex.Message);
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

    private void ShowError(string message)
    {
        StatusText.Text = message;
        HueMessage.Show(IsLoaded ? this : null, "QR Code Generator", message);
    }
}
