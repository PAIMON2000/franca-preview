using System.IO;
using System.Text;

namespace Franca.Services.UePak;

// UE FString 编解码：正长度 = UTF-8 字节 + 终止符；负长度 = UTF-16LE 码元数（长度含终止符）
internal static class FStringCodec
{
    public static string Read(BinaryReader r)
    {
        var len = r.ReadInt32();
        if (len > 0)
        {
            var bytes = r.ReadBytes(len - 1);
            r.ReadByte(); // 终止符
            return Encoding.UTF8.GetString(bytes);
        }
        if (len < 0)
        {
            var chars = new char[-len - 1];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)r.ReadUInt16();
            r.ReadUInt16(); // 终止符
            return new string(chars);
        }
        return ""; // UE 也允许零长度表示空字符串
    }

    public static void Write(BinaryWriter w, string s)
    {
        if (s.All(c => c <= 0x7F))
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write(bytes.Length + 1);
            w.Write(bytes);
            w.Write((byte)0);
        }
        else
        {
            w.Write(-(s.Length + 1));
            foreach (var c in s)
                w.Write((ushort)c);
            w.Write((ushort)0);
        }
    }
}
