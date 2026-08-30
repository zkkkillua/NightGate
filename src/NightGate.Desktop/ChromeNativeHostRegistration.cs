using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace NightGate.Desktop;

internal interface ICurrentUserChromeNativeHostRegistry
{
    string? ReadManifestPath(RegistryView view);

    void WriteManifestPath(RegistryView view, string manifestPath);
}

internal sealed class WindowsCurrentUserChromeNativeHostRegistry :
    ICurrentUserChromeNativeHostRegistry
{
    public string? ReadManifestPath(RegistryView view)
    {
        using RegistryKey currentUser = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser,
            view);
        using RegistryKey? key = currentUser.OpenSubKey(
            ChromeNativeHostRegistration.RegistrySubKey,
            writable: false);
        return key?.GetValue(
            string.Empty,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void WriteManifestPath(RegistryView view, string manifestPath)
    {
        using RegistryKey currentUser = RegistryKey.OpenBaseKey(
            RegistryHive.CurrentUser,
            view);
        using RegistryKey? key = currentUser.CreateSubKey(
            ChromeNativeHostRegistration.RegistrySubKey,
            writable: true);
        if (key is null)
        {
            throw new UnauthorizedAccessException(
                "The current user's Chrome native-host key is unavailable.");
        }

        key.SetValue(
            string.Empty,
            manifestPath,
            RegistryValueKind.String);
    }
}

internal sealed class ChromeNativeHostRegistration
{
    internal const string HostName = "com.nightgate.host";
    internal const string ExtensionOrigin =
        "chrome-extension://eefgemhlhbdodhlgjmicnoifhclhdgmm/";
    internal const string RegistrySubKey =
        "Software\\Google\\Chrome\\NativeMessagingHosts\\com.nightgate.host";

    private readonly string _desktopDirectory;
    private readonly ICurrentUserChromeNativeHostRegistry _registry;

    public ChromeNativeHostRegistration(
        string desktopDirectory,
        ICurrentUserChromeNativeHostRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopDirectory);
        ArgumentNullException.ThrowIfNull(registry);
        _desktopDirectory = desktopDirectory;
        _registry = registry;
    }

    public bool TryEnsureRegistered()
    {
        try
        {
            if (!TryResolveValidatedPayload(out string? manifestPath))
            {
                return false;
            }

            bool allViewsRegistered = true;
            foreach (RegistryView view in new[]
            {
                RegistryView.Registry32,
                RegistryView.Registry64,
            })
            {
                try
                {
                    string? currentPath = _registry.ReadManifestPath(view);
                    if (!PathsEqual(currentPath, manifestPath))
                    {
                        _registry.WriteManifestPath(view, manifestPath);
                    }

                    allViewsRegistered &= PathsEqual(
                        _registry.ReadManifestPath(view),
                        manifestPath);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException
                    and not StackOverflowException
                    and not AccessViolationException)
                {
                    allViewsRegistered = false;
                }
            }

            return allViewsRegistered;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // Chrome integration is optional. Registration failures must never
            // block the tray, service connection, or workstation fail-open path.
            return false;
        }
    }

    private bool TryResolveValidatedPayload(out string manifestPath)
    {
        manifestPath = string.Empty;
        DirectoryInfo desktop = new(Path.GetFullPath(_desktopDirectory));
        DirectoryInfo? apps = desktop.Parent;
        DirectoryInfo? install = apps?.Parent;
        if (apps is null
            || install is null
            || !desktop.Name.Equals("Desktop", StringComparison.OrdinalIgnoreCase)
            || !apps.Name.Equals("apps", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string expectedHostPath = Path.GetFullPath(Path.Combine(
            install.FullName,
            "apps",
            "NativeHost",
            "NightGate.NativeHost.exe"));
        manifestPath = Path.GetFullPath(Path.Combine(
            install.FullName,
            "native-host",
            "com.nightgate.host.json"));
        if (!File.Exists(expectedHostPath) || !File.Exists(manifestPath))
        {
            return false;
        }

        using FileStream stream = File.OpenRead(manifestPath);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (!TryReadString(root, "name", out string? name)
            || !string.Equals(name, HostName, StringComparison.Ordinal)
            || !TryReadString(root, "type", out string? type)
            || !string.Equals(type, "stdio", StringComparison.Ordinal)
            || !TryReadString(root, "path", out string? configuredHostPath)
            || !PathsEqual(configuredHostPath, expectedHostPath)
            || !root.TryGetProperty("allowed_origins", out JsonElement origins)
            || origins.ValueKind != JsonValueKind.Array
            || !origins.EnumerateArray().Any(origin =>
                origin.ValueKind == JsonValueKind.String
                && string.Equals(
                    origin.GetString(),
                    ExtensionOrigin,
                    StringComparison.Ordinal)))
        {
            manifestPath = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryReadString(
        JsonElement source,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!source.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
