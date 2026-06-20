using System.Security.Cryptography;
using System.Text;

namespace GoldSrcProbe.Auth;

/// <summary>RevEmu classic ticket (ReUnion CA_REVEMU). Port of kohtep/MultiEmulator RevEmu.h</summary>
public static class RevEmuTicket
{
    private const uint RevSignature = ('r' << 16) | ('e' << 8) | 'v';

    public static byte[] Generate(string? hwid = null)
    {
        hwid ??= CreateRandomHwid(16);
        var hash = RevHash(hwid);

        var ticket = new byte[152];
        WriteInt32(ticket, 0, 'J');
        WriteInt32(ticket, 4, (int)hash);
        WriteInt32(ticket, 8, (int)RevSignature);
        WriteInt32(ticket, 12, 0);
        WriteInt32(ticket, 16, (int)(hash << 1));
        WriteInt32(ticket, 20, unchecked((int)0x01100001));
        Encoding.ASCII.GetBytes(hwid, ticket.AsSpan(24, Math.Min(hwid.Length, 128)));

        return ticket;
    }

    public static uint RevHash(string str)
    {
        uint hash = 0x4E67C6A7;
        foreach (var ch in str)
        {
            if (ch == 0) break;
            hash ^= (hash >> 2) + ch + (32 * hash);
        }
        return hash;
    }

    public static string CreateCdKey()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string CreateRandomHwid(int len)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> rnd = stackalloc byte[len];
        RandomNumberGenerator.Fill(rnd);
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++)
            sb.Append(chars[rnd[i] % chars.Length]);
        return sb.ToString();
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
