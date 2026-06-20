namespace GoldSrcProbe.Auth;

/// <summary>AVSMP ticket (ReUnion CA_AVSMP). Port of MultiEmulator AVSMP.h</summary>
public static class AvsmpTicket
{
    public static byte[] Generate(int steamId = 0, bool universe = true)
    {
        if (steamId == 0)
            steamId = Random.Shared.Next(1, 0x3FFFFFFF);

        var ticket = new byte[28];
        WriteInt32(ticket, 0, 0x14);
        WriteInt32(ticket, 12, (steamId << 1) | (universe ? 1 : 0));
        WriteInt32(ticket, 16, unchecked((int)0x01100001));
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
