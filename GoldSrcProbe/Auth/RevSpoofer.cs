namespace GoldSrcProbe.Auth;

/// <summary>Port of kohtep/MultiEmulator RevSpoofer — adjusts HWID suffix for RevEmu2013 ticket.</summary>
internal static class RevSpoofer
{
    private const string Dictionary = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static uint Hash(string str)
    {
        uint hash = 0x4E67C6A7;
        foreach (var ch in str)
        {
            if (ch == 0)
                break;
            hash ^= (hash >> 2) + (uint)ch + (32 * hash);
        }

        return hash;
    }

    public static bool Spoof(Span<char> dest, uint targetHash)
    {
        var len = dest.Length;
        var start = Math.Max(0, len - 7);
        dest[start] = '\0';

        var prev = Hash(dest[..start].ToString());
        return ScanNext(dest, start, prev, targetHash, len);
    }

    private static bool ScanNext(Span<char> dest, int index, uint prevHash, uint targetHash, int len)
    {
        foreach (var ch in Dictionary)
        {
            var h = prevHash ^ ((prevHash >> 2) + (prevHash << 5) + ch);
            var ok = index + 1 < len - 3
                ? ScanNext(dest, index + 1, h, targetHash, len)
                : ScanLast3(dest, h, targetHash, len);

            if (ok)
            {
                dest[index] = ch;
                return true;
            }
        }

        return false;
    }

    private static bool ScanLast3(Span<char> dest, uint prevHash, uint targetHash, int len)
    {
        foreach (var c1 in Dictionary)
        {
            var h1 = prevHash ^ ((prevHash >> 2) + (prevHash << 5) + c1);
            var hh = h1 ^ ((h1 >> 2) + (h1 << 5));
            hh ^= (hh >> 2) + (hh << 5);
            if (((hh ^ targetHash) >> (8 + 5 + 3)) != 0)
                continue;

            foreach (var c2 in Dictionary)
            {
                var h2 = h1 ^ ((h1 >> 2) + (h1 << 5) + c2);
                hh = h2 ^ ((h2 >> 2) + (h2 << 5));
                if (((hh ^ targetHash) >> 8) != 0)
                    continue;

                foreach (var c3 in Dictionary)
                {
                    var h3 = h2 ^ ((h2 >> 2) + (h2 << 5) + c3);
                    if (h3 == targetHash)
                    {
                        dest[len - 3] = c1;
                        dest[len - 2] = c2;
                        dest[len - 1] = c3;
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
