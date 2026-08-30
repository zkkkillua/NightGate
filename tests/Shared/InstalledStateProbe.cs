using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

internal static class InstalledStateProbe
{
    private const int MaximumFrameLength = 1048576;
    private const string DefaultExpectedExtensionVersion = "0.1.5";

#if !NIGHTGATE_PROBE_TEST
    [STAThread]
    private static int Main(string[] args)
    {
        string outputPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "installed-state-probe-result.txt");
        string identity = WindowsIdentity.GetCurrent().Name;
        var userSid = WindowsIdentity.GetCurrent().User;
        string sid = userSid == null ? "unknown" : userSid.Value;
        File.WriteAllText(
            outputPath,
            string.Format("START\r\nidentity={0}\r\nsid={1}\r\n", identity, sid),
            new UTF8Encoding(false));

        try
        {
            if (args == null || args.Length > 1)
            {
                throw new InvalidDataException(
                    "Supply at most one expected extension version.");
            }
            string expectedExtensionVersion = args.Length == 1
                ? args[0]
                : DefaultExpectedExtensionVersion;
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(
                ".",
                "NightGateService",
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation))
            {
                pipe.Connect(2000);
                string requestId = Guid.NewGuid().ToString("N");
                string json =
                    "{\"version\":1,\"type\":\"getUserState\",\"requestId\":\"" +
                    requestId + "\",\"payload\":{}}";
                byte[] request = Encoding.UTF8.GetBytes(json);
                byte[] prefix = BitConverter.GetBytes(request.Length);
                pipe.Write(prefix, 0, prefix.Length);
                pipe.Write(request, 0, request.Length);
                pipe.Flush();

                byte[] responsePrefix = ReadExact(pipe, 4);
                int responseLength = BitConverter.ToInt32(responsePrefix, 0);
                if (responseLength <= 0 || responseLength > MaximumFrameLength)
                {
                    throw new InvalidDataException(
                        "NightGate returned an invalid response frame length.");
                }

                string response = Encoding.UTF8.GetString(
                    ReadExact(pipe, responseLength));
                ProbeValidatedResponse validated = ValidateResponse(
                    response,
                    requestId,
                    expectedExtensionVersion);
                File.WriteAllText(
                    outputPath,
                    FormatSuccess(identity, sid, validated),
                    new UTF8Encoding(false));
                return 0;
            }
        }
        catch (Exception exception)
        {
            File.WriteAllText(
                outputPath,
                string.Format(
                    "FAIL\r\nidentity={0}\r\nsid={1}\r\nerror={2}: {3}\r\n",
                    identity,
                    sid,
                    exception.GetType().Name,
                    exception.Message),
                new UTF8Encoding(false));
            return 1;
        }
    }
#endif

    internal static ProbeValidatedResponse ValidateResponse(
        string response,
        string requestId,
        string expectedExtensionVersion)
    {
        if (string.IsNullOrWhiteSpace(response)
            || string.IsNullOrWhiteSpace(requestId)
            || string.IsNullOrWhiteSpace(expectedExtensionVersion)
            || !Regex.IsMatch(
                expectedExtensionVersion,
                @"^\d+\.\d+\.\d+$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("Probe validation input is invalid.");
        }

        ProbeEnvelope envelope;
        try
        {
            byte[] json = Encoding.UTF8.GetBytes(response);
            using (MemoryStream input = new MemoryStream(json, false))
            {
                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(typeof(ProbeEnvelope));
#if NIGHTGATE_PROBE_TEST
#pragma warning disable CS8600
#endif
                envelope = (ProbeEnvelope)serializer.ReadObject(input);
#if NIGHTGATE_PROBE_TEST
#pragma warning restore CS8600
#endif
            }
        }
        catch (SerializationException exception)
        {
            throw new InvalidDataException(
                "NightGate returned malformed JSON.",
                exception);
        }

        if (envelope == null
            || envelope.Version != 1
            || !string.Equals(
                envelope.Type,
                "getUserStateResult",
                StringComparison.Ordinal)
            || !string.Equals(
                envelope.RequestId,
                requestId,
                StringComparison.Ordinal)
            || envelope.Payload == null
            || !string.Equals(
                envelope.Payload.Status,
                "success",
                StringComparison.Ordinal)
            || envelope.Payload.Data == null
            || envelope.Payload.Data.ChromeProtection == null)
        {
            throw new InvalidDataException(
                "NightGate returned an uncorrelated or unsuccessful user-state response.");
        }

        ProbeChromeProtection health = envelope.Payload.Data.ChromeProtection;
        DateTimeOffset lastHeartbeatAtUtc;
        if (health.IsHealthy != true
            || !string.Equals(health.Status, "healthy", StringComparison.Ordinal)
            || !string.Equals(
                health.ExtensionVersion,
                expectedExtensionVersion,
                StringComparison.Ordinal)
            || !DateTimeOffset.TryParse(
                health.LastHeartbeatAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out lastHeartbeatAtUtc)
            || lastHeartbeatAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Chrome protection is unhealthy, stale, or the wrong version.");
        }

        return new ProbeValidatedResponse(
            envelope.Type,
            health.Status,
            health.ExtensionVersion,
            lastHeartbeatAtUtc);
    }

    internal static string FormatSuccess(
        string identity,
        string sid,
        ProbeValidatedResponse response)
    {
        if (response == null)
        {
            throw new ArgumentNullException("response");
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "PASS\r\nidentity={0}\r\nsid={1}\r\nresponseType={2}\r\n" +
            "chromeStatus={3}\r\nextensionVersion={4}\r\n" +
            "lastHeartbeatAtUtc={5:o}\r\n",
            identity,
            sid,
            response.ResponseType,
            response.ChromeStatus,
            response.ExtensionVersion,
            response.LastHeartbeatAtUtc);
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            IAsyncResult pending = stream.BeginRead(
                buffer,
                offset,
                count - offset,
                null,
                null);
            if (!pending.AsyncWaitHandle.WaitOne(3000))
            {
                pending.AsyncWaitHandle.Close();
                throw new TimeoutException(
                    "NightGate did not return a response frame within 3 seconds.");
            }
            int read = stream.EndRead(pending);
            pending.AsyncWaitHandle.Close();
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    "NightGate closed an incomplete response frame.");
            }
            offset += read;
        }
        return buffer;
    }

    internal sealed class ProbeValidatedResponse
    {
        internal ProbeValidatedResponse(
            string responseType,
            string chromeStatus,
            string extensionVersion,
            DateTimeOffset lastHeartbeatAtUtc)
        {
            ResponseType = responseType;
            ChromeStatus = chromeStatus;
            ExtensionVersion = extensionVersion;
            LastHeartbeatAtUtc = lastHeartbeatAtUtc;
        }

        internal string ResponseType { get; private set; }

        internal string ChromeStatus { get; private set; }

        internal string ExtensionVersion { get; private set; }

        internal DateTimeOffset LastHeartbeatAtUtc { get; private set; }
    }

    [DataContract]
    private sealed class ProbeEnvelope
    {
        public ProbeEnvelope()
        {
            Type = string.Empty;
            RequestId = string.Empty;
            Payload = new ProbePayload();
        }

        [DataMember(Name = "version")]
        public int? Version { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "requestId")]
        public string RequestId { get; set; }

        [DataMember(Name = "payload")]
        public ProbePayload Payload { get; set; }
    }

    [DataContract]
    private sealed class ProbePayload
    {
        public ProbePayload()
        {
            Status = string.Empty;
            Data = new ProbeData();
        }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "data")]
        public ProbeData Data { get; set; }
    }

    [DataContract]
    private sealed class ProbeData
    {
        public ProbeData()
        {
            ChromeProtection = new ProbeChromeProtection();
        }

        [DataMember(Name = "chromeProtection")]
        public ProbeChromeProtection ChromeProtection { get; set; }
    }

    [DataContract]
    private sealed class ProbeChromeProtection
    {
        public ProbeChromeProtection()
        {
            Status = string.Empty;
            LastHeartbeatAtUtc = string.Empty;
            ExtensionVersion = string.Empty;
        }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "isHealthy")]
        public bool? IsHealthy { get; set; }

        [DataMember(Name = "lastHeartbeatAtUtc")]
        public string LastHeartbeatAtUtc { get; set; }

        [DataMember(Name = "extensionVersion")]
        public string ExtensionVersion { get; set; }
    }
}
