using System.Text;

namespace Franca.Services;

// xxHash64（公开算法参考实现，无依赖）：lab/004 包格式的查找键哈希
public static class XxHash64
{
    private const ulong P1 = 11400714785074694791UL;
    private const ulong P2 = 14029467366897019727UL;
    private const ulong P3 = 1609587929392839161UL;
    private const ulong P4 = 9650029242287828579UL;
    private const ulong P5 = 2870177450012600261UL;

    public static string HashHex(string text) =>
        Hash(Encoding.UTF8.GetBytes(text)).ToString("x16");

    public static ulong Hash(byte[] data, ulong seed = 0)
    {
        var length = data.Length;
        var index = 0;
        ulong hash;

        if (length >= 32)
        {
            ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            while (index + 32 <= length)
            {
                v1 = Round(v1, ReadUInt64(data, index));
                v2 = Round(v2, ReadUInt64(data, index + 8));
                v3 = Round(v3, ReadUInt64(data, index + 16));
                v4 = Round(v4, ReadUInt64(data, index + 24));
                index += 32;
            }
            hash = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
            hash = MergeRound(hash, v1);
            hash = MergeRound(hash, v2);
            hash = MergeRound(hash, v3);
            hash = MergeRound(hash, v4);
        }
        else
        {
            hash = seed + P5;
        }
        hash += (ulong)length;

        while (index + 8 <= length)
        {
            hash ^= Round(0, ReadUInt64(data, index));
            hash = Rotl(hash, 27) * P1 + P4;
            index += 8;
        }
        if (index + 4 <= length)
        {
            hash ^= (ulong)ReadUInt32(data, index) * P1;
            hash = Rotl(hash, 23) * P2 + P3;
            index += 4;
        }
        while (index < length)
        {
            hash ^= data[index] * P5;
            hash = Rotl(hash, 11) * P1;
            index++;
        }

        hash ^= hash >> 33;
        hash *= P2;
        hash ^= hash >> 29;
        hash *= P3;
        hash ^= hash >> 32;
        return hash;
    }

    private static ulong Round(ulong acc, ulong input) => Rotl(acc + input * P2, 31) * P1;

    private static ulong MergeRound(ulong acc, ulong value)
    {
        value = Round(0, value);
        return (acc ^ value) * P1 + P4;
    }

    private static ulong Rotl(ulong value, int bits) => (value << bits) | (value >> (64 - bits));

    private static ulong ReadUInt64(byte[] data, int offset) =>
        BitConverter.ToUInt64(data, offset); // xxHash 规定小端，x86/x64 直接读取即小端

    private static uint ReadUInt32(byte[] data, int offset) =>
        BitConverter.ToUInt32(data, offset);
}
