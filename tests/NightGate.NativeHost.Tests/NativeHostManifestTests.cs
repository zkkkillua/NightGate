using System.Text.Json;

namespace NightGate.NativeHost.Tests;

public sealed class NativeHostManifestTests
{
    [Fact]
    public void Manifest_BindsOnlyTheStableNightGateChromeExtension()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(
            root,
            "src",
            "NightGate.NativeHost",
            "com.nightgate.host.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement manifest = document.RootElement;

        Assert.Equal(5, manifest.EnumerateObject().Count());
        Assert.Equal("com.nightgate.host", manifest.GetProperty("name").GetString());
        Assert.Equal("stdio", manifest.GetProperty("type").GetString());
        Assert.Equal(
            "__NIGHTGATE_NATIVE_HOST_PATH__",
            manifest.GetProperty("path").GetString());
        JsonElement origin = Assert.Single(
            manifest.GetProperty("allowed_origins").EnumerateArray());
        Assert.Equal(
            "chrome-extension://eefgemhlhbdodhlgjmicnoifhclhdgmm/",
            origin.GetString());
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "NightGate.slnx")))
        {
            current = current.Parent;
        }
        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate NightGate.slnx.");
    }
}
