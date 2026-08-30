using System.Diagnostics;

namespace NightGate.Desktop;

public sealed class TraceCommitmentCountdownDiagnostics :
    ICommitmentCountdownDiagnostics
{
    public void RecordVisualFailure(
        CommitmentCountdownVisualOperation operation,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Trace.TraceWarning(
            "NightGate countdown visual degradation: operation={0}; " +
            "exception={1}; hresult=0x{2:X8}",
            operation,
            exception.GetType().FullName,
            exception.HResult);
    }
}
