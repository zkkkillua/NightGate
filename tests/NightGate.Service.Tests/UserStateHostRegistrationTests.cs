using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class UserStateHostRegistrationTests
{
    [Fact]
    public void Host_RegistersSingleSqliteRepositoryForEveryUserStateInterface()
    {
        using IHost host = NightGateHost.Create(
            ["--NightGate:ConfiguredWindowsUserSid=S-1-5-21-1-2-3-1001"],
            new FixedSidResolver());

        SqliteNightGateRepository concrete = host.Services
            .GetRequiredService<SqliteNightGateRepository>();

        Assert.Same(concrete, host.Services.GetRequiredService<IOnboardingRepository>());
        Assert.Same(concrete, host.Services.GetRequiredService<IRuleSettingsRepository>());
        Assert.Same(concrete, host.Services.GetRequiredService<INightSelfReportRepository>());
        Assert.Same(concrete, host.Services.GetRequiredService<INoticeClaimRepository>());
        Assert.IsType<NightGateProtocolCommandHandler>(
            host.Services.GetRequiredService<IProtocolCommandHandler>());
        Assert.IsType<PolicyMaintenanceIteration>(
            host.Services.GetRequiredService<IPolicyMaintenanceIteration>());
    }

    private sealed class FixedSidResolver : IWindowsSidResolver
    {
        public string ResolveAccountSid(string accountName) =>
            "S-1-5-21-1-2-3-1001";

        public string GetCurrentIdentitySid() => "S-1-5-19";
    }
}
