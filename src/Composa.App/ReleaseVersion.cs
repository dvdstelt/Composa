using System.Globalization;

namespace Composa.App;

/// <summary>
/// A version as it appears in a release tag, ordered the way Semantic Versioning says. Only what is
/// needed to answer "is that one newer than this one": build metadata is ignored, because two
/// builds of the same version are the same version.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string PreRelease) : IComparable<ReleaseVersion>
{
    public bool IsPreRelease => PreRelease.Length > 0;

    /// <summary>Parses "1.2.3", "v1.2.3", "1.2.3-beta.1" or "1.2.3-beta.1+sha". Returns false for anything else.</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V')) span = span[1..];

        var plus = span.IndexOf('+'); // Build metadata is not part of ordering.
        if (plus >= 0) span = span[..plus];

        var dash = span.IndexOf('-');
        var pre = dash >= 0 ? span[(dash + 1)..] : "";
        var core = dash >= 0 ? span[..dash] : span;

        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;
        if (dash >= 0 && pre.Length == 0) return false;

        version = new ReleaseVersion(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        // A pre-release comes before the release it leads up to: 1.0.0-beta is older than 1.0.0.
        if (!IsPreRelease && !other.IsPreRelease) return 0;
        if (!IsPreRelease) return 1;
        if (!other.IsPreRelease) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// Dot-separated identifiers, compared left to right. A numeric identifier orders below an
    /// alphanumeric one, and running out of identifiers first means the lower version, so
    /// alpha.1 precedes alpha.1.1.
    /// </summary>
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
            if (aNumeric && bNumeric) { if (an != bn) return an.CompareTo(bn); continue; }
            if (aNumeric != bNumeric) return aNumeric ? -1 : 1;
            var text = string.CompareOrdinal(a[i], b[i]);
            if (text != 0) return text < 0 ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}
