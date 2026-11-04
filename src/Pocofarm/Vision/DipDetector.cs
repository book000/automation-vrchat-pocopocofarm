namespace Pocofarm.Vision;

/// <summary>
/// ウキ位置の時系列から「ぴくぴく」(沈み)を検出する。基準位置は静止時の中央値。
/// 基準から ウキ幅×DipRatio 以上下がった状態が DipFrames 連続、またはウキを LostSeconds 以上見失えば沈みと判定する。
/// </summary>
public sealed class DipDetector(DipParams p)
{
    const int HistoryLimit = 90;

    readonly List<(double T, int Y, int W, int X)> hist = [];
    double? lostSince;
    bool lostFired, inDip;
    int dipRun;
    double lastT, dtAvg = 0.033, baseX;

    readonly Queue<bool> seenRing = new();
    int seenBeforeLoss;

    /// <summary>見失い沈みの確認(LostGuard)で採用しなかった回数。</summary>
    public int SuppressedLost { get; private set; }

    public double BaseY { get; private set; }
    public double BaseW { get; private set; } = 10.0;
    public bool Armed { get; private set; }

    /// <summary>今見えているウキが、直近に静止を確認した位置の近くか。近ければ着水直後に当たりで見え隠れしても短い静止で武装する。</summary>
    public bool Anchored { get; set; }

    /// <summary>静止待ちの状況(失敗時の原因調べ用)。</summary>
    public string Diagnostics => hist.Count == 0 ? "ウキ未検出" : $"検出{hist.Count}回 y={hist.Min(h => h.Y)}〜{hist.Max(h => h.Y)} 期間={hist[^1].T - hist[0].T:F1}s";

    /// <summary>1フレームを与える。"dip" を返したときが引くタイミング。</summary>
    public string? Update(double t, Bobber? b)
    {
        // 低 fps ではフレーム間隔が沈みの長さに近く、連続フレームを待つと見逃すので 1 フレームで確定する
        double dt = t - lastT;
        int need = dt > 0.1 ? 1 : p.DipFrames;
        if (dt > 0 && dt < 2) dtAvg = 0.9 * dtAvg + 0.1 * dt;
        lastT = t;
        // 静止位置から横に大きく離れた物体は別物(他人のアバターの一部など)なので、見えなかったものとして扱う
        if (b is not null && Armed && Math.Abs(b.X - baseX) > Math.Max(p.MaxLateralPx, BaseW * p.MaxLateralWidths)) b = null;
        if (Armed && p.LostGuardFrames > 0)
        {
            if (b is null && lostSince is null) seenBeforeLoss = seenRing.Count(x => x);      // 見失う直前までにウキが見えていた数
            seenRing.Enqueue(b is not null);
            if (seenRing.Count > p.LostGuardFrames) seenRing.Dequeue();
        }
        if (b is null)
        {
            dipRun = 0;
            lostSince ??= t;
            if (Armed && !lostFired && t - lostSince.Value >= p.LostSeconds)
            {
                lostFired = true;
                // 直前にほとんど見えていなかったなら、検出が不安定なだけで沈みではない
                if (p.LostGuardFrames > 0 && seenBeforeLoss < p.LostGuardSeen) { SuppressedLost++; return null; }
                return "dip";
            }
            return null;
        }
        lostSince = null; lostFired = false;
        if (!Armed)
        {
            TrySettle(t, b);
            return null;
        }
        double thr = Math.Max(p.DipMinPx, BaseW * p.DipRatio);
        if (b.Y >= BaseY + thr)
        {
            dipRun++;
            if (dipRun >= need && !inDip) { inDip = true; return "dip"; }
            return null;
        }
        dipRun = 0; inDip = false;
        double alpha = 1 - Math.Exp(-p.FollowRate * Math.Clamp(dt, 0.0, 1.0));     // 30fps で 1 フレームあたり約 0.03。低 fps でも秒あたりの追従速度を揃える
        if (Math.Abs(b.Y - BaseY) <= thr * 2) BaseY += alpha * (b.Y - BaseY);     // 通常揺れに追従。別物の誤検出(遠く離れた位置)で基準がずれると、正常位置が沈みに見えてしまう
        return null;
    }

    /// <summary>着水後の静止待ち: 直近の窓の y 揺れが小さければ武装する。</summary>
    void TrySettle(double t, Bobber b)
    {
        hist.Add((t, b.Y, b.W, b.X));
        if (hist.Count > HistoryLimit) hist.RemoveAt(0);
        // fps が低いと窓が短すぎて 3 枚も入らず永久に静止判定できないので、窓はフレーム間隔の数倍以上にする
        double win = Anchored ? Math.Max(p.SettleSeconds * p.AnchoredSettleScale, p.SettleFramesAnchored * dtAvg) : Math.Max(p.SettleSeconds, p.SettleFrames * dtAvg);
        var recent = hist.Where(h => t - h.T <= win).ToList();
        // 別物を一瞬ウキと誤検出するフレームが混ざっても静止を判定できるよう、中央値の近くに大半が集まっていれば静止とみなす
        double med = recent.Count > 0 ? recent.Select(h => (double)h.Y).Order().ElementAt(recent.Count / 2) : 0;
        var inliers = recent.Where(h => Math.Abs(h.Y - med) <= p.SettleRangePx / 2).ToList();
        if (recent.Count >= 2 && t - recent[0].T >= win * 0.9 && inliers.Count >= recent.Count * p.InlierRatio && inliers.Max(h => h.Y) - inliers.Min(h => h.Y) <= p.SettleRangePx)
        {
            BaseY = inliers.Select(h => h.Y).Order().ElementAt(inliers.Count / 2);
            BaseW = inliers.Select(h => h.W).Order().ElementAt(inliers.Count / 2);
            baseX = inliers.Select(h => h.X).Order().ElementAt(inliers.Count / 2);
            Armed = true;
            hist.Clear();
        }
    }
}
