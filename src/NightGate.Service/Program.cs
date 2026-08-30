using Microsoft.Extensions.Hosting;

namespace NightGate.Service;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using IHost host = NightGateHost.Create(args);
        await host.RunAsync().ConfigureAwait(false);
    }
}
