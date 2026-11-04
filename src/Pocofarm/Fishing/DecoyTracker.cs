namespace Pocofarm.Fishing;

/// <summary>
/// 自分のウキではない候補(他人のウキ・アバターの赤白)が見えた升目を、回数と最後に見た時刻で覚える。
/// 2 回以上見えた升目とその周囲 1 升を、探索から除く矩形にする。
/// </summary>
internal sealed class DecoyTracker
{
    /// <summary>升目ごとの最大カウント(古い記憶が永久に居座らないよう頭打ちにする)。</summary>
    internal const int MaxCount = 50;

    /// <summary>釣れない結果が何回続いたら覚えを捨てるか。</summary>
    internal const int StaleStreakLimit = 6;

    readonly Dictionary<(int, int), (int Count, double Last)> heat = [];
    int staleStreak;

    /// <summary>除外する矩形(画素座標 x0,y0,x1,y1)。<see cref="Learn"/> のたびに作り直す。</summary>
    public List<(int, int, int, int)> Rects { get; private set; } = [];

    /// <summary>覚えている升目の数。</summary>
    public int CellCount => heat.Count;

    /// <summary>
    /// 今見えているウキ候補を升目ごとに積み上げ、除外矩形を作り直す。
    /// tip(竿先の自分のウキ)と expectAway(引けずに水中に残った自分のウキ)の近くは数えない。
    /// </summary>
    public void Learn(IEnumerable<(int X, int Y)> candidates, (int X, int Y)? tip, (int X, int Y)? expectAway, int pad, int cell, double now, double keepSeconds)
    {
        foreach (var k in heat.Where(kv => now - kv.Value.Last > keepSeconds).Select(kv => kv.Key).ToList()) heat.Remove(k);
        var seen = new HashSet<(int, int)>();
        foreach (var c in candidates)
        {
            if (tip is { } t && Math.Abs(c.X - t.X) <= pad * 2 && Math.Abs(c.Y - t.Y) <= pad * 4) continue;
            if (expectAway is { } ea && Math.Abs(c.X - ea.X) <= pad * 2 && Math.Abs(c.Y - ea.Y) <= pad * 2) continue;
            seen.Add((c.X / cell, c.Y / cell));
        }
        foreach (var key in seen) heat[key] = (heat.TryGetValue(key, out var h) ? Math.Min(h.Count + 1, MaxCount) : 1, now);
        // 2 回以上見えた升目と、その周囲 1 升を除外の範囲にする
        Rects = heat.Where(kv => kv.Value.Count >= 2)
            .Select(kv => ((kv.Key.Item1 - 1) * cell, (kv.Key.Item2 - 1) * cell, (kv.Key.Item1 + 2) * cell, (kv.Key.Item2 + 2) * cell)).ToList();
    }

    /// <summary>
    /// 試行の結果を数える。釣れない結果が <see cref="StaleStreakLimit"/> 回続いたら覚えた升目を捨てて true を返す
    /// (視点が変わると他人のウキの位置も移るため)。除外矩形は次の <see cref="Learn"/> まで残る。
    /// </summary>
    public bool NoteOutcome(string outcome)
    {
        if (outcome == "caught") { staleStreak = 0; return false; }
        if (outcome is not ("missed_bite" or "early" or "escaped" or "unknown") || ++staleStreak < StaleStreakLimit) return false;
        staleStreak = 0;
        heat.Clear();
        return true;
    }
}
