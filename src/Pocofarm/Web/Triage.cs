using System.Text.Json.Nodes;
using Pocofarm.Core;

namespace Pocofarm.Web;

/// <summary>失敗した試行を原因(結果の種類)ごとにまとめ、証拠画像・ウキの軌跡・対処の目安を添える(純粋関数。対処は助言だけで自動では何も変えない)。</summary>
public static class Triage
{
    public const string Source = "docs/troubleshooting.md";

    /// <summary>docs/troubleshooting.md の該当節から起こした対処の目安。</summary>
    public static readonly Dictionary<string, string[]> Advice = new()
    {
        ["unknown"] =
        [
            "証拠画像を開き、結果のトースト(画面下)が toast_roi の中にあるか確認する。",
            "Windows の日本語 OCR 言語パック(OCR コンポーネント込み)が入っているか確認する。",
            "他のオーバーレイがトーストの位置を隠していないか確認する。",
        ],
        ["early"] =
        [
            "引くのが早すぎた。自動調整が沈みの回数(max_pecks_to_reel まで)を増やすので、様子を見る。",
            "続く場合は、視点の揺れや近くの他人のウキでウキが揺れていないか確認する。",
        ],
        ["missed_bite"] =
        [
            "引く前に「逃げられた」が出た=沈みを検出できていない。右のウキの軌跡で沈みが見えているか確認する。",
            "dip_ratio / dip_min_px を下げる。キャプチャ FPS も確認する(FPS カードや doctor)。",
        ],
        ["escaped"] = ["引くのが遅れて魚に逃げられた。多くは遅い引き。反応時間(p95)とキャプチャ FPS を確認する。"],
        ["cast_failed"] =
        [
            "視点が池を向いているか確認する(竿を持って池に向く)。視点補正(aim_recover)の結果はイベントで確認できる。",
            "ウキが竿先に残る不発なら、自動調整が reel_to_cast の待ちを延ばす。遅い環境では reel_to_cast_min / reel_to_cast_max を上げる。",
        ],
    };

    static IEnumerable<(double T, double Y, double W)> TracePoints(JsonObject e)
    {
        if (e["trace"] is not JsonArray a) yield break;
        foreach (var p in a)
            if (p is JsonArray { Count: >= 3 } r && r[0].AsNum() is { } t && r[1].AsNum() is { } y && r[2].AsNum() is { } w) yield return (t, y, w);
    }

    /// <summary>
    /// since(epoch 秒)以降の失敗をまとめる。各試行は "_t" を持つか ts から読む。
    /// 戻りは { total, groups: [{ outcome, n, share, advice, hints, examples: [...] }] }(件数の多い順)。
    /// </summary>
    public static Dictionary<string, object?> Build(IEnumerable<JsonObject> attempts, double since, Config cfg, int examples = 6)
    {
        var fails = attempts.Where(e => e.Str("type") == "attempt" && e.Str("outcome") is not ("caught" or "no_bite_reset" or "")
            && (e.Num("_t") ?? EventLog.Timestamp(e) ?? 0) >= since).ToList();
        var groups = fails.GroupBy(e => e.Str("outcome")).OrderByDescending(g => g.Count()).Select(g =>
        {
            var ex = g.TakeLast(examples).Reverse().Select(e =>
            {
                var pts = TracePoints(e).ToList();
                return new Dictionary<string, object?>
                {
                    ["attempt"] = e.Num("attempt"), ["ts"] = e.Str("ts"), ["note"] = e.Str("note"), ["ocr"] = e.Str("ocr"), ["engine"] = e.Str("engine"),
                    ["evidence"] = e.Str("evidence"), ["evidence_click"] = e.Str("evidence_click"), ["pecks_seen"] = e.Num("pecks_seen"),
                    ["trace"] = pts.Select(p => new[] { p.T, p.Y, p.W }).ToList(),
                    ["trace_range_px"] = pts.Count > 0 ? Math.Round(pts.Where(p => p.Y > 0).Select(p => p.Y).DefaultIfEmpty(0).Max() - pts.Where(p => p.Y > 0).Select(p => p.Y).DefaultIfEmpty(0).Min(), 1) : null,
                };
            }).ToList();
            var hints = new List<string>();
            if (g.Key == "missed_bite")
            {
                var ranges = ex.Select(x => x["trace_range_px"]).OfType<double>().ToList();
                if (ranges.Count > 0) hints.Add($"表示中の例でウキの縦の動きは最大 {ranges.Max():F0}px(沈み判定は dip_min_px={cfg.DipMinPx:F0}px、dip_ratio={cfg.DipRatio})。");
            }
            if (g.Key == "unknown")
            {
                int noOcr = g.Count(e => e.Str("engine").Length == 0);
                if (noOcr > 0) hints.Add($"{g.Count()} 件中 {noOcr} 件は OCR が文字を返していません(トーストが写っていない可能性)。");
            }
            return new Dictionary<string, object?>
            {
                ["outcome"] = g.Key, ["n"] = g.Count(), ["share"] = Math.Round(g.Count() * 100.0 / fails.Count, 1),
                ["advice"] = Advice.GetValueOrDefault(g.Key) ?? [], ["hints"] = hints, ["examples"] = ex,
            };
        }).ToList();
        return new() { ["total"] = fails.Count, ["groups"] = groups, ["source"] = Source, ["note"] = "対処は目安です。何も自動では変更しません" };
    }
}
