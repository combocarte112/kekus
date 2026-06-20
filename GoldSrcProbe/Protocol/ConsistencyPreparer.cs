using System.IO.Compression;
using System.Net;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace GoldSrcProbe.Protocol;

/// <summary>Fetch consistency-checked files from FastDL (like real CS client) before clc_fileconsistency.</summary>
internal static class ConsistencyPreparer
{
    private const int MaxParallel = 8;
    private const int HttpTimeoutMs = 2500;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMilliseconds(HttpTimeoutMs)
    };

    static ConsistencyPreparer()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Valve/Steam HTTP Client 1.0");
    }

    public static void Prepare(
        IList<ServerResource> resources,
        string? downloadUrl,
        string? gamePath,
        IPEndPoint? server)
    {
        var cacheRoot = Path.Combine(Directory.GetCurrentDirectory(), "output", "consistency_cache");
        Directory.CreateDirectory(cacheRoot);

        var serverKey = server is null ? "unknown" : $"{server.Address}:{server.Port}";
        var serverCache = Path.Combine(cacheRoot, serverKey.Replace(':', '_'));
        Directory.CreateDirectory(serverCache);

        var bases = BuildDownloadBases(downloadUrl);
        var hashTargets = resources.Where(r => r.NeedConsistency && IsReservedEmpty(r)).ToList();
        var needHttp = hashTargets.Where(r => GameFileHasher.IsAllZero(r.Md5) && !HasHashOnDisk(r, gamePath, serverCache)).ToList();

        if (needHttp.Count == 0)
            return;

        if (bases.Count == 0 || string.IsNullOrWhiteSpace(downloadUrl))
        {
            Console.WriteLine($"  [net] {needHttp.Count} fisiere lipsa — server fara sv_downloadurl (clientul CS le ia prin UDP in-game; botul inca nu)");
            return;
        }

        Console.WriteLine($"  [net] fastdl: {downloadUrl} — descarc {needHttp.Count} fisiere...");

        var ok = 0;
        var gate = new object();
        Parallel.ForEach(needHttp, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel }, res =>
        {
            var cached = CachePath(serverCache, res.Name);
            if (TryDownloadTo(bases, res.Name, cached))
            {
                GameFileHasher.TryHashFile(cached, res.Md5);
                lock (gate)
                {
                    ok++;
                    Console.WriteLine($"  [net] fastdl OK: {res.Name}");
                }
            }
        });

        if (ok < needHttp.Count)
            Console.WriteLine($"  [net] fastdl: {ok}/{needHttp.Count} fisiere descarcate");
    }

    private static bool HasHashOnDisk(ServerResource res, string? gamePath, string serverCache)
    {
        if (GameFileHasher.TryResolvePath(gamePath, res.Name, out var local) &&
            GameFileHasher.TryHashFile(local, res.Md5))
            return true;

        var cached = CachePath(serverCache, res.Name);
        return GameFileHasher.TryHashFile(cached, res.Md5);
    }

    private static string CachePath(string serverCache, string resourceName) =>
        Path.Combine(serverCache, resourceName.Replace('/', Path.DirectorySeparatorChar));

    private static List<string> BuildDownloadBases(string? downloadUrl)
    {
        var bases = new List<string>();
        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            var u = downloadUrl.Trim().TrimEnd('/');
            bases.Add(u);
            if (u.EndsWith("/cstrike", StringComparison.OrdinalIgnoreCase))
                bases.Add(u[..^"/cstrike".Length].TrimEnd('/'));
        }

        return bases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryDownloadTo(IReadOnlyList<string> bases, string resourceName, string destPath)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var rel = resourceName.Replace('\\', '/');
        var relPaths = new[] { $"cstrike/{rel}", rel, $"valve/{rel}" };

        foreach (var b in bases)
        {
            foreach (var p in relPaths)
            {
                // GoldSrc FastDL: .gz then plain (same order as hl.exe)
                if (TryDownloadUrl($"{b}/{p}.gz", destPath, CompressionKind.Gzip))
                    return true;
                if (TryDownloadUrl($"{b}/{p}", destPath, CompressionKind.None))
                    return true;
                if (TryDownloadUrl($"{b}/{p}.bz2", destPath, CompressionKind.Bzip2))
                    return true;
            }
        }

        return false;
    }

    private enum CompressionKind { None, Gzip, Bzip2 }

    private static bool TryDownloadUrl(string url, string destPath, CompressionKind compression)
    {
        try
        {
            using var response = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return false;

            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes.Length == 0)
                return false;

            bytes = compression switch
            {
                CompressionKind.Gzip => DecompressGzip(bytes),
                CompressionKind.Bzip2 => DecompressBzip2(bytes),
                _ => bytes
            };

            File.WriteAllBytes(destPath, bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecompressGzip(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecompressBzip2(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        using var decompress = new BZip2Stream(input, SharpCompress.Compressors.CompressionMode.Decompress, false);
        decompress.CopyTo(output);
        return output.ToArray();
    }

    private static bool IsReservedEmpty(ServerResource r)
    {
        foreach (var b in r.Reserved)
        {
            if (b != 0)
                return false;
        }

        return true;
    }
}
