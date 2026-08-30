using System.Diagnostics;
using NightGate.Core;

namespace NightGate.Service;

public readonly record struct CurrentProcessWitness(
    int ProcessId,
    long CreationUtcTicks,
    string ExecutablePath,
    int SessionId);

public interface ICurrentProcessWitnessProvider
{
    bool TryRead(int processId, out CurrentProcessWitness witness);
}

public sealed class SystemCurrentProcessWitnessProvider :
    ICurrentProcessWitnessProvider
{
    public bool TryRead(int processId, out CurrentProcessWitness witness)
    {
        witness = default;
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited
                || process.MainModule?.FileName is not { } executablePath
                || !Win32ExecutablePathCanonicalizer.TryCanonicalize(
                    executablePath,
                    out string canonicalPath))
            {
                return false;
            }

            DateTimeOffset createdAtUtc = new DateTimeOffset(
                    process.StartTime)
                .ToUniversalTime();
            int sessionId = process.SessionId;
            if (process.HasExited)
            {
                return false;
            }

            witness = new(
                process.Id,
                createdAtUtc.UtcTicks,
                canonicalPath,
                sessionId);
            return true;
        }
        catch (Exception)
        {
            // Process exit, access denial, and incomplete identity reads all
            // fail closed for the optional Team Rescue grant.
            return false;
        }
    }
}

internal sealed class UnavailableCurrentProcessWitnessProvider :
    ICurrentProcessWitnessProvider
{
    public static UnavailableCurrentProcessWitnessProvider Instance { get; } =
        new();

    private UnavailableCurrentProcessWitnessProvider()
    {
    }

    public bool TryRead(int processId, out CurrentProcessWitness witness)
    {
        witness = default;
        return false;
    }
}
