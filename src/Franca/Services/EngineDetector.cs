using System.IO;
using Franca.Models;

namespace Franca.Services;

// 引擎特征识别：纯本地文件特征匹配，无网络、无模型。每个引擎一行签名（可多行），
// 按优先级从上到下，命中即返回；单个签名所需路径不可读时跳过该签名继续往下。
// 签名全部来自对真实游戏发行目录的逐一举证；新增引擎 = 在 Signatures 里加一行。
// 识别目的：翻译安装支持 Unity 注入（IsUnity）与东方自研引擎静态路线（Touhou，
// 见 TouhouStaticService），商业引擎标识用于展示与后续适配铺路，
// FromSoftware 自研等无稳定特征者与工具类组件统一落 Unknown（UI 标注"自研或未知"）。
public static class EngineDetector
{
    private static readonly (GameEngine Engine, Func<string, bool> Match)[] Signatures =
    [
        // Unity IL2CPP：GameAssembly.dll + <名>_Data/il2cpp_data/
        (GameEngine.UnityIL2CPP, dir => File.Exists(Path.Combine(dir, "GameAssembly.dll"))
            && Directory.GetDirectories(dir, "*_Data").Any(d => Directory.Exists(Path.Combine(d, "il2cpp_data")))),

        // Unity Mono：<名>_Data/Managed/ 下有任意 dll。
        // 不能只看 Assembly-CSharp.dll——发行商可能剥离它（Potion Craft 只有自有程序集）
        (GameEngine.UnityMono, dir => Directory.GetDirectories(dir, "*_Data")
            .Select(d => Path.Combine(d, "Managed"))
            .Where(Directory.Exists)
            .Any(m => Directory.GetFiles(m, "*.dll").Length > 0)),
        // Unity Mono 兜底：MonoBleedingEdge（Mono 运行时随包分发）
        (GameEngine.UnityMono, dir => Directory.GetDirectories(dir, "*_Data").Length > 0
            && Directory.Exists(Path.Combine(dir, "MonoBleedingEdge"))),

        // Ren'Py：renpy/ 目录或 *.rpa 文件
        (GameEngine.RenPy, dir => Directory.Exists(Path.Combine(dir, "renpy"))
            || Directory.GetFiles(dir, "*.rpa").Length > 0
            || Directory.GetDirectories(dir).Any(d =>
                Directory.Exists(Path.Combine(d, "renpy")) || Directory.GetFiles(d, "*.rpa").Length > 0)),

        // RPG Maker MV/MZ：www/data/*.json + package.json
        (GameEngine.RPGMaker, dir =>
            File.Exists(Path.Combine(dir, "package.json"))
            && Directory.Exists(Path.Combine(dir, "www", "data"))
            && Directory.GetFiles(Path.Combine(dir, "www", "data"), "*.json").Length > 0),
        // RPG Maker 2000/2003/VX/VX Ace：RPG_RT.exe 或 RGSS*.dll
        (GameEngine.RPGMaker, dir =>
            File.Exists(Path.Combine(dir, "RPG_RT.exe")) || Directory.GetFiles(dir, "RGSS*.dll").Length > 0),

        // Unreal Engine：根目录自带 Engine/（Binaries 或 Content），
        // 覆盖目录名与游戏名不一致的布局（P3R 是 P3R/、ARC Raiders 是 PioneerGame/）
        (GameEngine.Unreal, dir =>
        {
            var engineDir = Path.Combine(dir, "Engine");
            return Directory.Exists(engineDir)
                && (Directory.Exists(Path.Combine(engineDir, "Binaries"))
                    || Directory.Exists(Path.Combine(engineDir, "Content")));
        }),
        // Unreal Engine：任意一层子目录的 Binaries/Win64/*-Win64-Shipping.exe
        (GameEngine.Unreal, dir => Directory.GetDirectories(dir)
            .Select(d => Path.Combine(d, "Binaries", "Win64"))
            .Where(Directory.Exists)
            .Any(p => Directory.GetFiles(p, "*-Win64-Shipping.exe").Length > 0)),

        // Clausewitz（PDS：CK3/维多利亚3/钢铁雄心）：clausewitz 目录或分支版本文件
        (GameEngine.Clausewitz, dir =>
            Directory.Exists(Path.Combine(dir, "clausewitz")) || File.Exists(Path.Combine(dir, "clausewitz_branch.txt"))),

        // MonoGame / XNA / FNA：框架 dll，或两层内任意 *.xnb 资源
        (GameEngine.MonoGame, dir => Directory.GetFiles(dir, "*.dll")
                .Any(f => Path.GetFileName(f) is "MonoGame.Framework.dll" or "FNA.dll" or "Microsoft.Xna.Framework.dll")
            || GetFilesWithinDepth(dir, "*.xnb", 2).Count > 0),

        // Godot：*.pck 包，或根目录 godot/ 目录（Turing Complete 的布局）
        (GameEngine.Godot, dir => Directory.GetFiles(dir, "*.pck").Length > 0
            || Directory.Exists(Path.Combine(dir, "godot"))),

        // GameMaker：data.win / game.win / data.unx 资源包
        (GameEngine.GameMaker, dir => Directory.GetFiles(dir).Any(f =>
            Path.GetFileName(f).ToLowerInvariant() is "data.win" or "game.win" or "data.unx")),

        // Source 2（Valve：Deadlock/Dota 2）：game/<mod>/gameinfo.gi
        (GameEngine.Source2, dir => GetFilesWithinDepth(dir, "gameinfo.gi", 2).Count > 0),

        // RE Engine（卡普空）：re_chunk_*.pak 分块包
        (GameEngine.REEngine, dir => Directory.GetFiles(dir, "re_chunk_*.pak").Length > 0),

        // 东方 Project 自研引擎（ZUN/thdat 系）：发行目录逐一举证（本机 Steam 官方版
        // th06c/th06nc/th10/th11，真机记录见兼容性矩阵）。三条互证特征：
        //  - th07+ 正作根目录 thbgm.dat 头 4 字节 "ZWAV"（th10/th11 实测）；
        //  - th06 系数据包 *.dat 头 4 字节 "PKGL"（th06c/th06nc 实测；原版 th06 为 "PBG3"，
        //    暂无本机样本，暂不纳入）；
        //  - th095+ 数据包 *.dat 头 16 字节经东方标准流解密后为 "THA1"（th10/th11 实测；
        //    算法移植自 thtk BSD 源码 thtk/thcrypt.c，thdat 头格式见 thtk/dattypes.h）。
        // th06nc 数据包在 data/ 子目录，故限深 2 搜索。
        (GameEngine.Touhou, dir =>
            HasFileMagic(Path.Combine(dir, "thbgm.dat"), "ZWAV")
            || GetFilesWithinDepth(dir, "*.dat", 2).Any(f =>
                HasFileMagic(f, "PKGL") || HasThDat95Header(f))),
    ];

    public static GameEngine Detect(string gameDir)
    {
        foreach (var (engine, match) in Signatures)
        {
            try
            {
                if (match(gameDir)) return engine;
            }
            catch
            {
                // 该签名所需路径不可读（目录被删/权限），尝试下一个签名
            }
        }
        return GameEngine.Unknown;
    }

    // 限深文件搜索（depth=2 表示根目录及其两层子目录），避免对巨型游戏目录做全树遍历
    private static List<string> GetFilesWithinDepth(string dir, string pattern, int depth)
    {
        var results = new List<string>();
        try
        {
            results.AddRange(Directory.GetFiles(dir, pattern));
            if (depth <= 0) return results;
            foreach (var sub in Directory.GetDirectories(dir))
                results.AddRange(GetFilesWithinDepth(sub, pattern, depth - 1));
        }
        catch
        {
            // 子目录不可读时跳过
        }
        return results;
    }

    // 限深 2 内的全部 *.dat（TouhouStaticService 复用同一搜索与头判定）
    internal static List<string> FindDatFilesWithinDepth(string dir, int depth) =>
        GetFilesWithinDepth(dir, "*.dat", depth);

    internal static bool IsThDatArchive(string path) => HasThDat95Header(path);

    // 读文件头与给定 ASCII magic 比对；文件不存在或头不可读时返回 false
    internal static bool HasFileMagic(string path, string magic)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var buffer = new byte[magic.Length];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Read(buffer, 0, buffer.Length) != buffer.Length) return false;
            return System.Text.Encoding.ASCII.GetString(buffer) == magic;
        }
        catch
        {
            return false;
        }
    }

    // th095+ thdat 归档头：磁盘上前 16 字节经东方标准流解密（key=0x1b, step=0x37）后为 "THA1"。
    // 算法移植自 thtk（BSD 许可证，tools/thtk-src/thtk/thcrypt.c 的 th_decrypt），
    // 头布局见 tools/thtk-src/thtk/dattypes.h 的 th95_archive_header_t。
    private static bool HasThDat95Header(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            const int block = 16;
            var data = new byte[block];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Read(data, 0, block) != block) return false;
            }
            ThDecryptHeader(data, 0x1b, 0x37, block);
            return data[0] == (byte)'T' && data[1] == (byte)'H'
                && data[2] == (byte)'A' && data[3] == (byte)'1';
        }
        catch
        {
            return false;
        }
    }

    private static void ThDecryptHeader(byte[] data, byte key, byte step, int block)
    {
        var temp = new byte[block];
        var increment = (block >> 1) + (block & 1);
        int input = 0, output = block - 1;
        while (output > 0)
        {
            temp[output--] = (byte)(data[input] ^ key);
            temp[output--] = (byte)(data[input + increment] ^ (byte)(key + step * increment));
            input++;
            key += step;
        }
        if ((block & 1) != 0)
        {
            temp[output] = (byte)(data[input] ^ key);
            key += step;
        }
        Array.Copy(temp, data, block);
    }
}
