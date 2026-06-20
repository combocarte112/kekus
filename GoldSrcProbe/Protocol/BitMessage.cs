namespace GoldSrcProbe.Protocol;

internal ref struct BitReader
{
    private ReadOnlySpan<byte> _data;
    private int _bitIndex;

    public BitReader(ReadOnlySpan<byte> data) => _data = data;

    public int BitIndex => _bitIndex;

    public bool ReadBit()
    {
        if (_bitIndex >= _data.Length * 8)
            return false;

        var b = _data[_bitIndex >> 3];
        var bit = (b >> (_bitIndex & 7)) & 1;
        _bitIndex++;
        return bit != 0;
    }

    public uint ReadBits(int count)
    {
        uint value = 0;
        for (var i = 0; i < count; i++)
        {
            value |= (uint)(ReadBit() ? 1 : 0) << i;
        }

        return value;
    }

    public void ReadBytes(Span<byte> dest)
    {
        for (var i = 0; i < dest.Length; i++)
            dest[i] = (byte)ReadBits(8);
    }

    /// <summary>MSG_ReadBitString — 8-bit chars until a zero terminator (not length-prefixed).</summary>
    public string ReadBitString()
    {
        var chars = new List<char>(64);
        while (true)
        {
            var c = (byte)ReadBits(8);
            if (c == 0)
                break;
            chars.Add((char)c);
        }

        return chars.Count == 0 ? string.Empty : new string(chars.ToArray());
    }

    public void SkipToByteBoundary()
    {
        if ((_bitIndex & 7) != 0)
            _bitIndex += 8 - (_bitIndex & 7);
    }
}

internal sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bitIndex;

    public int BitIndex => _bitIndex;

    public void WriteBit(bool value)
    {
        var byteIndex = _bitIndex >> 3;
        var bitOffset = _bitIndex & 7;

        while (_bytes.Count <= byteIndex)
            _bytes.Add(0);

        if (value)
            _bytes[byteIndex] |= (byte)(1 << bitOffset);

        _bitIndex++;
    }

    public void WriteBits(uint value, int count)
    {
        for (var i = 0; i < count; i++)
            WriteBit(((value >> i) & 1) != 0);
    }

    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            WriteBits(b, 8);
    }

    public byte[] ToArray()
    {
        var size = _bitIndex / 8 + ((_bitIndex & 7) != 0 ? 1 : 0);
        return _bytes.Count >= size ? _bytes.GetRange(0, size).ToArray() : _bytes.ToArray();
    }
}
