namespace NightGate.Desktop.Tests;

public sealed class InstalledStateProbeValidationTests
{
    private const string RequestId = "request-123";
    private const string ExpectedVersion = "0.1.5";

    [Fact]
    public void ValidResponse_ProducesOnlyMinimalHealthEvidence()
    {
        string response = Response(
            requestId: RequestId,
            status: "success",
            chromeStatus: "healthy",
            isHealthy: true,
            extensionVersion: ExpectedVersion,
            extraData: "\"rules\":{\"activeAppRules\":[{\"rootExecutablePath\":\"C:\\\\Private\\\\game.exe\"}]},");

        InstalledStateProbe.ProbeValidatedResponse validated =
            InstalledStateProbe.ValidateResponse(
                response,
                RequestId,
                ExpectedVersion);
        string output = InstalledStateProbe.FormatSuccess(
            "PC\\User",
            "S-1-5-21-1",
            validated);

        Assert.Contains("responseType=getUserStateResult", output, StringComparison.Ordinal);
        Assert.Contains("chromeStatus=healthy", output, StringComparison.Ordinal);
        Assert.Contains("extensionVersion=0.1.5", output, StringComparison.Ordinal);
        Assert.DoesNotContain("rootExecutablePath", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Private", output, StringComparison.Ordinal);
        Assert.DoesNotContain("response=", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong-request", "success", "healthy", true, "0.1.5")]
    [InlineData("request-123", "error", "healthy", true, "0.1.5")]
    [InlineData("request-123", "success", "protectionDegraded", false, "0.1.5")]
    [InlineData("request-123", "success", "healthy", true, "0.1.3")]
    public void InvalidOrStaleResponse_IsRejected(
        string requestId,
        string status,
        string chromeStatus,
        bool isHealthy,
        string extensionVersion)
    {
        string response = Response(
            requestId,
            status,
            chromeStatus,
            isHealthy,
            extensionVersion);

        Assert.Throws<InvalidDataException>(() => InstalledStateProbe.ValidateResponse(
            response,
            RequestId,
            ExpectedVersion));
    }

    private static string Response(
        string requestId,
        string status,
        string chromeStatus,
        bool isHealthy,
        string extensionVersion,
        string extraData = "") => $$"""
        {
          "version": 1,
          "type": "getUserStateResult",
          "requestId": "{{requestId}}",
          "payload": {
            "status": "{{status}}",
            "data": {
              {{extraData}}
              "chromeProtection": {
                "status": "{{chromeStatus}}",
                "isHealthy": {{isHealthy.ToString().ToLowerInvariant()}},
                "lastHeartbeatAtUtc": "2026-07-19T11:00:40.2721398+00:00",
                "extensionVersion": "{{extensionVersion}}"
              }
            }
          }
        }
        """;
}
