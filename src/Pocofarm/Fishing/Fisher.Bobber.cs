using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    /// <summary>直近の釣果 5 件以上の位置の中央値。足りなければ null。</summary>
    (int X, int Y)? LearnedLanding(Frame f)
    {
        if (landings.Count < 5) return null;
        var xs = landings.Select(l => l.X).Order().ToList(); var ys = landings.Select(l => l.Y).Order().ToList();
        return ((int)(xs[xs.Count / 2] * f.Width), (int)(ys[ys.Count / 2] * f.Height));
    }

    /// <summary>直近のウキ位置の履歴に、中央値より 5px(基準解像度)以上下がったフレームが 3 つ以上あるか(魚が掛かった沈み)。</summary>
    bool HasRecentDips()
    {
        var ys = trace.TakeLast(60).Where(x => x.Y > 0).Select(x => x.Y).ToList();
        if (ys.Count < 10) return false;
        ys.Sort();
        int med = ys[ys.Count / 2];
        return ys.Count(y => y - med >= 5 * scale) >= 3;
    }

    Bobber? DetectBobber(Frame f)
    {
        var r = rois.Bobber;
        var region = ((int)(r[0] * f.Width), (int)(r[1] * f.Height), (int)(r[2] * f.Width), (int)(r[3] * f.Height));
        var ex = ExcludeRects(f.Width, f.Height);
        if (state is SWaitBite or SCastWait) ex.AddRange(decoyTracker.Rects);
        // 近くを通る他人のアバターの赤い物体が追跡位置を奪うので、直近に静止を確認した位置の周辺を最優先で探す
        // 水面のウキ(細い)を先に探す。竿先の大きいウキや他人の大きな物体が白玉の多さで勝って、本物を隠すのを防ぐ
        // 幅では絞らない: 視点や距離によって水面のウキも竿先のウキと同じ幅に写る。他人のウキ・アバターは投げる前の位置(decoys)で除く
        int narrow = int.MaxValue;
        var bp = BobberParams.From(cfg);
        // 投げた直後は、竿先から飛んでいく自分のウキを追う。画面の別の場所に現れる他人のウキ・アバターの小物には乗り換えない
        Bobber? b = null;
        if (state is SCastWait && castFrom is { } cf0 && Now - tState < cfg.CastLostSeconds)
        {
            if (!castMoved) b = BobberDetector.Detect(f, region, cf0, radius: 200, exclude: ex, scale: scale, p: bp, maxWidth: narrow);
            else if (lastPos is { } lp0) b = BobberDetector.Detect(f, region, lp0, radius: 200, exclude: ex, scale: scale, p: bp, maxWidth: narrow);
        }
        b ??= anchorPos is { } a ? BobberDetector.Detect(f, region, a, exclude: ex, scale: scale, p: bp, maxWidth: narrow) : null;
        if (b is null && lastPos is { } n) b = BobberDetector.Detect(f, region, n, exclude: ex, scale: scale, p: bp, maxWidth: narrow);
        // 追跡中の物が他人のアバターの赤白(静止していて沈みも出ない偽物)だと、近傍探索は永久にそれを返す。時々全体を探し直し、明らかに本物らしい物があれば乗り換える
        // 直近に沈みが出ている追跡対象は本物なので乗り換えない(他人のウキの方が点数が高くても、乗り換えで沈み判定が毎回やり直しになり見逃す)
        if (b is not null && state is SWaitBite or SCastWait && ++auditTick % Math.Max(1, cfg.BobberAuditFrames) == 0 && !HasRecentDips())
        {
            Bobber? g;
            if (LearnedLanding(f) is { } land)
            {
                // 着水位置が学習できているときは、そこから外れた追跡対象だけを、着水位置の候補へ乗り換える(他人のアバターへの乗り換えを防ぐ)
                int far = (int)(cfg.BobberNearPx * scale * 3);
                bool onLanding = Math.Abs(b.X - land.X) <= far && Math.Abs(b.Y - land.Y) <= far;
                g = onLanding ? null : BobberDetector.Detect(f, region, land, exclude: ex, scale: scale, p: bp, maxWidth: narrow);
                if (g is not null && (Math.Abs(g.X - land.X) > far || Math.Abs(g.Y - land.Y) > far)) g = null;
            }
            else
            {
                g = BobberDetector.Detect(f, region, null, exclude: ex, scale: scale, p: bp, maxWidth: narrow);
                if (g is not null && !(g.Score > b.Score * cfg.BobberSwitchRatio)) g = null;
            }
            if (g is not null && Math.Abs(g.X - b.X) + Math.Abs(g.Y - b.Y) > cfg.BobberNearPx * scale * 2)
            {
                Log.Warn($"追跡中のウキ候補 ({b.X},{b.Y}) 点={b.Score} より ({g.X},{g.Y}) 点={g.Score} が本物らしいので乗り換えます");
                anchorPos = null; lastPos = null; castOrigin = null; startupAdopt = true;
                dip = NewDip();
                SetState(SCastWait, "ウキ候補の乗り換え→再静止待ち");
                return g;
            }
        }
        // 追跡できていないときは、まず学習済みの着水位置の周辺だけを探す(画面全体の他人のウキ・アバターに惑わされない)
        if (b is null && state is SWaitBite or SCastWait && LearnedLanding(f) is { } home)
            b = BobberDetector.Detect(f, region, home, radius: (int)(cfg.BobberNearPx * scale * 4), exclude: ex, scale: scale, p: bp, maxWidth: narrow);
        b ??= BobberDetector.Detect(f, region, null, exclude: ex, scale: scale, p: bp, maxWidth: narrow);
        return b ?? BobberDetector.Detect(f, region, null, exclude: ex, scale: scale, p: bp);
    }
}
