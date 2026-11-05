using System.Runtime.InteropServices;

namespace Franca.Services.UePak;

// Oodle 解压：加载本机已有的 oo2core_*.dll（游戏自带，例如 FromSoftware 作品随包分发）。
// 不随 Franca 分发任何 Oodle 二进制；找不到 dll 时相关条目明确报错。
public static class Oodle
{
    private delegate int DecompressDelegate(byte[] src, long srcLen, byte[] dst, long dstLen,
        int fuzz, int crc, int verbose, IntPtr decBufBase, long decBufSize,
        IntPtr cb, IntPtr cbCtx, IntPtr scratch, long scratchSize, int threadPhase);

    private static DecompressDelegate? _decompress;

    // 供 PakFileReader 在没有实例级解压器时使用；仅 CLI 显式 TryLoad 后非空。
    public static Func<byte[], byte[], int>? DefaultDecompressor { get; private set; }

    public static bool TryLoad(string dllPath)
    {
        if (_decompress != null) return true;
        var lib = LoadLibraryW(dllPath);
        if (lib == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                $"LoadLibrary 失败（进程 {(Environment.Is64BitProcess ? "x64" : "x86")}）");
        var proc = GetProcAddress(lib, "OodleLZ_Decompress");
        if (proc == IntPtr.Zero)
            throw new EntryPointNotFoundException("OodleLZ_Decompress 导出缺失（不是有效的 oo2core dll？）");
        _decompress = Marshal.GetDelegateForFunctionPointer<DecompressDelegate>(proc);
        DefaultDecompressor = Decompress;
        return true;
    }

    public static bool IsLoaded => _decompress != null;

    // 返回解压字节数；<= 0 表示失败。
    public static int Decompress(byte[] src, byte[] dst) =>
        _decompress?.Invoke(src, src.Length, dst, dst.Length, 0, 0, 0,
            IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0)
        ?? throw new NotSupportedException("Oodle 未加载");

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
