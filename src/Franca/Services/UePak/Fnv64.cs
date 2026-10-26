namespace Franca.Services.UePak;

// FNV-1a 64 位（UE pak v11）：挂载点相对路径的 lower-case UTF-16LE；初值为 offset basis + seed。
public static class Fnv64
{
    private const ulong Prime = 0x100000001B3UL;

    public static ulong HashPath(string path, ulong seed)
    {
        ulong h = unchecked(0xcbf29ce484222325UL + seed);
        foreach (var c in path.ToLowerInvariant())
        {
            h ^= (byte)c;
            h *= Prime;
            h ^= (byte)(c >> 8);
            h *= Prime;
        }
        return h;
    }
}
