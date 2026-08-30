using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class Win32ExecutablePathCanonicalizerTests
{
    [Theory]
    [InlineData(@"C:\Games\.\game.exe", @"C:\Games\game.exe")]
    [InlineData(@"\\server\share\folder\..\game.exe", @"\\server\share\game.exe")]
    [InlineData(@"\\?\C:\Games\game.exe", @"C:\Games\game.exe")]
    [InlineData(@"\\?\UNC\server\share\game.exe", @"\\server\share\game.exe")]
    [InlineData(@"C:\GLOBALROOT\game.exe", @"C:\GLOBALROOT\game.exe")]
    [InlineData(@"C:\Games\GLOBALROOT\game.exe", @"C:\Games\GLOBALROOT\game.exe")]
    [InlineData(@"\\server\share\GLOBALROOT\game.exe", @"\\server\share\GLOBALROOT\game.exe")]
    [InlineData("C:\\Games\\\U0001F3AE.exe", "C:\\Games\\\U0001F3AE.exe")]
    public void Canonicalize_NormalizesSupportedWin32DriveAndUncPaths(
        string value,
        string expected)
    {
        Assert.True(Win32ExecutablePathCanonicalizer.TryCanonicalize(value, out string actual));
        Assert.Equal(expected, actual, ignoreCase: true);
    }

    [Theory]
    [InlineData(@"game.exe")]
    [InlineData(@"\rooted-but-no-drive.exe")]
    [InlineData(@"\\.\C:\Games\game.exe")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\game.exe")]
    [InlineData(@"\??\C:\Games\game.exe")]
    [InlineData(@"\\server.exe")]
    [InlineData(@"\\server\file.exe")]
    [InlineData(@"\\server\\share\game.exe")]
    [InlineData(@"\\server\share\folder.\game.exe")]
    [InlineData(@"\\server\share\folder \game.exe")]
    [InlineData(@"C:\Games\folder.\game.exe")]
    [InlineData(@"C:\Games\folder \game.exe")]
    [InlineData(@"C:\\Games\game.exe")]
    [InlineData(@"C:\Games\CON.exe")]
    [InlineData(@"C:\Games\LPT1.txt\game.exe")]
    [InlineData(@"C:\Games\COM¹.exe")]
    [InlineData(@"C:\Games\COM².txt\game.exe")]
    [InlineData(@"C:\Games\COM³ .txt\game.exe")]
    [InlineData(@"C:\Games\LPT¹.exe")]
    [InlineData(@"C:\Games\LPT².txt\game.exe")]
    [InlineData(@"C:\Games\LPT³ .txt\game.exe")]
    [InlineData(@"C:\Games\NUL  .exe")]
    [InlineData(@"C:\Games\COM1 .txt\game.exe")]
    [InlineData(@"C:\Games\game.exe:stream")]
    [InlineData(@"C:\Games\ga?me.exe")]
    [InlineData(@"C:\Games\game.dll")]
    [InlineData(@"C:\Games\game.exe.")]
    [InlineData(@"C:\Games\game.exe ")]
    [InlineData(@"C:\Games\NUL\..\game.exe")]
    [InlineData(@"C:\Games\bad:stream\..\game.exe")]
    [InlineData(@"C:\Games\folder.\..\game.exe")]
    public void Canonicalize_RejectsRelativeDeviceMalformedAndNonExecutablePaths(string value)
    {
        Assert.False(Win32ExecutablePathCanonicalizer.TryCanonicalize(value, out _));
    }

    [Fact]
    public void Canonicalize_RejectsNulControlAndIllFormedUtf16WithoutTheorySerialization()
    {
        string[] values =
        [
            "C:\\Games\\bad" + '\0' + ".exe",
            "C:\\Games\\bad" + '\u0001' + ".exe",
            "C:\\Games\\bad" + '\u001f' + ".exe",
            "C:\\Games\\bad" + '\ud800' + ".exe",
            "C:\\Games\\bad" + '\udc00' + ".exe",
        ];

        Assert.All(
            values,
            value => Assert.False(
                Win32ExecutablePathCanonicalizer.TryCanonicalize(value, out _)));
    }

    [Fact]
    public void Canonicalize_AcceptsTheLargestCanonicalPathAndEquivalentPrefixes()
    {
        string drive = DrivePathWithLength(
            Win32ExecutablePathCanonicalizer.MaximumCanonicalPathCharacters);
        string unc = UncPathWithLength(
            Win32ExecutablePathCanonicalizer.MaximumCanonicalPathCharacters);

        Assert.True(Win32ExecutablePathCanonicalizer.TryCanonicalize(
            drive,
            out string canonicalDrive));
        Assert.True(Win32ExecutablePathCanonicalizer.TryCanonicalize(
            @"\\?\" + drive,
            out string prefixedDrive));
        Assert.True(Win32ExecutablePathCanonicalizer.TryCanonicalize(
            unc,
            out string canonicalUnc));
        Assert.True(Win32ExecutablePathCanonicalizer.TryCanonicalize(
            @"\\?\UNC\" + unc[2..],
            out string prefixedUnc));
        Assert.Equal(canonicalDrive, prefixedDrive, ignoreCase: true);
        Assert.Equal(canonicalUnc, prefixedUnc, ignoreCase: true);
    }

    [Fact]
    public void Canonicalize_RejectsACanonicalPathPastTheWin32Utf16Limit()
    {
        string value = DrivePathWithLength(
            Win32ExecutablePathCanonicalizer.MaximumCanonicalPathCharacters + 1);

        Assert.False(Win32ExecutablePathCanonicalizer.TryCanonicalize(value, out _));
    }

    private static string DrivePathWithLength(int length) =>
        @"C:\" + new string('a', length - 7) + ".exe";

    private static string UncPathWithLength(int length)
    {
        const string prefix = @"\\server\share\";
        return prefix + new string('a', length - prefix.Length - 4) + ".exe";
    }
}
