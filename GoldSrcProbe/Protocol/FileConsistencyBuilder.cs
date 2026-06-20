using System.Security.Cryptography;



namespace GoldSrcProbe.Protocol;



internal static class FileConsistencyBuilder

{

    private static readonly string[] SearchRoots = ["cstrike", "valve", ""];



    public static byte[] Build(

        IReadOnlyList<ServerResource> resources,

        uint spawnCount,

        string? gamePath,

        string? serverCacheDir = null)

    {

        var writer = new BitWriter();



        for (var i = 0; i < resources.Count; i++)

        {

            var res = resources[i];

            if (!res.NeedConsistency)

                continue;



            writer.WriteBit(true);

            writer.WriteBits((uint)i, 12);



            if (GameFileHasher.IsAllZero(res.Reserved))

            {

                var hash = ResolveHash(res, gamePath, serverCacheDir);

                writer.WriteBits(hash, 32);

            }

            else

            {

                var bounds = (byte[])res.Reserved.Clone();

                ComMunge.UnMunge(bounds, (int)spawnCount);

                writer.WriteBytes(bounds.AsSpan(1, 12));

                writer.WriteBytes(bounds.AsSpan(13, 12));

            }

        }



        writer.WriteBit(false);



        var bitData = writer.ToArray();

        ComMunge.Munge(bitData, (int)spawnCount);



        var packet = new byte[3 + bitData.Length];

        packet[0] = ProtocolConstants.ClcFileConsistency;

        BitConverter.TryWriteBytes(packet.AsSpan(1, 2), (ushort)bitData.Length);

        bitData.CopyTo(packet, 3);

        return packet;

    }



    private static uint ResolveHash(ServerResource res, string? gamePath, string? serverCacheDir)
    {
        if (!string.IsNullOrWhiteSpace(serverCacheDir))
        {
            var cached = Path.Combine(serverCacheDir, res.Name.Replace('/', Path.DirectorySeparatorChar));
            if (GameFileHasher.TryHashFilePrefix(cached, out var cachedHash))
                return cachedHash;
        }

        if (GameFileHasher.TryResolvePath(gamePath, res.Name, out var local) &&
            GameFileHasher.TryHashFilePrefix(local, out var localHash))
            return localHash;

        if (!GameFileHasher.IsAllZero(res.Md5))
            return BitConverter.ToUInt32(res.Md5, 0);

        if (string.IsNullOrWhiteSpace(gamePath))
            Console.WriteLine($"  [net] consistency missing file: {res.Name} (CsGamePath not set in config.json)");
        else
            Console.WriteLine($"  [net] consistency missing file: {res.Name} (not in {gamePath})");

        return 0;
    }

}


