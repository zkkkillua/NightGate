namespace NightGate.NativeHost;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            using Stream input = Console.OpenStandardInput();
            using Stream output = Console.OpenStandardOutput();
            ServicePipeNativeHostBackend backend = new(
                new NamedPipeServiceExchange(),
                new NativeHostServiceRequestIdSource(),
                new NativeHostClock());
            NativeHostExitCode result = await NativeHostSession
                .RunAsync(input, output, backend)
                .ConfigureAwait(false);
            return (int)result;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return (int)NativeHostExitCode.BackendUnavailable;
        }
    }

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException;
}
