using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class NightGateDesktopClientTests
{
    private const string DesktopSessionId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task GetPolicy_WritesExactEnvelopeAndDecodesCurrentServerWrapper()
    {
        ScriptedTransport transport = new(request => Response(PolicyResponse("policy-1")));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("policy-1"));

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.True(result.CanEnforce);
        Assert.False(result.IsDegraded);
        Assert.Equal(DesktopNightPhase.Grace, result.ExecutablePolicy!.Phase);
        Assert.Equal(1_783_382_760_000, result.ExecutablePolicy.Revision);
        Assert.Equal(new DateOnly(2026, 7, 6), result.ExecutablePolicy.Window.NightDate);
        DesktopAppRuleDto rule = Assert.Single(result.ExecutablePolicy.AppRules);
        Assert.Equal("game-primary", rule.Id);
        Assert.Equal(DesktopAppRuleCategory.Game, rule.Category);
        Assert.Same(result, client.CurrentPolicy);
        using JsonDocument request = JsonDocument.Parse(transport.Requests.Single());
        AssertExactEnvelope(request.RootElement, "getPolicy", "policy-1");
        Assert.Empty(request.RootElement.GetProperty("payload").EnumerateObject());
    }

    [Fact]
    public async Task GetPolicy_DesktopSessionModeUsesStableSessionEnvelope()
    {
        ScriptedTransport transport = new(
            _ => Response(PolicyResponse("desktop-1").Replace(
                "getPolicyResult",
                "getDesktopPolicyResult",
                StringComparison.Ordinal)),
            _ => Response(PolicyResponse("desktop-2").Replace(
                "getPolicyResult",
                "getDesktopPolicyResult",
                StringComparison.Ordinal)));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("desktop-1", "desktop-2"),
            DesktopSessionId);

        Assert.True((await client.GetPolicyAsync()).CanEnforce);
        Assert.True((await client.GetPolicyAsync()).CanEnforce);

        Assert.Equal(2, transport.Requests.Count);
        foreach (ReadOnlyMemory<byte> requestUtf8 in transport.Requests)
        {
            using JsonDocument request = JsonDocument.Parse(requestUtf8);
            Assert.Equal("getDesktopPolicy", request.RootElement.GetProperty("type").GetString());
            JsonElement payload = request.RootElement.GetProperty("payload");
            Assert.Equal(
                ["sessionId"],
                payload.EnumerateObject().Select(property => property.Name));
            Assert.Equal(DesktopSessionId, payload.GetProperty("sessionId").GetString());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdeF")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    public void DesktopSessionMode_RejectsNonCanonicalSessionIds(string sessionId)
    {
        Assert.Throws<ArgumentException>(() => new NightGateDesktopClient(
            new ScriptedTransport(),
            new FixedRequestIdSource("unused"),
            sessionId));
    }

    [Theory]
    [InlineData("free", DesktopNightPhase.Free)]
    [InlineData("lastStart", DesktopNightPhase.LastStart)]
    [InlineData("grace", DesktopNightPhase.Grace)]
    [InlineData("landingLocked", DesktopNightPhase.LandingLocked)]
    [InlineData("morning", DesktopNightPhase.Morning)]
    [InlineData("coolingOff", DesktopNightPhase.CoolingOff)]
    [InlineData("overrideActive", DesktopNightPhase.OverrideActive)]
    public async Task GetPolicy_DecodesEveryCanonicalPhase(
        string token,
        DesktopNightPhase expected)
    {
        ScriptedTransport transport = new(_ => Response(PolicyResponse("phase", $"\"{token}\"")));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("phase"));

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.True(result.CanEnforce);
        Assert.Equal(expected, result.ExecutablePolicy!.Phase);
    }

    [Theory]
    [InlineData("wrongVersion")]
    [InlineData("wrongType")]
    [InlineData("wrongRequestId")]
    [InlineData("extraEnvelopeField")]
    [InlineData("numericPhase")]
    [InlineData("missingRevision")]
    [InlineData("negativeRevision")]
    [InlineData("oversizedRevision")]
    [InlineData("extraStatusField")]
    [InlineData("unknownStatus")]
    public async Task InvalidResponse_IsRejectedAndCannotExecutePolicy(string mutation)
    {
        string response = mutation switch
        {
            "wrongVersion" => PolicyResponse("strict").Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            "wrongType" => PolicyResponse("strict").Replace("getPolicyResult", "getStatusResult", StringComparison.Ordinal),
            "wrongRequestId" => PolicyResponse("someone-else"),
            "extraEnvelopeField" => PolicyResponse("strict").Replace("\"payload\":", "\"extra\":true,\"payload\":", StringComparison.Ordinal),
            "numericPhase" => PolicyResponse("strict", "2"),
            "missingRevision" => PolicyResponse("strict").Replace(
                "\"revision\":1783382760000,",
                string.Empty,
                StringComparison.Ordinal),
            "negativeRevision" => PolicyResponse("strict").Replace(
                "\"revision\":1783382760000",
                "\"revision\":-1",
                StringComparison.Ordinal),
            "oversizedRevision" => PolicyResponse("strict").Replace(
                "\"revision\":1783382760000",
                "\"revision\":9007199254740992",
                StringComparison.Ordinal),
            "extraStatusField" => PolicyResponse("strict").Replace("\"status\":\"success\",", "\"status\":\"success\",\"extra\":true,", StringComparison.Ordinal),
            "unknownStatus" => PolicyResponse("strict").Replace("\"status\":\"success\"", "\"status\":\"healthy\"", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        ScriptedTransport transport = new(_ => Response(response));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("strict"));

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.False(result.CanEnforce);
        Assert.True(result.IsDegraded);
        Assert.Null(result.ExecutablePolicy);
        Assert.Same(result, client.CurrentPolicy);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("disconnect")]
    [InlineData("writeFault")]
    [InlineData("readFault")]
    public async Task TransportFailure_ClearsPreviouslyExecutablePolicy(string failure)
    {
        ScriptedTransport transport = new(
            _ => Response(PolicyResponse("first")),
            _ => failure switch
            {
                "timeout" => throw new TimeoutException(),
                "disconnect" => throw new EndOfStreamException(),
                "writeFault" => throw new IOException("write"),
                "readFault" => throw new InvalidDataException("read"),
                _ => throw new ArgumentOutOfRangeException(nameof(failure)),
            });
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("first", "second"));
        Assert.True((await client.GetPolicyAsync()).CanEnforce);

        DesktopPolicyResult failed = await client.GetPolicyAsync();

        Assert.False(failed.CanEnforce);
        Assert.True(failed.IsDegraded);
        Assert.Null(client.CurrentPolicy.ExecutablePolicy);
    }

    [Fact]
    public async Task ConcurrentPolicyReads_NewerRequestWinsWhenOlderResponseArrivesLast()
    {
        OutOfOrderTransport transport = new(Response(PolicyResponse("new", "\"morning\"")));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("old", "new"));

        Task<DesktopPolicyResult> older = client.GetPolicyAsync().AsTask();
        await transport.FirstRequestStarted.Task;
        DesktopPolicyResult newer = await client.GetPolicyAsync();
        transport.CompleteFirst(Response(PolicyResponse("old", "\"grace\"")));
        DesktopPolicyResult olderCompletion = await older;

        Assert.Equal(DesktopNightPhase.Morning, newer.ExecutablePolicy!.Phase);
        Assert.Equal(DesktopNightPhase.Morning, olderCompletion.ExecutablePolicy!.Phase);
        Assert.Equal(DesktopNightPhase.Morning, client.CurrentPolicy.ExecutablePolicy!.Phase);
    }

    [Fact]
    public async Task DisabledOrDegradedRuntimeStatus_IsAlwaysFailOpen()
    {
        string response = PolicyResponse("degraded")
            .Replace("\"status\":\"success\"", "\"status\":\"degraded\"", StringComparison.Ordinal)
            .Replace("\"enforcementEnabled\":true,\"isDegraded\":false,\"degradationCode\":null", "\"enforcementEnabled\":false,\"isDegraded\":true,\"degradationCode\":\"storage-unavailable\"", StringComparison.Ordinal);
        ScriptedTransport transport = new(_ => Response(response));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("degraded"));

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.False(result.CanEnforce);
        Assert.Equal("storage-unavailable", result.DegradationCode);
        Assert.Null(result.ExecutablePolicy);
    }

    [Theory]
    [InlineData("outerDegraded")]
    [InlineData("runtimeDisabled")]
    [InlineData("runtimeDegraded")]
    [InlineData("missingPolicy")]
    [InlineData("policyDisabled")]
    [InlineData("policyDegraded")]
    public async Task CanEnforce_RequiresOuterRuntimeAndPolicyLayersToAllBeHealthy(
        string mutation)
    {
        string response = MutatePolicyResponse("layers", (root, data, policy) =>
        {
            switch (mutation)
            {
                case "outerDegraded":
                    root["payload"]!["status"] = "degraded";
                    break;
                case "runtimeDisabled":
                    data["enforcementEnabled"] = false;
                    break;
                case "runtimeDegraded":
                    data["isDegraded"] = true;
                    break;
                case "missingPolicy":
                    data["policy"] = null;
                    break;
                case "policyDisabled":
                    policy["enforcementEnabled"] = false;
                    break;
                case "policyDegraded":
                    policy["isDegraded"] = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });
        ScriptedTransport transport = new(_ => Response(response));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("layers"));

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.False(result.CanEnforce);
        Assert.Null(result.ExecutablePolicy);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversize")]
    [InlineData("unknownPhase")]
    [InlineData("nestedExtra")]
    [InlineData("nullCollections")]
    public async Task MalformedUnknownOversizeOrNestedInvalidPolicy_IsFailOpen(string mutation)
    {
        ReadOnlyMemory<byte> response = mutation switch
        {
            "malformed" => Response("{not-json"),
            "oversize" => new byte[65_537],
            "unknownPhase" => Response(PolicyResponse("invalid", "\"bedtime\"")),
            "nestedExtra" => Response(MutatePolicyResponse(
                "invalid",
                (_, _, policy) => ((JsonObject)policy["window"]!)["pageTitle"] = "private")),
            "nullCollections" => Response(MutatePolicyResponse(
                "invalid",
                (_, _, policy) => policy["appRules"] = null)),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        ScriptedTransport transport = new(_ => response);
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("invalid"));

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.False(result.CanEnforce);
        Assert.True(result.IsDegraded);
        Assert.Null(client.CurrentPolicy.ExecutablePolicy);
    }

    [Theory]
    [InlineData("appNotConfigured")]
    [InlineData("missingRoot")]
    [InlineData("missingCategory")]
    [InlineData("relativeRoot")]
    [InlineData("nonExecutableRoot")]
    [InlineData("nonCanonicalRoot")]
    [InlineData("relativeHelper")]
    [InlineData("helperEqualsRoot")]
    [InlineData("duplicateHelper")]
    [InlineData("sessionTooShort")]
    [InlineData("sessionTooLong")]
    [InlineData("duplicateAppId")]
    [InlineData("duplicateAppRoot")]
    [InlineData("blankSite")]
    [InlineData("duplicateSite")]
    [InlineData("unorderedWindow")]
    [InlineData("nightDateMismatch")]
    [InlineData("boundaryOutsideNight")]
    [InlineData("nullOverrideIds")]
    [InlineData("blankOverrideId")]
    [InlineData("duplicateOverrideId")]
    [InlineData("unorderedOverrideTimes")]
    [InlineData("unknownTeamRescueRuleId")]
    [InlineData("nonTeamRescueIdentifiers")]
    public async Task SemanticallyMalformedPolicy_FailsOpenAndClearsPriorExecutablePolicy(
        string mutation)
    {
        string malformed = MutatePolicyResponse("invalid", (_, _, policy) =>
        {
            JsonObject app = policy["appRules"]![0]!.AsObject();
            JsonObject window = policy["window"]!.AsObject();
            switch (mutation)
            {
                case "appNotConfigured":
                    app["isConfigured"] = false;
                    break;
                case "missingRoot":
                    app["rootExecutablePath"] = null;
                    break;
                case "missingCategory":
                    app["category"] = null;
                    break;
                case "relativeRoot":
                    app["rootExecutablePath"] = @"Games\game.exe";
                    break;
                case "nonExecutableRoot":
                    app["rootExecutablePath"] = @"C:\Games\game.com";
                    break;
                case "nonCanonicalRoot":
                    app["rootExecutablePath"] = @"C:\Games\.\game.exe";
                    break;
                case "relativeHelper":
                    app["helperExecutablePaths"] = new JsonArray(@"helpers\voice.exe");
                    break;
                case "helperEqualsRoot":
                    app["helperExecutablePaths"] = new JsonArray(@"c:\games\GAME.exe");
                    break;
                case "duplicateHelper":
                    app["helperExecutablePaths"] = new JsonArray(
                        @"C:\Games\helper.exe",
                        @"c:\games\HELPER.exe");
                    break;
                case "sessionTooShort":
                    app["sessionMinutes"] = 14;
                    break;
                case "sessionTooLong":
                    app["sessionMinutes"] = 91;
                    break;
                case "duplicateAppId":
                    policy["appRules"]!.AsArray().Add(app.DeepClone());
                    policy["appRules"]![1]!["id"] = "GAME-PRIMARY";
                    policy["appRules"]![1]!["rootExecutablePath"] = @"C:\Games\other.exe";
                    break;
                case "duplicateAppRoot":
                    policy["appRules"]!.AsArray().Add(app.DeepClone());
                    policy["appRules"]![1]!["id"] = "other-game";
                    policy["appRules"]![1]!["rootExecutablePath"] = @"c:\games\GAME.exe";
                    break;
                case "blankSite":
                    policy["siteRules"] = new JsonArray(new JsonObject { ["domain"] = "  " });
                    break;
                case "duplicateSite":
                    policy["siteRules"] = new JsonArray(
                        new JsonObject { ["domain"] = "video.example" },
                        new JsonObject { ["domain"] = "VIDEO.EXAMPLE" });
                    break;
                case "unorderedWindow":
                    window["lastStart"] = window["protectedStart"]!.DeepClone();
                    break;
                case "nightDateMismatch":
                    window["nightDate"] = "2026-07-07";
                    break;
                case "boundaryOutsideNight":
                    window["wake"] = "2026-07-08T09:00:00+00:00";
                    break;
                case "nullOverrideIds":
                    SetActiveOverride(
                        policy,
                        "emergency",
                        new JsonArray());
                    policy["activeOverride"]!["allowedProcessIdentifiers"] = null;
                    break;
                case "blankOverrideId":
                    SetActiveOverride(
                        policy,
                        "emergency",
                        new JsonArray(" "));
                    break;
                case "duplicateOverrideId":
                    SetActiveOverride(
                        policy,
                        "teamRescue",
                        new JsonArray("game-primary", "GAME-PRIMARY"));
                    break;
                case "unorderedOverrideTimes":
                    SetActiveOverride(
                        policy,
                        "entertainment",
                        new JsonArray());
                    policy["activeOverride"]!["requestedAtUtc"] =
                        "2026-07-07T00:11:00+00:00";
                    break;
                case "unknownTeamRescueRuleId":
                    SetActiveOverride(
                        policy,
                        "teamRescue",
                        new JsonArray("not-configured"));
                    break;
                case "nonTeamRescueIdentifiers":
                    SetActiveOverride(
                        policy,
                        "emergency",
                        new JsonArray("game-primary"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });
        ScriptedTransport transport = new(
            _ => Response(PolicyResponse("valid")),
            _ => Response(malformed));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("valid", "invalid"));
        Assert.True((await client.GetPolicyAsync()).CanEnforce);

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.False(result.CanEnforce);
        Assert.True(result.IsDegraded);
        Assert.Null(result.ExecutablePolicy);
        Assert.Null(client.CurrentPolicy.ExecutablePolicy);
    }

    [Theory]
    [InlineData("step4Weekday")]
    [InlineData("step4Weekend")]
    [InlineData("uncPaths")]
    [InlineData("teamRescue")]
    [InlineData("emptyTeamRescue")]
    [InlineData("emergency")]
    [InlineData("entertainment")]
    public async Task SemanticallyValidPolicyShapes_RemainExecutable(string shape)
    {
        string response = MutatePolicyResponse("valid-shape", (_, _, policy) =>
        {
            JsonObject window = policy["window"]!.AsObject();
            JsonObject app = policy["appRules"]![0]!.AsObject();
            switch (shape)
            {
                case "step4Weekday":
                    SetWindow(
                        window,
                        "2026-07-06",
                        "2026-07-06T21:00:00+00:00",
                        "2026-07-06T23:20:00+00:00",
                        "2026-07-06T23:55:00+00:00",
                        "2026-07-07T00:15:00+00:00",
                        "2026-07-07T08:15:00+00:00");
                    break;
                case "step4Weekend":
                    SetWindow(
                        window,
                        "2026-07-10",
                        "2026-07-10T21:00:00+08:00",
                        "2026-07-11T00:20:00+08:00",
                        "2026-07-11T00:55:00+08:00",
                        "2026-07-11T01:15:00+08:00",
                        "2026-07-11T09:15:00+08:00");
                    break;
                case "uncPaths":
                    app["rootExecutablePath"] = @"\\server\games\title\game.exe";
                    app["helperExecutablePaths"] = new JsonArray(
                        @"\\server\games\title\voice.exe");
                    break;
                case "teamRescue":
                    SetActiveOverride(
                        policy,
                        "teamRescue",
                        new JsonArray("game-primary"));
                    break;
                case "emptyTeamRescue":
                    SetActiveOverride(
                        policy,
                        "teamRescue",
                        new JsonArray());
                    break;
                case "emergency":
                    SetActiveOverride(
                        policy,
                        "emergency",
                        new JsonArray());
                    break;
                case "entertainment":
                    SetActiveOverride(
                        policy,
                        "entertainment",
                        new JsonArray());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        });
        ScriptedTransport transport = new(_ => Response(response));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("valid-shape"));

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.True(result.CanEnforce);
        Assert.NotNull(result.ExecutablePolicy);
    }

    [Theory]
    [InlineData("teamDelayedStart")]
    [InlineData("teamShortDuration")]
    [InlineData("teamLongDuration")]
    [InlineData("emergencyDelayedStart")]
    [InlineData("emergencyShortDuration")]
    [InlineData("emergencyLongDuration")]
    [InlineData("entertainmentShortCooling")]
    [InlineData("entertainmentLongCooling")]
    [InlineData("entertainmentShortDuration")]
    [InlineData("entertainmentLongDuration")]
    [InlineData("coolingOffWithoutOverride")]
    [InlineData("overrideActiveWithoutOverride")]
    [InlineData("basePhaseWithOverride")]
    [InlineData("evaluatedBeforeRequest")]
    [InlineData("evaluatedAtEnd")]
    [InlineData("coolingOffForImmediateOverride")]
    [InlineData("overrideActiveDuringEntertainmentCooling")]
    [InlineData("coolingOffAtEntertainmentStart")]
    public async Task OverrideTimingOrPhaseMismatch_FailsOpenAndClearsExecutablePolicy(
        string mutation)
    {
        string malformed = MutatePolicyResponse("invalid", (_, _, policy) =>
        {
            switch (mutation)
            {
                case "teamDelayedStart":
                    SetActiveOverride(policy, "teamRescue", new JsonArray("game-primary"));
                    SetOverrideTimes(policy, "00:00", "00:01", "00:21");
                    break;
                case "teamShortDuration":
                    SetActiveOverride(policy, "teamRescue", new JsonArray("game-primary"));
                    SetOverrideTimes(policy, "00:00", "00:00", "00:19");
                    break;
                case "teamLongDuration":
                    SetActiveOverride(policy, "teamRescue", new JsonArray("game-primary"));
                    SetOverrideTimes(policy, "00:00", "00:00", "00:21");
                    break;
                case "emergencyDelayedStart":
                    SetActiveOverride(policy, "emergency", new JsonArray());
                    SetOverrideTimes(policy, "00:00", "00:01", "00:31");
                    break;
                case "emergencyShortDuration":
                    SetActiveOverride(policy, "emergency", new JsonArray());
                    SetOverrideTimes(policy, "00:00", "00:00", "00:29");
                    break;
                case "emergencyLongDuration":
                    SetActiveOverride(policy, "emergency", new JsonArray());
                    SetOverrideTimes(policy, "00:00", "00:00", "00:31");
                    break;
                case "entertainmentShortCooling":
                    SetActiveOverride(policy, "entertainment", new JsonArray());
                    SetOverrideTimes(policy, "00:00", "00:09", "00:29");
                    break;
                case "entertainmentLongCooling":
                    SetActiveOverride(policy, "entertainment", new JsonArray());
                    SetOverrideTimes(policy, "00:00", "00:11", "00:31");
                    break;
                case "entertainmentShortDuration":
                    SetActiveOverride(policy, "entertainment", new JsonArray());
                    SetOverrideTimes(policy, "00:00", "00:10", "00:29");
                    break;
                case "entertainmentLongDuration":
                    SetActiveOverride(policy, "entertainment", new JsonArray());
                    SetOverrideTimes(policy, "00:00", "00:10", "00:31");
                    break;
                case "coolingOffWithoutOverride":
                    policy["phase"] = "coolingOff";
                    break;
                case "overrideActiveWithoutOverride":
                    policy["phase"] = "overrideActive";
                    break;
                case "basePhaseWithOverride":
                    policy["activeOverride"] = ActiveOverride(
                        "teamRescue",
                        new JsonArray("game-primary"));
                    break;
                case "evaluatedBeforeRequest":
                    SetActiveOverride(policy, "teamRescue", new JsonArray("game-primary"));
                    policy["evaluatedAt"] = "2026-07-06T23:59:59+00:00";
                    break;
                case "evaluatedAtEnd":
                    SetActiveOverride(policy, "teamRescue", new JsonArray("game-primary"));
                    policy["evaluatedAt"] = "2026-07-07T00:20:00+00:00";
                    break;
                case "coolingOffForImmediateOverride":
                    SetActiveOverride(policy, "teamRescue", new JsonArray("game-primary"));
                    policy["phase"] = "coolingOff";
                    break;
                case "overrideActiveDuringEntertainmentCooling":
                    SetActiveOverride(policy, "entertainment", new JsonArray());
                    policy["phase"] = "overrideActive";
                    break;
                case "coolingOffAtEntertainmentStart":
                    SetActiveOverride(policy, "entertainment", new JsonArray());
                    policy["evaluatedAt"] = "2026-07-07T00:10:00+00:00";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });
        ScriptedTransport transport = new(
            _ => Response(PolicyResponse("valid")),
            _ => Response(malformed));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("valid", "invalid"));
        Assert.True((await client.GetPolicyAsync()).CanEnforce);

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.False(result.CanEnforce);
        Assert.True(result.IsDegraded);
        Assert.Null(result.ExecutablePolicy);
        Assert.Null(client.CurrentPolicy.ExecutablePolicy);
    }

    [Theory]
    [InlineData("teamAtRequest")]
    [InlineData("entertainmentAtStart")]
    public async Task OverridePhaseBoundaryShapes_RemainExecutable(string shape)
    {
        string response = MutatePolicyResponse("boundary", (_, _, policy) =>
        {
            switch (shape)
            {
                case "teamAtRequest":
                    SetActiveOverride(policy, "teamRescue", new JsonArray("game-primary"));
                    policy["evaluatedAt"] = "2026-07-07T00:00:00+00:00";
                    break;
                case "entertainmentAtStart":
                    SetActiveOverride(policy, "entertainment", new JsonArray());
                    policy["evaluatedAt"] = "2026-07-07T00:10:00+00:00";
                    policy["phase"] = "overrideActive";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        });
        NightGateDesktopClient client = new(
            new ScriptedTransport(_ => Response(response)),
            new FixedRequestIdSource("boundary"));

        DesktopPolicyResult result = await client.GetPolicyAsync();

        Assert.True(result.CanEnforce);
    }

    [Fact]
    public async Task AcceptedOverride_ExposesWindowAndRefreshesPolicyImmediately()
    {
        ScriptedTransport transport = new(
            _ => Response(OverrideAcceptedResponse("override", "teamRescue")),
            _ => Response(PolicyResponse("refresh", "\"overrideActive\"")));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("override", "refresh"));

        DesktopOverrideResult result = await client.RequestOverrideAsync(
            new(DesktopOverrideKind.TeamRescue));

        Assert.True(result.Accepted);
        Assert.Equal(DesktopOverrideKind.TeamRescue, result.ActiveWindow!.Kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 7, 0, 20, 0, TimeSpan.Zero), result.ActiveWindow.EndsAtUtc);
        Assert.True(result.PolicyAfterRequest.CanEnforce);
        Assert.Equal(DesktopNightPhase.OverrideActive, client.CurrentPolicy.ExecutablePolicy!.Phase);
        Assert.Equal(2, transport.Requests.Count);
        using JsonDocument overrideRequest = JsonDocument.Parse(transport.Requests[0]);
        AssertExactEnvelope(overrideRequest.RootElement, "requestOverride", "override");
        Assert.Equal(
            "teamRescue",
            overrideRequest.RootElement.GetProperty("payload").GetProperty("kind").GetString());
        using JsonDocument refreshRequest = JsonDocument.Parse(transport.Requests[1]);
        AssertExactEnvelope(refreshRequest.RootElement, "getPolicy", "refresh");
    }

    [Fact]
    public async Task AcceptedOverride_StillExposesWindowWhenImmediateRefreshFailsOpen()
    {
        ScriptedTransport transport = new(
            _ => Response(OverrideAcceptedResponse("override", "entertainment")),
            _ => Response("{malformed"));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("override", "refresh"));

        DesktopOverrideResult result = await client.RequestOverrideAsync(
            new(DesktopOverrideKind.Entertainment));

        Assert.True(result.Accepted);
        Assert.Equal(DesktopOverrideKind.Entertainment, result.ActiveWindow!.Kind);
        Assert.False(result.PolicyAfterRequest.CanEnforce);
        Assert.Null(client.CurrentPolicy.ExecutablePolicy);
    }

    public static TheoryData<DesktopOverrideRequest, string, int> InvalidAcceptedOverrideDurations => new()
    {
        { new(DesktopOverrideKind.TeamRescue), "teamRescue", 19 },
        { new(DesktopOverrideKind.TeamRescue), "teamRescue", 21 },
        {
            new(DesktopOverrideKind.Emergency, DesktopEmergencyReason.Health),
            "emergency",
            29
        },
        {
            new(DesktopOverrideKind.Emergency, DesktopEmergencyReason.Health),
            "emergency",
            31
        },
        { new(DesktopOverrideKind.Entertainment), "entertainment", 19 },
        { new(DesktopOverrideKind.Entertainment), "entertainment", 21 },
    };

    [Theory]
    [MemberData(nameof(InvalidAcceptedOverrideDurations))]
    public async Task AcceptedOverrideWithWrongDuration_FailsOpenWithoutRefreshing(
        DesktopOverrideRequest request,
        string responseKind,
        int durationMinutes)
    {
        ScriptedTransport transport = new(
            _ => Response(PolicyResponse("initial")),
            _ => Response(OverrideAcceptedResponse("override", responseKind, durationMinutes)));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("initial", "override"));
        Assert.True((await client.GetPolicyAsync()).CanEnforce);

        DesktopOverrideResult result = await client.RequestOverrideAsync(request);

        Assert.False(result.Accepted);
        Assert.Null(result.ActiveWindow);
        Assert.False(result.PolicyAfterRequest.CanEnforce);
        Assert.Null(client.CurrentPolicy.ExecutablePolicy);
        Assert.Equal(2, transport.Requests.Count);
    }

    public static TheoryData<DesktopOverrideRequest, string, int> ValidAcceptedOverrideDurations => new()
    {
        { new(DesktopOverrideKind.TeamRescue), "teamRescue", 20 },
        {
            new(DesktopOverrideKind.Emergency, DesktopEmergencyReason.Health),
            "emergency",
            30
        },
        { new(DesktopOverrideKind.Entertainment), "entertainment", 20 },
    };

    [Theory]
    [MemberData(nameof(ValidAcceptedOverrideDurations))]
    public async Task AcceptedOverrideWithExactDuration_ExposesWindowButFailedRefreshStaysOpen(
        DesktopOverrideRequest request,
        string responseKind,
        int durationMinutes)
    {
        ScriptedTransport transport = new(
            _ => Response(OverrideAcceptedResponse("override", responseKind, durationMinutes)),
            _ => Response("{malformed"));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("override", "refresh"));

        DesktopOverrideResult result = await client.RequestOverrideAsync(request);

        Assert.True(result.Accepted);
        Assert.Equal(request.Kind, result.ActiveWindow!.Kind);
        Assert.Equal(
            TimeSpan.FromMinutes(durationMinutes),
            result.ActiveWindow.EndsAtUtc - result.ActiveWindow.StartsAtUtc);
        Assert.False(result.PolicyAfterRequest.CanEnforce);
        Assert.Null(client.CurrentPolicy.ExecutablePolicy);
        Assert.Equal(2, transport.Requests.Count);
    }

    public static TheoryData<DesktopOverrideRequest, string> MismatchedAcceptedOverrideKinds => new()
    {
        { new(DesktopOverrideKind.TeamRescue), "emergency" },
        { new(DesktopOverrideKind.Entertainment), "teamRescue" },
        {
            new(DesktopOverrideKind.Emergency, DesktopEmergencyReason.Health),
            "entertainment"
        },
    };

    [Theory]
    [MemberData(nameof(MismatchedAcceptedOverrideKinds))]
    public async Task AcceptedOverrideKindMismatch_FailsOpenWithoutExposingWindow(
        DesktopOverrideRequest request,
        string responseKind)
    {
        ScriptedTransport transport = new(
            _ => Response(PolicyResponse("initial")),
            _ => Response(OverrideAcceptedResponse("override", responseKind)));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("initial", "override"));
        Assert.True((await client.GetPolicyAsync()).CanEnforce);

        DesktopOverrideResult result = await client.RequestOverrideAsync(request);

        Assert.False(result.Accepted);
        Assert.Null(result.ActiveWindow);
        Assert.False(result.PolicyAfterRequest.CanEnforce);
        Assert.Null(client.CurrentPolicy.ExecutablePolicy);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("TeamRescue")]
    [InlineData("1")]
    public async Task OverrideResponse_RejectsUnknownNoncanonicalOrNumericKind(string token)
    {
        string kindJson = token == "1" ? token : $"\"{token}\"";
        string response = OverrideAcceptedResponse("override", "teamRescue")
            .Replace("\"teamRescue\"", kindJson, StringComparison.Ordinal);
        ScriptedTransport transport = new(_ => Response(response));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("override"));

        DesktopOverrideResult result = await client.RequestOverrideAsync(
            new(DesktopOverrideKind.TeamRescue));

        Assert.False(result.Accepted);
        Assert.Null(result.ActiveWindow);
        Assert.False(client.CurrentPolicy.CanEnforce);
    }

    [Fact]
    public async Task InvalidOverrideReasonCombination_IsRejectedBeforeTransport()
    {
        ScriptedTransport transport = new(_ => Response(OverrideRejectedResponse("unused")));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("unused"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.RequestOverrideAsync(new(DesktopOverrideKind.Emergency)));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.RequestOverrideAsync(new(
                DesktopOverrideKind.TeamRescue,
                DesktopEmergencyReason.Health)));

        Assert.Empty(transport.Requests);
    }

    public static TheoryData<DesktopOverrideRequest, string, string?> OverrideRequests => new()
    {
        { new(DesktopOverrideKind.TeamRescue), "teamRescue", null },
        { new(DesktopOverrideKind.Entertainment), "entertainment", null },
        { new(DesktopOverrideKind.Emergency, DesktopEmergencyReason.Health), "emergency", "health" },
        { new(DesktopOverrideKind.Emergency, DesktopEmergencyReason.Safety), "emergency", "safety" },
        { new(DesktopOverrideKind.Emergency, DesktopEmergencyReason.UrgentWork), "emergency", "urgentWork" },
    };

    [Theory]
    [MemberData(nameof(OverrideRequests))]
    public async Task OverrideRequest_WritesOnlyCanonicalTypedPayload(
        DesktopOverrideRequest request,
        string kind,
        string? reason)
    {
        ScriptedTransport transport = new(_ => Response(OverrideRejectedResponse("override")));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("override"));

        DesktopOverrideResult result = await client.RequestOverrideAsync(request);

        Assert.False(result.Accepted);
        using JsonDocument json = JsonDocument.Parse(transport.Requests.Single());
        JsonElement payload = json.RootElement.GetProperty("payload");
        Assert.Equal(kind, payload.GetProperty("kind").GetString());
        if (reason is null)
        {
            Assert.False(payload.TryGetProperty("emergencyReason", out _));
        }
        else
        {
            Assert.Equal(reason, payload.GetProperty("emergencyReason").GetString());
        }
    }

    [Theory]
    [InlineData(PrivacySafeEventKind.MissedLock, "missedLock")]
    [InlineData(PrivacySafeEventKind.WorkstationLocked, "workstationLocked")]
    [InlineData(PrivacySafeEventKind.LateNewEntertainment, "lateNewEntertainment")]
    [InlineData(PrivacySafeEventKind.DeliberateBypass, "deliberateBypass")]
    public async Task RecordEvent_AllowsOnlyPrivacySafeEventKinds(
        PrivacySafeEventKind kind,
        string token)
    {
        ScriptedTransport transport = new(_ => Response(RecordResponse("event")));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("event"));

        DesktopRecordEventResult result = await client.RecordEventAsync(kind);

        Assert.True(result.Recorded);
        using JsonDocument json = JsonDocument.Parse(transport.Requests.Single());
        AssertExactEnvelope(json.RootElement, "recordEvent", "event");
        JsonElement payload = json.RootElement.GetProperty("payload");
        Assert.Equal(["kind"], payload.EnumerateObject().Select(property => property.Name));
        Assert.Equal(token, payload.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task RecordEvent_RejectsUndefinedKindBeforeTransport()
    {
        ScriptedTransport transport = new(_ => Response(RecordResponse("unused")));
        NightGateDesktopClient client = new(transport, new FixedRequestIdSource("unused"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await client.RecordEventAsync((PrivacySafeEventKind)999));

        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task EndDesktopSession_AcceptedResponseIsStrictAndIdempotent()
    {
        ScriptedTransport transport = new(_ => Response(EndDesktopSessionResponse("end")));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("end"),
            DesktopSessionId);

        DesktopEndSessionResult first = await client.EndDesktopSessionAsync();
        DesktopEndSessionResult second = await client.EndDesktopSessionAsync();

        Assert.True(first.Accepted);
        Assert.Null(first.Error);
        Assert.Equal(first, second);
        using JsonDocument request = JsonDocument.Parse(Assert.Single(transport.Requests));
        AssertExactEnvelope(request.RootElement, "endDesktopSession", "end");
        JsonElement payload = request.RootElement.GetProperty("payload");
        Assert.Equal(["sessionId"], payload.EnumerateObject().Select(property => property.Name));
        Assert.Equal(DesktopSessionId, payload.GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("wrongType")]
    [InlineData("extraField")]
    [InlineData("acceptedWithError")]
    public async Task EndDesktopSession_FailureIsFailOpenAndDoesNotBecomeSticky(string failure)
    {
        string response = failure switch
        {
            "wrongType" => EndDesktopSessionResponse("first").Replace(
                "endDesktopSessionResult",
                "getDesktopPolicyResult",
                StringComparison.Ordinal),
            "extraField" => EndDesktopSessionResponse("first").Replace(
                "\"accepted\":true",
                "\"accepted\":true,\"extra\":true",
                StringComparison.Ordinal),
            "acceptedWithError" => EndDesktopSessionResponse("first").Replace(
                "\"accepted\":true",
                "\"accepted\":true,\"error\":\"unexpected\"",
                StringComparison.Ordinal),
            _ => EndDesktopSessionResponse("first"),
        };
        ScriptedTransport transport = new(
            _ => failure == "transport"
                ? throw new IOException("offline")
                : Response(response),
            _ => Response(EndDesktopSessionResponse("retry")));
        NightGateDesktopClient client = new(
            transport,
            new FixedRequestIdSource("first", "retry"),
            DesktopSessionId);

        DesktopEndSessionResult failed = await client.EndDesktopSessionAsync();
        DesktopEndSessionResult retried = await client.EndDesktopSessionAsync();

        Assert.False(failed.Accepted);
        Assert.Equal("service-unavailable", failed.Error);
        Assert.True(retried.Accepted);
        Assert.Equal(2, transport.Requests.Count);
    }

    private static void AssertExactEnvelope(JsonElement root, string type, string requestId)
    {
        Assert.Equal(
            ["version", "type", "requestId", "payload"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(type, root.GetProperty("type").GetString());
        Assert.Equal(requestId, root.GetProperty("requestId").GetString());
    }

    private static string PolicyResponse(string requestId, string phaseJson = "\"grace\"")
    {
        string response =
            """
            {"version":1,"type":"getPolicyResult","requestId":"__REQUEST_ID__","payload":{"status":"success","data":{"enforcementEnabled":true,"isDegraded":false,"degradationCode":null,"policy":{"revision":1783382760000,"evaluatedAt":"2026-07-07T00:06:00+00:00","phase":__PHASE__,"window":{"nightDate":"2026-07-06","protectedStart":"2026-07-06T21:00:00+00:00","lastStart":"2026-07-07T00:05:00+00:00","lock":"2026-07-07T00:40:00+00:00","lightsOut":"2026-07-07T01:00:00+00:00","wake":"2026-07-07T09:00:00+00:00"},"appRules":[{"id":"game-primary","rootExecutablePath":"C:\\Games\\game.exe","helperExecutablePaths":[],"category":"game","sessionMinutes":35,"isConfigured":true}],"siteRules":[{"domain":"video.example"}],"enforcementEnabled":true,"isDegraded":false,"activeOverride":null}}}}
            """
            .Replace("__REQUEST_ID__", requestId, StringComparison.Ordinal)
            .Replace("__PHASE__", phaseJson, StringComparison.Ordinal);
        JsonObject? activeOverride = phaseJson switch
        {
            "\"coolingOff\"" => ActiveOverride("entertainment", new JsonArray()),
            "\"overrideActive\"" => ActiveOverride(
                "teamRescue",
                new JsonArray("game-primary")),
            _ => null,
        };
        return activeOverride is null
            ? response
            : response.Replace(
                "\"activeOverride\":null",
                $"\"activeOverride\":{activeOverride.ToJsonString()}",
                StringComparison.Ordinal);
    }

    private static string OverrideAcceptedResponse(
        string requestId,
        string kind,
        int durationMinutes = 20)
    {
        string endsAtUtc = new DateTimeOffset(
                2026,
                7,
                7,
                0,
                0,
                0,
                TimeSpan.Zero)
            .AddMinutes(durationMinutes)
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        return
            """
            {"version":1,"type":"requestOverrideResult","requestId":"__REQUEST_ID__","payload":{"status":"success","data":{"accepted":true,"kind":"__KIND__","startsAtUtc":"2026-07-07T00:00:00+00:00","endsAtUtc":"__ENDS_AT__"}}}
            """
            .Replace("__REQUEST_ID__", requestId, StringComparison.Ordinal)
            .Replace("__KIND__", kind, StringComparison.Ordinal)
            .Replace("__ENDS_AT__", endsAtUtc, StringComparison.Ordinal);
    }

    private static string OverrideRejectedResponse(string requestId) =>
        """
        {"version":1,"type":"requestOverrideResult","requestId":"__REQUEST_ID__","payload":{"status":"success","data":{"accepted":false,"error":"cooldown"}}}
        """
        .Replace("__REQUEST_ID__", requestId, StringComparison.Ordinal);

    private static string RecordResponse(string requestId) =>
        """
        {"version":1,"type":"recordEventResult","requestId":"__REQUEST_ID__","payload":{"status":"success","data":{"recorded":true}}}
        """
        .Replace("__REQUEST_ID__", requestId, StringComparison.Ordinal);

    private static string EndDesktopSessionResponse(string requestId) =>
        """
        {"version":1,"type":"endDesktopSessionResult","requestId":"__REQUEST_ID__","payload":{"status":"success","data":{"accepted":true}}}
        """
        .Replace("__REQUEST_ID__", requestId, StringComparison.Ordinal);

    private static string MutatePolicyResponse(
        string requestId,
        Action<JsonObject, JsonObject, JsonObject> mutation)
    {
        JsonObject root = JsonNode.Parse(PolicyResponse(requestId))!.AsObject();
        JsonObject data = root["payload"]!["data"]!.AsObject();
        JsonObject policy = data["policy"]!.AsObject();
        mutation(root, data, policy);
        return root.ToJsonString();
    }

    private static JsonObject ActiveOverride(string kind, JsonArray allowedIdentifiers) => new()
    {
        ["kind"] = kind,
        ["requestedAtUtc"] = "2026-07-07T00:00:00+00:00",
        ["startsAtUtc"] = kind == "entertainment"
            ? "2026-07-07T00:10:00+00:00"
            : "2026-07-07T00:00:00+00:00",
        ["endsAtUtc"] = kind == "emergency"
            ? "2026-07-07T00:30:00+00:00"
            : kind == "entertainment"
                ? "2026-07-07T00:30:00+00:00"
                : "2026-07-07T00:20:00+00:00",
        ["allowedProcessIdentifiers"] = allowedIdentifiers,
    };

    private static void SetActiveOverride(
        JsonObject policy,
        string kind,
        JsonArray allowedIdentifiers)
    {
        policy["activeOverride"] = ActiveOverride(kind, allowedIdentifiers);
        policy["phase"] = kind == "entertainment" ? "coolingOff" : "overrideActive";
    }

    private static void SetOverrideTimes(
        JsonObject policy,
        string requestedAt,
        string startsAt,
        string endsAt)
    {
        JsonObject activeOverride = policy["activeOverride"]!.AsObject();
        activeOverride["requestedAtUtc"] = $"2026-07-07T{requestedAt}:00+00:00";
        activeOverride["startsAtUtc"] = $"2026-07-07T{startsAt}:00+00:00";
        activeOverride["endsAtUtc"] = $"2026-07-07T{endsAt}:00+00:00";
    }

    private static void SetWindow(
        JsonObject window,
        string nightDate,
        string protectedStart,
        string lastStart,
        string @lock,
        string lightsOut,
        string wake)
    {
        window["nightDate"] = nightDate;
        window["protectedStart"] = protectedStart;
        window["lastStart"] = lastStart;
        window["lock"] = @lock;
        window["lightsOut"] = lightsOut;
        window["wake"] = wake;
    }

    private static ReadOnlyMemory<byte> Response(string json) => Encoding.UTF8.GetBytes(json);

    private sealed class FixedRequestIdSource(params string[] requestIds) : IProtocolRequestIdSource
    {
        private readonly Queue<string> _requestIds = new(requestIds);

        public string NextRequestId() => _requestIds.Dequeue();
    }

    private sealed class ScriptedTransport(
        params Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>[] steps) : INightGatePipeTransport
    {
        private readonly Queue<Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>> _steps =
            new(steps);

        public List<ReadOnlyMemory<byte>> Requests { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(requestUtf8.ToArray());
            return ValueTask.FromResult(_steps.Dequeue()(requestUtf8));
        }
    }

    private sealed class OutOfOrderTransport(ReadOnlyMemory<byte> secondResponse) :
        INightGatePipeTransport
    {
        private readonly TaskCompletionSource<ReadOnlyMemory<byte>> _firstResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstRequestStarted.TrySetResult();
                return new(_firstResponse.Task.WaitAsync(cancellationToken));
            }

            return ValueTask.FromResult(secondResponse);
        }

        public void CompleteFirst(ReadOnlyMemory<byte> response) =>
            _firstResponse.TrySetResult(response);
    }
}
