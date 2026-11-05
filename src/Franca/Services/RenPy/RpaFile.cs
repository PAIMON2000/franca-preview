// Ren'Py archive (.rpa) reader. Supports RPA-3.0 (xored index, optional
// multi-segment entries with known start prefix) and RPA-2.0.
// Format reference: renpy/loader.py init_archives/load_file (6.99.12).

using System;
using System.Collections.Generic;
using System.IO;

namespace Franca.Services.RenPy;

public sealed class RpaFile : IDisposable
{
    private readonly FileStream _fs;
    private readonly Dictionary<string, List<(long Offset, int Dlen, byte[] Start)>> _index = new(StringComparer.OrdinalIgnoreCase);

    private RpaFile(string path)
    {
        _fs = File.OpenRead(path);
    }

    public static RpaFile Open(string path)
    {
        var rv = new RpaFile(path);
        try
        {
            rv.ReadIndex();
            return rv;
        }
        catch
        {
            rv.Dispose();
            throw;
        }
    }

    public IReadOnlyCollection<string> Names => _index.Keys;

    private void ReadIndex()
    {
        using var reader = new StreamReader(_fs, System.Text.Encoding.ASCII, leaveOpen: true);
        var header = reader.ReadLine() ?? throw new InvalidDataException("rpa: empty file");
        _fs.Position = 0;

        if (header.StartsWith("RPA-3.0 ", StringComparison.Ordinal))
        {
            long indexOffset = Convert.ToInt64(header[8..24], 16);
            long key = Convert.ToInt64(header[25..33], 16);

            _fs.Position = indexOffset;
            var indexObj = PyPickle.Loads(TryInflate(ReadAllRemaining()));
            foreach (var kv in (Dictionary<string, object?>)indexObj!)
            {
                var segments = new List<(long, int, byte[])>();
                foreach (var seg in (List<object?>)kv.Value!)
                {
                    var t = (PyTuple)seg!;
                    if (t.Count == 2)
                    {
                        segments.Add(((ToLong(t[0]) ^ key), (int)(ToLong(t[1]) ^ key), []));
                    }
                    else
                    {
                        segments.Add((
                            ToLong(t[0]) ^ key,
                            (int)(ToLong(t[1]) ^ key),
                            ToBytes(t[2])));
                    }
                }
                _index[kv.Key] = segments;
            }
        }
        else if (header.StartsWith("RPA-2.0 ", StringComparison.Ordinal))
        {
            long indexOffset = Convert.ToInt64(header[8..], 16);
            _fs.Position = indexOffset;
            var indexObj = PyPickle.Loads(TryInflate(ReadAllRemaining()));
            foreach (var kv in (Dictionary<string, object?>)indexObj!)
            {
                var t = (PyTuple)((List<object?>)kv.Value!)[0]!;
                _index[kv.Key] = [(ToLong(t[0]), (int)ToLong(t[1]), [])];
            }
        }
        else
        {
            throw new InvalidDataException($"rpa: unsupported header '{header}'");
        }
    }

    public byte[] Extract(string name)
    {
        if (!_index.TryGetValue(name, out var segments))
            throw new FileNotFoundException($"rpa: entry not found: {name}");

        using var ms = new MemoryStream();
        foreach (var (offset, dlen, start) in segments)
        {
            ms.Write(start);
            _fs.Position = offset;
            var buf = new byte[dlen];
            var read = _fs.Read(buf, 0, dlen);
            if (read != dlen) throw new InvalidDataException($"rpa: truncated entry {name}");
            ms.Write(TryInflate(buf));
        }
        return ms.ToArray();
    }

    private byte[] ReadAllRemaining()
    {
        using var ms = new MemoryStream();
        _fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static long ToLong(object? o) => o switch
    {
        int i => i,
        long l => l,
        byte b => b,
        _ => Convert.ToInt64(o),
    };

    private static byte[] ToBytes(object? o) => o switch
    {
        null => [],
        string s => System.Text.Encoding.UTF8.GetBytes(s),
        BytesHolder b => b.Data,
        _ => throw new InvalidDataException($"rpa: unexpected start prefix type {o.GetType().Name}"),
    };

    /// <summary>Inflate when the zlib magic is present, otherwise return input.</summary>
    public static byte[] TryInflate(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x78) return data;
        using var input = new MemoryStream(data);
        using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    public void Dispose() => _fs.Dispose();
}
