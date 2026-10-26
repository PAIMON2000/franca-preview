using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Franca.Models;

namespace Franca.Services;

// 东方 thcrap 路线的菜单界面精灵图本地化：th10/th11 的主菜单、难度选择、画面标题、
// 结算界面、装备选择的文字均烘焙在 dat 内 title.anm 的 PNG 精灵图里。本服务在补丁构建时
// 程序化生成目标语言版本：thanm -x 只读解出原图 → 清格/扩散修补 → 本地字体成像
// （WPF 成像，零运行时成本、零模型调用）。词条翻译走与对话相同的端点管线
// （缓存/熔断/续译语义一致），zh-CN 优先取内置人工校译表，未命中再走管线；
// 其他语言全部走管线。单元格矩形来自 thanm -l 的 sprite 表（tools/menu-img-zh 为原型）。
// 产物写入 <patchDir>/<gameId>/title/<原名>.png，thcrap 运行时按同名文件替换 dat 内图像。
public static partial class TouhouMenuImageService
{
    // 编辑操作：clear=整格清成透明；dark=暗色文字掩码后最近邻扩散修补（保留笔触底纹）；
    // red=红色文字掩码修补（保留木牌底色）
    internal sealed record Edit(int X, int Y, int W, int H, string Mode);
    // 文字绘制：Literal=false 时 Source 为待译原文；Literal=true 时 Source 即最终内容
    // （EASY 等英文艺术字、★ 星级等不译的美术元素）。Prefix 为译文前的固定前缀
    // （如 "Stage 1　"，不进翻译管线）
    internal sealed record Txt(double X, double Y, double W, double H, string Source, double Size,
        (int R, int G, int B) Fill, (int R, int G, int B)? Outline = null, double PenW = 1.4,
        bool HCenter = false, bool VCenter = true, byte Alpha = 255, double Rotate = 0,
        bool Fit = false, bool Literal = false, string Prefix = "");
    // 椭圆笔触（th10 装备明细的红色底纹等纯装饰）
    internal sealed record Ell(double X, double Y, double W, double H, (int R, int G, int B) Color,
        byte Alpha, double Rotate = 0);
    // 半透明底板（文字盖住立绘等复杂背景时垫底）
    internal sealed record Plate(double X, double Y, double W, double H, byte Alpha,
        (int R, int G, int B) Color);
    // Anm=所在 anm 文件（thdat 解包到 stage 根）；RelPath=dat/补丁内相对路径（含子目录）
    internal sealed record ImgSpec(string Anm, string RelPath, List<Edit> Edits, List<object> Draws);

    private static readonly (int, int, int) White = (245, 245, 245);
    private static readonly (int, int, int) Black = (18, 18, 18);

    // ---- 管线入口：thanm 解出 title.anm → 词条翻译 → 逐图渲染到补丁目录。
    // 返回端点全熔断放弃的条数；thanm/title.anm 缺失只记日志跳过（不阻断启用）----
    internal static async Task<int> GenerateAsync(string gameId, string stage, string patchGameDir,
        GameInfo game, AppSettings settings, Action<string> log,
        TouhouStaticService.InstallTracker tracker, CancellationToken ct)
    {
        var specs = Specs(gameId);
        if (specs is null) return 0; // 非 th10/th11：无布局表
        var thanm = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thtk", "thanm.exe");
        if (!File.Exists(thanm))
        {
            log(UiStrings.Get("inst.tc.menuimg.skip"));
            return 0;
        }
        // 按 anm 分组解包（只读于 dat；解出 stage/<子目录>/*.png）
        foreach (var anmName in specs.Select(s => s.Anm).Distinct())
        {
            if (!File.Exists(Path.Combine(stage, anmName)))
            {
                log(UiStrings.Get("inst.tc.menuimg.skip"));
                continue; // 单个 anm 缺失跳过对应图
            }
            TouhouStaticService.RunTool(thanm, $"-x {anmName}", stage);
        }

        // 收集待译词条：title01 标签与 select01 大标题原文为英文，其余为日文
        var enTexts = new List<string>();
        var jaTexts = new List<string>();
        var seenEn = new HashSet<string>(StringComparer.Ordinal);
        var seenJa = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        foreach (var draw in spec.Draws)
        {
            if (draw is not Txt t || t.Literal) continue;
            if (IsEnglishSource(t.Source)) { if (seenEn.Add(t.Source)) enTexts.Add(t.Source); }
            else if (seenJa.Add(t.Source)) jaTexts.Add(t.Source);
        }

        // zh-CN 校译表优先（含 th10/th11 同文异译的按游戏覆盖）；未命中按源语言分两批进管线
        var target = settings.TargetLanguage;
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendEn = new List<string>();
        var pendJa = new List<string>();
        foreach (var text in enTexts)
            if (TryCurated(gameId, text, target, out var curated)) resolved[text] = curated;
            else pendEn.Add(text);
        foreach (var text in jaTexts)
            if (TryCurated(gameId, text, target, out var curated)) resolved[text] = curated;
            else pendJa.Add(text);

        var abandoned = 0;
        if (pendEn.Count > 0)
        {
            var (t, a) = await TouhouStaticService.TranslateBatchAsync(
                game, pendEn, settings, log, tracker, ct, "mt:touhou-thcrap", sourceLang: "en");
            foreach (var kv in t) resolved[kv.Key] = kv.Value;
            abandoned += a;
        }
        if (pendJa.Count > 0)
        {
            var (t, a) = await TouhouStaticService.TranslateBatchAsync(
                game, pendJa, settings, log, tracker, ct, "mt:touhou-thcrap", sourceLang: "ja");
            foreach (var kv in t) resolved[kv.Key] = kv.Value;
            abandoned += a;
        }

        var fontDir = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "thcrap", "fonts");
        var rendered = 0;
        foreach (var spec in specs)
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(stage, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue; // 单图缺失跳过，不影响其余
            var outPath = Path.Combine(patchGameDir, spec.RelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            RenderImage(spec, src, outPath, fontDir,
                text => ResolveTerm(gameId, text, target, resolved));
            rendered++;
        }
        log(UiStrings.Format("inst.tc.menuimg", rendered));
        return abandoned;
    }

    // 词条解析（纯函数，探针可直验）：zh-CN 校译表优先（先按游戏覆盖键 gameId|source，
    // 再按通用键），其次管线结果，最后保留原文
    internal static string ResolveTerm(string gameId, string source, string targetLanguage,
        IReadOnlyDictionary<string, string>? pipeline) =>
        TryCurated(gameId, source, targetLanguage, out var curated) ? curated
        : pipeline is not null && pipeline.TryGetValue(source, out var piped) ? piped
        : source;

    private static bool TryCurated(string gameId, string source, string targetLanguage, out string value)
    {
        value = "";
        if (!targetLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return false;
        if (CuratedZh.TryGetValue(gameId + "|" + source, out value!)) return true;
        return CuratedZh.TryGetValue(source, out value!);
    }

    private static bool IsEnglishSource(string text) =>
        text.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') && !text.Any(c => c >= 0x3000);

    // ---- 渲染：编辑操作（清格/扩散修补）→ 装饰 → 文字（描边、字号自收缩）----
    private static void RenderImage(ImgSpec spec, string inPath, string outPath, string fontDir,
        Func<string, string> resolve)
    {
        var bmp = new BitmapImage(new Uri(Path.GetFullPath(inPath)));
        var wb = new WriteableBitmap(new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0));
        int w = wb.PixelWidth, h = wb.PixelHeight, stride = wb.BackBufferStride;
        var px = new byte[h * stride];
        wb.CopyPixels(px, stride, 0);
        foreach (var e in spec.Edits)
        {
            if (e.Mode == "clear")
                for (int yy = e.Y; yy < e.Y + e.H; yy++)
                    Array.Clear(px, yy * stride + e.X * 4, e.W * 4);
            else
                Inpaint(px, w, h, stride, e);
        }
        wb.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);

        var family = new FontFamily(new Uri(Path.GetFullPath(fontDir) + "/"), "./NotoSansSC.ttf#Noto Sans SC");
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(wb, new Rect(0, 0, w, h));
            foreach (var d in spec.Draws)
                switch (d)
                {
                    case Plate p:
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(p.Alpha,
                            (byte)p.Color.R, (byte)p.Color.G, (byte)p.Color.B)), null,
                            new Rect(p.X, p.Y, p.W, p.H));
                        break;
                    case Ell el:
                        dc.PushTransform(new RotateTransform(el.Rotate, el.X + el.W / 2, el.Y + el.H / 2));
                        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(el.Alpha,
                            (byte)el.Color.R, (byte)el.Color.G, (byte)el.Color.B)), null,
                            new Point(el.X + el.W / 2, el.Y + el.H / 2), el.W / 2, el.H / 2);
                        dc.Pop();
                        break;
                    case Txt t:
                        DrawText(dc, t, typeface, t.Prefix + (t.Literal ? t.Source : resolve(t.Source)));
                        break;
                }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outPath);
        enc.Save(fs);
    }

    private static void DrawText(DrawingContext dc, Txt t, Typeface typeface, string text)
    {
        double size = t.Size;
        Geometry g;
        Rect bounds;
        while (true)
        {
            var ft = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                typeface, size, Brushes.White, 1.0);
            g = ft.BuildGeometry(new Point(0, 0));
            bounds = g.Bounds;
            if (bounds.Width <= t.W - 2 && bounds.Height <= t.H - 2) break;
            if (!t.Fit || size <= 8) break;
            size -= 0.5;
        }
        double dx = t.HCenter ? t.X + (t.W - bounds.Width) / 2 - bounds.X : t.X - bounds.X;
        double dy = t.VCenter ? t.Y + (t.H - bounds.Height) / 2 - bounds.Y : t.Y - bounds.Y;
        dc.PushTransform(new TranslateTransform(dx, dy));
        if (t.Rotate != 0)
            dc.PushTransform(new RotateTransform(t.Rotate, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        var fill = new SolidColorBrush(Color.FromArgb(t.Alpha, (byte)t.Fill.R, (byte)t.Fill.G, (byte)t.Fill.B));
        Pen? pen = t.Outline is null ? null : new Pen(new SolidColorBrush(
            Color.FromRgb((byte)t.Outline.Value.R, (byte)t.Outline.Value.G, (byte)t.Outline.Value.B)), t.PenW)
        { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(fill, pen, g);
        if (t.Rotate != 0) dc.Pop();
        dc.Pop();
    }

    // 文字像素掩码（按亮度/红色阈值 + 2px 膨胀吃抗锯齿边）+ 最近邻扩散填充
    private static void Inpaint(byte[] px, int w, int h, int stride, Edit e)
    {
        bool IsText(int x, int y)
        {
            int o = y * stride + x * 4;
            int b = px[o], g = px[o + 1], r = px[o + 2], a = px[o + 3];
            if (a < 40) return false;
            int lum = (r * 77 + g * 150 + b * 29) >> 8;
            return e.Mode switch
            {
                "dark" => lum < 100,
                "red" => r > 140 && r > g + 60 && r > b + 60,
                _ => false,
            };
        }
        var masked = new bool[w * h];
        for (int y = e.Y; y < Math.Min(e.Y + e.H, h); y++)
            for (int x = e.X; x < Math.Min(e.X + e.W, w); x++)
                masked[y * w + x] = IsText(x, y);
        for (int d = 0; d < 2; d++)
        {
            var prev = (bool[])masked.Clone();
            for (int y = e.Y; y < Math.Min(e.Y + e.H, h); y++)
                for (int x = e.X; x < Math.Min(e.X + e.W, w); x++)
                {
                    if (prev[y * w + x]) continue;
                    if ((x > 0 && prev[y * w + x - 1]) || (x < w - 1 && prev[y * w + x + 1]) ||
                        (y > 0 && prev[(y - 1) * w + x]) || (y < h - 1 && prev[(y + 1) * w + x]))
                        masked[y * w + x] = true;
                }
        }
        for (int pass = 0; pass < 800; pass++)
        {
            int filled = 0;
            for (int y = e.Y; y < Math.Min(e.Y + e.H, h); y++)
                for (int x = e.X; x < Math.Min(e.X + e.W, w); x++)
                {
                    if (!masked[y * w + x]) continue;
                    long bb = 0, bg = 0, br = 0, ba = 0; int n = 0;
                    foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    {
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h || masked[ny * w + nx]) continue;
                        int o = ny * stride + nx * 4;
                        bb += px[o]; bg += px[o + 1]; br += px[o + 2]; ba += px[o + 3]; n++;
                    }
                    if (n == 0) continue;
                    int oo = y * stride + x * 4;
                    px[oo] = (byte)(bb / n); px[oo + 1] = (byte)(bg / n);
                    px[oo + 2] = (byte)(br / n); px[oo + 3] = (byte)(ba / n);
                    masked[y * w + x] = false; filled++;
                }
            if (filled == 0) break;
        }
    }
}
