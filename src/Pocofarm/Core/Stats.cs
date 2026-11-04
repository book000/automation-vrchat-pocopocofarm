using System.Text.Json.Nodes;

namespace Pocofarm.Core;

/// <summary>試行イベントから調整・運用向けの統計を計算する(ダッシュボード用の純粋関数)。</summary>
public static class Stats
{
    static readonly int[] WithinCounts = [10, 50, 100];

    static double R1(double v) => Math.Round(v, 1);

    /// <summary>線形補間のパーセンタイル。空なら null。</summary>
    public static double? Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0) return null;
        var v = values.Order().ToArray();
        double k = (v.Length - 1) * p / 100;
        int lo = (int)k, hi = Math.Min(lo + 1, v.Length - 1);
        return R1(v[lo] + (v[hi] - v[lo]) * (k - lo));
    }

    static Dictionary<string, object?> Dist(List<double> v) => new()
    {
        ["n"] = v.Count, ["p50"] = Percentile(v, 50), ["p95"] = Percentile(v, 95), ["max"] = v.Count > 0 ? R1(v.Max()) : null,
    };

    /// <summary>当たりまでの待ち分布。1 秒刻みで実測の最大値までだけ並べ、30 秒を超えた分は末尾にまとめる(空の区間で画面を埋めない)。</summary>
    static List<Dictionary<string, object?>> WaitHist(List<double> values)
    {
        if (values.Count == 0) return [];
        int bins = Math.Min(30, (int)Math.Ceiling(values.Max()));
        var counts = new int[Math.Max(bins, 1) + 1];
        foreach (var w in values) counts[Math.Min((int)w, counts.Length - 1)]++;
        var rows = new List<Dictionary<string, object?>>();
        for (int k = 0; k < counts.Length - 1; k++) rows.Add(new() { ["label"] = $"{k}-{k + 1}s", ["n"] = counts[k] });
        if (counts[^1] > 0 || values.Max() > bins) rows.Add(new() { ["label"] = $"{counts.Length - 1}s+", ["n"] = counts[^1] });
        return rows;
    }

    /// <summary>
    /// レベルアップなどの表示で魚名が読めなかった釣果の魚種を、「持っている N匹」の飛びから推定して書き込む(メモリ上のみ。記録ファイルは変えない)。
    /// known を渡すと、その一覧にない魚名(OCR の読み違い)も読めなかった扱いにする。
    /// 魚ごとの所持数は釣るたびに +1 なので、不明が 1 件あった魚は、次に読めた所持数が +2 に飛ぶ。推定した行には fish_inferred を付ける。
    /// </summary>
    public static void InferMissingFish(IEnumerable<JsonObject> attempts, ICollection<string>? known = null)
    {
        var inv = new Dictionary<string, int>();
        var pending = new Queue<JsonObject>();
        foreach (var e in attempts)
        {
            if (e.Str("outcome") != "caught") continue;
            var fish = e.Str("fish");
            // OCR の読み違いで一覧にない名前になったものは、魚名が読めなかった扱いにして所持数の飛びから推定する
            if (known is not null && fish.Length > 0 && !known.Contains(fish)) { e["fish"] = ""; fish = ""; }
            if (fish.Length == 0) { pending.Enqueue(e); continue; }
            if (e.Num("count") is not { } c) continue;
            if (inv.TryGetValue(fish, out var prev) && c - prev >= 2 && pending.Count > 0)
            {
                var u = pending.Dequeue();
                u["fish"] = fish; u["fish_inferred"] = true;
            }
            inv[fish] = (int)c;
        }
    }

    static List<Dictionary<string, object?>> Places(List<JsonObject> attempts)
    {
        var groups = new Dictionary<(int, int), List<JsonObject>>();
        foreach (var e in attempts)
        {
            if (e["pos"] is not JsonArray { Count: >= 2 } pos || e.Str("outcome") == "no_bite_reset" || pos[0].AsNum() is not { } px || pos[1].AsNum() is not { } py) continue;
            var key = ((int)Math.Floor(px / 80) * 80, (int)Math.Floor(py / 80) * 80);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(e);
        }
        return groups.Select(g =>
        {
            var widths = g.Value.Select(e => e.Num("bobber_w")).Where(w => w is > 0).Select(w => w!.Value).ToList();
            int caught = g.Value.Count(e => e.Str("outcome") == "caught");
            return new Dictionary<string, object?>
            {
                ["x"] = g.Key.Item1, ["y"] = g.Key.Item2, ["w"] = Percentile(widths, 50), ["n"] = g.Value.Count,
                ["caught"] = caught, ["rate"] = R1(caught * 100.0 / g.Value.Count),
            };
        }).OrderByDescending(r => (int)r["n"]!).Take(8).ToList();
    }

    /// <summary>二項割合の Wilson 区間(95%)。</summary>
    public static (double Lo, double Hi) Wilson(int k, int n, double z = 1.96)
    {
        if (n == 0) return (0.0, 1.0);
        double p = (double)k / n, d = 1 + z * z / n, c = (p + z * z / (2 * n)) / d;
        double m = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
        return (Math.Max(0.0, c - m), Math.Min(1.0, c + m));
    }

    /// <summary>釣果の履歴から「次の1匹」の魚種確率を推定する。隣り合う魚の独立性をカイ二乗で確かめる。</summary>
    public static Dictionary<string, object?> NextCatch(IEnumerable<JsonObject> caught, IReadOnlyDictionary<string, int> coins)
    {
        var seq = caught.Select(e => e.Str("fish")).Where(f => f.Length > 0).ToList();
        int n = seq.Count;
        var c = new Dictionary<string, int>();
        foreach (var f in seq) c[f] = c.GetValueOrDefault(f) + 1;
        var kinds = c.Keys.ToList();
        var pairs = new Dictionary<(string, string), int>();
        for (int i = 0; i + 1 < n; i++) pairs[(seq[i], seq[i + 1])] = pairs.GetValueOrDefault((seq[i], seq[i + 1])) + 1;
        double chi = 0;
        foreach (var a in kinds)
        {
            int ra = kinds.Sum(b => pairs.GetValueOrDefault((a, b)));
            foreach (var b in kinds)
            {
                double e = n > 0 ? (double)ra * c[b] / n : 0;
                if (e > 0) chi += Math.Pow(pairs.GetValueOrDefault((a, b)) - e, 2) / e;
            }
        }
        int df = (kinds.Count - 1) * (kinds.Count - 1);
        var probs = c.OrderByDescending(kv => kv.Value).Select(kv =>
        {
            var (lo, hi) = Wilson(kv.Value, n);
            return new Dictionary<string, object?>
            {
                ["fish"] = kv.Key, ["p"] = R1((double)kv.Value / n * 100), ["lo"] = R1(lo * 100), ["hi"] = R1(hi * 100), ["n"] = kv.Value,
            };
        }).ToList();
        var rare = probs.Count > 0 ? probs.MinBy(r => (double)r["p"]!) : null;
        object? rareObj = null;
        if (rare is not null)
        {
            double rp = (double)rare["p"]! / 100;
            rareObj = new Dictionary<string, object?>
            {
                ["fish"] = rare["fish"],
                ["within"] = WithinCounts.ToDictionary(m => m.ToString(), m => (int)Math.Round((1 - Math.Pow(1 - rp, m)) * 100)),
            };
        }
        return new()
        {
            ["n"] = n, ["probs"] = probs, ["chi2"] = R1(chi), ["df"] = df,
            // 自由度 df のカイ二乗が平均+2σ(df+2√(2df))以内なら「前の魚に依存しない」と見なす
            ["independent"] = df == 0 || chi <= df + 2 * Math.Sqrt(2.0 * df),
            ["expected_coins"] = n > 0 ? R1(probs.Sum(r => (int)r["n"]! / (double)n * coins.GetValueOrDefault((string)r["fish"]!))) : null,
            ["rare"] = rareObj,
        };
    }

    /// <summary>attempt イベント(各要素に "_t" = 発生時刻の epoch 秒)から統計を作る。</summary>
    public static Dictionary<string, object?> Compute(List<JsonObject> attempts, double now,
        IReadOnlyDictionary<string, int>? coins = null, IEnumerable<JsonObject>? history = null)
    {
        var price = coins ?? new Dictionary<string, int>();
        var real = attempts.Where(e => e.Str("outcome") != "no_bite_reset").ToList();   // 確認クリックは成否の対象外
        double T(JsonObject e) => e.Num("_t") ?? 0;
        bool Caught(JsonObject e) => e.Str("outcome") == "caught";

        int streak = 0;
        for (int i = real.Count - 1; i >= 0 && Caught(real[i]); i--) streak++;
        var hourCaught = real.Where(e => T(e) >= now - 3600 && Caught(e)).ToList();
        var caughtT = real.Where(Caught).Select(T).TakeLast(50).ToList();
        var gaps = caughtT.Zip(caughtT.Skip(1), (a, b) => b - a).Where(g => g < 120).ToList();   // 長い中断を挟んだ間隔は除く
        var caughtAll = real.Where(Caught).ToList();
        var fishN = caughtAll.GroupBy(e => e.Str("fish") is { Length: > 0 } f ? f : "不明")
            .OrderByDescending(g => g.Count()).Select(g => (Fish: g.Key, N: g.Count())).ToList();
        var inventory = new Dictionary<string, int>();
        foreach (var e in caughtAll)
            if (e.Str("fish") is { Length: > 0 } f && e.Num("count") is { } cnt) inventory[f] = (int)cnt;

        int PriceOf(string f) => price.GetValueOrDefault(f);
        int CoinOf(IEnumerable<JsonObject> es) => es.Sum(e => PriceOf(e.Str("fish")));   // 魚種不明は0コイン
        var fish = fishN.Select(x => new Dictionary<string, object?>
        {
            ["fish"] = x.Fish, ["n"] = x.N, ["share"] = R1(x.N * 100.0 / caughtAll.Count), ["every"] = R1((double)caughtAll.Count / x.N),
            ["price"] = price.TryGetValue(x.Fish, out var p) ? p : null, ["coins"] = x.N * PriceOf(x.Fish),
        }).ToList();
        var waits = real.Select(e => e.Num("wait_s")).Where(w => w is not null).Select(w => w!.Value).ToList();
        List<double> Col(string k) => real.Select(e => e.Num(k)).Where(v => v is not null).Select(v => v!.Value).ToList();
        var lastFail = real.LastOrDefault(e => !Caught(e));
        int total = CoinOf(caughtAll);

        return new()
        {
            ["streak"] = streak,
            ["since_failure_s"] = real.Count > 0 ? (int)Math.Round(now - (lastFail is null ? now : T(lastFail))) : null,
            ["reaction"] = Dist(Col("reaction_ms")),
            ["result"] = Dist(Col("result_ms")),
            ["wait"] = Dist(waits),
            ["wait_hist"] = WaitHist(waits),
            ["next"] = NextCatch((history ?? []).Concat(caughtAll).Where(Caught), price),
            ["coins_total"] = total,
            ["coins_per_hour"] = CoinOf(hourCaught),
            ["coins_per_catch"] = caughtAll.Count > 0 ? R1((double)total / caughtAll.Count) : null,
            ["exp_per_hour"] = (int)hourCaught.Sum(e => e.Num("exp") ?? 0),
            ["cycle_s"] = Percentile(gaps, 50),
            ["fish"] = fish, ["fish_inferred"] = caughtAll.Count(e => e["fish_inferred"] is not null),
            ["inventory"] = inventory,
            ["engines"] = real.Select(e => e.Str("engine")).Where(s => s.Length > 0).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count()),
            ["places"] = Places(attempts),
        };
    }
}
