namespace Franca.Services;

// 悬浮窗状态组装（lab/011）：纯逻辑、数据源全部可注入，探针可测；窗口只做渲染。
// 展示语义是状态指示而非进度条（lab/010：逐条按需，不存在总量）：
// 在途>0 → 正在翻译；失败>0 → 提示查端点健康；数字长期不动 → 可能没指向中继，跑诊断。
// 硬限制：独占全屏的游戏看不到悬浮窗，建议无边框模式（设置页提示与此注释同步）。
public sealed class OverlayStatusModel
{
    // 中继计数器快照（原始值；命中率在此计算，避免调用方重复携带）
    public sealed record Counts(long Served, long CacheHits, long DictHits, long InFlight, long Failures);

    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _relayRunning;
    private readonly Func<Counts> _counts;

    public OverlayStatusModel(Func<AppSettings> settings, Func<bool> relayRunning, Func<Counts> counts)
    {
        _settings = settings;
        _relayRunning = relayRunning;
        _counts = counts;
    }

    // 开关打开且中继在跑才显示；中继停止或开关关闭即隐藏，避免展示陈旧数据
    public bool ShouldShow => _settings().OverlayEnabled && _relayRunning();

    // 三行状态：模式 / 已处理+缓存命中率+词典命中率 / 在途+失败（附提示）
    public IReadOnlyList<string> BuildLines()
    {
        var counts = _counts();
        var mode = _settings().QualityMode ? UiStrings.Get("mode.quality") : UiStrings.Get("mode.speed");
        var cacheRate = Rate(counts.Served, counts.CacheHits);
        var dictRate = Rate(counts.Served, counts.DictHits);
        return new[]
        {
            UiStrings.Format("overlay.mode", mode),
            UiStrings.Format("overlay.served", counts.Served, cacheRate, dictRate),
            UiStrings.Format("overlay.flight", counts.InFlight, counts.Failures) + Hint(counts),
        };
    }

    private static int Rate(long served, long hits) =>
        served == 0 ? 0 : (int)Math.Round(100.0 * hits / served);

    private static string Hint(Counts counts)
    {
        if (counts.InFlight > 0) return " · " + UiStrings.Get("overlay.hint.wait");
        if (counts.Failures > 0) return " · " + UiStrings.Get("overlay.hint.fail");
        return "";
    }
}
