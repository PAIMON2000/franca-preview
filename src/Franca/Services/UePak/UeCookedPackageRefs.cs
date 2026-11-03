using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Franca.Services.UePak;

// Cooked UE4 package reference reader (layout verified against Octopath Traveler II,
// UE 4.26-style cooked .uasset: magic, legacy version -7, package version 5).
// Reads the name table and import table only, so it works for any asset kind.
// Font assets reference their FontFace sub-assets through imports; the ordered
// FontFace import list is the composite font's face chain (default + fallbacks).
public sealed class UeCookedPackageRefs
{
    public sealed record Import(string ClassName, string ObjectName)
    {
        // Full "/Game/..." path when this is a Package import (path is the object name).
        public string? PackagePath => ClassName == "Package" && ObjectName.StartsWith("/", StringComparison.Ordinal)
            ? ObjectName : null;
    }
    public sealed record FontAsset(string Name, IReadOnlyList<Import> Imports)
    {
        // Ordered FontFace object names (default typeface first, then fallbacks).
        public IReadOnlyList<string> FontFaces => Imports.Where(im => im.ClassName == "FontFace")
            .Select(im => im.ObjectName).ToArray();
        // Resolve a FontFace import to its "/Game/..." package path when present.
        public string? PathOf(string fontFace)
        {
            var tail = "/" + fontFace;
            return Imports.Where(im => im.ClassName == "Package")
                .Select(im => im.ObjectName).FirstOrDefault(p => p.EndsWith(tail, StringComparison.Ordinal));
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("Unsupported cooked package: " + message); }

    public static IReadOnlyList<string> ReadNames(byte[] data)
    {
        int I32(int p)
        { Require(p >= 0 && p <= data.Length - 4, "integer bounds"); return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p)); }
        string ReadString(ref int p)
        {
            var n = I32(p); p += 4;
            if (n == 0) return "";
            Require(n != int.MinValue, "string size");
            var bytes = Math.Abs((long)n) * (n < 0 ? 2 : 1);
            Require(bytes <= data.Length - p && bytes <= 4_000_000, "string bounds");
            var terminator = n < 0 ? 2 : 1;
            Require(data[p + (int)bytes - 1] == 0, "string terminator");
            var value = (n < 0 ? Encoding.Unicode : new UTF8Encoding(false, true)).GetString(data, p, (int)bytes - terminator);
            p += (int)bytes;
            return value;
        }
        Require(data.Length >= 193, "package too short");
        Require(I32(0) == unchecked((int)0x9e2a83c1) && I32(4) == -7 && I32(20) == 0
            && I32(24) == data.Length && I32(28) == 5 && data.AsSpan(32, 5).SequenceEqual("None\0"u8),
            "package summary");
        var nameCount = I32(41);
        var nameOffset = I32(45);
        var importOffset = I32(69);
        Require(nameCount > 0 && nameCount <= 1_000_000 && nameOffset >= 193 && nameOffset < data.Length, "name table");
        var p = nameOffset;
        var names = new string[nameCount];
        for (var i = 0; i < names.Length; i++) { names[i] = ReadString(ref p); p = checked(p + 4); }
        Require(p == importOffset, "name table extent");
        return names;
    }

    public static FontAsset ReadFontAsset(string name, byte[] data)
    {
        var names = ReadNames(data);
        var importCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(65));
        var importOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(69));
        Require(importCount >= 0 && importCount <= 1_000_000, "import count");
        Require((long)importOffset + (long)importCount * 28 <= data.Length, "import table bounds");
        int I32(int p)
        { Require(p >= 0 && p <= data.Length - 4, "integer bounds"); return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p)); }
        string Name(int at)
        {
            var index = I32(at); var number = I32(at + 4);
            Require(index >= 0 && index < names.Count && number >= 0, "FName");
            return names[index] + (number == 0 ? "" : "_" + (number - 1));
        }
        var imports = new List<Import>(importCount);
        for (var i = 0; i < importCount; i++)
        {
            var at = importOffset + i * 28;
            // FObjectImport: classPackage FName, className FName, outer FPackageIndex, objectName FName
            imports.Add(new Import(ClassName: Name(at + 8), ObjectName: Name(at + 20)));
        }
        return new FontAsset(name, imports);
    }
}
