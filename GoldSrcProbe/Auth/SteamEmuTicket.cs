namespace GoldSrcProbe.Auth;

/// <summary>SteamEmu ticket (ReUnion CA_STEAMEMU). Port of MultiEmulator SteamEmu.h</summary>
public static class SteamEmuTicket
{
    public static byte[] Generate(int steamId = 0)
    {
        if (steamId == 0)
            steamId = Random.Shared.Next(1, int.MaxValue);

        var ticket = new byte[768];
        WriteInt32(ticket, 80, -1);
        WriteInt32(ticket, 84, steamId);
        return ticket;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
