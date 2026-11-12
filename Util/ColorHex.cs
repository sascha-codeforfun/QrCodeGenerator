using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace QrCodeGenerator.Util;

/// <summary>Parsing and formatting of #RRGGBB colors used throughout the app.</summary>
internal static class ColorHex
{
    private static readonly Regex HexPattern = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>Returns "#RRGGBB" (upper case), adding a missing '#'. Throws on anything else.</summary>
    public static string Normalize(string? hex)
    {
        hex = (hex ?? string.Empty).Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (!HexPattern.IsMatch(hex))
            throw new InvalidOperationException($"'{hex}' is not a valid 6-digit hex color (e.g. #000000).");
        return hex.ToUpperInvariant();
    }

    /// <summary>Non-throwing parse; accepts the value with or without a leading '#'.</summary>
    public static bool TryParse(string? hex, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string h = hex.Trim();
        if (!h.StartsWith('#')) h = "#" + h;
        if (!HexPattern.IsMatch(h)) return false;
        color = FromDigits(h.Substring(1));
        return true;
    }

    /// <summary>Throwing parse (same rules as <see cref="Normalize"/>).</summary>
    public static Color Parse(string? hex) => FromDigits(Normalize(hex).Substring(1));

    /// <summary>RGBA bytes (alpha 255) as QRCoder's PNG renderer expects.</summary>
    public static byte[] ToRgba(string? hex)
    {
        Color c = Parse(hex);
        return new byte[] { c.R, c.G, c.B, 255 };
    }

    public static string Format(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color FromDigits(string rrggbb) => Color.FromRgb(
        byte.Parse(rrggbb.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(rrggbb.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(rrggbb.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
