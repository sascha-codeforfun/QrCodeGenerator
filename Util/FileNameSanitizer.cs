using System.Net;
using System.Text.RegularExpressions;

namespace QrCodeGenerator.Util;

/// <summary>Turns URLs into safe, readable file names for the batch ZIP.</summary>
internal static class FileNameSanitizer
{
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

    /// <summary>Returns <paramref name="baseName"/>, or baseName_2, _3 … if already used.</summary>
    public static string UniqueName(string baseName, HashSet<string> used)
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

    private static string RegistrableDomain(string host)
    {
        if (string.IsNullOrEmpty(host)) return host;

        // Leave IP literals untouched.
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;

        string[] labels = host.Split('.');
        if (labels.Length <= 2) return host;

        string lastTwo = labels[^2] + "." + labels[^1];
        if (TwoLevelTlds.Contains(lastTwo) && labels.Length >= 3)
            return labels[^3] + "." + lastTwo;

        return lastTwo;
    }
}
