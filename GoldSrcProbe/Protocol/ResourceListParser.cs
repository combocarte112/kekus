namespace GoldSrcProbe.Protocol;

/// <summary>
/// Parses svc_resourcelist bitstream (ReHLDS SV_SendResources_internal).
/// </summary>
internal static class ResourceListParser
{
    private const byte ResCustom = 4;
    private const byte TDecal = 3;

    public static (List<ServerResource> Resources, bool ConsistencyRequired, int WireConsistencyCount) Parse(
        byte[] data,
        int offset,
        string? gamePath = null)
    {
        if (offset >= data.Length)
            return ([], false, 0);

        var span = data.AsSpan(offset);
        var heuristic = TryParse(span, null, gamePath);
        if (heuristic is not null)
            return heuristic.Value;

        var candidates = CollectMd5Candidates(span);
        if (candidates.Count == 0)
            return ([], false, 0);

        var limit = Math.Min(candidates.Count, 12);
        var masks = 1 << limit;
        var maxIndex = candidates[^1] + 1;
        for (var mask = 1; mask < masks; mask++)
        {
            var md5Mask = new bool[maxIndex];
            for (var b = 0; b < limit; b++)
            {
                if ((mask & (1 << b)) != 0)
                    md5Mask[candidates[b]] = true;
            }

            var attempt = TryParse(span, md5Mask, gamePath);
            if (attempt is not null)
                return attempt.Value;
        }

        return ([], false, 0);
    }

    private static List<int> CollectMd5Candidates(ReadOnlySpan<byte> span)
    {
        var candidates = new List<int>();
        var reader = new BitReader(span);
        var count = (int)reader.ReadBits(12);

        for (var i = 0; i < count; i++)
        {
            var type = (byte)reader.ReadBits(4);
            var name = reader.ReadBitString();
            reader.ReadBits(12);
            var downloadSize = reader.ReadBits(24);
            reader.ReadBits(3);

            if (downloadSize > 0 && (type == TDecal ||
                name.EndsWith(".spr", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)))
                candidates.Add(i);

            SkipResourceTail(reader, ShouldReadWireMd5Candidate(type, name, downloadSize));
        }

        return candidates;
    }

    private static (List<ServerResource> Resources, bool ConsistencyRequired, int WireConsistencyCount)? TryParse(
        ReadOnlySpan<byte> span,
        bool[]? md5Mask,
        string? gamePath)
    {
        var reader = new BitReader(span);
        var resources = new List<ServerResource>();

        if (reader.BitIndex + 12 > span.Length * 8)
            return null;

        var count = (int)reader.ReadBits(12);
        if (count <= 0 || count > 4096)
            return null;

        for (var i = 0; i < count; i++)
        {
            if (reader.BitIndex + 4 + 8 > span.Length * 8)
                return null;

            var type = (byte)reader.ReadBits(4);
            var name = reader.ReadBitString();
            var index = reader.ReadBits(12);
            var downloadSize = reader.ReadBits(24);
            var wireFlags = (byte)reader.ReadBits(3);

            var res = new ServerResource
            {
                Name = name,
                ResourceType = type,
                ResourceIndex = index,
                DownloadSize = downloadSize,
                Flags = wireFlags
            };

            var readMd5 = md5Mask is not null && i < md5Mask.Length && md5Mask[i]
                || md5Mask is null && ShouldReadWireMd5Decal(type, name, downloadSize);

            if (readMd5)
            {
                if (reader.BitIndex + 128 > span.Length * 8)
                    return null;
                reader.ReadBytes(res.Md5);
                res.Flags |= ResCustom;
            }

            if (!reader.ReadBit())
            {
                resources.Add(res);
                continue;
            }

            if (reader.BitIndex + 256 > span.Length * 8)
                return null;
            reader.ReadBytes(res.Reserved);
            resources.Add(res);
        }

        var consistencyRequired = false;
        var wireConsistencyCount = 0;
        if (reader.ReadBit())
        {
            consistencyRequired = true;
            uint lastIndex = 0;
            while (reader.ReadBit())
            {
                wireConsistencyCount++;
                if (reader.ReadBit())
                    lastIndex += reader.ReadBits(5);
                else
                    lastIndex = reader.ReadBits(10);

                if (lastIndex >= resources.Count)
                    return null;

                var r = resources[(int)lastIndex];
                if (!r.NeedConsistency)
                {
                    r.NeedConsistency = true;
                    ComputeLocalMd5(r, gamePath);
                }
            }
        }

        var marked = resources.Count(r => r.NeedConsistency);
        if (consistencyRequired && wireConsistencyCount != marked)
            return null;

        if (!IsAlignedToMessageEnd(reader, span))
            return null;

        return (resources, consistencyRequired, wireConsistencyCount);
    }

    private static bool ShouldReadWireMd5Decal(byte type, string name, uint downloadSize)
    {
        if (type != TDecal || downloadSize == 0)
            return false;

        if (name.Equals("tempdecal.wad", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.StartsWith('!'))
            return true;

        return name.Contains('/');
    }

    private static bool ShouldReadWireMd5Candidate(byte type, string name, uint downloadSize)
    {
        if (ShouldReadWireMd5Decal(type, name, downloadSize))
            return true;

        return downloadSize > 0 && (
            name.EndsWith(".spr", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAlignedToMessageEnd(BitReader reader, ReadOnlySpan<byte> span)
    {
        var maxBits = span.Length * 8;
        if (reader.BitIndex > maxBits)
            return false;

        var remainder = reader.BitIndex & 7;
        if (remainder == 0)
            return reader.BitIndex == maxBits;

        for (var i = 0; i < 8 - remainder; i++)
        {
            if (reader.BitIndex >= maxBits)
                break;
            if (reader.ReadBit())
                return false;
        }

        return reader.BitIndex == maxBits;
    }

    private static void SkipResourceTail(BitReader reader, bool readMd5)
    {
        if (readMd5)
            reader.ReadBytes(new byte[16]);
        if (reader.ReadBit())
            reader.ReadBytes(new byte[32]);
    }

    private static void ComputeLocalMd5(ServerResource res, string? gamePath)
    {
        if (!GameFileHasher.IsAllZero(res.Md5))
            return;

        if (GameFileHasher.TryResolvePath(gamePath, res.Name, out var path))
            GameFileHasher.TryHashFile(path, res.Md5);
    }

    private static bool IsAllZero(byte[] data) => GameFileHasher.IsAllZero(data);
}
