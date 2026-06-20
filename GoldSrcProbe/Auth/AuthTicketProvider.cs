namespace GoldSrcProbe.Auth;

/// <summary>ReUnion / DProto auth kinds (dp_authkind_e). SXEI/HLTV/Steam need real client.</summary>
public enum AuthEmulatorType
{
    Auto,
    RevEmu2013 = 10,
    RevEmu = 4,
    Sc2009 = 7,
    OldRevEmu = 5,
    SteamEmu = 3,
    Avsmp = 8,
    Setti = 1,
}

public static class AuthTicketProvider
{
    public static byte[] Generate(AuthEmulatorType type, int steamId = 0)
    {
        return type switch
        {
            AuthEmulatorType.RevEmu2013 => RevEmu2013Ticket.Generate(steamId),
            AuthEmulatorType.RevEmu => RevEmuTicket.Generate(),
            AuthEmulatorType.Sc2009 => Sc2009Ticket.Generate(steamId),
            AuthEmulatorType.OldRevEmu => OldRevEmuTicket.Generate(steamId),
            AuthEmulatorType.SteamEmu => SteamEmuTicket.Generate(steamId),
            AuthEmulatorType.Avsmp => AvsmpTicket.Generate(steamId),
            AuthEmulatorType.Setti => SettiTicket.Generate(),
            AuthEmulatorType.Auto => RevEmu2013Ticket.Generate(steamId),
            _ => RevEmu2013Ticket.Generate(steamId)
        };
    }

    public static string Describe(AuthEmulatorType type) => type switch
    {
        AuthEmulatorType.RevEmu2013 => "RevEmu2013",
        AuthEmulatorType.RevEmu => "RevEmu",
        AuthEmulatorType.Sc2009 => "SC2009",
        AuthEmulatorType.OldRevEmu => "OldRevEmu",
        AuthEmulatorType.SteamEmu => "SteamEmu",
        AuthEmulatorType.Avsmp => "AVSMP",
        AuthEmulatorType.Setti => "Setti",
        AuthEmulatorType.Auto => "auto",
        _ => type.ToString()
    };

    /// <summary>Auth bypass order for --auth auto (ReUnion kinds 1-9 emulators).</summary>
    public static IReadOnlyList<AuthEmulatorType> GetBypassSequence() => new[]
    {
        AuthEmulatorType.RevEmu2013,
        AuthEmulatorType.RevEmu,
        AuthEmulatorType.Sc2009,
        AuthEmulatorType.OldRevEmu,
        AuthEmulatorType.SteamEmu,
        AuthEmulatorType.Avsmp,
        AuthEmulatorType.Setti,
    };

    public static AuthEmulatorType Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AuthEmulatorType.Auto;

        return value.Trim().ToLowerInvariant() switch
        {
            "auto" or "bypass" => AuthEmulatorType.Auto,
            "revemu2013" or "2013" or "aes" or "10" => AuthEmulatorType.RevEmu2013,
            "revemu" or "rev" or "classic" or "4" => AuthEmulatorType.RevEmu,
            "sc2009" or "steamclient2009" or "7" => AuthEmulatorType.Sc2009,
            "oldrevemu" or "oldrev" or "5" => AuthEmulatorType.OldRevEmu,
            "steamemu" or "3" => AuthEmulatorType.SteamEmu,
            "avsmp" or "8" => AuthEmulatorType.Avsmp,
            "setti" or "dproto" or "1" => AuthEmulatorType.Setti,
            _ => AuthEmulatorType.Auto
        };
    }
}
