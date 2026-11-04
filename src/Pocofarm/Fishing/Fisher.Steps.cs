using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    /// <summary>投げクリックを送って着水待ちへ。ウキが引く前の位置に残っていれば引けていないので投げない。</summary>
    void StartCast(double now, Bobber? b)
    {
        if (expectAway is { } ea && b is not null && Math.Abs(b.X - ea.X) < cfg.BobberNearPx * scale && Math.Abs(b.Y - ea.Y) < cfg.BobberNearPx * scale)
        {
            Log.Warn("引いたはずなのにウキが水中の元位置にある。投げずに当たり待ちへ戻る");
            events.Write("desync", ("note", "reel_ineffective"), ("pos", new[] { ea.X, ea.Y }));
            expectAway = null;
            dip = NewDip();
            castOrigin = null;
            startupAdopt = true;
            pecks = 0;
            SetState(FishState.CastWait, "引き未反映→再静止待ち");
            return;
        }
        if (!Click(ClickKind.Cast)) return;
        RememberDecoys(b);
        castFrom = b is null ? null : (b.X, b.Y);
        castDt = Now - lastReelAt;
        expectAway = null;
        dip = NewDip();
        pecks = 0;
        castOrigin = null;
        castMoved = false;
        castLastSeen = Now;
        SetState(FishState.CastWait, "投げクリック送信");
    }
    /// <summary>ウキが静止して当たり待ち態勢に入ったとき。投げの成否を確認する。</summary>
    void OnArmed(double now)
    {
        tArmed = now;
        castFailures = 0; alertedTier = 0;
        castVerified = castVerified || castMoved;
        if (castVerified) unverifiedWait = cfg.UnverifiedWait;
        startupAdopt = false;
        SetState(FishState.WaitBite, $"ウキ静止 base={dip.BaseY:F0}");
        anchorPos = lastPos;
        Log.Info($"ウキ静止を確認 (x,y)={lastPos} 幅={dip.BaseW:F0}");
        if (castDt >= 0) { Log.Info($"[castlock] dt={castDt:F2} ok"); tuner.OnCastSucceeded(castDt); castDt = -1; }
    }

    /// <summary>引きクリックを送り、結果待ちに入る。</summary>
    void DoReel(double now, Frame f, string why)
    {
        var pos = lastPos;
        if (!Click(ClickKind.Reel)) return;
        double tClick = Now;
        lastReelAt = tClick;
        expectAway = pos;
        attempt = new Attempt(events.NextAttempt(), now, tClick, pos, pecks)
        {
            Trace = trace.TakeLast(cfg.AttemptTraceLength).Select(x => new[] { Math.Round(x.T, 2), x.Y, (double)x.W }).ToList(),
            FrameClick = f.Clone(), TArmed = tArmed, BaseW = dip.BaseW,
        };
        SetState(FishState.ResultWait, why);
    }
    void Step(double now, Frame f, Bobber? b, bool fresh)
    {
        switch (state)
        {
            case FishState.Cooldown:
            {
                double wait = recorder.SessionAttempts > 0 ? cfg.RecastDelay : 0.5;
                // ゲームは直前の引き(釣果)から約 1.8 秒は投げを受け付けない。結果の確定が早いと RecastDelay だけでは足りないので、引きクリックからも数える
                if (fresh && ++learnTick % 8 == 0) LearnDecoys(f, b);
                if (AimRecoverDue && TryAimRecover(f)) break;      // 連続失敗の原因が視点のずれなら、通知より先に自分で直す
                if (now - tState >= wait && now - lastReelAt >= tuner.ReelToCast && now - recorder.LastCaughtAt >= cfg.AfterCatchCastMin && !StartupProbe(now, b)) StartCast(now, b);
                break;
            }
            case FishState.CastWait:
            {
                if (b is not null) castLastSeen = now;
                if (b is not null && !startupAdopt && fresh)
                {
                    if (castOrigin is null) castOrigin = (b.X, b.Y);      // 投げ後に最初に見えた位置を起点にする
                    else if (!castMoved && Math.Abs(b.X - castOrigin.Value.X) + Math.Abs(b.Y - castOrigin.Value.Y) > cfg.CastMovedPx * scale) castMoved = true;
                }
                dip.Anchored = b is not null && anchorPos is { } ap && Math.Abs(b.X - ap.X) <= cfg.BobberNearPx * scale * 2 && Math.Abs(b.Y - ap.Y) <= cfg.BobberNearPx * scale * 2;
                if (fresh || b is null) dip.Update(now, b);
                // 低 fps だと最初に見えた時点で着水済みで動きを観測できないので、投げから一定時間たった静止も着水とみなす(未確認扱いで当たり待ちに入る)
                // 静止の基準位置と今のウキ位置が大きくずれているのは、別の候補(手・他人のウキ)で静止を判定してしまったとき。そのまま当たり待ちに入ると、本物の出現が「沈み」になって早引きする
                if (dip.Armed && b is not null && Math.Abs(b.Y - dip.BaseY) > cfg.BobberNearPx * scale) { Log.Info($"静止の基準 y={dip.BaseY:F0} と現在位置 y={b.Y} がずれているので静止判定をやり直します"); dip = NewDip(); }
                if (dip.Armed && (castMoved || startupAdopt || now - tState > cfg.CastSettleFallback))
                {
                    // 竿先にぶら下がったウキは着水していない。投げのクリックが効かなかったので撮り直して投げ直す
                    // 投げる前のウキ位置から動かないまま静止したなら、投げが効いていない(幅は視点で変わるので使わない)
                    bool stayedAtTip = !castMoved && !startupAdopt && b is not null && castFrom is { } cf
                        && Math.Abs(b.X - cf.X) <= cfg.BobberNearPx * scale && Math.Abs(b.Y - cf.Y) <= cfg.BobberNearPx * scale;
                    if (stayedAtTip)
                    {
                        Log.Warn($"ウキが竿先に残っています(幅={dip.BaseW:F0})。投げが不発だったので投げ直します");
                        castFailures++;
                        if (castDt >= 0) { Log.Info($"[castlock] dt={castDt:F2} fail"); tuner.OnCastFailed(castDt); castDt = -1; }
                        RecordAttempt("cast_failed", null, f, null, $"投げが不発(ウキが竿先に残った・連続{castFailures}回)");
                        SetState(FishState.Cooldown, "cast_failed");
                        dip = NewDip();
                        // 待ち時間では直らない連続の不発(視線のずれなど)は、間隔を空けて、気づけるよう通知する
                        if (castFailures >= cfg.CastFailAlertCount)
                        {
                            status.Set("message", "投げが池に届いていません。視線を池に向けてください");
                            tState = Now + CastFailBackoff();
                            AlertCastFailures();
                        }
                        EscalateCastFailures();
                        return;
                    }
                    OnArmed(now);
                    return;
                }
                if (expectAway is { } ea && b is not null && !startupAdopt && now - tState > cfg.ReelIneffectiveSeconds
                    && Math.Abs(b.X - ea.X) < cfg.BobberNearPx * scale && Math.Abs(b.Y - ea.Y) < cfg.BobberNearPx * scale)
                {
                    Log.Warn("ウキが引く前の位置から動かない。引けていなかったので当たり待ちへ戻る");
                    events.Write("desync", ("note", "reel_ineffective_inplace"), ("pos", new[] { ea.X, ea.Y }));
                    expectAway = null; startupAdopt = true;
                    return;
                }
                if (now - tState > cfg.CastTimeout || (!startupAdopt && now - castLastSeen > cfg.CastLostSeconds)) CastFailed(f);
                break;
            }
            case FishState.WaitBite:
            {
                var ev = fresh || b is null ? dip.Update(now, b) : null;
                if (ev == "dip")
                {
                    pecks++;
                    Log.Info($"沈み検出 #{pecks} (y={(b is null ? "lost" : b.Y.ToString())})");
                    if (pecks >= tuner.PecksToReel) DoReel(now, f, $"沈み{pecks}回目で引く");
                }
                else if (now - tArmed > (castVerified ? tuner.BiteTimeout() : unverifiedWait)) NoBite(now, f);
                break;
            }
            case FishState.ResultWait:
                if (attempt is null) SetState(FishState.Cooldown, "result_lost");      // 確定処理中の例外で試行が外れた
                else FinishResult(now, f);
                break;
            case FishState.NeedsAttention:
                break;      // 人が確認するまで投げない(一時停止→再開で解除)
        }
    }

    /// <summary>起動直後は既存ウキが水中か空中か分からない。見えていれば確認クリックで判定する。</summary>
    bool StartupProbe(double now, Bobber? b)
    {
        if (!startupAdopt || recorder.SessionAttempts > 0 || b is null) return false;
        if (now - tState < cfg.ProbeWatchSeconds) return true;           // 起動直後はウキの様子を見る
        startupAdopt = false;
        Log.Info("起動時にウキが見えているため、確認クリックで水中/空中を判定します");
        tArmed = now;
        NoBite(now, cur ?? frame!);
        return true;
    }
    /// <summary>長時間当たりがない/検出できない。確認クリックを送り、結果で水中だったか空中だったかを判断する。</summary>
    void NoBite(double now, Frame f)
    {
        Log.Info("確認クリックを送ります(当たりなし/起動直後)");
        var pos = lastPos;
        if (!Click(ClickKind.Probe)) return;
        double t = Now;
        lastReelAt = t;
        expectAway = pos;
        attempt = new Attempt(events.NextAttempt(), t, t, pos, pecks)
        {
            Probe = true,
            Trace = trace.TakeLast(cfg.AttemptTraceLength).Select(x => new[] { Math.Round(x.T, 2), x.Y, (double)x.W }).ToList(),
        };
        SetState(FishState.ResultWait, "当たりなし確認クリック");
    }
}
