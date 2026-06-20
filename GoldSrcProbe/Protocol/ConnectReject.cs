namespace GoldSrcProbe.Protocol;

internal static class ConnectReject
{
    public static string Normalize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        return message.TrimStart('\0', '9').Trim();
    }

    public static bool IsServerFull(string? message) =>
        Normalize(message).Contains("full", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reject where trying other auth tickets cannot help.</summary>
    public static bool IsDefinitive(string? message)
    {
        var m = Normalize(message);
        if (m.Length == 0)
            return false;

        if (IsServerFull(m))
            return true;

        if (m.Contains("bad challenge", StringComparison.OrdinalIgnoreCase))
            return false;

        // ReUnion auth failure for one emulator — other tickets may still work in auto bypass
        if (IsAuthReject(m))
            return false;

        ReadOnlySpan<string> patterns =
        [
            "banned",
            "password",
            "invalid cd",
            "invalid password",
            "kicked",
            "you have been",
        ];

        foreach (var p in patterns)
        {
            if (m.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>ReUnion / DProto rejected this auth ticket — try next emulator.</summary>
    public static bool IsAuthReject(string? message)
    {
        var m = Normalize(message);
        if (m.Length == 0)
            return false;

        ReadOnlySpan<string> patterns =
        [
            "steam validation",
            "no-steam",
            "not allowed on this server",
            "invalid ticket",
            "invalid auth",
            "auth rejected",
            "cd key",
        ];

        foreach (var p in patterns)
        {
            if (m.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Safe to wait and retry once (slot may open, transient timeout).</summary>
    public static bool IsRetriable(string? message)
    {
        if (IsServerFull(message))
            return true;

        var m = Normalize(message);
        if (m.Length == 0)
            return true;

        return m.Equals("No S2C_CONNECTION", StringComparison.OrdinalIgnoreCase);
    }

    public static string ReachabilityFor(string? message)
    {
        if (IsServerFull(message))
            return "full";

        var m = Normalize(message);
        if (m.Contains("banned", StringComparison.OrdinalIgnoreCase))
            return "banned";

        if (m.Length == 0)
            return "no-response";

        return "rejected";
    }
}
