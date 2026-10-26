using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Franca.Diagnostics;

// Blueprint contract graph (--probe-blueprint): maps source files to architecture.json
// modules, scans real cross-module type references, reconciles them with the declared
// dependsOn edges and rewrites docs/BLUEPRINT.md. Deterministic output, no timestamps.
// Scanner hardening: comments and string literals are blanked before matching,
// type names shorter than 4 chars are ignored, member accesses (progress.Report)
// and parameter-name positions (KnownIssueDefinition Issue,) do not count as references.
internal static class BlueprintProbe
{
    private sealed class ModuleInfo
    {
        public required string Id { get; init; }
        public List<string> Paths { get; } = new();
        public List<string> Owns { get; } = new();
        public List<string> DependsOn { get; } = new();
        public List<string> Inputs { get; } = new();
        public List<string> Outputs { get; } = new();
        public List<string> Invariants { get; } = new();
        public List<string> Probes { get; } = new();
        public List<string> Files { get; } = new();
        public HashSet<string> Types { get; } = new(StringComparer.Ordinal);
    }

    private static readonly Regex TypeDecl = new(
        @"\b(?:class|interface|enum|struct|record)\s+(\w+)", RegexOptions.Compiled);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        { "class", "interface", "enum", "struct", "record" };

    public static int Run(string? rootArg)
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"RESULT\tBLUEPRINT\t{name}\t{(ok ? "PASS" : "FAIL")}");
            if (!ok) failures++;
        }
        void Warn(string name) => Console.WriteLine($"RESULT\tBLUEPRINT\t{name}\tWARN");

        var root = ResolveRoot(rootArg);
        if (root is null)
        {
            Check("repo_root_found", false);
            return failures;
        }
        Check("repo_root_found", true);

        var modules = LoadModules(Path.Combine(root, "architecture.json"));
        Check("modules_loaded", modules.Count > 0);

        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var unmapped = new List<string>();
        foreach (var file in files)
        {
            var module = modules.FirstOrDefault(m => Matches(m, file));
            if (module is null)
            {
                unmapped.Add(file);
                Warn("unmapped_file_" + file);
                continue;
            }
            module.Files.Add(file);
        }

        var strippedByFile = new Dictionary<string, string>(StringComparer.Ordinal);
        string Stripped(string file)
        {
            if (!strippedByFile.TryGetValue(file, out var text))
                strippedByFile[file] = text = StripCommentsAndStrings(File.ReadAllText(Path.Combine(root, file)));
            return text;
        }

        foreach (var m in modules)
            foreach (var file in m.Files)
                foreach (Match match in TypeDecl.Matches(Stripped(file)))
                {
                    var name = match.Groups[1].Value;
                    if (!Keywords.Contains(name) && name.Length >= 4)
                        m.Types.Add(name);
                }

        // Actual cross-module edges: file in module A whole-word-references a type declared in module B.
        var edges = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in modules)
        {
            foreach (var file in m.Files)
            {
                var text = Stripped(file);
                foreach (var other in modules)
                {
                    if (ReferenceEquals(other, m)) continue;
                    foreach (var type in other.Types)
                    {
                        foreach (Match match in Regex.Matches(text, @"\b" + Regex.Escape(type) + @"\b"))
                        {
                            if (IsFalsePositive(text, match.Index, type.Length)) continue;
                            var key = m.Id + "->" + other.Id;
                            edges[key] = edges.TryGetValue(key, out var n) ? n + 1 : 1;
                            evidence.TryAdd(key, $"{type}（见 `{file}`）");
                            break;
                        }
                    }
                }
            }
        }

        var undeclared = new List<string>();
        foreach (var key in edges.Keys)
        {
            var split = key.Split("->");
            var from = modules.First(m => m.Id == split[0]);
            if (!from.DependsOn.Contains(split[1]))
            {
                undeclared.Add(key);
                Check("undeclared_edge_" + split[0] + "_" + split[1], false);
            }
        }

        var stale = new List<string>();
        foreach (var m in modules)
            foreach (var dep in m.DependsOn)
                if (!edges.ContainsKey(m.Id + "->" + dep))
                {
                    stale.Add(m.Id + "->" + dep);
                    Warn("stale_edge_" + m.Id + "_" + dep);
                }

        foreach (var m in modules)
            Check("module_" + m.Id, undeclared.All(e => !e.StartsWith(m.Id + "->", StringComparison.Ordinal)));
        Check("overall", undeclared.Count == 0);

        var doc = RenderMarkdown(modules, edges, undeclared, stale, unmapped, evidence);
        var outPath = Path.Combine(root, "docs", "BLUEPRINT.md");
        File.WriteAllText(outPath, doc, new UTF8Encoding(false));
        Console.WriteLine($"RESULT\tBLUEPRINT\twrote_{Path.GetRelativePath(root, outPath).Replace('\\', '/')}\tPASS");
        return failures;
    }

    private static bool IsFalsePositive(string text, int start, int length)
    {
        if (start > 0 && text[start - 1] == '.') return true; // member access: progress.Report
        var after = start + length < text.Length ? text[start + length] : '\0';
        if (after is ',' or ')' or ';')
        {
            var i = start - 1;
            while (i >= 0 && text[i] == ' ') i--;
            if (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                return true; // parameter-name position: SomeType Issue,
        }
        return false;
    }

    private static string StripCommentsAndStrings(string text)
    {
        var chars = text.ToCharArray();
        void Blank(int from, int to)
        {
            for (var k = from; k < to && k < chars.Length; k++)
                if (chars[k] is not ('\n' or '\r')) chars[k] = ' ';
        }
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                var j = i;
                while (j < chars.Length && chars[j] != '\n') j++;
                Blank(i, j);
                i = j;
                continue;
            }
            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var j = i + 2;
                while (j + 1 < chars.Length && !(chars[j] == '*' && chars[j + 1] == '/')) j++;
                j = Math.Min(j + 2, chars.Length);
                Blank(i, j);
                i = j - 1;
                continue;
            }
            if (c == '\'')
            {
                var j = i + 1;
                while (j < chars.Length && chars[j] != '\'') j += chars[j] == '\\' ? 2 : 1;
                j = Math.Min(j + 1, chars.Length);
                Blank(i, j);
                i = j - 1;
                continue;
            }
            if (c != '"') continue;
            var verbatim = (i > 0 && chars[i - 1] == '@') || (i > 1 && chars[i - 2] == '@');
            var interpolated = (i > 0 && chars[i - 1] == '$') || (i > 1 && chars[i - 2] == '$');
            var j2 = i + 1;
            while (j2 < chars.Length)
            {
                if (!verbatim && chars[j2] == '\\') { j2 += 2; continue; }
                if (interpolated && chars[j2] == '{')
                {
                    if (j2 + 1 < chars.Length && chars[j2 + 1] == '{') { j2 += 2; continue; }
                    var depth = 1;
                    j2++;
                    while (j2 < chars.Length && depth > 0)
                    {
                        if (chars[j2] == '{') depth++;
                        else if (chars[j2] == '}') depth--;
                        j2++;
                    }
                    continue;
                }
                if (interpolated && chars[j2] == '}' && j2 + 1 < chars.Length && chars[j2 + 1] == '}') { j2 += 2; continue; }
                if (chars[j2] == '"')
                {
                    if (verbatim && j2 + 1 < chars.Length && chars[j2 + 1] == '"') { j2 += 2; continue; }
                    break;
                }
                j2++;
            }
            // Keep interpolation holes visible: blank only up to the first hole.
            var end = Math.Min(j2 + 1, chars.Length);
            if (interpolated)
            {
                var holeStart = Array.IndexOf(chars, '{', i, end - i);
                if (holeStart >= 0)
                {
                    Blank(i, holeStart);
                    // holes contain no string literals in this codebase's probe-relevant patterns;
                    // blank the remainder after the last hole conservatively by scanning again
                    var lastHoleEnd = -1;
                    var d = 0;
                    for (var k = holeStart; k < end; k++)
                    {
                        if (chars[k] == '{') d++;
                        else if (chars[k] == '}') { d--; if (d == 0) lastHoleEnd = k; }
                    }
                    if (lastHoleEnd >= 0) Blank(lastHoleEnd + 1, end);
                }
                else Blank(i, end);
            }
            else Blank(i, end);
            i = end - 1;
        }
        return new string(chars);
    }

    private static string? ResolveRoot(string? arg)
    {
        if (!string.IsNullOrEmpty(arg))
            return File.Exists(Path.Combine(arg, "architecture.json")) ? Path.GetFullPath(arg) : null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "architecture.json")))
                return dir.FullName;
        return null;
    }

    private static List<ModuleInfo> LoadModules(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<ModuleInfo>();
        foreach (var el in doc.RootElement.GetProperty("modules").EnumerateArray())
        {
            var m = new ModuleInfo { Id = el.GetProperty("id").GetString()! };
            Fill(m.Paths, el, "paths");
            Fill(m.Owns, el, "owns");
            Fill(m.DependsOn, el, "dependsOn");
            if (el.TryGetProperty("contract", out var c))
            {
                Fill(m.Inputs, c, "inputs");
                Fill(m.Outputs, c, "outputs");
                Fill(m.Invariants, c, "invariants");
                Fill(m.Probes, c, "probes");
            }
            list.Add(m);
        }
        return list;
    }

    private static void Fill(List<string> target, JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                target.Add(item.GetString() ?? "");
    }

    private static bool Matches(ModuleInfo m, string file)
    {
        foreach (var p in m.Paths)
        {
            if (p.EndsWith(".cs", StringComparison.Ordinal))
            {
                if (p.Contains('*'))
                {
                    var pattern = "^" + Regex.Escape(p).Replace("\\*", "[^/]*") + "$";
                    if (Regex.IsMatch(file, pattern)) return true;
                }
                else if (string.Equals(p, file, StringComparison.Ordinal)) return true;
            }
            else if (file.StartsWith(p + "/", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string NodeId(string id) => Regex.Replace(id, "[^A-Za-z0-9_]", "_");

    private static string NodeLabel(ModuleInfo m)
    {
        var owns = string.Join(" · ", m.Owns.Take(2));
        return (m.Id + "<br/>" + owns).Replace('"', '\'');
    }

    private static string RenderMarkdown(List<ModuleInfo> modules, SortedDictionary<string, int> edges,
        List<string> undeclared, List<string> stale, List<string> unmapped, Dictionary<string, string> evidence)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Franca 蓝图（模块契约图）");
        sb.AppendLine();
        sb.AppendLine("> 自动生成：`Franca.exe --probe-blueprint`。数据源是 `architecture.json` 的 modules（paths / dependsOn / contract），边由真实源码引用扫描得出。请勿手改本文件；改模块边界时更新 architecture.json 并重跑探针。关键数据流时序见 [BLUEPRINT-FLOWS.md](BLUEPRINT-FLOWS.md)。");
        sb.AppendLine();
        sb.AppendLine("```mermaid");
        sb.AppendLine("graph LR");
        foreach (var m in modules)
            sb.AppendLine($"  {NodeId(m.Id)}[\"{NodeLabel(m)}\"]");
        var styleLines = new List<string>();
        var index = 0;
        foreach (var key in edges.Keys)
        {
            var split = key.Split("->");
            sb.AppendLine($"  {NodeId(split[0])} --> {NodeId(split[1])}");
            if (undeclared.Contains(key))
                styleLines.Add($"  linkStyle {index} stroke:#d33,stroke-width:2px");
            index++;
        }
        foreach (var key in stale)
        {
            var split = key.Split("->");
            sb.AppendLine($"  {NodeId(split[0])} -.-> {NodeId(split[1])}");
            styleLines.Add($"  linkStyle {index} stroke:#999");
            index++;
        }
        if (styleLines.Count > 0)
            sb.AppendLine(string.Join(Environment.NewLine, styleLines));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("实线 = 代码中真实存在的引用；红色实线 = 存在但未在 dependsOn 声明（探针 FAIL）；灰色虚线 = 已声明但代码中暂无引用（WARN）。");
        sb.AppendLine();
        sb.AppendLine("## 对账结果");
        sb.AppendLine();
        sb.AppendLine(undeclared.Count == 0
            ? "- 未声明边：无"
            : "- 未声明边：" + string.Join("、", undeclared.Select(e => $"`{e}`（{evidence[e]}）")));
        sb.AppendLine(stale.Count == 0
            ? "- 漂移边：无"
            : "- 漂移边：" + string.Join("、", stale.Select(e => $"`{e}`")));
        sb.AppendLine(unmapped.Count == 0
            ? "- 未归类文件：无"
            : "- 未归类文件：" + string.Join("、", unmapped.Select(f => $"`{f}`")));
        sb.AppendLine();
        sb.AppendLine("## 模块契约");
        foreach (var m in modules)
        {
            sb.AppendLine();
            sb.AppendLine($"### `{m.Id}`");
            sb.AppendLine();
            sb.AppendLine($"- 职责：{string.Join("；", m.Owns)}");
            sb.AppendLine($"- 文件：{m.Files.Count} 个；声明依赖：{(m.DependsOn.Count == 0 ? "无" : string.Join("、", m.DependsOn.Select(d => $"`{d}`")))}");
            AppendItems(sb, "输入", m.Inputs);
            AppendItems(sb, "输出", m.Outputs);
            AppendItems(sb, "不变量", m.Invariants);
            AppendItems(sb, "验证探针", m.Probes);
        }
        return sb.ToString();
    }

    private static void AppendItems(StringBuilder sb, string title, List<string> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"- {title}：");
        foreach (var item in items)
            sb.AppendLine($"  - `{item}`");
    }
}
