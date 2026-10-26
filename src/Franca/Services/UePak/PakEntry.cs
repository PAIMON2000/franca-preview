namespace Franca.Services.UePak;

// pak v11 索引/数据区共用的单条目记录；Blocks 偏移相对条目起始（数据区头之后）
public sealed class PakEntry
{
    public string Path { get; set; } = "";
    public long Offset { get; set; }
    public long CompressedSize { get; set; }
    public long UncompressedSize { get; set; }
    public uint CompressionMethod { get; set; }
    public List<(long Start, long End)> Blocks { get; } = new();
    public byte[] Hash { get; set; } = new byte[20];
    public uint CompressionBlockSize { get; set; }
    public bool Encrypted { get; set; }
}
