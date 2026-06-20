namespace GoldSrcProbe.Auth;

/// <summary>OldRevEmu ticket (ReUnion CA_OLDREVEMU). Port of MultiEmulator OldRevEmu.h</summary>
public static class OldRevEmuTicket
{
    public static byte[] Generate(int steamId = 0)
    {
        if (steamId == 0)
            steamId = Random.Shared.Next(1, 0x3FFFFFFF);

        var ticket = new byte[10];
        WriteInt32(ticket, 0, 0xFFFF);
        WriteInt32(ticket, 4, unchecked((int)((steamId ^ 0xC9710266) << 1)));
        ticket[8] = 0;
        ticket[9] = 0;
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
