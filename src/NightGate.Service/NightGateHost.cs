using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Principal;
using NightGate.Core;

namespace NightGate.Service;

public static class NightGateHost
{
    public const string WindowsServiceName = "NightGate.LocalService";
    public const string PipeName = "NightGateService";
    public const string ConfiguredWindowsUserSidConfigurationKey =
        "NightGate:ConfiguredWindowsUserSid";

    public static IHost Create(string[] args) => Create(args, new WindowsSidResolver());

    public static IHost Create(string[] args, IWindowsSidResolver sidResolver)
    {
        ArgumentNullException.ThrowIfNull(sidResolver);
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Services.Configure<WindowsServiceLifetimeOptions>(
            options => options.ServiceName = WindowsServiceName);
        builder.Services.AddWindowsService(options => options.ServiceName = WindowsServiceName);

        builder.Services.AddSingleton<IWindowsBootEventSource, WindowsEventLogBootEventSource>();
        builder.Services.AddSingleton<IBootSessionIdProvider, WindowsBootSessionIdProvider>();
        builder.Services.AddSingleton<ISystemUptimeSource, EnvironmentSystemUptimeSource>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<ITimeZoneProvider, SystemTimeZoneProvider>();
        builder.Services.AddSingleton<IConfiguredRuleProvider>(
            _ => new ConfigurationConfiguredRuleProvider(builder.Configuration));
        builder.Services.AddSingleton<IConfiguredSiteRuleProvider>(
            _ => new ConfigurationConfiguredSiteRuleProvider(builder.Configuration));
        builder.Services.AddSingleton<IAllowedProcessSnapshotProvider>(
            services => services.GetRequiredService<PersistedActiveRuleSnapshot>());
        builder.Services.AddSingleton<IActiveRuleSnapshotPublisher>(
            services => services.GetRequiredService<PersistedActiveRuleSnapshot>());
        builder.Services.AddSingleton<IActiveProcessSnapshotPublisher>(
            services => services.GetRequiredService<PersistedActiveRuleSnapshot>());
        builder.Services.AddSingleton<OverridePolicy>();
        builder.Services.AddSingleton<NightMutationGate>();
        builder.Services.AddSingleton<INightMutationGate>(
            services => services.GetRequiredService<NightMutationGate>());
        builder.Services.AddSingleton(
            _ => new SqliteNightGateRepository(SqliteNightGateRepository.GetProductionDatabasePath()));
        builder.Services.AddSingleton<INightStateRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<IProgressRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<IHistoryRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<IBrowserEventRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<IProcessPersistenceRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<IOnboardingRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<IChromeProtectionHealthRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<IRuleSettingsRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<INightSelfReportRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<INoticeClaimRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());
        builder.Services.AddSingleton<ILegacyTaskMigrationRepository>(
            services => services.GetRequiredService<SqliteNightGateRepository>());

        builder.Services.AddSingleton<InMemoryServiceStatus>();
        builder.Services.AddSingleton<DesktopSessionLease>();
        builder.Services.AddSingleton<IServiceStatusReader>(
            services => services.GetRequiredService<InMemoryServiceStatus>());
        builder.Services.AddSingleton<IServiceStatusPublisher>(
            services => services.GetRequiredService<InMemoryServiceStatus>());
        builder.Services.AddSingleton<IProtocolCommandHandler, NightGateProtocolCommandHandler>();
        builder.Services.AddSingleton<JsonProtocolCodec>();
        builder.Services.AddSingleton<ServiceCommandDispatcher>();

        string configuredUserSid = RequireCanonicalSid(
            builder.Configuration[ConfiguredWindowsUserSidConfigurationKey],
            "The NightGate:ConfiguredWindowsUserSid setting is required and must be a canonical Windows SID.");
        string currentServiceSid = RequireCanonicalSid(
            sidResolver.GetCurrentIdentitySid(),
            "The NightGate service identity did not provide a canonical Windows SID.");
        if (string.Equals(configuredUserSid, currentServiceSid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "NightGate:ConfiguredWindowsUserSid must identify the interactive desktop user, not the service identity.");
        }

        builder.Services.AddSingleton<ICurrentProcessWitnessProvider,
            SystemCurrentProcessWitnessProvider>();
        builder.Services.AddSingleton(services => new PersistedActiveRuleSnapshot(
            services.GetRequiredService<IClock>(),
            configuredUserSid,
            services.GetRequiredService<ICurrentProcessWitnessProvider>()));
        builder.Services.AddSingleton(sidResolver);
        builder.Services.AddSingleton<IPipePeerIdentityProvider, WindowsPipePeerIdentityProvider>();
        builder.Services.AddSingleton<IPipePeerAuthorizer>(
            _ => new ConfiguredPipePeerAuthorizer(configuredUserSid, currentServiceSid));
        builder.Services.AddSingleton<NamedPipeServerAdapter>();
        builder.Services.AddSingleton<INamedPipeServerFactory>(
            _ => new SystemNamedPipeServerFactory(PipeName, configuredUserSid));
        builder.Services.AddSingleton<IServiceLoopIteration, NamedPipeServiceIteration>();
        builder.Services.AddSingleton<IServiceLoopDelay, FixedServiceLoopDelay>();
        builder.Services.AddSingleton<IPolicyMaintenanceIteration, PolicyMaintenanceIteration>();
        builder.Services.AddSingleton<IPolicyMaintenanceScheduler, PolicyMaintenanceScheduler>();
        builder.Services.AddSingleton<IPolicyMaintenanceDelay, BoundaryAwarePolicyMaintenanceDelay>();
        builder.Services.AddHostedService<NightGateWorker>();
        builder.Services.AddHostedService<NightPolicyWorker>();

        return builder.Build();
    }

    private static string RequireCanonicalSid(string? value, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(errorMessage);
        }

        try
        {
            string canonical = new SecurityIdentifier(value).Value;
            if (!string.Equals(value, canonical, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(errorMessage);
            }

            return canonical;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(errorMessage, exception);
        }
    }
}
