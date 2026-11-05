using Pocofarm.Core;
using Pocofarm.Platform;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    int stallTier;
    double lastTierAt, lastProgressAt = Now;
    int sameStateErrors;
    FishState errState;

    // ---------------------------------------------------------------- 例外の封じ込め

    /// <summary>
    /// 主ループで例外が出たとき。同じ状態で <see cref="Config.StateErrorLimit"/> 回続いたら、壊れた可能性のある試行・沈み検出を捨てて
    /// 投げ待ちへ戻し、error イベントに残す(同じ例外で同じ状態に居座り続けないため)。
    /// </summary>
    void ContainError(Exception ex)
    {
        sameStateErrors = state == errState ? sameStateErrors + 1 : 1;
        errState = state;
        if (sameStateErrors < Math.Max(1, cfg.StateErrorLimit) || state is FishState.NeedsAttention) return;
        sameStateErrors = 0;
        Log.Warn($"状態 {state.Wire()} で例外が続いたため、試行を捨てて投げ待ちへ戻します");
        events.Write("error", ("message", ex.Message), ("state", state.Wire()), ("note", "state_reset"));
        attempt = null;
        dip = NewDip();
        lastPos = null; castOrigin = null; startupAdopt = true;
        SetState(FishState.Cooldown, "error_reset");
    }

    // ---------------------------------------------------------------- 要確認状態

    /// <summary>投げを止めて人の確認を待つ。一時停止→再開(または手動の recast)で解除する。</summary>
    void EnterNeedsAttention(string reason)
    {
        attempt = null;
        SetState(FishState.NeedsAttention, reason);
        status.Set("needs_attention", reason);
        events.Write("needs_attention", ("reason", reason), ("miss_streak", recorder.MissStreak), ("cast_failures", castFailures));
        Log.Warn($"要確認: {reason}");
        if (cfg.LocalNotify) DesktopNotice.Show("ポコファーム: 釣りが止まっています", reason);
        if (!dryRun) Notify.Toast("ぽこぽこファーム", reason);
    }

    /// <summary>人が確認して再開したとき。要確認状態を解いて、失敗の数え直しと検出のやり直しをする。</summary>
    void ClearAttention(string source)
    {
        if (state != FishState.NeedsAttention) return;
        events.Write("attention_cleared", ("source", source));
        castFailures = 0; alertedTier = 0; stallTier = 0;
        lastProgressAt = Now;
        recorder.ResetMissStreak();
        status.Set("needs_attention", null);
        dip = NewDip();
        lastPos = null; anchorPos = null; castOrigin = null; startupAdopt = true;
        SetState(FishState.Cooldown, "attention_cleared");
    }

    // ---------------------------------------------------------------- 進行監視

    /// <summary>釣果があった。段階と無釣果の計時をやり直す。</summary>
    void NoteProgress()
    {
        stallTier = 0;
        lastProgressAt = Now;
    }

    /// <summary>釣果がない時間の上限[秒]。実測の当たり待ち(p95)× 倍率と下限のうち長い方。</summary>
    double StallNoCatchLimit() => Math.Max(cfg.StallNoCatchMinutes * 60.0, cfg.StallWaitP95Factor * (tuner.WaitP95() ?? 0.0));

    /// <summary>
    /// 釣れない結果が続く、または長く釣果がないとき、段階的に復旧を試みる。
    /// 1: キャプチャを作り直し、検出・他人のウキの記憶・引く回数の調整を初期化する。 2: 確認クリックを 1 回送る。 3: 通知して要確認にする。
    /// </summary>
    void CheckStall(double now)
    {
        if (!cfg.StallWatchdog || dryRun || state is FishState.NeedsAttention or FishState.WaitWindow || control.Paused) return;
        bool misses = recorder.MissStreak >= cfg.StallMissStreak;
        bool noCatch = now - Math.Max(lastProgressAt, control.LastResumeAt) > StallNoCatchLimit();
        if (!misses && !noCatch) return;
        if (stallTier > 0 && now - lastTierAt < cfg.StallTierGapSeconds) return;
        stallTier++; lastTierAt = now;
        var why = misses ? $"釣れない結果が{recorder.MissStreak}回続いています" : $"{(now - lastProgressAt) / 60:F0}分間釣果がありません";
        events.Write("stall", ("tier", stallTier), ("reason", why), ("miss_streak", recorder.MissStreak), ("state", state.Wire()));
        Log.Warn($"進行監視 段階{stallTier}: {why}");
        switch (stallTier)
        {
            case 1:
                cap?.Dispose(); cap = null;
                dip = NewDip();
                decoyTracker.Clear(); landings.Clear(); SaveLandings();
                tuner.ResetAdaptive();
                lastPos = null; anchorPos = null; castOrigin = null; startupAdopt = true;
                break;
            case 2:
                if (state is FishState.WaitBite or FishState.CastWait && attempt is null && (cur ?? frame) is { } fr) NoBite(now, fr);
                break;
            default:
                EnterNeedsAttention($"{why}。復旧を試みましたが直りません。画面と視線を確認して、一時停止→再開してください");
                break;
        }
    }
}
