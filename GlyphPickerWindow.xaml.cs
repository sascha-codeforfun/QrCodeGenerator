using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace QrCodeGenerator;

/// <summary>
/// Combined font + glyph chooser. Works on local copies of the font path and character
/// string; the caller reads <see cref="ResultFontPath"/> / <see cref="ResultChars"/> only
/// when ShowDialog() returns true (OK). Cancel leaves the caller's state untouched.
/// </summary>
public partial class GlyphPickerWindow : Window
{
    private const int PageSize = 250;

    private readonly List<Entry> _entries = new();
    private readonly List<Entry> _filtered = new();
    private readonly Brush _tileBrush;

    private string? _fontPath;
    private MainWindow.LoadedFont? _font;
    private int _page;

    /// <summary>Chosen font file path (null = none), valid after OK.</summary>
    public string? ResultFontPath { get; private set; }

    /// <summary>Chosen character string, valid after OK.</summary>
    public string ResultChars { get; private set; } = string.Empty;

    private readonly record struct Entry(char Ch, ushort GlyphIndex, int CodePoint);

    public GlyphPickerWindow(string? initialFontPath, string initialChars)
    {
        InitializeComponent();

        _tileBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED));
        _tileBrush.Freeze();

        CharsBox.Text = initialChars ?? string.Empty;

        if (!string.IsNullOrEmpty(initialFontPath) && File.Exists(initialFontPath))
            LoadFont(initialFontPath!);
        else
            RefreshEmptyState();

        UpdatePreview();

        Closed += (_, _) => _font?.Dispose();
    }

    // ---------- font selection ----------

    private void SystemFontsClick(object sender, RoutedEventArgs e)
    {
        var picker = new SystemFontPickerWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedPath != null)
            LoadFont(picker.SelectedPath);
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Font files (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
            LoadFont(dialog.FileName);
    }

    private void ClearFontClick(object sender, RoutedEventArgs e)
    {
        _font?.Dispose();
        _font = null;
        _fontPath = null;
        _entries.Clear();
        _filtered.Clear();
        GlyphPanel.Children.Clear();
        FontNameText.Text = "No font chosen";
        CountText.Text = string.Empty;
        PrevButton.IsEnabled = NextButton.IsEnabled = false;
        RefreshEmptyState();
        UpdatePreview();
    }

    private void LoadFont(string path)
    {
        try
        {
            MainWindow.LoadedFont loaded = MainWindow.LoadFontRobust(path);
            _font?.Dispose();
            _font = loaded;
            _fontPath = path;
            FontNameText.Text = Path.GetFileName(path);

            BuildEntries();
            ApplyFilter(SearchBox.Text);
            RefreshEmptyState();
            UpdatePreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Choose font & glyph",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshEmptyState()
    {
        bool hasFont = _font != null;
        EmptyHint.Visibility = hasFont ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- glyph grid ----------

    private void BuildEntries()
    {
        _entries.Clear();
        if (_font == null) return;

        foreach (KeyValuePair<int, ushort> kv in _font.Typeface.CharacterToGlyphMap)
        {
            int cp = kv.Key;
            if (cp > 0xFFFF) continue;
            char ch = (char)cp;
            if (char.IsControl(ch)) continue;
            _entries.Add(new Entry(ch, kv.Value, cp));
        }
        _entries.Sort((a, b) => a.CodePoint.CompareTo(b.CodePoint));
    }

    private void ApplyFilter(string? filter)
    {
        string q = (filter ?? string.Empty).Trim();
        _filtered.Clear();

        foreach (Entry e in _entries)
        {
            if (q.Length == 0) { _filtered.Add(e); continue; }
            string hex = e.CodePoint.ToString("x4");
            if (hex.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Ch.ToString() == q)
                _filtered.Add(e);
        }

        _page = 0;
        RenderPage();
    }

    private void RenderPage()
    {
        GlyphPanel.Children.Clear();
        if (_font == null)
        {
            CountText.Text = string.Empty;
            PrevButton.IsEnabled = NextButton.IsEnabled = false;
            return;
        }

        GlyphTypeface gtf = _font.Typeface;
        int total = _filtered.Count;
        int pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        int start = _page * PageSize;
        int end = Math.Min(start + PageSize, total);

        for (int i = start; i < end; i++)
        {
            Entry e = _filtered[i];
            ImageSource? img = GlyphImage(gtf, e.GlyphIndex);
            if (img == null) continue;

            var tile = new Button
            {
                Style = (Style)FindResource("GlyphTile"),
                Content = new Image { Source = img, Stretch = Stretch.Uniform },
                Tag = e.Ch,
                ToolTip = $"U+{e.CodePoint:X4}",
            };
            tile.Click += Tile_Click;
            GlyphPanel.Children.Add(tile);
        }

        CountText.Text = total == 0
            ? "no glyphs"
            : $"Page {_page + 1}/{pages} · {start + 1}\u2013{end} of {total}";
        PrevButton.IsEnabled = _page > 0;
        NextButton.IsEnabled = _page < pages - 1;
        GlyphScroll.ScrollToTop();
    }

    private ImageSource? GlyphImage(GlyphTypeface gtf, ushort glyphIndex)
    {
        try
        {
            Geometry outline = gtf.GetGlyphOutline(glyphIndex, 100, 100);
            PathGeometry pg = PathGeometry.CreateFromGeometry(outline);
            Rect b = pg.Bounds;
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return null;
            pg.FillRule = FillRule.Nonzero;
            var di = new DrawingImage(new GeometryDrawing(_tileBrush, null, pg));
            di.Freeze();
            return di;
        }
        catch
        {
            return null;
        }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: char ch })
        {
            CharsBox.Text += ch;
            CharsBox.CaretIndex = CharsBox.Text.Length;   // TextChanged -> UpdatePreview
        }
    }

    // ---------- preview ----------

    private void CharsBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_font == null || string.IsNullOrEmpty(CharsBox.Text))
        {
            PreviewImage.Source = null;
            return;
        }
        try
        {
            PathGeometry geo = MainWindow.BuildGlyphGeometry(_font.Typeface, CharsBox.Text);
            geo.FillRule = FillRule.Nonzero;
            var di = new DrawingImage(new GeometryDrawing(_tileBrush, null, geo));
            di.Freeze();
            PreviewImage.Source = di;
        }
        catch
        {
            PreviewImage.Source = null;
        }
    }

    // ---------- pager / search ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);
    private void PrevButton_Click(object sender, RoutedEventArgs e) { _page--; RenderPage(); }
    private void NextButton_Click(object sender, RoutedEventArgs e) { _page++; RenderPage(); }

    // ---------- commit / cancel ----------

    private void OkClick(object sender, RoutedEventArgs e)
    {
        ResultFontPath = _fontPath;
        ResultChars = CharsBox.Text ?? string.Empty;
        DialogResult = true;
        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
