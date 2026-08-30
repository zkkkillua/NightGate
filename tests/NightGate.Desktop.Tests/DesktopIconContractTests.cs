using System.Buffers.Binary;

namespace NightGate.Desktop.Tests;

public sealed class DesktopIconContractTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    [Fact]
    public void ProductIcon_ContainsEightOrdered32BitAlphaPngFrames()
    {
        string path = Asset("NightGate.ico");
        Assert.True(File.Exists(path), "The generated product icon is missing.");

        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        ushort count = reader.ReadUInt16();
        Assert.Equal(ExpectedSizes.Length, count);

        List<IconEntry> entries = [];
        for (int index = 0; index < count; index++)
        {
            int width = reader.ReadByte();
            int height = reader.ReadByte();
            Assert.Equal(0, reader.ReadByte());
            Assert.Equal(0, reader.ReadByte());
            ushort planes = reader.ReadUInt16();
            ushort bitCount = reader.ReadUInt16();
            uint byteCount = reader.ReadUInt32();
            uint imageOffset = reader.ReadUInt32();
            entries.Add(new(
                width == 0 ? 256 : width,
                height == 0 ? 256 : height,
                planes,
                bitCount,
                byteCount,
                imageOffset));
        }

        Assert.Equal(ExpectedSizes, entries.Select(entry => entry.Width));
        Assert.Equal(ExpectedSizes, entries.Select(entry => entry.Height));

        byte[] pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        foreach (IconEntry entry in entries)
        {
            Assert.Equal((ushort)1, entry.Planes);
            Assert.Equal((ushort)32, entry.BitCount);
            Assert.True(entry.ByteCount >= 33);
            Assert.InRange(
                (long)entry.ImageOffset + entry.ByteCount,
                0,
                stream.Length);

            stream.Position = entry.ImageOffset;
            byte[] header = reader.ReadBytes(33);
            Assert.Equal(pngSignature, header[..8]);
            Assert.Equal(13u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)));
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(header, 12, 4));
            Assert.Equal(entry.Width, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
            Assert.Equal(entry.Height, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
            Assert.Equal(8, header[24]);
            Assert.Equal(6, header[25]);
        }
    }

    [Fact]
    public void DesktopProject_UsesOneIconForExecutableResourcesAndPublish()
    {
        string project = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "NightGate.Desktop.csproj"));
        string resource = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "DesktopIconResource.cs"));
        string window = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "MainWindow.xaml"));

        Assert.Contains("<ApplicationIcon>..\\..\\assets\\NightGate.ico</ApplicationIcon>", project);
        Assert.Contains("<AssemblyTitle>\u6536\u5c3e NightGate</AssemblyTitle>", project);
        Assert.Contains("<Product>\u6536\u5c3e NightGate</Product>", project);
        Assert.Contains("<Resource Include=\"..\\..\\assets\\NightGate.ico\"", project);
        Assert.Contains("Link=\"Assets\\NightGate.ico\"", project);
        Assert.Contains("<Content Include=\"..\\..\\assets\\NightGate.ico\"", project);
        Assert.Contains("Link=\"NightGate.ico\"", project);
        Assert.Contains("CopyToPublishDirectory=\"Always\"", project);
        Assert.Contains("pack://application:,,,/Assets/NightGate.ico", resource);
        Assert.Contains("CreateTrayIcon", resource);
        Assert.Contains("Title=\"\u6536\u5c3e NightGate\"", window);
        Assert.Contains("Icon=\"pack://application:,,,/Assets/NightGate.ico\"", window);
    }

    [Fact]
    public void TrayShell_OwnsAndDisposesTheProductIcon()
    {
        string source = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "TrayApplicationShell.cs"));

        Assert.DoesNotContain("SystemIcons.Application", source, StringComparison.Ordinal);
        Assert.Contains("DesktopIconResource.CreateTrayIcon()", source, StringComparison.Ordinal);
        Assert.Contains("Icon = _trayIcon", source, StringComparison.Ordinal);
        Assert.Contains("_trayIcon.Dispose();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_IsOfflineAndDefinesTheCanonicalFrameSet()
    {
        string script = File.ReadAllText(Repo("scripts", "New-NightGateIcon.ps1"));
        string svg = File.ReadAllText(Asset("NightGate.Icon.svg"));

        Assert.Contains("@(16, 20, 24, 32, 40, 48, 64, 256)", script);
        Assert.Contains("Format32bppArgb", script);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-RestMethod", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("viewBox=\"0 0 64 64\"", svg);
    }

    private static string Asset(string fileName) => Repo("assets", fileName);

    private static string Repo(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "NightGate.slnx")))
        {
            current = current.Parent;
        }

        return Path.Combine(
            current?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Could not locate NightGate.slnx."),
            Path.Combine(segments));
    }

    private sealed record IconEntry(
        int Width,
        int Height,
        ushort Planes,
        ushort BitCount,
        uint ByteCount,
        uint ImageOffset);
}
