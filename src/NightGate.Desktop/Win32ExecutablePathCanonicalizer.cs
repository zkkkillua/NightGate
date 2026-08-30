namespace NightGate.Desktop;

internal static class Win32ExecutablePathCanonicalizer
{
    internal const int MaximumCanonicalPathCharacters =
        NightGate.Core.Win32ExecutablePathCanonicalizer.MaximumCanonicalPathCharacters;
    internal const int MaximumQueryBufferCharacters =
        NightGate.Core.Win32ExecutablePathCanonicalizer.MaximumQueryBufferCharacters;

    public static bool TryCanonicalize(string? candidate, out string canonicalPath) =>
        NightGate.Core.Win32ExecutablePathCanonicalizer.TryCanonicalize(
            candidate,
            out canonicalPath);
}
