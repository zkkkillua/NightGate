using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace NightGate.Core;

public enum AppRuleCategory
{
    Game,
    Voice,
}

public sealed record AppRule
{
    public const int MaximumIdLength = 128;
    public const int MaximumHelperExecutablePaths = 32;
    public const int MaximumExecutablePathLength = 1024;

    public AppRule(string id, int sessionMinutes = 35)
    {
        Id = NormalizeId(id);
        SessionMinutes = ValidateSessionMinutes(sessionMinutes);
        HelperExecutablePaths = [];
    }

    public AppRule(
        string id,
        string rootExecutablePath,
        IEnumerable<string> helperExecutablePaths,
        AppRuleCategory category,
        int sessionMinutes = 35)
    {
        Id = NormalizeId(id);
        SessionMinutes = ValidateSessionMinutes(sessionMinutes);
        if (category is not AppRuleCategory.Game and not AppRuleCategory.Voice)
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        string canonicalRoot = CanonicalizeExecutablePath(
            rootExecutablePath,
            nameof(rootExecutablePath));
        ArgumentNullException.ThrowIfNull(helperExecutablePaths);
        var canonicalHelpers = ImmutableArray.CreateBuilder<string>();
        HashSet<string> seenHelpers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string helperExecutablePath in helperExecutablePaths)
        {
            if (canonicalHelpers.Count >= MaximumHelperExecutablePaths)
            {
                throw new ArgumentException(
                    $"At most {MaximumHelperExecutablePaths} helper executable paths are allowed.",
                    nameof(helperExecutablePaths));
            }

            string canonicalHelper;
            try
            {
                canonicalHelper = CanonicalizeExecutablePath(
                    helperExecutablePath,
                    nameof(helperExecutablePaths));
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    "Every helper executable path must be an absolute .exe path.",
                    nameof(helperExecutablePaths),
                    exception);
            }

            if (string.Equals(canonicalRoot, canonicalHelper, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "A helper executable path cannot equal the root executable path.",
                    nameof(helperExecutablePaths));
            }

            if (!seenHelpers.Add(canonicalHelper))
            {
                throw new ArgumentException(
                    "Helper executable paths must be unique after canonicalization.",
                    nameof(helperExecutablePaths));
            }

            canonicalHelpers.Add(canonicalHelper);
        }

        RootExecutablePath = canonicalRoot;
        HelperExecutablePaths = canonicalHelpers.ToImmutable();
        Category = category;
    }

    [JsonConstructor]
    private AppRule(
        string id,
        string? rootExecutablePath,
        ImmutableArray<string> helperExecutablePaths,
        AppRuleCategory? category,
        int sessionMinutes)
        : this(
            id,
            rootExecutablePath
                ?? throw new ArgumentException("A persisted app rule requires a root path."),
            helperExecutablePaths.IsDefault
                ? throw new ArgumentException("Persisted helper paths must be initialized.")
                : (IEnumerable<string>)helperExecutablePaths,
            category
                ?? throw new ArgumentException("A persisted app rule requires a category."),
            sessionMinutes)
    {
    }

    public string Id { get; }

    public string? RootExecutablePath { get; }

    public ImmutableArray<string> HelperExecutablePaths { get; }

    public AppRuleCategory? Category { get; }

    public int SessionMinutes { get; }

    public bool IsConfigured => RootExecutablePath is not null && Category is not null;

    private static string NormalizeId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string normalized = id.Trim();
        if (normalized.Length > MaximumIdLength)
        {
            throw new ArgumentException(
                $"Rule IDs cannot exceed {MaximumIdLength} characters.",
                nameof(id));
        }

        return normalized;
    }

    private static int ValidateSessionMinutes(int sessionMinutes)
    {
        if (sessionMinutes is < 15 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionMinutes));
        }

        return sessionMinutes;
    }

    private static string CanonicalizeExecutablePath(string path, string parameterName)
    {
        if (!Win32ExecutablePathCanonicalizer.TryCanonicalize(path, out string canonicalPath))
        {
            throw new ArgumentException(
                "Executable path must be an unambiguous absolute Win32 .exe path.",
                parameterName);
        }

        if (canonicalPath.Length > MaximumExecutablePathLength)
        {
            throw new ArgumentException(
                $"Executable paths cannot exceed {MaximumExecutablePathLength} characters.",
                parameterName);
        }

        return canonicalPath;
    }
}
