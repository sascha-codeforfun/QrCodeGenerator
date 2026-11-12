using System.IO;
using System.Windows;
using System.Windows.Media;
using QrCodeGenerator.Util;

namespace QrCodeGenerator.Rendering;

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

/// <summary>Loads TTF/OTF/TTC fonts and extracts vector outlines for the center glyph.</summary>
internal static class GlyphFonts
{
    /// <summary>
    /// Loads a font's glyph typeface robustly and keeps any temp copy alive until disposed.
    /// Tries the original path first; if WPF's resolver trips (e.g. a font at the root of a
    /// secondary drive), copies the file to the system-drive temp folder and loads from there.
    /// </summary>
    public static LoadedFont LoadRobust(string fontPath)
    {
        Guard.RequireFile(fontPath, "font file");

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

    /// <summary>
    /// Extracts a single PathGeometry for the given character(s) from a font file,
    /// laid out left-to-right using the font's advance widths.
    /// </summary>
    public static PathGeometry BuildGeometry(string fontPath, string text)
    {
        using LoadedFont lf = LoadRobust(fontPath);
        return BuildGeometry(lf.Typeface, text);
    }

    /// <summary>Extracts the combined outline for the given characters from a loaded typeface.</summary>
    public static PathGeometry BuildGeometry(GlyphTypeface gtf, string text)
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
}
