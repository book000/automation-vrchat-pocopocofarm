using System.Text.RegularExpressions;

namespace Pocofarm.Core;

/// <summary>分類済みトースト。Kind は caught/early/escaped/aim/levelup/achieve。</summary>
public sealed record Toast(string Kind, string Text, string Fish = "", int? Exp = null, int? Count = null, bool Strong = false)
{
    public const string Caught = "caught", Early = "early", Escaped = "escaped", Aim = "aim", LevelUp = "levelup", Achieve = "achieve";

    static readonly Regex EarlyRe = new("かかっていません|まだかか|ぴくぴく沈|びくびく沈");
    static readonly Regex EscapedRe = new("逃げられ|げられた|逃げ");
    static readonly Regex AimRe = new("池の水に向け|水へ向けて|池から1[~〜～-]5m|竿の先を水");
    static readonly Regex LevelUpRe = new(@"レベル\d*になった|レベル.{0,4}になっ");
    static readonly Regex AchieveRe = new("実績|達成");
    static readonly Regex CaughtRe = new(@"釣れ|経験[値価直]|持っている\d+");
    static readonly Regex FishRe = new(@"([^\s!！+＋、。:：]{1,10}?)が釣れ");
    static readonly Regex ExpRe = new(@"[+＋](\d+)");
    static readonly Regex CountRe = new(@"持っている(\d+)");
    static readonly Regex Ws = new(@"\s+");
    // 手伝いを頼む画面などのメニュー文言は通知ではない
    static readonly Regex NekoMenuRe = new("お手伝いさん|押すと|クリックすると|してもらう");
    static readonly Regex NekoRe = new(@"ね[こコ]ち.{0,2}ん[:：;]?");


    /// <summary>より情報が揃った読取りを優先するための点数。</summary>
    public int Score => (Strong ? 2 : 0) + (Fish.Length > 0 ? 1 : 0) + (Exp is not null ? 1 : 0) + (Count is not null ? 1 : 0);

    /// <summary>OCR 文字列をトーストに分類する。該当なしは null。優先順位: レベルアップ/実績/視線ずれ/早すぎ/逃げられた/釣果。</summary>
    public static Toast? Classify(string? text, IReadOnlyList<string>? knownFish = null)
    {
        var t = Ws.Replace(text ?? "", "");
        if (t.Length == 0) return null;
        if (LevelUpRe.IsMatch(t)) return new(LevelUp, t, Strong: true);
        if (AchieveRe.IsMatch(t)) return new(Achieve, t, Strong: true);
        if (AimRe.IsMatch(t)) return new(Aim, t, Strong: true);
        if (EarlyRe.IsMatch(t)) return new(Early, t, Strong: t.Contains("かかっていません"));
        if (EscapedRe.IsMatch(t)) return new(Escaped, t, Strong: t.Contains("逃げられた"));
        if (!CaughtRe.IsMatch(t)) return null;
        var fish = "";
        var m = FishRe.Match(t);
        if (m.Success) fish = NormalizeFish(m.Groups[1].Value, knownFish ?? []);
        var exp = ExpRe.Match(t);
        var cnt = CountRe.Match(t);
        int? expValue = exp.Success ? ParseInt(exp.Groups[1].Value) : null;
        if (fish.Length > 0 && knownFish is { Count: > 0 } && !knownFish.Contains(fish) && expValue is { } ev) fish = FishByExp(fish, ev, knownFish);
        return new(Caught, t, fish, expValue, cnt.Success ? ParseInt(cnt.Groups[1].Value) : null, t.Contains("釣れ") && exp.Success);
    }

    /// <summary>ねこちゃんに関する通知を取り出す。「ねこちゃん: 本文」なら本文、「ねこちゃんに〜わたしました」のような文なら全文を返す。無ければ null。釣果などと同じ領域に重なって出るので分類とは独立に見る。</summary>
    public static string? ExtractNeko(string? text)
    {
        var t = Ws.Replace(text ?? "", "");
        var m = NekoRe.Match(t);
        if (!m.Success || NekoMenuRe.IsMatch(t)) return null;
        // 「ねこちゃん:」のように話者として出るときは本文だけ、文中に出るときは文の全体を残す
        var body = m.Value.EndsWith(':') || m.Value.EndsWith('：') || m.Value.EndsWith(';') ? t[(m.Index + m.Length)..] : t;
        return body.Length >= 2 ? body[..Math.Min(body.Length, 120)] : null;
    }

    /// <summary>全角数字も読める整数変換。</summary>
    public static int ParseInt(string s) => int.Parse(new string(s.Select(c => (char)('0' + (int)char.GetNumericValue(c))).ToArray()));

    // 魚ごとの獲得経験値(実測)。名前の文字が読み違えられたとき(コイ→ロイ)に、経験値から魚を補うために使う
    static readonly Dictionary<string, int> FishExp = new() { ["フナ"] = 2, ["ドジョウ"] = 2, ["コイ"] = 4, ["ナマズ"] = 6, ["ニジマス"] = 8, ["金色のコイ"] = 30 };

    // 竿 Lv5(経験値 2.4 倍)での実測値。ニジマスとエビ、フナとドジョウは同じ値なので、名前の近さで決める
    static readonly Dictionary<string, int> FishExpRod5 = new()
    {
        ["フナ"] = 5, ["ドジョウ"] = 5, ["コイ"] = 10, ["ナマズ"] = 14, ["ニジマス"] = 19, ["エビ"] = 19, ["サケ"] = 24,
        ["金色のコイ"] = 72, ["イトウ"] = 144, ["ヒラメ"] = 432, ["ブリ"] = 480, ["タイ"] = 528, ["マグロ"] = 600,
    };

    /// <summary>知らない魚名のとき、同じ経験値の既知の魚が 1 種類ならその魚、複数なら名前が最も近い魚(0.3 以上)に寄せる。決められなければ読んだままにする。</summary>
    public static string FishByExp(string name, int exp, IReadOnlyList<string> known)
    {
        var cands = known.Where(k => FishExpRod5.TryGetValue(k, out var e) && e == exp).ToList();
        if (cands.Count == 0) cands = known.Where(k => FishExp.TryGetValue(k, out var e) && e == exp).ToList();
        if (cands.Count == 1) return cands[0];
        var best = cands.Select(k => (k, r: SeqRatio(name, k))).Where(x => x.r >= 0.3).OrderByDescending(x => x.r).FirstOrDefault();
        return best.k ?? name;
    }

    /// <summary>装飾ノイズを除き、既知の魚名に近ければ寄せる(ドドジョウ→ドジョウ)。</summary>
    static string NormalizeFish(string raw, IReadOnlyList<string> known)
    {
        var name = raw.Trim("ーｰ‐‑–—-・|_ 　".ToCharArray());
        if (name.Length == 0) return "";
        if (known.Contains(name)) return name;
        var tails = known.Where(k => name.EndsWith(k, StringComparison.Ordinal)).ToList();
        if (tails.Count > 0) return tails.MaxBy(k => k.Length)!;
        // difflib.get_close_matches(cutoff=0.6) 相当: 最高スコア、同点は文字列の大きい方
        string? best = null; double bestScore = 0;
        foreach (var k in known)
        {
            var s = SeqRatio(name, k);
            if (s < 0.6) continue;
            if (best is null || s > bestScore || (s == bestScore && string.CompareOrdinal(k, best) > 0)) { best = k; bestScore = s; }
        }
        return best ?? name;
    }

    /// <summary>difflib.SequenceMatcher.ratio() の移植(junk 無し)。</summary>
    public static double SeqRatio(string a, string b) =>
        a.Length + b.Length == 0 ? 1.0 : 2.0 * Matches(a, 0, a.Length, b, 0, b.Length) / (a.Length + b.Length);

    static int Matches(string a, int alo, int ahi, string b, int blo, int bhi)
    {
        if (alo >= ahi || blo >= bhi) return 0;
        // 最長一致(同長なら a の前、次に b の前)
        int bi = 0, bj = 0, bk = 0;
        var prev = new int[bhi - blo + 1];
        for (int i = alo; i < ahi; i++)
        {
            var cur = new int[bhi - blo + 1];
            for (int j = blo; j < bhi; j++)
            {
                if (a[i] != b[j]) continue;
                int k = cur[j - blo + 1] = prev[j - blo] + 1;
                if (k > bk) { bi = i - k + 1; bj = j - k + 1; bk = k; }
            }
            prev = cur;
        }
        if (bk == 0) return 0;
        return bk + Matches(a, alo, bi, b, blo, bj) + Matches(a, bi + bk, ahi, b, bj + bk, bhi);
    }
}
