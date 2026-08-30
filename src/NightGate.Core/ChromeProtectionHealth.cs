using System.Text.Json.Serialization;

namespace NightGate.Core;

public sealed record ChromeProtectionHealth
{
    public const string ExpectedExtensionId = "eefgemhlhbdodhlgjmicnoifhclhdgmm";
    public const string MinimumCompatibleExtensionVersion = "0.1.4";
    public const int MaximumExtensionVersionLength = 32;

    public ChromeProtectionHealth(
        string ExtensionId,
        string ExtensionVersion,
        string ProfileTokenSha256,
        long PolicyRevision,
        bool IncognitoAllowed,
        DateTimeOffset ObservedAtUtc,
        bool ProtectionReady = false)
    {
        if (!IsChromeExtensionId(ExtensionId))
        {
            throw new ArgumentException(
                "The Chrome extension ID must contain exactly 32 lower-case a-p characters.",
                nameof(ExtensionId));
        }

        if (!IsManifestVersion(ExtensionVersion))
        {
            throw new ArgumentException(
                "The extension version must contain one to four bounded numeric components.",
                nameof(ExtensionVersion));
        }

        if (!IsLowerHexSha256(ProfileTokenSha256))
        {
            throw new ArgumentException(
                "The Chrome profile token hash must be a lower-case SHA-256 value.",
                nameof(ProfileTokenSha256));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(PolicyRevision);
        if (ObservedAtUtc == default || ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The health observation time must be a non-default UTC value.",
                nameof(ObservedAtUtc));
        }

        this.ExtensionId = ExtensionId;
        this.ExtensionVersion = ExtensionVersion;
        this.ProfileTokenSha256 = ProfileTokenSha256;
        this.PolicyRevision = PolicyRevision;
        this.IncognitoAllowed = IncognitoAllowed;
        this.ObservedAtUtc = ObservedAtUtc;
        this.ProtectionReady = ProtectionReady;
    }

    public string ExtensionId { get; }

    public string ExtensionVersion { get; }

    public string ProfileTokenSha256 { get; }

    public long PolicyRevision { get; }

    public bool IncognitoAllowed { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public bool ProtectionReady { get; }

    [JsonIgnore]
    public bool IsExpectedExtension =>
        string.Equals(ExtensionId, ExpectedExtensionId, StringComparison.Ordinal)
        && CompareManifestVersions(
            ExtensionVersion,
            MinimumCompatibleExtensionVersion) >= 0;

    public bool IsFreshAt(DateTimeOffset nowUtc, TimeSpan maximumAge)
    {
        if (nowUtc == default || nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The health evaluation time must be a non-default UTC value.",
                nameof(nowUtc));
        }

        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        return nowUtc >= ObservedAtUtc && nowUtc - ObservedAtUtc <= maximumAge;
    }

    private static bool IsChromeExtensionId(string? value) =>
        value is { Length: 32 }
        && value.All(character => character is >= 'a' and <= 'p');

    private static bool IsManifestVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumExtensionVersionLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        string[] components = value.Split('.', StringSplitOptions.None);
        if (components.Length is < 1 or > 4)
        {
            return false;
        }

        return components.All(component =>
            component.Length > 0
            && component.All(char.IsAsciiDigit)
            && ushort.TryParse(component, out _));
    }

    private static int CompareManifestVersions(string left, string right)
    {
        string[] leftParts = left.Split('.');
        string[] rightParts = right.Split('.');
        for (int index = 0; index < 4; index++)
        {
            int leftPart = index < leftParts.Length ? ushort.Parse(leftParts[index]) : 0;
            int rightPart = index < rightParts.Length ? ushort.Parse(rightParts[index]) : 0;
            int comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return 0;
    }

    private static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
