using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QrCodeGenerator;

public partial class ColorPickerWindow : Window
{
    private static readonly string[] Presets =
    {
        "#000000", "#1E2024", "#333333", "#555555", "#808080", "#AAAAAA", "#CCCCCC", "#FFFFFF",
        "#1D4ED8", "#2563EB", "#0E7490", "#166534", "#15803D", "#B45309", "#B91C1C", "#7C3AED",
        "#BE185D", "#DB2777", "#EA580C", "#CA8A04", "#4D7C0F", "#0F766E", "#1E3A8A", "#111827",
    };

    private readonly Color? _pair;
    private readonly string _pairLabel;
    private bool _sync;   // guard against recursive hex<->rgb updates

    /// <summary>The chosen color as #RRGGBB, or null if cancelled.</summary>
    public string? SelectedHex { get; private set; }

    public ColorPickerWindow(string initialHex, string? pairHex = null, string? pairLabel = null)
    {
        InitializeComponent();

        _pair = TryParse(pairHex, out Color pc) ? pc : (Color?)null;
        _pairLabel = pairLabel ?? "the other color";
        ContrastPanel.Visibility = _pair.HasValue ? Visibility.Visible : Visibility.Collapsed;

        BuildPalette();

        Color initial = TryParse(initialHex, out Color c) ? c : Colors.Black;
        SetColor(initial, updateHex: true, updateRgb: true);
    }

    private void BuildPalette()
    {
        foreach (string hex in Presets)
        {
            TryParse(hex, out Color col);
            var brush = new SolidColorBrush(col); brush.Freeze();
            var b = new Button
            {
                Style = (Style)FindResource("Swatch"),
                Width = 34,
                Height = 30,
                Margin = new Thickness(3),
                Background = brush,
                Tag = hex,
                ToolTip = hex,
            };
            b.Click += (_, _) => SetColor(col, updateHex: true, updateRgb: true);
            PalettePanel.Children.Add(b);
        }
    }

    private void SetColor(Color c, bool updateHex, bool updateRgb)
    {
        _sync = true;
        try
        {
            var brush = new SolidColorBrush(c); brush.Freeze();
            PreviewSwatch.Background = brush;

            if (updateHex) HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            if (updateRgb)
            {
                RBox.Text = c.R.ToString();
                GBox.Text = c.G.ToString();
                BBox.Text = c.B.ToString();
            }

            UpdateContrast(c);
        }
        finally
        {
            _sync = false;
        }
    }

    private void UpdateContrast(Color c)
    {
        if (!_pair.HasValue) return;

        double ratio = ContrastRatio(c, _pair.Value);
        var fg = new SolidColorBrush(c); fg.Freeze();
        var bg = new SolidColorBrush(_pair.Value); bg.Freeze();
        ContrastSample.Background = bg;
        ContrastSampleText.Foreground = fg;

        (string rating, string advice) = ratio switch
        {
            >= 7.0 => ("excellent", "Plenty of contrast — scans reliably."),
            >= 4.5 => ("good", "Solid contrast for QR codes."),
            >= 3.0 => ("okay", "Usable, but test a scan to be safe."),
            _ => ("low", "Too little contrast — the code may not scan."),
        };
        ContrastText.Text = $"Contrast vs {_pairLabel}: {ratio:0.0}:1 — {rating}";
        ContrastAdvice.Text = advice;
    }

    private static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Chan(double v)
        {
            v /= 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Chan(c.R) + 0.7152 * Chan(c.G) + 0.0722 * Chan(c.B);
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync) return;
        if (TryParse(HexBox.Text, out Color c))
            SetColor(c, updateHex: false, updateRgb: true);
    }

    private void Rgb_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync) return;
        if (byte.TryParse(RBox.Text, out byte r) &&
            byte.TryParse(GBox.Text, out byte g) &&
            byte.TryParse(BBox.Text, out byte b))
        {
            SetColor(Color.FromRgb(r, g, b), updateHex: true, updateRgb: false);
        }
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryParse(HexBox.Text, out Color c))
        {
            SelectedHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show("That isn't a valid color. Use a 6-digit hex like #1D4ED8.",
                "Pick a color", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool TryParse(string? hex, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string h = hex.Trim();
        if (!h.StartsWith("#")) h = "#" + h;
        if (!Regex.IsMatch(h, "^#[0-9a-fA-F]{6}$")) return false;
        h = h.TrimStart('#');
        color = Color.FromRgb(
            byte.Parse(h.Substring(0, 2), NumberStyles.HexNumber),
            byte.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
            byte.Parse(h.Substring(4, 2), NumberStyles.HexNumber));
        return true;
    }
}
