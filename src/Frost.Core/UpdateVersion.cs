using System.Globalization;

namespace Frost.Core;

public static class UpdateVersion
{
    /// <summary>Parses a release tag "v1.2.3" / "1.2.3"; anything else (e.g. "v1.2", "1.2.3-beta") is null.</summary>
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        var p = (tag[0] is 'v' or 'V' ? tag[1..] : tag).Split('.');
        return p.Length == 3
            && int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var c)
            ? new Version(a, b, c) : null;
    }

    /// <summary>True if the tag is a newer MAJOR.MINOR.PATCH than <paramref name="current"/> (its revision is ignored).</summary>
    public static bool IsNewer(string? tag, Version current) =>
        Parse(tag) is { } v && v > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>The hex SHA-256 from a GitHub asset digest ("sha256:&lt;64 hex&gt;"); null if missing or malformed.</summary>
    public static string? ParseSha256Digest(string? digest)
    {
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[7..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }
}
