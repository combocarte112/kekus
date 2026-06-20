namespace GoldSrcProbe.Models;

public sealed class ServerEndpoint
{
    public required string Host { get; init; }
    public required int Port { get; init; }

    public string Key => $"{Host}:{Port}";

    public const int DefaultGamePort = 27015;

    public static ServerEndpoint Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            throw new FormatException("Empty or comment line");

        var idx = line.LastIndexOf(':');
        if (idx > 0 &&
            int.TryParse(line[(idx + 1)..].Trim(), out var port) &&
            port is >= 1 and <= 65535)
            return new ServerEndpoint { Host = line[..idx].Trim(), Port = port };

        if (System.Net.IPAddress.TryParse(line, out _))
            return new ServerEndpoint { Host = line, Port = DefaultGamePort };

        throw new FormatException($"Invalid server line (use ip:port or ip): {line}");
    }
}

public sealed class A2SPlayer
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
    public float DurationSeconds { get; set; }
}

public sealed class A2SInfo
{
    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    public string GameDir { get; set; } = "";
    public string GameDescription { get; set; } = "";
    public int Players { get; set; }
    public int MaxPlayers { get; set; }
    public int Bots { get; set; }
    public bool Dedicated { get; set; }
    public bool Secure { get; set; }
    public byte Os { get; set; }
    public bool GoldSource { get; set; }
    public string? ReHldsVersion { get; set; }
    public bool LikelyReHlds => !string.IsNullOrEmpty(ReHldsVersion);
}

public sealed class StatusPlayer
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string UniqueId { get; set; } = "";
    public int Frags { get; set; }
    public string Time { get; set; } = "";
    public int Ping { get; set; }
    public int Loss { get; set; }
    public string Address { get; set; } = "";
    public bool IsBot => UniqueId.Equals("BOT", StringComparison.OrdinalIgnoreCase)
        || UniqueId.Contains("BOT", StringComparison.OrdinalIgnoreCase);

    public SteamAuthKind AuthKind { get; set; } = SteamAuthKind.Unknown;
}

public enum PlayerTrust
{
    Real,
    Bot,
    Emulator,
    Probe,
    Suspicious,
    Unknown
}

public enum SteamAuthKind
{
    Unknown,
    LegitSteam,
    Emulator,
    ProbeBot,
    EngineBot,
    Valve,
    Lan,
    Pending,
    Hltv
}

public sealed class AnalyzedPlayer
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SteamId { get; set; } = "";
    public int Ping { get; set; }
    public string Time { get; set; } = "";
    public int Frags { get; set; }
    public int Loss { get; set; }
    public string Address { get; set; } = "";
    public PlayerTrust Trust { get; set; }
    public SteamAuthKind AuthKind { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public sealed class VerifySummary
{
    public int Total { get; set; }
    public int Real { get; set; }
    public int Bots { get; set; }
    public int Emulators { get; set; }
    public int Probes { get; set; }
    public int Suspicious { get; set; }
    public bool LikelyFakeServer { get; set; }
    public List<AnalyzedPlayer> Players { get; set; } = [];
}

public sealed class ServerVerifyResult
{
    public required string Address { get; init; }
    public DateTime VerifiedAtUtc { get; init; } = DateTime.UtcNow;
    public bool Joined { get; set; }
    public int SignonState { get; set; }
    /// <summary>Auth ticket type that succeeded (e.g. RevEmu2013).</summary>
    public string? AuthUsed { get; set; }
    public string? Error { get; set; }
    public string? Reachability { get; set; }
    public int ConnectAttempts { get; set; }
    public VerifySummary? Summary { get; set; }
    public List<StatusPlayer> RawStatus { get; set; } = [];
    /// <summary>Last lines from server console when status parsing fails (debug).</summary>
    public string? ConsoleSnippet { get; set; }
}

public sealed class VerifyReport
{
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public List<ServerVerifyResult> Servers { get; set; } = [];
}

public sealed class ServerProbeResult
{
    public required string Address { get; init; }
    public DateTime ProbedAtUtc { get; init; } = DateTime.UtcNow;
    public bool Online { get; set; }
    public string? Error { get; set; }

    public A2SInfo? A2S { get; set; }
    public List<A2SPlayer> A2SPlayers { get; set; } = [];
    public int A2SPlayerCount => A2SPlayers.Count;

    public bool ConnectOk { get; set; }
    public string? ConnectError { get; set; }
    /// <summary>open | a2s-shield | no-response</summary>
    public string? ConnectReachability { get; set; }
    public int? SignonState { get; set; }
    public List<StatusPlayer> StatusPlayers { get; set; } = [];
    public int StatusPlayerCount => StatusPlayers.Count;
    public int StatusBotCount => StatusPlayers.Count(p => p.IsBot);
    public int StatusHumanCount => StatusPlayerCount - StatusBotCount;

    /// <summary>A2S INFO count minus A2S PLAYER list length (query-side gap).</summary>
    public int? A2SListGap =>
        A2S is null ? null : Math.Max(0, A2S.Players - A2SPlayerCount);

    /// <summary>Only when connect+status succeeded: abs(A2S players - status lines).</summary>
    public int? PlayerCountMismatch =>
        ConnectOk && A2S is not null ? Math.Abs(A2S.Players - StatusPlayerCount) : null;

    /// <summary>Heuristic: verified connect shows far fewer players than A2S advertises.</summary>
    public bool? FakePlayerSuspect =>
        ConnectOk && A2S is not null && PlayerCountMismatch is int d && d >= 3;
}

public sealed class ProbeReport
{
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public string ProbeVersion { get; init; } = "1.0";
    public List<ServerProbeResult> Servers { get; set; } = [];

    public int ConnectVerifiedCount => Servers.Count(s => s.ConnectOk);
    public int FakePlayerSuspectCount => Servers.Count(s => s.FakePlayerSuspect == true);
}
