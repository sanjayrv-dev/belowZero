namespace SSDShredder.Core;

public static class PathPolicy
{
    /// <summary>v1: absolute local drive-letter path only. Rejects UNC, device namespaces and alternate-stream paths.</summary>
    public static bool IsAcceptable(string? path, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(path)) { reason = "empty path"; return false; }
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        { reason = "UNC or device namespace"; return false; }
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
        { reason = "not an absolute drive-letter path"; return false; }
        if (path.IndexOf(':', 2) >= 0) { reason = "alternate data stream syntax"; return false; }
        if (path.Contains('\0')) { reason = "NUL in path"; return false; }
        return true;
    }
}
