using System.Security.Cryptography;

namespace GoldSrcProbe.Protocol;

internal static class GameFileHasher
{
    private static readonly string[] SearchRoots = ["cstrike", "valve", ""];

    public static bool TryResolvePath(string? gamePath, string resourceName, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(gamePath))
            return false;

        foreach (var root in SearchRoots)
        {
            path = string.IsNullOrEmpty(root)
                ? Path.Combine(gamePath, resourceName.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(gamePath, root, resourceName.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(path))
                return true;
        }

        if (IsSound(resourceName))
        {
            path = Path.Combine(gamePath, "sound", resourceName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return true;
        }

        path = string.Empty;
        return false;
    }

    public static bool TryHashFile(string path, Span<byte> dest)
    {
        if (!File.Exists(path) || dest.Length < 16)
            return false;

        try
        {
            MD5.HashData(File.ReadAllBytes(path)).CopyTo(dest);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryHashFilePrefix(string path, out uint hash)
    {
        Span<byte> md5 = stackalloc byte[16];
        if (!TryHashFile(path, md5))
        {
            hash = 0;
            return false;
        }

        hash = BitConverter.ToUInt32(md5);
        return true;
    }

    public static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b != 0)
                return false;
        }

        return true;
    }

    private static bool IsSound(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 && name.Length - dot == 4 &&
               name.AsSpan(dot + 1).Equals("wav", StringComparison.OrdinalIgnoreCase);
    }
}
