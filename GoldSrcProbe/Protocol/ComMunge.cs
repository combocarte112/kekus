namespace GoldSrcProbe.Protocol;

/// <summary>GoldSource COM_Munge / COM_Munge2 (ReHLDS).</summary>
internal static class ComMunge
{
    private static readonly byte[] Table1 =
    [
        0x7A, 0x64, 0x05, 0xF1,
        0x1B, 0x9B, 0xA0, 0xB5,
        0xCA, 0xED, 0x61, 0x0D,
        0x4A, 0xDF, 0x8E, 0xC7
    ];

    private static readonly byte[] Table2 =
    [
        0x05, 0x61, 0x7A, 0xED,
        0x1B, 0xCA, 0x0D, 0x9B,
        0x4A, 0xF1, 0x64, 0xC7,
        0xB5, 0x8E, 0xDF, 0xA0
    ];

    /// <summary>COM_Munge v1 — consistency uses full <paramref name="sequence"/> (spawncount), not low byte.</summary>
    public static void Munge(Span<byte> data, int sequence) =>
        MungeCore(data, sequence, Table1, truncateSeq: false);

    public static void UnMunge(Span<byte> data, int sequence) =>
        UnMungeCore(data, sequence, Table1, truncateSeq: false);

    public static void Munge2(Span<byte> data, int sequence) =>
        MungeCore(data, sequence, Table2, truncateSeq: true);

    public static void UnMunge2(Span<byte> data, int sequence) =>
        UnMungeCore(data, sequence, Table2, truncateSeq: true);

    private static void MungeCore(Span<byte> data, int sequence, ReadOnlySpan<byte> table, bool truncateSeq)
    {
        var seq = truncateSeq ? (byte)sequence : sequence;
        var len = data.Length & ~3;
        var count = len / 4;
        Span<byte> bytes = stackalloc byte[4];

        for (var i = 0; i < count; i++)
        {
            var offset = i * 4;
            var c = BitConverter.ToInt32(data.Slice(offset, 4));
            c ^= ~seq;
            c = (int)BinaryPrimitivesExt.Swap32((uint)c);

            BitConverter.TryWriteBytes(bytes, c);
            for (var j = 0; j < 4; j++)
                bytes[j] ^= (byte)(0xA5 | (j << j) | j | table[(i + j) & 0x0F]);

            c = BitConverter.ToInt32(bytes);
            c ^= seq;
            BitConverter.TryWriteBytes(data.Slice(offset, 4), c);
        }
    }

    private static void UnMungeCore(Span<byte> data, int sequence, ReadOnlySpan<byte> table, bool truncateSeq)
    {
        var seq = truncateSeq ? (byte)sequence : sequence;
        var len = data.Length & ~3;
        var count = len / 4;
        Span<byte> bytes = stackalloc byte[4];

        for (var i = 0; i < count; i++)
        {
            var offset = i * 4;
            var c = BitConverter.ToInt32(data.Slice(offset, 4));
            c ^= seq;

            BitConverter.TryWriteBytes(bytes, c);
            for (var j = 0; j < 4; j++)
                bytes[j] ^= (byte)(0xA5 | (j << j) | j | table[(i + j) & 0x0F]);

            c = BitConverter.ToInt32(bytes);
            c = (int)BinaryPrimitivesExt.Swap32((uint)c);
            c ^= ~seq;
            BitConverter.TryWriteBytes(data.Slice(offset, 4), c);
        }
    }

    public static void UnMunge3(Span<byte> data, int sequence)
    {
        ReadOnlySpan<byte> table =
        [
            0x20, 0x07, 0x13, 0x61,
            0x03, 0x45, 0x17, 0x72,
            0x0A, 0x2D, 0x48, 0x0C,
            0x4A, 0x12, 0xA9, 0xB5
        ];

        var seq = (byte)sequence;
        var len = data.Length & ~3;
        var count = len / 4;
        Span<byte> bytes = stackalloc byte[4];

        for (var i = 0; i < count; i++)
        {
            var offset = i * 4;
            var c = BitConverter.ToInt32(data.Slice(offset, 4));
            c ^= seq;

            BitConverter.TryWriteBytes(bytes, c);
            for (var j = 0; j < 4; j++)
                bytes[j] ^= (byte)(0xA5 | (j << j) | j | table[(i + j) & 0x0F]);

            c = BitConverter.ToInt32(bytes);
            c = (int)BinaryPrimitivesExt.Swap32((uint)c);
            c ^= ~seq;
            BitConverter.TryWriteBytes(data.Slice(offset, 4), c);
        }
    }
}
