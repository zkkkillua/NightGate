namespace NightGate.Core;

public static class Win32ExecutablePathCanonicalizer
{
    public const int MaximumCanonicalPathCharacters = 32_766;
    public const int MaximumQueryBufferCharacters = 32_767;
    private const int MaximumRawPathCharacters = MaximumCanonicalPathCharacters + 6;

    public static bool TryCanonicalize(string? candidate, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.IndexOf('\0') >= 0
            || candidate.Length > MaximumRawPathCharacters
            || !IsWellFormedUtf16(candidate))
        {
            return false;
        }

        string normalized = candidate.Replace('/', '\\');
        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = @"\\" + normalized[8..];
        }
        else if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            string drivePath = normalized[4..];
            if (!IsDrivePath(drivePath))
            {
                return false;
            }

            normalized = drivePath;
        }

        if (normalized.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || normalized.Length > MaximumCanonicalPathCharacters
            || !Path.IsPathFullyQualified(normalized)
            || !(IsDrivePath(normalized) || IsUncPath(normalized))
            || ContainsEmptySegment(normalized)
            || !HasValidSegments(normalized, allowDotSegments: true))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(normalized).Replace('/', '\\');
            if (fullPath.Length > MaximumCanonicalPathCharacters
                || !(IsDrivePath(fullPath) || IsUncPath(fullPath))
                || !HasValidSegments(fullPath, allowDotSegments: false)
                || !string.Equals(
                    Path.GetExtension(fullPath),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            canonicalPath = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDrivePath(string path) =>
        path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && path[2] == '\\';

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        && path.Length > 2
        && path[2] != '\\';

    private static bool ContainsEmptySegment(string path)
    {
        int rootLength = IsDrivePath(path) ? 3 : 2;
        return (path.Length > rootLength && path[rootLength] == '\\')
            || path.IndexOf("\\\\", rootLength, StringComparison.Ordinal) >= 0;
    }

    private static bool HasValidSegments(string path, bool allowDotSegments)
    {
        bool drivePath = IsDrivePath(path);
        string[] segments = drivePath
            ? path[3..].Split('\\')
            : path[2..].Split('\\');
        int minimumSegments = drivePath ? 1 : 3;
        if (segments.Length < minimumSegments)
        {
            return false;
        }

        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (allowDotSegments && segment is "." or "..")
            {
                if (!drivePath && index < 2)
                {
                    return false;
                }

                continue;
            }

            if (segment.Length == 0
                || segment[^1] is ' ' or '.'
                || segment.Any(static character => character < ' ')
                || segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0
                || IsReservedDosDeviceName(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReservedDosDeviceName(string segment)
    {
        string stem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }
}
