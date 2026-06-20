namespace GoldSrcProbe.Protocol;

internal static class MessageSkipper
{
    public static int SkipDeltaDescription(byte[] data, int offset)
    {
        offset = SkipString(data, offset);
        if (offset + 2 > data.Length)
            return data.Length;

        var fieldCount = BitConverter.ToUInt16(data, offset);
        offset += 2;

        var reader = new BitReader(data.AsSpan(offset));
        for (var i = 0; i < fieldCount; i++)
        {
            _ = reader.ReadBits(1);
            _ = reader.ReadBitString();
            _ = reader.ReadBits(16);
            _ = reader.ReadBits(1);
            _ = reader.ReadBits(16);
            _ = reader.ReadBits(1);
            _ = reader.ReadBits(16);
            _ = reader.ReadBits(1);
            _ = reader.ReadBits(16);
            _ = reader.ReadBits(1);
            _ = reader.ReadBits(16);
        }

        reader.SkipToByteBoundary();
        return offset + reader.BitIndex / 8 + ((reader.BitIndex & 7) != 0 ? 1 : 0);
    }

    public static int SkipString(byte[] data, int offset)
    {
        while (offset < data.Length && data[offset] != 0)
            offset++;
        if (offset < data.Length)
            offset++;
        return offset;
    }
}
