namespace GoldSrcProbe.Protocol;

public enum ConnectReachability
{
    /// <summary>Real HLDS/ReHLDS challenge (0x41 'A').</summary>
    Open,
    /// <summary>Responds but with A2S/info packet instead of challenge — DDoS shield.</summary>
    A2SShield,
    /// <summary>No UDP response to getchallenge.</summary>
    NoResponse
}

public readonly record struct ConnectProbeResult(
    ConnectReachability Reachability,
    string? Challenge,
    byte? FakeResponseType,
    string Summary)
{
    public bool CanJoin => Reachability == ConnectReachability.Open;
}

internal static class ConnectProbe
{
    public static ConnectProbeResult Analyze(byte[]? packet)
    {
        if (packet is null || packet.Length < 5)
            return new ConnectProbeResult(
                ConnectReachability.NoResponse,
                null,
                null,
                "no getchallenge response");

        if (BitConverter.ToInt32(packet, 0) != -1)
            return new ConnectProbeResult(
                ConnectReachability.NoResponse,
                null,
                null,
                "invalid OOB header");

        var type = packet[4];
        if (type == ProtocolConstants.S2CChallenge)
        {
            var text = System.Text.Encoding.ASCII.GetString(packet, 5, packet.Length - 5).Trim();
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var challenge = parts.Length >= 2 ? parts[1] : parts.Length == 1 ? parts[0] : null;
            if (!string.IsNullOrEmpty(challenge))
                return new ConnectProbeResult(
                    ConnectReachability.Open,
                    challenge,
                    null,
                    "ReHLDS/HLDS challenge OK");

            return new ConnectProbeResult(
                ConnectReachability.A2SShield,
                null,
                type,
                "malformed challenge packet");
        }

        // Proxy/shield returns A2S INFO (m/I) or other OOB instead of 'A'
        var typeName = type switch
        {
            ProtocolConstants.S2A_INFO => "GoldSource info (m)",
            ProtocolConstants.S2A_INFO_DETAILED => "Source info (I)",
            ProtocolConstants.S2A_PLAYER => "player list",
            _ => $"0x{type:X2}"
        };

        return new ConnectProbeResult(
            ConnectReachability.A2SShield,
            null,
            type,
            $"A2S shield — got {typeName}, not challenge (A)");
    }
}
