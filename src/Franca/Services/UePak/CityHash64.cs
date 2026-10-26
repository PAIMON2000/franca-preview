// Portions adapted from Google CityHash, Copyright (c) 2011 Google, Inc.
// MIT License: see LICENSES/CityHash.txt. C# port and UE string wrapper modified by Franca.
using System.Buffers.Binary;
using System.Text;

namespace Franca.Services.UePak;

// CityHash64（google/cityhash，MIT 风格公开算法参考实现）：locres v3 命名空间/key/源文哈希。
// 移植自 city.cc 1.1 的 CityHash64 无种子变体（k0/k1/k2 常数、HashLen0to16/17to32/33to64、
// weak_hash_len_32_with_seeds 与长输入主循环）。
public static class CityHash64
{
    private const ulong K0 = 0xC3A5C85C97CB3127UL;
    private const ulong K1 = 0xB492B66FBE98F273UL;
    private const ulong K2 = 0x9AE16A3B2F90404FUL;
    private const ulong KMul = 0x9DDFEA08EB382D69UL;

    public static ulong Hash(byte[] s) => Hash(s, 0, s.Length);

    public static ulong Hash(byte[] s, int offset, int len)
    {
        if (len <= 16) return HashLen0to16(s, offset, len);
        if (len <= 32) return HashLen17to32(s, offset, len);
        if (len <= 64) return HashLen33to64(s, offset, len);

        var x = Fetch64(s, offset + len - 40);
        var y = Fetch64(s, offset + len - 16) + Fetch64(s, offset + len - 56);
        var z = HashLen16(Fetch64(s, offset + len - 48) + (ulong)len, Fetch64(s, offset + len - 24));
        var v = WeakHashLen32WithSeeds(s, offset + len - 64, (ulong)len, z);
        var w = WeakHashLen32WithSeeds(s, offset + len - 32, y + K1, x);
        x = x * K1 + Fetch64(s, offset);

        var pos = offset;
        var remaining = (len - 1) & ~63;
        do
        {
            x = Rotate(x + y + v.Item1 + Fetch64(s, pos + 8), 37) * K1;
            y = Rotate(y + v.Item2 + Fetch64(s, pos + 48), 42) * K1;
            x ^= w.Item2;
            y += v.Item1 + Fetch64(s, pos + 40);
            z = Rotate(z + w.Item1, 33) * K1;
            v = WeakHashLen32WithSeeds(s, pos, v.Item2 * K1, x + w.Item1);
            w = WeakHashLen32WithSeeds(s, pos + 32, z + w.Item2, y + Fetch64(s, pos + 16));
            (z, x) = (x, z);
            pos += 64;
            remaining -= 64;
        } while (remaining != 0);

        return HashLen16(HashLen16(v.Item1, w.Item1) + ShiftMix(y) * K1 + z,
            HashLen16(v.Item2, w.Item2) + x);
    }

    public static uint StringHash32(string s)
    {
        if (s.Length == 0) return 0; // FTextKey's empty identity
        var bytes = new byte[s.Length * 2];
        for (var i = 0; i < s.Length; i++)
        {
            bytes[i * 2] = (byte)s[i];
            bytes[i * 2 + 1] = (byte)(s[i] >> 8);
        }
        var hash = Hash(bytes);
        return unchecked((uint)hash + (uint)(hash >> 32) * 23);
    }

    private static ulong Fetch32(byte[] s, int p) =>
        (uint)(s[p] | (s[p + 1] << 8) | (s[p + 2] << 16) | (s[p + 3] << 24));

    private static ulong Fetch64(byte[] s, int p) =>
        Fetch32(s, p) | ((ulong)Fetch32(s, p + 4) << 32);

    private static ulong Rotate(ulong val, int shift) =>
        shift == 0 ? val : (val >> shift) | (val << (64 - shift));

    private static ulong ShiftMix(ulong val) => val ^ (val >> 47);

    private static ulong HashLen16(ulong u, ulong v) => HashLen16(u, v, KMul);

    private static ulong HashLen16(ulong u, ulong v, ulong mul)
    {
        var a = (u ^ v) * mul;
        a ^= a >> 47;
        var b = (v ^ a) * mul;
        b ^= b >> 47;
        b *= mul;
        return b;
    }

    private static ulong HashLen0to16(byte[] s, int p, int len)
    {
        if (len >= 8)
        {
            var mul = K2 + (ulong)len * 2;
            var a = Fetch64(s, p) + K2;
            var b = Fetch64(s, p + len - 8);
            var c = Rotate(b, 37) * mul + a;
            var d = (Rotate(a, 25) + b) * mul;
            return HashLen16(c, d, mul);
        }
        if (len >= 4)
        {
            var mul = K2 + (ulong)len * 2;
            var a = Fetch32(s, p);
            return HashLen16((ulong)len + (a << 3), Fetch32(s, p + len - 4), mul);
        }
        if (len > 0)
        {
            var a = s[p];
            var b = s[p + (len >> 1)];
            var c = s[p + len - 1];
            var y = (ulong)a + ((ulong)b << 8);
            var z = (ulong)len + ((ulong)c << 2);
            return ShiftMix(y * K2 ^ z * K0) * K2;
        }
        return K2;
    }

    private static ulong HashLen17to32(byte[] s, int p, int len)
    {
        var mul = K2 + (ulong)len * 2;
        var a = Fetch64(s, p) * K1;
        var b = Fetch64(s, p + 8);
        var c = Fetch64(s, p + len - 8) * mul;
        var d = Fetch64(s, p + len - 16) * K2;
        return HashLen16(Rotate(a + b, 43) + Rotate(c, 30) + d,
            a + Rotate(b + K2, 18) + c, mul);
    }

    private static ulong HashLen33to64(byte[] s, int p, int len)
    {
        var mul = K2 + (ulong)len * 2;
        var a = Fetch64(s, p) * K2;
        var b = Fetch64(s, p + 8);
        var c = Fetch64(s, p + len - 24);
        var d = Fetch64(s, p + len - 32);
        var e = Fetch64(s, p + 16) * K2;
        var f = Fetch64(s, p + 24) * 9;
        var g = Fetch64(s, p + len - 8);
        var h = Fetch64(s, p + len - 16) * mul;
        var u = Rotate(a + g, 43) + (Rotate(b, 30) + c) * 9;
        var v = ((a + g) ^ d) + f + 1;
        var w = BinaryPrimitives.ReverseEndianness((u + v) * mul) + h;
        var x = Rotate(e + f, 42) + c;
        var y = (BinaryPrimitives.ReverseEndianness((v + w) * mul) + g) * mul;
        var z = e + f + c;
        a = BinaryPrimitives.ReverseEndianness((x + z) * mul + y) + b;
        b = ShiftMix((z + a) * mul + d + h) * mul;
        return b + x;
    }

    private static (ulong, ulong) WeakHashLen32WithSeeds(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rotate(b + a + z, 21);
        var c = a;
        a += x;
        a += y;
        b += Rotate(a, 44);
        return (a + z, b + c);
    }

    private static (ulong, ulong) WeakHashLen32WithSeeds(byte[] s, int p, ulong a, ulong b) =>
        WeakHashLen32WithSeeds(Fetch64(s, p), Fetch64(s, p + 8),
            Fetch64(s, p + 16), Fetch64(s, p + 24), a, b);
}

// CRC-32（IEEE 802.3，查表法）：locres v2 命名空间/key/源文哈希
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Hash(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    public static uint StringHash(string s)
    {
        // UE StrCrc32 consumes four little-endian bytes per UTF-16 code unit, without NUL.
        var bytes = new byte[s.Length * 4];
        for (var i = 0; i < s.Length; i++)
        {
            bytes[i * 4] = (byte)s[i];
            bytes[i * 4 + 1] = (byte)(s[i] >> 8);
        }
        return Hash(bytes);
    }
}
