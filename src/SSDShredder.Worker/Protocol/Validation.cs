namespace SSDShredder.Worker.Protocol;

/// <summary>Syntactic checks shared by the command line and the wire protocol.</summary>
public static class Validation
{
    public static bool IsLowerHex32(string? s)
    {
        if (s is null || s.Length != 32) return false;
        foreach (char c in s)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }

    /// <summary>Canonical lowercase 8-4-4-4-12 UUID.</summary>
    public static bool IsCanonicalUuid(string? s)
    {
        if (s is null || s.Length != 36) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-') return false;
            }
            else if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Absolute local drive-letter path to a file. Rejects UNC, device namespaces,
    /// alternate-stream syntax, relative segments and anything Windows would normalise.
    /// </summary>
    public static bool IsAbsoluteLocalFilePath(string? p)
    {
        if (p is null || p.Length < 4 || p.Length > 32767) return false;
        if (!char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\') return false;
        if (p[^1] == '\\') return false;
        for (int i = 2; i < p.Length; i++)
        {
            char c = p[i];
            if (c < 0x20 || c is ':' or '/' or '<' or '>' or '"' or '|' or '?' or '*') return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(p), p, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Canonical volume GUID path: \\?\Volume{lowercase-uuid}\</summary>
    public static bool IsVolumeGuidPath(string? s)
    {
        const string prefix = @"\\?\Volume{";
        if (s is null || s.Length != prefix.Length + 36 + 2) return false;
        if (!s.StartsWith(prefix, StringComparison.Ordinal) || !s.EndsWith(@"}\", StringComparison.Ordinal)) return false;
        return IsCanonicalUuid(s.Substring(prefix.Length, 36));
    }
}
