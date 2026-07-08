using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QrCodeGenerator;

public partial class SystemFontPickerWindow : Window
{
    private sealed record FontItem(string Display, string Path)
    {
        public override string ToString() => Display;
    }

    private readonly List<FontItem> _all = new();

    /// <summary>The chosen font file path, or null if cancelled.</summary>
    public string? SelectedPath { get; private set; }

    public SystemFontPickerWindow()
    {
        InitializeComponent();
        Enumerate();
        Populate(null);
    }

    private void Enumerate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (FontFamily fam in Fonts.SystemFontFamilies)
            {
                string family = FamilyName(fam);
                try
                {
                    foreach (Typeface tf in fam.GetTypefaces())
                    {
                        if (!tf.TryGetGlyphTypeface(out GlyphTypeface gtf)) continue;

                        Uri uri = gtf.FontUri;
                        if (uri == null || !uri.IsFile) continue;

                        string path = uri.LocalPath;
                        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                        if (ext is not (".ttf" or ".otf" or ".ttc")) continue;
                        if (!seen.Add(path)) continue;   // one entry per file

                        var styleParts = new List<string>();
                        if (tf.Weight != FontWeights.Normal) styleParts.Add(tf.Weight.ToString());
                        if (tf.Style != FontStyles.Normal) styleParts.Add(tf.Style.ToString());
                        string style = styleParts.Count > 0 ? " (" + string.Join(" ", styleParts) + ")" : string.Empty;
                        _all.Add(new FontItem(family + style, path));
                    }
                }
                catch
                {
                    // skip a family that won't enumerate
                }
            }
        }
        catch
        {
            // If system font enumeration is blocked, the list is simply empty;
            // the user can still use Browse… on the main window.
        }

        _all.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
    }

    private static string FamilyName(FontFamily fam)
    {
        foreach (var name in fam.FamilyNames.Values) return name;
        return fam.Source;
    }

    private void Populate(string? filter)
    {
        string q = (filter ?? string.Empty).Trim();
        FontList.Items.Clear();

        int shown = 0;
        foreach (FontItem f in _all)
        {
            if (q.Length > 0 && f.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            FontList.Items.Add(f);
            shown++;
        }

        CountText.Text = _all.Count == 0
            ? "No installed fonts found — use Browse… on the main window."
            : (q.Length > 0 ? $"{shown} of {_all.Count} fonts" : $"{_all.Count} fonts");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Populate(SearchBox.Text);

    private void UseButton_Click(object sender, RoutedEventArgs e) => Commit();

    private void FontList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void Commit()
    {
        if (FontList.SelectedItem is FontItem f)
        {
            SelectedPath = f.Path;
            DialogResult = true;
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
