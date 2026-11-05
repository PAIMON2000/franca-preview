// Python pickle reader for Ren'Py rpyc/rpa payloads.
// Covers protocol 0-4 opcodes used by Ren'Py 6.99 (py2.7, protocol 2) and
// newer Ren'Py 8 (py3, protocol 4). Unknown GLOBAL classes become PyFake
// objects carrying their class name and state dictionary; REDUCE of known
// value types (PyExpr) collapses to their string value.
// Opcode reference: CPython Lib/pickletools.py.

using System;
using System.IO;
using System.Text;

namespace Franca.Services.RenPy;

/// <summary>A pickle of a class instance whose class we do not emulate.</summary>
public sealed class PyFake
{
    public required string Module { get; init; }
    public required string Name { get; init; }
    public object?[] Args { get; set; } = [];
    public Dictionary<string, object?> State { get; } = new(StringComparer.Ordinal);

    public string FullName => Module.Length == 0 ? Name : Module + "." + Name;

    public object? Get(string key) => State.TryGetValue(key, out var v) ? v : null;
}

public sealed class BytesHolder
{
    public required byte[] Data { get; init; }
    public override string ToString() => "b'" + Convert.ToHexString(Data) + "'";
}

/// <summary>Ordered tuple; Ren'Py frequently pickles (label, condition, block) triples.</summary>
public sealed class PyTuple : List<object?>
{
    public PyTuple() { }
    public PyTuple(IEnumerable<object?> items) : base(items) { }
}

public sealed class PyPickle
{
    private readonly byte[] _buf;
    private int _pos;
    private readonly List<object?> _memo = [];

    private PyPickle(byte[] buf) { _buf = buf; }

    public static object? Loads(byte[] data) => new PyPickle(data).Load();

    private int ReadByte()
    {
        if (_pos >= _buf.Length) throw new InvalidDataException("pickle: unexpected end of stream");
        return _buf[_pos++];
    }

    private byte[] ReadBytes(int n)
    {
        if (n < 0 || _pos + n > _buf.Length) throw new InvalidDataException("pickle: truncated read");
        var rv = _buf[_pos..(_pos + n)];
        _pos += n;
        return rv;
    }

    private ulong ReadUIntLE(int n)
    {
        ulong rv = 0;
        for (var i = 0; i < n; i++) rv |= (ulong)ReadByte() << (8 * i);
        return rv;
    }

    private long ReadIntLE(int n)
    {
        ulong u = ReadUIntLE(n);
        if (n < 8 && n > 0 && (u & (1UL << (8 * n - 1))) != 0) u |= ulong.MaxValue << (8 * n);
        return (long)u;
    }

    private string ReadLine(Encoding enc)
    {
        var start = _pos;
        while (_pos < _buf.Length && _buf[_pos] != (byte)'\n') _pos++;
        var line = _buf[start.._pos];
        if (_pos < _buf.Length) _pos++;
        return enc.GetString(line).TrimEnd('\r');
    }

    private object? Load()
    {
        var stack = new Stack<object?>();
        var markStack = new Stack<int>();

        while (true)
        {
            int op = ReadByte();
            switch (op)
            {
                case 0x80: ReadByte(); break;                                   // PROTO
                case 0x95: ReadIntLE(8); break;                                 // FRAME
                case 0x2e: return stack.Pop();                                  // STOP

                case 0x28: markStack.Push(stack.Count); break;                  // MARK
                case 0x30: stack.Pop(); break;                                  // POP
                case 0x31: PopToMark(stack, markStack); break;                  // POP_MARK
                case 0x32: stack.Push(stack.Peek()); break;                     // DUP

                case 0x4e: stack.Push(null); break;                             // NONE
                case 0x88: stack.Push(true); break;                             // NEWTRUE
                case 0x89: stack.Push(false); break;                            // NEWFALSE
                case 0x49:                                                        // INT / bool
                    {
                        var s = ReadLine(Encoding.ASCII);
                        if (s == "01") stack.Push(true);
                        else if (s == "00") stack.Push(false);
                        else stack.Push(int.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    }
                case 0x4a: stack.Push((int)ReadIntLE(4)); break;                // BININT
                case 0x4b: stack.Push(ReadByte()); break;                       // BININT1
                case 0x4d: stack.Push((int)ReadUIntLE(2)); break;               // BININT2
                case 0x46:                                                        // FLOAT
                    stack.Push(double.Parse(ReadLine(Encoding.ASCII),
                        System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case 0x47:                                                        // BINFLOAT (BE)
                    {
                        var b = ReadBytes(8);
                        if (BitConverter.IsLittleEndian) Array.Reverse(b);
                        stack.Push(BitConverter.ToDouble(b));
                        break;
                    }
                case 0x4c:                                                        // LONG
                    stack.Push(long.Parse(ReadLine(Encoding.ASCII).TrimEnd('L'),
                        System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case 0x8a:                                                        // LONG1
                case 0x8b:                                                        // LONG2
                    {
                        int n = op == 0x8a ? ReadByte() : (int)ReadUIntLE(4);
                        var raw = ReadBytes(n);
                        long rv = 0;
                        int limit = Math.Min(n, 8);
                        for (var i = 0; i < limit; i++) rv |= (long)raw[i] << (8 * i);
                        if (n > 0 && n < 8 && (raw[n - 1] & 0x80) != 0) rv |= -1L << (8 * n);
                        stack.Push(rv);
                        break;
                    }

                case 0x56: stack.Push(DecodeRawUnicodeEscape(ReadLine(Encoding.UTF8))); break; // UNICODE
                case 0x58:                                                        // BINUNICODE
                    {
                        int n = (int)ReadUIntLE(4);
                        stack.Push(Encoding.UTF8.GetString(ReadBytes(n)));
                        break;
                    }
                case 0x8c:                                                        // SHORT_BINUNICODE
                    {
                        int n = ReadByte();
                        stack.Push(Encoding.UTF8.GetString(ReadBytes(n)));
                        break;
                    }
                case 0x8d:                                                        // BINUNICODE8
                    {
                        int n = (int)ReadUIntLE(8);
                        stack.Push(Encoding.UTF8.GetString(ReadBytes(n)));
                        break;
                    }
                case 0x53: stack.Push(DecodePy2String(ReadLine(Encoding.UTF8))); break; // STRING
                case 0x55:                                                        // SHORT_BINSTRING
                    {
                        int n = ReadByte();
                        stack.Push(Encoding.UTF8.GetString(ReadBytes(n)));
                        break;
                    }
                case 0x54:                                                        // BINSTRING
                    {
                        int n = (int)ReadIntLE(4);
                        stack.Push(Encoding.UTF8.GetString(ReadBytes(n)));
                        break;
                    }
                case 0x43:                                                        // SHORT_BINBYTES
                    {
                        int n = ReadByte();
                        stack.Push(new BytesHolder { Data = ReadBytes(n) });
                        break;
                    }
                case 0x42:                                                        // BINBYTES
                    {
                        int n = (int)ReadIntLE(4);
                        stack.Push(new BytesHolder { Data = ReadBytes(n) });
                        break;
                    }
                case 0x96:                                                        // BYTEARRAY8
                    {
                        int n = (int)ReadUIntLE(8);
                        stack.Push(new BytesHolder { Data = ReadBytes(n) });
                        break;
                    }
                case 0x97: ReadBytes(0); break;                                   // NEXT_BUFFER: unsupported, empty

                case 0x74: stack.Push(new PyTuple(PopToMark(stack, markStack))); break; // TUPLE
                case 0x29: stack.Push(new PyTuple()); break;                     // EMPTY_TUPLE
                case 0x85: { var a = stack.Pop(); stack.Push(new PyTuple { a }); break; }     // TUPLE1
                case 0x86: { var b = stack.Pop(); var a = stack.Pop(); stack.Push(new PyTuple { a, b }); break; } // TUPLE2
                case 0x87: { var c = stack.Pop(); var b = stack.Pop(); var a = stack.Pop(); stack.Push(new PyTuple { a, b, c }); break; } // TUPLE3

                case 0x6c: stack.Push(new List<object?>(PopToMark(stack, markStack))); break; // LIST
                case 0x5d: stack.Push(new List<object?>()); break;                // EMPTY_LIST
                case 0x61:                                                        // APPEND
                    {
                        var item = stack.Pop();
                        ((List<object?>)stack.Peek()!).Add(item);
                        break;
                    }
                case 0x65:                                                        // APPENDS
                    {
                        var items = PopToMark(stack, markStack);
                        ((List<object?>)stack.Peek()!).AddRange(items);
                        break;
                    }

                case 0x64:                                                        // DICT (MARK, key/value pairs)
                    {
                        var items = PopToMark(stack, markStack);
                        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                        for (var i = 0; i + 1 < items.Count; i += 2)
                            d[AsKey(items[i])] = items[i + 1];
                        stack.Push(d);
                        break;
                    }
                case 0x7d: stack.Push(new Dictionary<string, object?>(StringComparer.Ordinal)); break; // EMPTY_DICT
                case 0x73:                                                        // SETITEM
                    {
                        var v = stack.Pop();
                        var k = AsKey(stack.Pop());
                        ((Dictionary<string, object?>)stack.Peek()!)[k] = v;
                        break;
                    }
                case 0x75:                                                        // SETITEMS
                    {
                        var items = PopToMark(stack, markStack);
                        var d = (Dictionary<string, object?>)stack.Peek()!;
                        for (var i = 0; i + 1 < items.Count; i += 2)
                            d[AsKey(items[i])] = items[i + 1];
                        break;
                    }

                case 0x70: Memo(int.Parse(ReadLine(Encoding.ASCII), System.Globalization.CultureInfo.InvariantCulture), stack.Peek()); break; // PUT
                case 0x71: Memo(ReadByte(), stack.Peek()); break;                 // BINPUT
                case 0x72: Memo((int)ReadUIntLE(4), stack.Peek()); break;         // LONG_BINPUT
                case 0x67: stack.Push(_memo[int.Parse(ReadLine(Encoding.ASCII), System.Globalization.CultureInfo.InvariantCulture)]); break; // GET
                case 0x68: stack.Push(_memo[ReadByte()]); break;                  // BINGET
                case 0x6a: stack.Push(_memo[(int)ReadUIntLE(4)]); break;          // LONG_BINGET
                case 0x94: Memo(_memo.Count, stack.Peek()); break;                // MEMOIZE

                case 0x63:                                                        // GLOBAL
                    {
                        var module = ReadLine(Encoding.UTF8);
                        var name = ReadLine(Encoding.UTF8);
                        stack.Push(new PyGlobal { Module = module, Name = name });
                        break;
                    }
                case 0x93:                                                        // STACK_GLOBAL
                    {
                        var name = (string)stack.Pop()!;
                        var module = (string)stack.Pop()!;
                        stack.Push(new PyGlobal { Module = module, Name = name });
                        break;
                    }
                case 0x52:                                                        // REDUCE
                    {
                        var args = (PyTuple)stack.Pop()!;
                        var callable = stack.Pop();
                        stack.Push(Reduce(callable, args));
                        break;
                    }
                case 0x62:                                                        // BUILD
                    {
                        var state = stack.Pop();
                        Build(stack.Peek(), state);
                        break;
                    }
                case 0x81:                                                        // NEWOBJ
                    {
                        var args = (PyTuple)stack.Pop()!;
                        var callable = stack.Pop();
                        stack.Push(new PyFake
                        {
                            Module = ModuleOf(callable),
                            Name = NameOf(callable),
                            Args = [.. args],
                        });
                        break;
                    }
                case 0x92:                                                        // NEWOBJ_EX
                    {
                        var kwargs = (PyTuple)stack.Pop()!;
                        var args = (PyTuple)stack.Pop()!;
                        var callable = stack.Pop();
                        stack.Push(new PyFake
                        {
                            Module = ModuleOf(callable),
                            Name = NameOf(callable),
                            Args = [.. args, .. kwargs],
                        });
                        break;
                    }
                case 0x69:                                                        // INST (module\nname, MARK args)
                    {
                        var module = ReadLine(Encoding.UTF8);
                        var name = ReadLine(Encoding.UTF8);
                        var args = PopToMark(stack, markStack);
                        stack.Push(new PyFake { Module = module, Name = name, Args = [.. args] });
                        break;
                    }
                case 0x6f:                                                        // OBJ
                    {
                        var args = PopToMark(stack, markStack);
                        var callable = args[0];
                        stack.Push(new PyFake
                        {
                            Module = ModuleOf(callable),
                            Name = NameOf(callable),
                            Args = [.. args.Skip(1)],
                        });
                        break;
                    }
                case 0x50: stack.Push(new PyFake { Module = "", Name = "persid:" + ReadLine(Encoding.UTF8) }); break; // PERSID
                case 0x51:                                                        // BINPERSID
                    {
                        var pid = stack.Pop();
                        stack.Push(new PyFake { Module = "", Name = "persid", Args = [pid] });
                        break;
                    }
                case 0x8f: stack.Push(new List<object?>()); break;                // EMPTY_SET
                case 0x8e:                                                        // BINBYTES8 handled above; ADDITEMS below
                    throw new InvalidDataException($"pickle: unexpected opcode 0x{op:x2}");
                case 0x90:                                                        // ADDITEMS
                    {
                        var items = PopToMark(stack, markStack);
                        ((List<object?>)stack.Peek()!).AddRange(items);
                        break;
                    }
                case 0x91: stack.Push(new PyTuple(PopToMark(stack, markStack))); break; // FROZENSET

                default:
                    throw new InvalidDataException($"pickle: unknown opcode 0x{op:x2} at {_pos - 1}");
            }
        }

        void Memo(int idx, object? value)
        {
            while (_memo.Count <= idx) _memo.Add(null);
            _memo[idx] = value;
        }
    }

    private sealed class PyGlobal
    {
        public required string Module { get; init; }
        public required string Name { get; init; }
    }

    private static string ModuleOf(object? callable) => callable is PyGlobal g ? g.Module : "";
    private static string NameOf(object? callable) => callable switch
    {
        PyGlobal g => g.Name,
        PyFake f => f.Name,
        _ => callable?.ToString() ?? "?",
    };

    private static object? Reduce(object? callable, PyTuple args)
    {
        // renpy.ast.PyExpr: __getnewargs__ = (text, filename, linenumber) — we only need text.
        if (callable is PyGlobal { Name: "PyExpr" })
            return args.Count > 0 ? args[0] : "";

        return new PyFake
        {
            Module = ModuleOf(callable),
            Name = NameOf(callable),
            Args = [.. args],
        };
    }

    private static void Build(object? target, object? state)
    {
        // Ren'Py 6.99 AST nodes pickle their state as (None, {attrs}) — unwrap.
        if (state is PyTuple { Count: >= 2 } t && t[^1] is Dictionary<string, object?> inner)
            state = inner;

        switch (target)
        {
            case PyFake f when state is Dictionary<string, object?> d:
                foreach (var kv in d) f.State[kv.Key] = kv.Value;
                break;
            case PyFake f:
                f.State["__state__"] = state;
                break;
            case Dictionary<string, object?> d when state is Dictionary<string, object?> sd:
                foreach (var kv in sd) d[kv.Key] = kv.Value;
                break;
            case List<object?> l when state is PyTuple tuple:
                l.AddRange(tuple);
                break;
        }
    }

    private List<object?> PopToMark(Stack<object?> stack, Stack<int> marks)
    {
        if (marks.Count == 0) throw new InvalidDataException("pickle: mark underflow");
        int mark = marks.Pop();
        var items = new List<object?>();
        while (stack.Count > mark) items.Add(stack.Pop());
        items.Reverse();
        return items;
    }

    private static string AsKey(object? key) => key switch
    {
        null => "None",
        string s => s,
        BytesHolder b => Encoding.UTF8.GetString(b.Data),
        _ => key.ToString() ?? "?",
    };

    private static string DecodePy2String(string repr)
    {
        // pickle STRING items carry a py2-bytes repr such as 'abc\\n' or "it's"
        if (repr.Length < 2) return repr;
        var body = repr[1..^1];
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length)
            {
                var next = body[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'v' => '\v',
                    'x' when i + 2 < body.Length =>
                        (char)Convert.ToInt32(body.Substring(++i, 2), 16),
                    _ => next,
                });
            }
            else sb.Append(body[i]);
        }
        return sb.ToString();
    }

    private static string DecodeRawUnicodeEscape(string line)
    {
        // raw-unicode-escape: \uXXXX sequences only; other backslashes literal.
        if (!line.Contains("\\u")) return line;
        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] == 'u' && i + 5 < line.Length)
            {
                sb.Append((char)Convert.ToInt32(line.Substring(i + 2, 4), 16));
                i += 5;
            }
            else sb.Append(line[i]);
        }
        return sb.ToString();
    }
}
