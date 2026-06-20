using System.Security.Cryptography;
using System.Text;

namespace GoldSrcProbe.Auth;

/// <summary>SteamClient 2009 ticket (ReUnion CA_SC2009). Port of MultiEmulator SC2009.h</summary>
public static class Sc2009Ticket
{
    private const uint RevSignature = ('r' << 16) | ('e' << 8) | 'v';
    private static readonly byte[] AesKeyRand = Encoding.ASCII.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUV");
    private static readonly byte[] AesKeyRev = Encoding.ASCII.GetBytes("_YOU_SERIOUSLY_NEED_TO_GET_LAID_");

    public static byte[] Generate(int steamId = 0)
    {
        Span<char> hwid = stackalloc char[32];
        var hwidStr = RevEmuTicket.CreateRandomHwid(32);
        hwidStr.AsSpan().CopyTo(hwid);
        var revHash = RevSpoofer.Hash(hwid.ToString());
        if (steamId != 0)
            RevSpoofer.Spoof(hwid, (uint)steamId);

        var ticket = new byte[178];
        WriteInt32(ticket, 0, 'S');
        WriteInt32(ticket, 4, (int)revHash);
        WriteInt32(ticket, 8, (int)RevSignature);
        WriteInt32(ticket, 12, 0);
        WriteInt32(ticket, 16, (int)(revHash << 1));
        WriteInt32(ticket, 20, unchecked((int)0x01100001));

        var hwidBytes = Encoding.ASCII.GetBytes(hwid.ToString());
        EncryptAesBlock(hwidBytes, AesKeyRand).CopyTo(ticket, 24);
        EncryptAesBlock(AesKeyRand, AesKeyRev).CopyTo(ticket, 56);
        SHA256.HashData(hwidBytes).CopyTo(ticket, 88);

        return ticket;
    }

    private static byte[] EncryptAesBlock(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var block = new byte[32];
        input.CopyTo(block);
        return aes.EncryptEcb(block, PaddingMode.None);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
