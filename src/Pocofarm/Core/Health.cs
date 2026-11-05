using System.Text.Json.Nodes;

namespace Pocofarm.Core;

/// <summary>健康状態の原因 1 件。Recent / Baseline は直近 30 件と直近 300 件の値。</summary>
public sealed record HealthCause(string Key, string Message, double? Recent, double? Baseline);

/// <summary>健康状態。Level は ok / warn / bad / unknown(直近の試行が足りない)。</summary>
public sealed record HealthReport(string Level, List<HealthCause> Causes, int Recent, int Baseline)
{
    public Dictionary<string, object?> ToJson() => new()
    {
        ["level"] = Level,
        ["recent"] = Recent,
        ["baseline"] = Baseline,
        ["causes"] = Causes.Select(c => new Dictionary<string, object?> { ["key"] = c.Key, ["message"] = c.Message, ["recent"] = c.Recent, ["baseline"] = c.Baseline }).ToList(),
    };
}

/// <summary>
/// 直近 30 件の試行を直近 300 件と比べて、いつもと違う状態(反応の遅れ・結果不明・見逃し・OCR 不調・FPS 低下)を見つける純粋関数。
/// 閾値は data/ の記録(良好な時間帯と不調な時間帯)に当てて、良好な時間帯で鳴らず不調な時間帯で鳴るように決めた。
/// 相対(基準との差)だけだと、ずっと不調な期間では基準も悪くなって鳴らないため、絶対値の下限も併用する。
/// </summary>
public static class Health
{
    public const int RecentN = 30, BaselineN = 300, MinRecent = 20;

    /// <summary>ダッシュボードが最後に計算した結果。釣りの本体(同じプロセス)が status.json に載せたいときに読む。</summary>
    public static HealthReport? Latest { get => latest; set => latest = value; }

    static volatile HealthReport? latest;

    // 相対: 反応 p95 が基準の 2 倍かつ 40ms 超 / 結果不明 +15pt / 見逃し +25pt / OCR 空 +30pt / FPS が基準の 6 割未満
    const double ReactRatio = 2.0, ReactMinMs = 40, UnknownDelta = 0.15, MissedDelta = 0.25, NoOcrDelta = 0.30, FpsRatio = 0.6;
    // 絶対: 反応 p95 60ms 以上 / 見逃し 30% 以上 / 結果不明 12% 以上 / 成功率 50% 以下 / FPS 15 未満
    const double ReactAbsMs = 60, MissedAbs = 0.30, UnknownAbs = 0.12, SuccessAbs = 0.50, FpsAbs = 15;

    static double Rate(List<JsonObject> w, Func<JsonObject, bool> f) => w.Count == 0 ? 0 : (double)w.Count(f) / w.Count;

    static double? P95(List<JsonObject> w)
    {
        var v = w.Select(e => e.Num("reaction_ms")).Where(x => x is not null).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : Stats.Percentile(v, 95);
    }

    /// <summary>attempts は時刻順。確認クリックの引き直し(no_bite_reset)は含めても外しても良い(ここで外す)。fps は直近のキャプチャ FPS の標本(古い順)。</summary>
    public static HealthReport Evaluate(IEnumerable<JsonObject> attempts, IReadOnlyList<double>? fps = null)
    {
        var all = attempts.Where(e => e.Str("type") is "attempt" or "" && e.Str("outcome") != "no_bite_reset").TakeLast(BaselineN).ToList();
        var rec = all.TakeLast(RecentN).ToList();
        var causes = new List<HealthCause>();
        if (rec.Count < MinRecent) return new("unknown", causes, rec.Count, all.Count);

        void Add(string key, string msg, double r, double b) => causes.Add(new(key, msg, Math.Round(r, 3), Math.Round(b, 3)));

        if (P95(rec) is { } rr)
        {
            double rb = P95(all) ?? rr;
            if (rr >= ReactAbsMs || (rr > ReactRatio * rb && rr > ReactMinMs)) Add("reaction", $"反応時間が遅い(p95 {rr:F0}ms / 基準 {rb:F0}ms)。キャプチャ FPS・CPU 負荷を確認", rr, rb);
        }
        double u = Rate(rec, e => e.Str("outcome") == "unknown"), ub = Rate(all, e => e.Str("outcome") == "unknown");
        if (u >= UnknownAbs || u - ub >= UnknownDelta) Add("unknown", $"結果不明が多い({u:P0} / 基準 {ub:P0})。トーストの位置・OCR を確認", u, ub);
        double m = Rate(rec, e => e.Str("outcome") == "missed_bite"), mb = Rate(all, e => e.Str("outcome") == "missed_bite");
        if (m >= MissedAbs || m - mb >= MissedDelta) Add("missed_bite", $"当たり見逃しが多い({m:P0} / 基準 {mb:P0})。ウキの検出・FPS を確認", m, mb);
        double o = Rate(rec, e => e.Str("engine").Length == 0), ob = Rate(all, e => e.Str("engine").Length == 0);
        if (o - ob >= NoOcrDelta) Add("ocr", $"結果を OCR で読めない試行が増えた({o:P0} / 基準 {ob:P0})。OCR エンジン・言語パックを確認", o, ob);
        double s = Rate(rec, e => e.Str("outcome") == "caught");
        if (s <= SuccessAbs) Add("success", $"成功率が低い({s:P0})", s, Rate(all, e => e.Str("outcome") == "caught"));
        if (fps is { Count: >= 10 })
        {
            double f = fps.TakeLast(RecentN).Average(), fb = fps.TakeLast(BaselineN).Average();
            if (f < FpsAbs || (f < fb * FpsRatio && fb - f >= 5)) Add("fps", $"キャプチャ FPS が低い({f:F0} / 基準 {fb:F0})。VRChat が最小化・背面になっていないか確認", f, fb);
        }
        // 成功率が落ちている、または原因が複数なら bad。原因が 1 つだけなら warn
        string level = causes.Count == 0 ? "ok" : causes.Count >= 2 || causes.Any(c => c.Key == "success") ? "bad" : "warn";
        return new(level, causes, rec.Count, all.Count);
    }
}
