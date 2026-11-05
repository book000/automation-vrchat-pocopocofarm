using System.Text.Json.Nodes;

namespace Pocofarm.Core;

// 日次/週次のまとめと、レベル到達の見込み(いずれも純粋関数)
public static partial class Stats
{
    /// <summary>コインは釣果の魚種から売値で見積もった値で、実際の所持コインや売却額ではない。</summary>
    public const string CoinsNote = "fish_coins(設定の売値表)から見積もった値です。実際の売却額・所持コインではありません";

    /// <summary>レアとみなす魚の出現率の上限[%]と、判定に必要な最小の釣果数。</summary>
    const double RareShare = 5;
    const int RareMinCatches = 20;
    const double OfflineGap = 300;

    static double Tm(JsonObject e) => e.Num("_t") ?? EventLog.Timestamp(e) ?? 0;

    /// <summary>ローカル時刻の日付(日の境界はこの日付で決める)。</summary>
    public static DateTime LocalDay(double t) => DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).ToLocalTime().Date;

    /// <summary>ローカルの日付 day の 0 時(epoch 秒)。</summary>
    public static double DayStart(DateTime day) => new DateTimeOffset(DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(day.Date)).ToUnixTimeSeconds();

    static (int Attempts, int Caught, double? Rate) Rate(List<JsonObject> attempts, double from, double to)
    {
        var real = attempts.Where(e => Tm(e) >= from && Tm(e) < to && e.Str("outcome") != "no_bite_reset").ToList();
        int c = real.Count(e => e.Str("outcome") == "caught");
        return (real.Count, c, real.Count > 0 ? R1(c * 100.0 / real.Count) : null);
    }

    /// <summary>一時停止の区間(pause→resume)。終わっていない停止は end まで。</summary>
    static List<(double S, double E)> PausedSpans(List<JsonObject> evs, double end)
    {
        var spans = new List<(double, double)>();
        double? open = null;
        foreach (var e in evs.OrderBy(Tm))
        {
            var ty = e.Str("type");
            if (ty == "pause") open ??= Tm(e);
            else if (ty == "resume" && open is { } s) { spans.Add((s, Tm(e))); open = null; }
        }
        if (open is { } o) spans.Add((o, Math.Max(o, end)));
        return spans;
    }

    static double Overlap(double a, double b, IEnumerable<(double S, double E)> spans) => spans.Sum(p => Math.Max(0, Math.Min(b, p.E) - Math.Max(a, p.S)));

    /// <summary>
    /// 期間 [from, to)(epoch 秒)のまとめ。前の同じ長さの期間と成功率を比べる。
    /// events は attempt・hud・pause/resume・session_start・gap・farm_scan を含む全イベント("_t" があれば使う)。
    /// 魚名の推定(InferMissingFish)は呼び出し側で済ませておく。
    /// </summary>
    public static Dictionary<string, object?> Summarize(IEnumerable<JsonObject> events, double from, double to, IReadOnlyDictionary<string, int>? coins = null)
    {
        var evs = events.ToList();
        var price = coins ?? new Dictionary<string, int>();
        var attempts = evs.Where(e => e.Str("type") == "attempt").ToList();
        double len = to - from;
        var cur = Rate(attempts, from, to);
        var prev = Rate(attempts, from - len, from);
        var inRange = attempts.Where(e => Tm(e) >= from && Tm(e) < to && e.Str("outcome") != "no_bite_reset").ToList();
        var caught = inRange.Where(e => e.Str("outcome") == "caught").ToList();

        var fish = caught.GroupBy(e => e.Str("fish") is { Length: > 0 } f ? f : "不明").OrderByDescending(g => g.Count()).Select(g => (Fish: g.Key, N: g.Count())).ToList();
        var topFish = fish.Take(5).Select(x => new Dictionary<string, object?> { ["fish"] = x.Fish, ["n"] = x.N, ["share"] = R1(x.N * 100.0 / caught.Count) }).ToList();
        var rare = caught.Count >= RareMinCatches
            ? fish.Where(x => x.Fish != "不明" && x.N * 100.0 / caught.Count <= RareShare).Select(x => new Dictionary<string, object?> { ["fish"] = x.Fish, ["n"] = x.N }).ToList()
            : [];

        var huds = evs.Where(e => e.Str("type") == "hud" && e.Num("level") is not null && Tm(e) >= from && Tm(e) < to).OrderBy(Tm).ToList();
        object? level = huds.Count > 0
            ? new Dictionary<string, object?> { ["from"] = huds[0].Num("level"), ["to"] = huds[^1].Num("level"), ["gained"] = huds[^1].Num("level") - huds[0].Num("level") }
            : null;

        double end = evs.Count > 0 ? Math.Min(to, evs.Max(Tm)) : to;
        var spans = PausedSpans(evs.Where(e => Tm(e) < to).ToList(), end);
        double paused = Overlap(from, to, spans);
        var times = inRange.Select(Tm).Order().ToList();
        double offline = 0;
        for (int i = 1; i < times.Count; i++)
            if (times[i] - times[i - 1] > OfflineGap) offline += times[i] - times[i - 1] - Overlap(times[i - 1], times[i], spans);
        double gaps = evs.Where(e => e.Str("type") == "gap" && Tm(e) >= from && Tm(e) < to).Sum(e => e.Num("seconds") ?? 0);

        var scans = evs.Where(e => e.Str("type") == "farm_scan" && Tm(e) >= from && Tm(e) < to).OrderBy(Tm).ToList();
        var lastScan = scans.LastOrDefault(e => (e.Num("plots") ?? 0) > 0);      // 畑が見つからなかった撮影は状態として使わない
        object? farm = lastScan is null ? null : new Dictionary<string, object?>
        {
            ["scans"] = scans.Count,
            ["last_t"] = Tm(lastScan),
            ["plots"] = lastScan.Num("plots"),
            ["unplanted"] = lastScan.Num("unplanted"),
            ["dry"] = lastScan.Num("dry"),
            ["done"] = lastScan.Num("done"),
            ["hidden"] = lastScan.Num("hidden"),
        };

        return new()
        {
            ["from"] = from,
            ["to"] = to,
            ["attempts"] = cur.Attempts,
            ["caught"] = cur.Caught,
            ["success_rate"] = cur.Rate,
            ["prev"] = new Dictionary<string, object?> { ["attempts"] = prev.Attempts, ["caught"] = prev.Caught, ["success_rate"] = prev.Rate },
            ["success_delta"] = cur.Rate is { } a && prev.Rate is { } b ? R1(a - b) : null,
            ["exp"] = (int)caught.Sum(e => e.Num("exp") ?? 0),
            ["coins_est"] = caught.Sum(e => price.GetValueOrDefault(e.Str("fish"))),
            ["coins_note"] = CoinsNote,
            ["level"] = level,
            ["top_fish"] = topFish,
            ["rare"] = rare,
            ["downtime"] = new Dictionary<string, object?>
            {
                ["paused_s"] = (int)Math.Round(paused),
                ["offline_s"] = (int)Math.Round(offline),
                ["capture_gap_s"] = (int)Math.Round(gaps),
                ["restarts"] = evs.Count(e => e.Str("type") == "session_start" && Tm(e) >= from && Tm(e) < to),
            },
            ["farm"] = farm,
        };
    }

    /// <summary>
    /// レベル到達の見込みと日別の推移。events は attempt と hud を含む("_t" があれば使う)。now は現在(epoch 秒)。
    /// 1 レベルに要る経験値は、観測したレベルアップの間(丸ごと観測できたレベル)の獲得経験値の中央値。無ければ経験値バーの動きとの比で見積もる。
    /// </summary>
    public static Dictionary<string, object?> Progress(IEnumerable<JsonObject> events, double now, IReadOnlyDictionary<string, int>? coins = null, int goal = 0, int days = 7)
    {
        var evs = events.ToList();
        var price = coins ?? new Dictionary<string, int>();
        var caught = evs.Where(e => e.Str("type") == "attempt" && e.Str("outcome") == "caught").OrderBy(Tm).ToList();
        double Exp(double a, double b) => caught.Where(e => Tm(e) > a && Tm(e) <= b).Sum(e => e.Num("exp") ?? 0);

        // 読み違いで下がった値は捨てる(単調増加に直す)
        var pts = new List<(double T, double V, int Lv)>();
        foreach (var e in evs.Where(e => e.Str("type") == "hud" && e.Num("level") is not null).OrderBy(Tm))
        {
            int lv = (int)e.Num("level")!.Value;
            double v = lv + Math.Clamp(e.Num("xp") ?? 0, 0, 0.999);
            if (pts.Count > 0 && (v < pts[^1].V - 0.001 || lv > pts[^1].Lv + 1)) continue;
            pts.Add((Tm(e), v, lv));
        }

        var ups = new List<double>();
        for (int i = 1; i < pts.Count; i++) if (pts[i].Lv > pts[i - 1].Lv) ups.Add(pts[i].T);
        var samples = new List<double>();
        for (int i = 1; i < ups.Count; i++) { double x = Exp(ups[i - 1], ups[i]); if (x > 0) samples.Add(x); }

        double? perLevel = null; string? source = null;
        if (samples.Count > 0) { perLevel = Percentile(samples.TakeLast(5).ToList(), 50); source = "level_ups"; }
        else if (pts.Count >= 2 && pts[^1].V - pts[0].V >= 0.3 && Exp(pts[0].T, pts[^1].T) > 0) { perLevel = R1(Exp(pts[0].T, pts[^1].T) / (pts[^1].V - pts[0].V)); source = "bar"; }

        double hourExp = Exp(now - 3600, now);
        double? expPerHour = hourExp > 0 ? R1(hourExp) : null;
        object? level = null; object? eta = null;
        if (pts.Count > 0)
        {
            var last = pts[^1];
            level = new Dictionary<string, object?> { ["level"] = last.Lv, ["xp"] = R1((last.V - last.Lv) * 100) };
            if (perLevel is { } pl && expPerHour is { } rate)
            {
                double remain = (1 - (last.V - last.Lv)) * pl;
                eta = new Dictionary<string, object?>
                {
                    ["next_level"] = last.Lv + 1,
                    ["hours"] = R1(remain / rate),
                    ["goal"] = goal > last.Lv ? goal : null,
                    ["goal_hours"] = goal > last.Lv ? R1((remain + (goal - last.Lv - 1) * pl) / rate) : null,
                };
            }
        }

        // 日別(ローカルの日付)。稼働時間は 5 分刻みで試行があった枠の数から数える
        var today = LocalDay(now);
        var daily = new List<Dictionary<string, object?>>();
        for (int d = days - 1; d >= 0; d--)
        {
            var day = today.AddDays(-d);
            var cs = caught.Where(e => LocalDay(Tm(e)) == day).ToList();
            var act = evs.Where(e => e.Str("type") == "attempt" && LocalDay(Tm(e)) == day).Select(e => (long)(Tm(e) / 300)).Distinct().Count() * 300 / 3600.0;
            int exp = (int)cs.Sum(e => e.Num("exp") ?? 0), c = cs.Sum(e => price.GetValueOrDefault(e.Str("fish")));
            daily.Add(new()
            {
                ["day"] = day.ToString("yyyy-MM-dd"),
                ["caught"] = cs.Count,
                ["exp"] = exp,
                ["coins_est"] = c,
                ["active_h"] = R1(act),
                ["exp_per_h"] = act > 0 ? R1(exp / act) : null,
                ["coins_per_h"] = act > 0 ? R1(c / act) : null,
            });
        }
        return new()
        {
            ["level"] = level,
            ["exp_per_level"] = perLevel,
            ["exp_per_level_source"] = source,
            ["level_up_samples"] = samples.Select(R1).ToList(),
            ["exp_per_hour"] = expPerHour,
            ["eta"] = eta,
            ["daily"] = daily,
            ["coins_note"] = CoinsNote,
        };
    }
}
