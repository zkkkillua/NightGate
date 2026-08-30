using System.Collections.Immutable;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class UserStateCommandTests
{
    private static readonly TimeZoneInfo ChinaTime = TimeZoneInfo.CreateCustomTimeZone(
        "NightGate-UserState-UTC+8",
        TimeSpan.FromHours(8),
        "NightGate User State UTC+8",
        "NightGate User State UTC+8");

    [Fact]
    public async Task GetUserState_ReturnsOnlyPersistedFactsForServiceLocalRollingPeriodAndLogicalNight()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero);
        DateOnly currentNight = new(2026, 7, 14);
        NightSelfReport selfReport = new(currentNight, true, null, now.AddHours(-1));
        UserStateRepository repository = new()
        {
            Progress = ProgressState.Initial with { CurrentStep = 2 },
            Onboarding = new(
                2,
                true,
                false,
                true,
                1,
                ChromeDegradedAcknowledged: true),
            Rules = RuleSettingsState.Initial,
            Outcomes =
            [
                Outcome(currentNight, qualifies: true, now.AddHours(-2)),
                Outcome(currentNight.AddDays(-7), qualifies: false, now.AddDays(-7)),
            ],
            SelfReport = selfReport,
            ChromeHealth = Health(now.AddSeconds(-30), incognitoAllowed: true),
        };
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(now));

        ProtocolCommandResult result = await handler.ExecuteAsync(new GetUserStateCommand());

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(2, result.Payload.GetProperty("progress").GetProperty("currentStep").GetInt32());
        Assert.Equal(2, result.Payload.GetProperty("onboarding").GetProperty("completedStep").GetInt32());
        Assert.True(result.Payload.GetProperty("onboarding")
            .GetProperty("chromeDegradedAcknowledged")
            .GetBoolean());
        Assert.Equal(0, result.Payload.GetProperty("rules").GetProperty("activeAppRules").GetArrayLength());
        Assert.Equal("2026-07-15", result.Payload.GetProperty("weeklyReport").GetProperty("periodEnd").GetString());
        Assert.Equal("2026-07-14", result.Payload.GetProperty("currentNightDate").GetString());
        Assert.Equal("2026-07-14", result.Payload.GetProperty("selfReport").GetProperty("nightDate").GetString());
        JsonElement chrome = result.Payload.GetProperty("chromeProtection");
        Assert.Equal("healthy", chrome.GetProperty("status").GetString());
        Assert.True(chrome.GetProperty("isHealthy").GetBoolean());
        Assert.True(chrome.GetProperty("incognitoProtected").GetBoolean());
        Assert.Equal(5, chrome.EnumerateObject().Count());
        Assert.False(chrome.TryGetProperty("profileTokenSha256", out _));
        Assert.False(chrome.TryGetProperty("extensionId", out _));
        Assert.Equal(7, result.Payload.EnumerateObject().Count());
        Assert.False(result.Payload.TryGetProperty("enforcementEnabled", out _));
        Assert.True(repository.LatestOutcomeCountRequested >= 21);
        Assert.Equal(currentNight, repository.SelfReportNightRequested);
    }

    [Theory]
    [InlineData(ChromeHealthCase.Missing, "missing")]
    [InlineData(ChromeHealthCase.Stale, "stale")]
    [InlineData(ChromeHealthCase.Future, "stale")]
    [InlineData(ChromeHealthCase.ExtensionMismatch, "extensionMismatch")]
    [InlineData(ChromeHealthCase.StorageDegraded, "degraded")]
    public async Task GetUserState_ReportsChromeDegradationWithoutExposingIdentity(
        ChromeHealthCase healthCase,
        string expectedStatus)
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            ChromeHealth = healthCase switch
            {
                ChromeHealthCase.Missing or ChromeHealthCase.StorageDegraded => null,
                ChromeHealthCase.Stale => Health(now.AddSeconds(-91), false),
                ChromeHealthCase.Future => Health(now.AddSeconds(1), false),
                ChromeHealthCase.ExtensionMismatch => Health(
                    now.AddSeconds(-1),
                    false,
                    new string('a', 32)),
                _ => throw new ArgumentOutOfRangeException(nameof(healthCase)),
            },
            ChromeHealthReadDegraded = healthCase == ChromeHealthCase.StorageDegraded,
        };

        ProtocolCommandResult result = await CreateHandler(repository, new MutableClock(now))
            .ExecuteAsync(new GetUserStateCommand());

        Assert.Equal(StorageMode.Success, result.Mode);
        JsonElement chrome = result.Payload.GetProperty("chromeProtection");
        Assert.Equal(expectedStatus, chrome.GetProperty("status").GetString());
        Assert.False(chrome.GetProperty("isHealthy").GetBoolean());
        Assert.False(chrome.TryGetProperty("profileTokenSha256", out _));
    }

    [Fact]
    public async Task GetUserState_UsesCurrentProgressionStepToDeriveLogicalNight()
    {
        DateTimeOffset now = new(2026, 7, 14, 15, 30, 0, TimeSpan.Zero);
        UserStateRepository stepOne = new()
        {
            Progress = ProgressState.Initial,
        };
        UserStateRepository stepFour = new()
        {
            Progress = ProgressState.Initial with { CurrentStep = 4 },
        };

        ProtocolCommandResult first = await CreateHandler(stepOne, new MutableClock(now))
            .ExecuteAsync(new GetUserStateCommand());
        ProtocolCommandResult fourth = await CreateHandler(stepFour, new MutableClock(now))
            .ExecuteAsync(new GetUserStateCommand());

        Assert.Equal("2026-07-14", first.Payload.GetProperty("currentNightDate").GetString());
        Assert.Equal("2026-07-14", fourth.Payload.GetProperty("currentNightDate").GetString());
        Assert.Equal(new DateOnly(2026, 7, 14), stepOne.SelfReportNightRequested);
        Assert.Equal(new DateOnly(2026, 7, 14), stepFour.SelfReportNightRequested);
    }

    [Fact]
    public async Task GetUserState_ChangedSystemTimeZoneKeepsPinnedActiveNightDate()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            ActiveState = PinnedActiveNight(new DateOnly(2026, 7, 14), ChinaTime),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(now),
                new FixedTimeZoneProvider(TimeZoneInfo.Utc))
            .ExecuteAsync(new GetUserStateCommand());

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(
            "2026-07-14",
            result.Payload.GetProperty("currentNightDate").GetString());
        Assert.Equal(new DateOnly(2026, 7, 14), repository.SelfReportNightRequested);
    }

    [Fact]
    public async Task GetUserState_MissingDependencyDegradesWithoutPartialFacts()
    {
        UserStateRepository repository = new();
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)),
            status: status,
            includeUserStateDependencies: false);

        ProtocolCommandResult result = await handler.ExecuteAsync(new GetUserStateCommand());

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.False(result.Payload.TryGetProperty("progress", out _));
    }

    [Theory]
    [InlineData(UserStateRead.NightState)]
    [InlineData(UserStateRead.Progress)]
    [InlineData(UserStateRead.Onboarding)]
    [InlineData(UserStateRead.RuleSettings)]
    [InlineData(UserStateRead.Outcomes)]
    [InlineData(UserStateRead.SelfReport)]
    public async Task GetUserState_AnyStorageFailureDegradesWithoutPartialFacts(
        UserStateRead degradedRead)
    {
        UserStateRepository repository = new()
        {
            DegradedRead = degradedRead,
        };
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)),
            status: status);

        ProtocolCommandResult result = await handler.ExecuteAsync(new GetUserStateCommand());

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.False(result.Payload.TryGetProperty("weeklyReport", out _));
    }

    [Fact]
    public async Task GetUserState_TimeZoneFailureDegradesWithoutPartialFacts()
    {
        UserStateRepository repository = new();
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)),
                new ThrowingTimeZoneProvider(),
                status)
            .ExecuteAsync(new GetUserStateCommand());

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.False(result.Payload.TryGetProperty("currentNightDate", out _));
    }

    [Fact]
    public async Task GetUserState_InvalidPersistedProgressDegradesInsteadOfReturningPartialFacts()
    {
        UserStateRepository repository = new()
        {
            Progress = ProgressState.Initial with { CurrentStep = 99 },
        };
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)),
                status: status)
            .ExecuteAsync(new GetUserStateCommand());

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
    }

    [Fact]
    public async Task CompleteOnboardingStep_CompareExchangeConflictRereadsAndAdvancesOneStep()
    {
        UserStateRepository repository = new()
        {
            OnboardingConflictsRemaining = 1,
        };
        CompleteOnboardingStepCommand command = new(1, false, false, false, 0);

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)))
            .ExecuteAsync(command);

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(1, repository.Onboarding.CompletedStep);
        Assert.Equal(2, repository.OnboardingSaveCalls);
        Assert.Equal(2, repository.OnboardingReadCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_ExactRetryIsIdempotentWithoutWriteOrClock()
    {
        OnboardingState state = new(2, true, false, true, 1);
        UserStateRepository repository = new()
        {
            Onboarding = state,
        };
        CompleteOnboardingStepCommand command = new(2, true, false, true, 1);

        ProtocolCommandResult result = await CreateHandler(repository, new ThrowingClock())
            .ExecuteAsync(command);

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(state, repository.Onboarding);
        Assert.Equal(0, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_RejectsSkippedStepWithoutWrite()
    {
        UserStateRepository repository = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)))
            .ExecuteAsync(new CompleteOnboardingStepCommand(2, false, false, false, 0));

        Assert.Equal("invalidStepSequence", result.Payload.GetProperty("error").GetString());
        Assert.Equal(0, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_RejectsRegressingIPhoneFactWithoutWrite()
    {
        UserStateRepository repository = new()
        {
            Onboarding = new(3, true, true, true, 2),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)))
            .ExecuteAsync(new CompleteOnboardingStepCommand(
                3,
                true,
                false,
                true,
                1));

        Assert.Equal("factsNotMonotonic", result.Payload.GetProperty("error").GetString());
        Assert.Equal(0, repository.OnboardingSaveCalls);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public async Task CompleteOnboardingStep_ChromeStepRequiresVerificationAndIncognitoDecision(
        bool chromeVerified,
        bool incognitoProtected,
        bool incognitoWarningAcknowledged)
    {
        UserStateRepository repository = new()
        {
            Onboarding = new(2),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)))
            .ExecuteAsync(new CompleteOnboardingStepCommand(
                3,
                chromeVerified,
                incognitoProtected,
                incognitoWarningAcknowledged,
                0));

        Assert.Equal("chromeSetupIncomplete", result.Payload.GetProperty("error").GetString());
        Assert.Equal(0, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_MissingExtensionRequiresExplicitDegradedAcknowledgement()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            Onboarding = new(2),
        };
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(now));

        ProtocolCommandResult rejected = await handler.ExecuteAsync(
            new CompleteOnboardingStepCommand(3, false, false, false, 0, false));
        ProtocolCommandResult accepted = await handler.ExecuteAsync(
            new CompleteOnboardingStepCommand(3, false, false, false, 0, true));

        Assert.Equal(
            "chromeSetupIncomplete",
            rejected.Payload.GetProperty("error").GetString());
        Assert.True(accepted.Payload.GetProperty("accepted").GetBoolean());
        Assert.True(repository.Onboarding.ChromeDegradedAcknowledged);
        Assert.True(accepted.Payload.GetProperty("onboarding")
            .GetProperty("chromeDegradedAcknowledged")
            .GetBoolean());
    }

    [Fact]
    public async Task CompleteOnboardingStep_NotReadyProtectionCannotVerifyChrome()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            Onboarding = new(2),
            ChromeHealth = Health(now, incognitoAllowed: true, protectionReady: false),
        };

        ProtocolCommandResult result = await CreateHandler(repository, new MutableClock(now))
            .ExecuteAsync(new CompleteOnboardingStepCommand(3, true, true, true, 0, true));

        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.False(repository.Onboarding.ChromeVerified);
        Assert.False(repository.Onboarding.IncognitoProtected);
        Assert.True(repository.Onboarding.ChromeDegradedAcknowledged);
    }

    [Theory]
    [InlineData(ChromeHealthCase.Stale)]
    [InlineData(ChromeHealthCase.ExtensionMismatch)]
    public async Task CompleteOnboardingStep_UnhealthyExtensionFactsRequireExplicitDegradedAcknowledgement(
        ChromeHealthCase healthCase)
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            Onboarding = new(2, true, true, true, 1),
            ChromeHealth = healthCase switch
            {
                ChromeHealthCase.Stale => Health(now.AddSeconds(-91), false),
                ChromeHealthCase.ExtensionMismatch => Health(
                    now.AddSeconds(-1),
                    false,
                    new string('a', 32)),
                _ => throw new ArgumentOutOfRangeException(nameof(healthCase)),
            },
        };
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(now));

        ProtocolCommandResult rejected = await handler.ExecuteAsync(
            new CompleteOnboardingStepCommand(3, false, false, false, 1, false));
        ProtocolCommandResult accepted = await handler.ExecuteAsync(
            new CompleteOnboardingStepCommand(3, false, false, false, 1, true));

        Assert.Equal(
            "chromeSetupIncomplete",
            rejected.Payload.GetProperty("error").GetString());
        Assert.True(accepted.Payload.GetProperty("accepted").GetBoolean());
        Assert.True(repository.Onboarding.ChromeDegradedAcknowledged);
        Assert.True(repository.Onboarding.ChromeVerified);
        Assert.True(repository.Onboarding.IncognitoProtected);
        Assert.True(repository.Onboarding.IncognitoWarningAcknowledged);
    }

    [Fact]
    public async Task CompleteOnboardingStep_HealthyExtensionCannotUseDegradedAcknowledgementForMissingIncognitoDecision()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            Onboarding = new(2),
            ChromeHealth = Health(now.AddSeconds(-10), incognitoAllowed: false),
        };

        ProtocolCommandResult result = await CreateHandler(repository, new MutableClock(now))
            .ExecuteAsync(new CompleteOnboardingStepCommand(
                3,
                false,
                false,
                false,
                0,
                ChromeDegradedAcknowledged: true));

        Assert.Equal("chromeSetupIncomplete", result.Payload.GetProperty("error").GetString());
        Assert.False(repository.Onboarding.ChromeDegradedAcknowledged);
        Assert.Equal(0, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_ChromeWarningAcknowledgementCanSatisfyIncognitoDecision()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            Onboarding = new(2),
            ChromeHealth = Health(now.AddSeconds(-10), incognitoAllowed: false),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(now))
            .ExecuteAsync(new CompleteOnboardingStepCommand(3, false, true, true, 0));

        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(3, repository.Onboarding.CompletedStep);
        Assert.True(repository.Onboarding.ChromeVerified);
        Assert.False(repository.Onboarding.IncognitoProtected);
    }

    [Fact]
    public async Task CompleteOnboardingStep_UsesFreshIncognitoFactDespiteSpoofedFalseRequest()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            Onboarding = new(2),
            ChromeHealth = Health(now.AddSeconds(-10), incognitoAllowed: true),
        };

        ProtocolCommandResult result = await CreateHandler(repository, new MutableClock(now))
            .ExecuteAsync(new CompleteOnboardingStepCommand(3, false, false, false, 0));

        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.True(repository.Onboarding.ChromeVerified);
        Assert.True(repository.Onboarding.IncognitoProtected);
    }

    [Fact]
    public async Task RecordChromeHealth_UsesServerTimeAndBindsFirstProfileHash()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new();
        NightGateProtocolCommandHandler handler = CreateHandler(repository, new MutableClock(now));
        RecordChromeHealthCommand first = new(
            ChromeProtectionHealth.ExpectedExtensionId,
            "1.0.0",
            new string('a', 64),
            7,
            true,
            true);

        ProtocolCommandResult recorded = await handler.ExecuteAsync(first);
        ProtocolCommandResult mismatched = await handler.ExecuteAsync(first with
        {
            ProfileTokenSha256 = new string('b', 64),
        });

        Assert.Equal(StorageMode.Success, recorded.Mode);
        Assert.Equal("recorded", recorded.Payload.GetProperty("status").GetString());
        Assert.Equal(now, repository.ChromeHealth!.ObservedAtUtc);
        Assert.True(repository.ChromeHealth.ProtectionReady);
        Assert.Equal(new string('a', 64), repository.ChromeHealth.ProfileTokenSha256);
        Assert.Equal(StorageMode.Degraded, mismatched.Mode);
        Assert.Equal("degraded", mismatched.Payload.GetProperty("status").GetString());
        Assert.Equal(1, repository.ChromeHealthSaveCalls);
    }

    [Fact]
    public async Task RecordChromeHealth_PersistsButRejectsAnOlderIncompatibleWorker()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new();
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(now));
        RecordChromeHealthCommand oldWorker = new(
            ChromeProtectionHealth.ExpectedExtensionId,
            "0.1.3",
            new string('a', 64),
            7,
            false,
            true);

        ProtocolCommandResult rejected = await handler.ExecuteAsync(oldWorker);

        Assert.Equal(StorageMode.Degraded, rejected.Mode);
        Assert.Equal("degraded", rejected.Payload.GetProperty("status").GetString());
        Assert.Equal("0.1.3", repository.ChromeHealth!.ExtensionVersion);
        Assert.Equal(1, repository.ChromeHealthSaveCalls);

        ProtocolCommandResult accepted = await handler.ExecuteAsync(oldWorker with
        {
            ExtensionVersion = ChromeProtectionHealth.MinimumCompatibleExtensionVersion,
        });

        Assert.Equal(StorageMode.Success, accepted.Mode);
        Assert.Equal("recorded", accepted.Payload.GetProperty("status").GetString());
        Assert.Equal(
            ChromeProtectionHealth.MinimumCompatibleExtensionVersion,
            repository.ChromeHealth!.ExtensionVersion);
        Assert.Equal(2, repository.ChromeHealthSaveCalls);
    }

    [Fact]
    public async Task RecordChromeHealth_AllowsAReplacementProfileAfterThePriorHeartbeatIsStale()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            ChromeHealth = Health(now.AddMinutes(-2), incognitoAllowed: true),
        };
        NightGateProtocolCommandHandler handler = CreateHandler(repository, new MutableClock(now));
        RecordChromeHealthCommand replacement = new(
            ChromeProtectionHealth.ExpectedExtensionId,
            "1.0.1",
            new string('b', 64),
            now.ToUnixTimeMilliseconds(),
            false,
            false);

        ProtocolCommandResult result = await handler.ExecuteAsync(replacement);

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal("recorded", result.Payload.GetProperty("status").GetString());
        Assert.Equal(new string('b', 64), repository.ChromeHealth!.ProfileTokenSha256);
        Assert.False(repository.ChromeHealth.ProtectionReady);
    }

    [Fact]
    public async Task UserState_ReportsProtectionDegradedWhenFreshExpectedExtensionIsNotReady()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            ChromeHealth = Health(now, incognitoAllowed: true, protectionReady: false),
        };

        ProtocolCommandResult result = await CreateHandler(repository, new MutableClock(now))
            .ExecuteAsync(new GetUserStateCommand());

        JsonElement protection = result.Payload
            .GetProperty("chromeProtection");
        Assert.Equal("protectionDegraded", protection.GetProperty("status").GetString());
        Assert.False(protection.GetProperty("isHealthy").GetBoolean());
    }

    [Fact]
    public async Task UserState_ReportsMismatchForAnOlderIncompatibleExtension()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            ChromeHealth = Health(
                now,
                incognitoAllowed: true,
                extensionVersion: "0.1.3"),
        };

        ProtocolCommandResult result = await CreateHandler(repository, new MutableClock(now))
            .ExecuteAsync(new GetUserStateCommand());

        JsonElement protection = result.Payload.GetProperty("chromeProtection");
        Assert.Equal("extensionMismatch", protection.GetProperty("status").GetString());
        Assert.False(protection.GetProperty("isHealthy").GetBoolean());
        Assert.Equal("0.1.3", protection.GetProperty("extensionVersion").GetString());
    }

    [Fact]
    public async Task UserState_FreshReadyHealthFromPriorPolicyRevisionIsNotHealthy()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstEvaluation = now.AddSeconds(-30);
        NightWindow fixedWindow = new(
            new DateOnly(2026, 7, 14),
            new DateTimeOffset(2026, 7, 14, 15, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 15, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 16, 35, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 16, 55, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(HealthyRuntimeStatus(firstEvaluation, fixedWindow));
        long priorRevision = status.Current.Policy!.Revision;
        await status.PublishAsync(HealthyRuntimeStatus(
            now,
            fixedWindow,
            NightPhase.LastStart));
        Assert.True(status.Current.Policy!.Revision > priorRevision);
        UserStateRepository repository = new()
        {
            ChromeHealth = Health(
                now,
                incognitoAllowed: true,
                policyRevision: priorRevision),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(now),
                status: status)
            .ExecuteAsync(new GetUserStateCommand());

        JsonElement protection = result.Payload.GetProperty("chromeProtection");
        Assert.Equal("protectionDegraded", protection.GetProperty("status").GetString());
        Assert.False(protection.GetProperty("isHealthy").GetBoolean());
    }

    [Fact]
    public async Task UserState_PeriodicEquivalentPolicyRenewalKeepsReadyHealthHealthy()
    {
        DateTimeOffset firstEvaluation = new(2026, 7, 14, 15, 59, 30, TimeSpan.Zero);
        DateTimeOffset now = firstEvaluation.AddSeconds(30);
        NightWindow fixedWindow = new(
            new DateOnly(2026, 7, 14),
            new DateTimeOffset(2026, 7, 14, 15, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 16, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 16, 35, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 16, 55, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(HealthyRuntimeStatus(firstEvaluation, fixedWindow));
        long appliedRevision = status.Current.Policy!.Revision;
        await status.PublishAsync(HealthyRuntimeStatus(now, fixedWindow));
        Assert.Equal(appliedRevision, status.Current.Policy!.Revision);
        Assert.Equal(now, status.Current.Policy.EvaluatedAt);
        UserStateRepository repository = new()
        {
            ChromeHealth = Health(
                now,
                incognitoAllowed: true,
                policyRevision: appliedRevision),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(now),
                status: status)
            .ExecuteAsync(new GetUserStateCommand());

        JsonElement protection = result.Payload.GetProperty("chromeProtection");
        Assert.Equal("healthy", protection.GetProperty("status").GetString());
        Assert.True(protection.GetProperty("isHealthy").GetBoolean());
    }

    [Fact]
    public async Task CompleteOnboardingStep_HealthFromPriorPolicyRevisionCannotVerifyChrome()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(HealthyRuntimeStatus(now));
        UserStateRepository repository = new()
        {
            Onboarding = new(2),
            ChromeHealth = Health(
                now,
                incognitoAllowed: true,
                policyRevision: now.AddMilliseconds(-1).ToUnixTimeMilliseconds()),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(now),
                status: status)
            .ExecuteAsync(new CompleteOnboardingStepCommand(3, true, true, true, 0));

        Assert.Equal("chromeSetupIncomplete", result.Payload.GetProperty("error").GetString());
        Assert.Equal(0, repository.OnboardingSaveCalls);
        Assert.False(repository.Onboarding.ChromeVerified);
    }

    [Fact]
    public async Task CompleteOnboardingStep_IPhoneStepMustCoverCurrentProgressionStep()
    {
        UserStateRepository repository = new()
        {
            Progress = ProgressState.Initial with { CurrentStep = 3 },
            Onboarding = new(3, true, false, true, 2),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)))
            .ExecuteAsync(new CompleteOnboardingStepCommand(4, true, false, true, 2));

        Assert.Equal("iPhoneSetupIncomplete", result.Payload.GetProperty("error").GetString());
        Assert.Equal(0, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_IPhoneStepAcceptsCoverageThroughCurrentProgressionStep()
    {
        UserStateRepository repository = new()
        {
            Progress = ProgressState.Initial with { CurrentStep = 3 },
            Onboarding = new(3, true, false, true, 2),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)))
            .ExecuteAsync(new CompleteOnboardingStepCommand(4, true, false, true, 3));

        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(4, repository.Onboarding.CompletedStep);
        Assert.Equal(3, repository.Onboarding.IPhoneConfirmedThroughStep);
    }

    [Fact]
    public async Task CompleteOnboardingStep_FifthStepUsesServiceUtcCompletionTime()
    {
        DateTimeOffset serviceTime = new(2026, 7, 15, 0, 5, 0, TimeSpan.FromHours(8));
        UserStateRepository repository = new()
        {
            Progress = ProgressState.Initial with { CurrentStep = 2 },
            Onboarding = new(4, true, false, true, 2),
        };

        ProtocolCommandResult result = await CreateHandler(repository, new MutableClock(serviceTime))
            .ExecuteAsync(new CompleteOnboardingStepCommand(5, true, false, true, 2));

        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(serviceTime.ToUniversalTime(), repository.Onboarding.CompletedAtUtc);
        Assert.Equal(TimeSpan.Zero, repository.Onboarding.CompletedAtUtc!.Value.Offset);
    }

    [Fact]
    public async Task CompleteOnboardingStep_CasConflictAndClockRollbackUseFirstServiceObservation()
    {
        DateTimeOffset first = new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero);
        SequenceClock clock = new(first, first.AddHours(-4));
        UserStateRepository repository = new()
        {
            Progress = ProgressState.Initial with { CurrentStep = 2 },
            Onboarding = new(4, true, false, true, 2),
            OnboardingConflictsRemaining = 1,
        };

        ProtocolCommandResult result = await CreateHandler(repository, clock)
            .ExecuteAsync(new CompleteOnboardingStepCommand(5, true, false, true, 2));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(first, repository.Onboarding.CompletedAtUtc);
        Assert.Equal(1, clock.ReadCalls);
        Assert.Equal(2, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_RepairsLegacyFifthStepMissingCompletionTime()
    {
        DateTimeOffset now = new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero);
        UserStateRepository repository = new()
        {
            Onboarding = new(5, true, false, true, 2),
        };

        ProtocolCommandResult result = await CreateHandler(repository, new MutableClock(now))
            .ExecuteAsync(new CompleteOnboardingStepCommand(5, true, false, true, 2));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(now, repository.Onboarding.CompletedAtUtc);
        Assert.Equal(1, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_DependencyFailureDegradesWithoutWrite()
    {
        UserStateRepository repository = new()
        {
            ThrowOnOnboardingRead = true,
        };
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)),
                status: status)
            .ExecuteAsync(new CompleteOnboardingStepCommand(1, false, false, false, 0));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.Equal(0, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_MissingRepositoryDegradesWithoutWriting()
    {
        UserStateRepository repository = new();
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)),
                status: status,
                includeUserStateDependencies: false)
            .ExecuteAsync(new CompleteOnboardingStepCommand(1, false, false, false, 0));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.Equal(0, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task CompleteOnboardingStep_FifthStepClockFailureDegradesWithoutWriting()
    {
        UserStateRepository repository = new()
        {
            Onboarding = new(4, true, false, true, 1),
        };
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new ThrowingClock(),
                status: status)
            .ExecuteAsync(new CompleteOnboardingStepCommand(5, true, false, true, 1));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.Equal(0, repository.OnboardingSaveCalls);
    }

    [Fact]
    public async Task SaveNightSelfReport_UsesOneServiceObservationForLogicalNightAndServerTimestamp()
    {
        DateTimeOffset first = new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero);
        SequenceClock clock = new(first, first.AddHours(-4));
        UserStateRepository repository = new()
        {
            Progress = ProgressState.Initial with { CurrentStep = 4 },
        };

        ProtocolCommandResult result = await CreateHandler(repository, clock)
            .ExecuteAsync(new SaveNightSelfReportCommand(
                new DateOnly(2026, 7, 14),
                null,
                false));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("saved").GetBoolean());
        Assert.Equal(1, clock.ReadCalls);
        Assert.Equal(first, repository.SelfReport!.UpdatedAtUtc);
        Assert.Null(repository.SelfReport.PhoneOutOfReach);
        Assert.False(repository.SelfReport.WakeWithinWindow);
    }

    [Fact]
    public async Task SaveNightSelfReport_RejectsDateOtherThanServiceDerivedLogicalNight()
    {
        UserStateRepository repository = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero)))
            .ExecuteAsync(new SaveNightSelfReportCommand(
                new DateOnly(2026, 7, 15),
                true,
                true));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.False(result.Payload.GetProperty("saved").GetBoolean());
        Assert.Equal("nightDateMismatch", result.Payload.GetProperty("error").GetString());
        Assert.Equal(0, repository.SelfReportSaveCalls);
    }

    [Fact]
    public async Task SaveNightSelfReport_ChangedSystemTimeZoneKeepsPinnedNightOwnership()
    {
        UserStateRepository repository = new()
        {
            ActiveState = PinnedActiveNight(new DateOnly(2026, 7, 14), ChinaTime),
        };

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero)),
                new FixedTimeZoneProvider(TimeZoneInfo.Utc))
            .ExecuteAsync(new SaveNightSelfReportCommand(
                new DateOnly(2026, 7, 14),
                true,
                true));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("saved").GetBoolean());
        Assert.Equal(new DateOnly(2026, 7, 14), repository.SelfReport!.NightDate);
    }

    [Fact]
    public async Task SaveNightSelfReport_StorageFailureDegradesWithoutPartialSuccess()
    {
        UserStateRepository repository = new()
        {
            SelfReportWriteDegraded = true,
        };
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero)),
                status: status)
            .ExecuteAsync(new SaveNightSelfReportCommand(
                new DateOnly(2026, 7, 14),
                true,
                true));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.False(result.Payload.TryGetProperty("saved", out _));
    }

    [Fact]
    public async Task SaveNightSelfReport_ProgressReadFailureDegradesWithoutWrite()
    {
        UserStateRepository repository = new()
        {
            DegradedRead = UserStateRead.Progress,
        };
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero)),
                status: status)
            .ExecuteAsync(new SaveNightSelfReportCommand(
                new DateOnly(2026, 7, 14),
                true,
                true));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.Equal(0, repository.SelfReportSaveCalls);
    }

    [Fact]
    public async Task SaveNightSelfReport_TimeZoneFailureDegradesWithoutWrite()
    {
        UserStateRepository repository = new();
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero)),
                new ThrowingTimeZoneProvider(),
                status)
            .ExecuteAsync(new SaveNightSelfReportCommand(
                new DateOnly(2026, 7, 14),
                true,
                true));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.Equal(0, repository.SelfReportSaveCalls);
    }

    [Fact]
    public async Task SaveNightSelfReport_MissingRepositoryDegradesWithoutWrite()
    {
        UserStateRepository repository = new();
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await CreateHandler(
                repository,
                new MutableClock(new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero)),
                status: status,
                includeUserStateDependencies: false)
            .ExecuteAsync(new SaveNightSelfReportCommand(
                new DateOnly(2026, 7, 14),
                true,
                true));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.True(status.Current.IsDegraded);
        Assert.Equal(0, repository.SelfReportSaveCalls);
    }

    private static NightGateProtocolCommandHandler CreateHandler(
        UserStateRepository repository,
        IClock clock,
        ITimeZoneProvider? timeZoneProvider = null,
        InMemoryServiceStatus? status = null,
        bool includeUserStateDependencies = true)
    {
        InMemoryServiceStatus sharedStatus = status ?? new();
        if (status is null)
        {
            sharedStatus.PublishAsync(HealthyRuntimeStatus(DateTimeOffset.FromUnixTimeMilliseconds(1)))
                .GetAwaiter()
                .GetResult();
        }
        return new(
            repository,
            repository,
            repository,
            sharedStatus,
            sharedStatus,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            timeZoneProvider: timeZoneProvider ?? new FixedTimeZoneProvider(ChinaTime),
            onboardingRepository: includeUserStateDependencies ? repository : null,
            ruleSettingsRepository: includeUserStateDependencies ? repository : null,
            selfReportRepository: includeUserStateDependencies ? repository : null,
            chromeProtectionHealthRepository: includeUserStateDependencies ? repository : null);
    }

    private static NightOutcome Outcome(
        DateOnly nightDate,
        bool qualifies,
        DateTimeOffset closedAtUtc) => new(
        Guid.NewGuid(),
        nightDate,
        closedAtUtc.ToUniversalTime(),
        EmergencyUsed: !qualifies,
        TeamRescueUsed: false,
        EntertainmentUsed: false,
        DeliberateBypass: false,
        LateNewEntertainment: false,
        MissedLock: false);

    private static NightState PinnedActiveNight(
        DateOnly nightDate,
        TimeZoneInfo timeZone) => new(
        Guid.NewGuid(),
        nightDate,
        new DateTimeOffset(2026, 7, 14, 13, 1, 0, TimeSpan.Zero),
        NightPhase.Free,
        null,
        false,
        false,
        false,
        false,
        false,
        false,
        ScheduleTimeZoneSerialized: NightScheduleTimeZone.Capture(timeZone));

    private static ChromeProtectionHealth Health(
        DateTimeOffset observedAtUtc,
        bool incognitoAllowed,
        string? extensionId = null,
        bool protectionReady = true,
        long policyRevision = 1,
        string extensionVersion = "1.0.0") => new(
        extensionId ?? ChromeProtectionHealth.ExpectedExtensionId,
        extensionVersion,
        new string('c', 64),
        policyRevision,
        incognitoAllowed,
        observedAtUtc,
        protectionReady);

    private static ServiceRuntimeStatus HealthyRuntimeStatus(
        DateTimeOffset evaluatedAtUtc,
        NightWindow? fixedWindow = null,
        NightPhase phase = NightPhase.Free)
    {
        NightWindow window = fixedWindow ?? new(
            new DateOnly(2026, 7, 14),
            evaluatedAtUtc,
            evaluatedAtUtc,
            evaluatedAtUtc,
            evaluatedAtUtc,
            evaluatedAtUtc);
        return new(
            true,
            false,
            null,
            new PolicySnapshot(
                evaluatedAtUtc,
                phase,
                window,
                ImmutableArray<AppRule>.Empty,
                ImmutableArray<SiteRule>.Empty));
    }

    private sealed class EmptyAllowedProcesses : IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => [];
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class SequenceClock(params DateTimeOffset[] values) : IClock
    {
        private readonly Queue<DateTimeOffset> _values = new(values);

        public int ReadCalls { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                ReadCalls++;
                return _values.Count > 1 ? _values.Dequeue() : _values.Peek();
            }
        }
    }

    private sealed class ThrowingClock : IClock
    {
        public DateTimeOffset UtcNow => throw new InvalidOperationException("clock unavailable");
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo local) : ITimeZoneProvider
    {
        public TimeZoneInfo Local { get; } = local;
    }

    private sealed class ThrowingTimeZoneProvider : ITimeZoneProvider
    {
        public TimeZoneInfo Local => throw new InvalidOperationException("time zone unavailable");
    }

    public enum UserStateRead
    {
        None,
        NightState,
        Progress,
        Onboarding,
        RuleSettings,
        Outcomes,
        SelfReport,
    }

    public enum ChromeHealthCase
    {
        Missing,
        Stale,
        Future,
        ExtensionMismatch,
        StorageDegraded,
    }

    private sealed class UserStateRepository :
        INightStateRepository,
        IProgressRepository,
        IHistoryRepository,
        IOnboardingRepository,
        IRuleSettingsRepository,
        INightSelfReportRepository,
        IChromeProtectionHealthRepository
    {
        private long _onboardingVersion = 1;
        private long _chromeHealthVersion;

        public ProgressState Progress { get; set; } = ProgressState.Initial;

        public NightState? ActiveState { get; set; }

        public OnboardingState Onboarding { get; set; } = OnboardingState.Initial;

        public RuleSettingsState Rules { get; set; } = RuleSettingsState.Initial;

        public IReadOnlyList<NightOutcome> Outcomes { get; set; } = [];

        public NightSelfReport? SelfReport { get; set; }

        public ChromeProtectionHealth? ChromeHealth { get; set; }

        public bool ChromeHealthReadDegraded { get; set; }

        public int ChromeHealthSaveCalls { get; private set; }

        public UserStateRead DegradedRead { get; set; }

        public bool ThrowOnOnboardingRead { get; set; }

        public bool SelfReportWriteDegraded { get; set; }

        public int OnboardingConflictsRemaining { get; set; }

        public int OnboardingReadCalls { get; private set; }

        public int OnboardingSaveCalls { get; private set; }

        public int SelfReportSaveCalls { get; private set; }

        public int LatestOutcomeCountRequested { get; private set; }

        public DateOnly? SelfReportNightRequested { get; private set; }

        public ValueTask<StorageResult<ProgressState>> ReadProgressAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            DegradedRead == UserStateRead.Progress
                ? new StorageResult<ProgressState>(StorageMode.Degraded, Progress)
                : new(StorageMode.Success, Progress, Version: 1));

        public ValueTask<StorageWriteResult> SaveProgressAsync(
            ProgressState progress,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<OnboardingState>> ReadOnboardingAsync(
            CancellationToken cancellationToken = default)
        {
            OnboardingReadCalls++;
            if (ThrowOnOnboardingRead)
            {
                throw new InvalidDataException("simulated invalid onboarding storage");
            }

            return ValueTask.FromResult(
                DegradedRead == UserStateRead.Onboarding
                    ? new StorageResult<OnboardingState>(StorageMode.Degraded, Onboarding)
                    : new(StorageMode.Success, Onboarding, Version: _onboardingVersion));
        }

        public ValueTask<StorageWriteResult> SaveOnboardingAsync(
            OnboardingState state,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            OnboardingSaveCalls++;
            if (OnboardingConflictsRemaining-- > 0)
            {
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            Assert.Equal(_onboardingVersion, expectedVersion);
            Onboarding = state;
            _onboardingVersion++;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }

        public ValueTask<StorageResult<RuleSettingsState>> ReadRuleSettingsAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            DegradedRead == UserStateRead.RuleSettings
                ? new StorageResult<RuleSettingsState>(StorageMode.Degraded, Rules)
                : new(StorageMode.Success, Rules, Version: 1));

        public ValueTask<StorageWriteResult> SaveRuleSettingsAsync(
            RuleSettingsState state,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<ChromeProtectionHealth?>>
            ReadChromeProtectionHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ChromeHealthReadDegraded
                    ? new StorageResult<ChromeProtectionHealth?>(StorageMode.Degraded, null)
                    : new(StorageMode.Success, ChromeHealth, Version: _chromeHealthVersion));

        public ValueTask<StorageWriteResult> SaveChromeProtectionHealthAsync(
            ChromeProtectionHealth health,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            ChromeHealthSaveCalls++;
            if (expectedVersion != _chromeHealthVersion)
            {
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            ChromeHealth = health;
            _chromeHealthVersion++;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }

        public ValueTask<StorageResult<NightSelfReport?>> ReadSelfReportAsync(
            DateOnly nightDate,
            CancellationToken cancellationToken = default)
        {
            SelfReportNightRequested = nightDate;
            return ValueTask.FromResult(
                DegradedRead == UserStateRead.SelfReport
                    ? new StorageResult<NightSelfReport?>(StorageMode.Degraded, null)
                    : new(StorageMode.Success, SelfReport));
        }

        public ValueTask<StorageWriteResult> SaveSelfReportAsync(
            NightSelfReport report,
            CancellationToken cancellationToken = default)
        {
            SelfReportSaveCalls++;
            if (SelfReportWriteDegraded)
            {
                return ValueTask.FromResult(new StorageWriteResult(StorageMode.Degraded));
            }

            SelfReport = report;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }

        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestOutcomesAsync(
            int count,
            CancellationToken cancellationToken = default)
        {
            LatestOutcomeCountRequested = count;
            return ValueTask.FromResult(
                DegradedRead == UserStateRead.Outcomes
                    ? new StorageResult<IReadOnlyList<NightOutcome>>(
                        StorageMode.Degraded,
                        Array.Empty<NightOutcome>())
                    : new(StorageMode.Success, Outcomes));
        }

        public ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DegradedRead == UserStateRead.NightState
                    ? new StorageResult<NightState?>(
                        StorageMode.Degraded,
                        null,
                        "night-state-unavailable")
                    : new(
                        StorageMode.Success,
                        ActiveState,
                        Version: 1));

        public ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
            NightState state,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(
            NightState state,
            ProgressState progress,
            NightEvent nightEvent,
            long? expectedStateVersion = null,
            long? expectedProgressVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(
            NightState closedState,
            NightOutcome outcome,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestEligibleOutcomesAsync(
            int count,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageWriteResult> SaveOutcomeAsync(
            NightOutcome outcome,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageWriteResult> RecordEventAsync(
            NightEvent nightEvent,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageWriteResult> PurgeEventsOlderThanAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<StorageWriteResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
