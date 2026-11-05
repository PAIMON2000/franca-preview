// rpyc (Ren'Py compiled script) loading: RENPY RPC2 slot table or v1 raw
// blob, zlib-compressed pickle of (data, stmts). Format reference:
// unrpyc read_ast_from_file.

using System;
using System.Collections.Generic;
using System.IO;

namespace Franca.Services.RenPy;

public static class RpycFile
{
    public static List<object?> LoadStatements(byte[] rpyc)
    {
        byte[] blob;
        if (rpyc.Length >= 10 && System.Text.Encoding.ASCII.GetString(rpyc, 0, 10) == "RENPY RPC2")
        {
            var position = 10;
            byte[]? slot1 = null;
            while (position + 12 <= rpyc.Length)
            {
                int slot = BitConverter.ToInt32(rpyc, position);
                int start = BitConverter.ToInt32(rpyc, position + 4);
                int length = BitConverter.ToInt32(rpyc, position + 8);
                position += 12;
                if (slot == 0) break;
                if (slot == 1) slot1 = rpyc[start..(start + length)];
            }
            blob = slot1 ?? throw new InvalidDataException("rpyc: slot 1 not found");
        }
        else
        {
            blob = rpyc; // rpyc v1
        }

        var unpickled = PyPickle.Loads(RpaFile.TryInflate(blob));
        // loads returns (data, stmts); stmts is a list of top-level AST nodes.
        if (unpickled is PyTuple { Count: >= 2 } pair && pair[1] is List<object?> stmts)
            return stmts;
        if (unpickled is List<object?> direct)
            return direct;
        throw new InvalidDataException("rpyc: unexpected pickle root");
    }
}
