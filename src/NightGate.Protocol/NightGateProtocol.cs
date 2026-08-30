namespace NightGate.Protocol;

public static class NightGateProtocol
{
    public const int Version = 1;
    public const int MaximumBodyBytes = 65_536;
    public const int LengthPrefixBytes = sizeof(int);
    public const int MaximumRequestIdCharacters = 64;

    public static bool IsValidRequestId(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)
            || requestId.Length > MaximumRequestIdCharacters)
        {
            return false;
        }

        foreach (char character in requestId)
        {
            if (character is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
