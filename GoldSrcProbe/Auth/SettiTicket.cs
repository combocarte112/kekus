namespace GoldSrcProbe.Auth;

/// <summary>Setti scanner ticket (legacy DProto). Port of MultiEmulator Setti.h</summary>
public static class SettiTicket
{
    public static byte[] Generate()
    {
        var ticket = new byte[768];
        WriteInt32(ticket, 0, unchecked((int)0xD4CA7F7B));
        WriteInt32(ticket, 4, unchecked((int)0xC7DB6023));
        WriteInt32(ticket, 8, unchecked((int)0x6D6A2E1F));
        WriteInt32(ticket, 20, unchecked((int)0xB4C43105));
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
