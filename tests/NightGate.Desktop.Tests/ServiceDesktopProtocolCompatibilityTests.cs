using System.Collections.Immutable;
using System.Text.Json;
using NightGate.Core;
using NightGate.Desktop;
using NightGate.Service;

namespace NightGate.Desktop.Tests;

public sealed class ServiceDesktopProtocolCompatibilityTests
{
    [Fact]
    public void CurrentServicePolicySnapshotStrictlyDeserializesInDesktop()
    {
        DateTimeOffset evaluatedAt = new(2026, 7, 19, 9, 0, 0, TimeSpan.Zero);
        NightWindow window = new(
            new DateOnly(2026, 7, 19),
            evaluatedAt.AddHours(-1),
            evaluatedAt.AddMinutes(30),
            evaluatedAt.AddMinutes(65),
            evaluatedAt.AddMinutes(85),
            evaluatedAt.AddHours(9));
        ServiceRuntimeStatus serviceStatus = new(
            true,
            false,
            null,
            new PolicySnapshot(
                evaluatedAt,
                NightPhase.Free,
                window,
                ImmutableArray<AppRule>.Empty,
                ImmutableArray<SiteRule>.Empty)
            {
                Revision = 42,
            });
        string serviceJson = ProtocolCommandResult
            .Success(serviceStatus)
            .Payload
            .GetRawText();

        DesktopServiceRuntimeStatusDto? desktopStatus =
            JsonSerializer.Deserialize<DesktopServiceRuntimeStatusDto>(
                serviceJson,
                DesktopJson.Options);

        Assert.NotNull(desktopStatus?.Policy);
        Assert.Equal(42, desktopStatus.Policy.Revision);
        Assert.Equal(evaluatedAt, desktopStatus.Policy.EvaluatedAt);
    }
}
