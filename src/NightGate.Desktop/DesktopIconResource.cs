using System.Windows;
using System.Windows.Resources;

namespace NightGate.Desktop;

internal static class DesktopIconResource
{
    internal const string PackUri =
        "pack://application:,,,/Assets/NightGate.ico";

    internal static System.Drawing.Icon CreateTrayIcon()
    {
        StreamResourceInfo resource = System.Windows.Application.GetResourceStream(
                new Uri(PackUri, UriKind.Absolute))
            ?? throw new InvalidOperationException(
                "NightGate icon resource is missing.");
        using System.IO.Stream stream = resource.Stream;
        using System.Drawing.Icon source = new(stream);
        return (System.Drawing.Icon)source.Clone();
    }
}
