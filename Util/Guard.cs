using System.IO;

namespace QrCodeGenerator.Util;

internal static class Guard
{
    /// <summary>Throws a user-facing message if a previously chosen file has gone missing.</summary>
    public static void RequireFile(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException(
                $"The selected {what} no longer exists — it may have been moved, renamed, or deleted. " +
                "Pick it again with Browse….");
    }
}
