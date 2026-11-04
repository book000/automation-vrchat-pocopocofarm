using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    /// <summary>「池の水に向けて投げよう」通知: 視線が池を向いておらず投げが空振りした。</summary>
    void AimError(Sighting s, Frame f)
    {
        castFailures++;
        RecordAttempt("cast_failed", s.Toast, f, null, $"視線が池を向いていない(連続{castFailures}回)");
        status.Set("message", castFailures >= cfg.CastFailAlertCount ? "投げが池に届いていません。視線を池に向けてください" : "");
        SetState(FishState.Cooldown, "aim_error");
        if (castFailures >= cfg.CastFailAlertCount)
        {
            tState = Now + CastFailBackoff();
            AlertCastFailures();
        }
    }

    /// <summary>連続失敗の待ち時間。3 回目から倍々に延ばし、<see cref="Config.CastFailPause"/> の 6 倍で頭打ちにする。</summary>
    double CastFailBackoff() => cfg.CastFailPause * Math.Min(6.0, 1 << Math.Min(3, Math.Max(0, castFailures - cfg.CastFailAlertCount)));

    /// <summary>投げ失敗が続いたら、画面を見ていなくても気づけるよう連続の最初の 1 回だけ通知する。</summary>
    void AlertCastFailures()
    {
        if (castAlerted || AimRecoverDue) return;      // 視点補正を試す前は通知しない(直れば通知は要らない。直らなければ補正のあとに通知する)
        castAlerted = true;
        Notify.Toast("ぽこぽこファーム", "投げが連続で失敗しています。視線を池に向けてください");
    }

    /// <summary>連続失敗が閾値に達していて、この失敗数ではまだ視点補正を試していない。</summary>
    bool AimRecoverDue => cfg.AimRecover && !dryRun && aim.HasReference && castFailures >= cfg.CastFailAlertCount && aimTriedFor < castFailures;

    /// <summary>
    /// 視点が池からずれている疑い(連続失敗)があるとき、基準の画面との比較でマウスを動かして戻す。
    /// 補正の間だけ VRChat を前面にし、終わったら元のウィンドウとカーソル位置へ戻す。直ったら true。
    /// 直らなければ false を返し、呼び出し側は従来どおり通知と待ちに進む。
    /// </summary>
    bool TryAimRecover(Frame f)
    {
        aimTriedFor = castFailures;
        if (cap is not { } capture || !WinApi.IsAlive(hwnd)) return false;
        var prevFg = WinApi.ForegroundWindow();
        var cursor = WinApi.CursorPos();
        if (!WinApi.EnsureForeground(hwnd)) { Log.Warn("[aim] VRChat を前面にできないため視点補正を見送ります"); AlertCastFailures(); return false; }
        Frame? grab = null; long seen = 0;
        AimRecoverResult result;
        try
        {
            result = aim.Recover(f, (dx, dy) =>
            {
                WinApi.MouseMove(dx, dy);
                Thread.Sleep((int)(cfg.AimRecoverSettle * 1000));
                capture.CopyLatestTo(ref grab, ref seen);
                return grab is null ? null : ToWorkFrame(grab).Clone();     // 次の測定まで grab を書き換えられても崩れないよう複製
            }, () => control.Poll());
        }
        finally
        {
            WinApi.MoveCursor(cursor.X, cursor.Y);
            if (prevFg != 0 && prevFg != hwnd) WinApi.EnsureForeground(prevFg);
        }
        lastTick = Now;      // 補正に数秒かかるので、処理の途切れ(gap)と誤認させない
        if (result == AimRecoverResult.Aligned)
        {
            castFailures = 0; castAlerted = false;
            status.Set("message", "");
            SetState(FishState.Cooldown, "aim_recovered");
            return true;
        }
        AlertCastFailures();
        return false;
    }
    /// <summary>投げてもウキが着水しなかった(視線が池でない等)。</summary>
    void CastFailed(Frame f)
    {
        castFailures++;
        RecordAttempt("cast_failed", null, f, null, $"投げ後{cfg.CastTimeout:F0}秒でウキが静止せず(連続{castFailures}回) {dip.Diagnostics} 移動={castMoved} 武装={dip.Armed}",
            new() { ["trace"] = TraceTail(cfg.EvidenceTraceLength) });
        if (castFailures >= cfg.CastFailAlertCount)
        {
            status.Set("message", "投げに連続失敗: 視線が池を向いているか確認してください");
            AlertCastFailures();
        }
        SetState(FishState.Cooldown, "cast_failed");
        if (castFailures >= cfg.CastFailAlertCount) tState = Now + CastFailBackoff();
        dip = NewDip();
    }
}
