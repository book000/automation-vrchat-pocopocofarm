using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    // ---------------------------------------------------------------- 試行の確定

    /// <summary>
    /// 釣れない結果が続いたら、覚えた着水位置と他人のウキの升目を捨てる。視点が変わると着水位置が移るのに、古い着水位置へ乗り換えて、
    /// 釣れないので学び直せない行き詰まりになるため。
    /// </summary>
    void ForgetStaleView(string outcome)
    {
        if (!decoyTracker.NoteOutcome(outcome)) return;
        landings.Clear();
        Log.Warn("釣れない結果が続いたため、覚えた着水位置と他人のウキの位置を捨てて学び直します");
    }

    Dictionary<string, object?> RecordAttempt(string outcome, Toast? toast, Frame? f, Attempt? a, string note, Dictionary<string, object?>? extra = null)
    {
        var fields = recorder.Record(outcome, toast, f, a, note, pecks, extra);
        if (outcome == "caught" && fields["wait_s"] is double ws) tuner.NoteBiteWait(ws);
        ForgetStaleView(outcome);
        return fields;
    }
    /// <summary>結果待ちの打ち切り判定。トースト確定か、タイムアウト時の再読取りと unknown 記録。</summary>
    void FinishResult(double now, Frame f)
    {
        var a = attempt!;
        double waited = now - a.TClick;
        var best = a.Best;
        bool done = false;
        if (best is not null)
        {
            bool complete = best.Kind != Toast.Caught || (best.Fish.Length > 0 && best.Exp is not null && best.Count is not null);
            done = complete || (a.FirstSeen is { } fs && now - fs > cfg.ResultFirstSeenGrace);
        }
        else if (waited > (a.Probe ? cfg.ProbeResultTimeout : cfg.ResultTimeout) && now > a.InfoUntil)
        {
            best = reader.Recover(a);
            if (best is not null) a.Engine = "rapidocr再読取";
            if (best is null && a.Info.Count > 0)
            {
                // レベルアップ/実績が出ている = 経験値が入った = 釣れている。魚種は読めなかった
                best = new Toast(Toast.Caught, "", "", null, null);
                a.PartialNote = "レベルアップ/実績通知のみ(魚種不明)";
            }
            done = true;
        }
        if (!done) return;
        if (best is null && a.Probe)
        {
            // 水中か空中か不明なまま引いたが何も出なかった = 空中で投げ動作になった
            RecordAttempt("no_bite_reset", null, f, a, "トーストなし: 空中のウキを投げた");
            attempt = null;
            dip = NewDip();
            castOrigin = a.PosBefore;       // 空中の位置から離れたら投げ成功
            castMoved = false; startupAdopt = false; expectAway = null;
            SetState(FishState.CastWait, "確認クリックが投げになった");
            return;
        }
        if (best is null)
        {
            RecordAttempt("unknown", null, f, a, "引いた後に結果トーストを検出できなかった");
            attempt = null;
            // クリックが投げになった/効かなかった可能性がある。ウキの動きで判断するため着水待ちへ(expectAway は維持)
            dip = NewDip();
            castOrigin = a.PosBefore; castMoved = false; startupAdopt = false;
            SetState(FishState.CastWait, "結果不明→ウキの動きで状態確認");
            return;
        }
        var outcome = best.Kind switch { Toast.Caught => "caught", Toast.Early => "early", _ => "escaped" };
        // 確認クリック(再起動直後など)で「まだ早い」「逃げられた」が出ても、停止中に当たりが過ぎただけで検出の失敗ではない
        if (a.Probe && outcome is "early" or "escaped") RecordAttempt("no_bite_reset", best, f, a, outcome == "early" ? "当たりなしで引き直した" : "確認クリックで逃げられた表示(停止中に当たりが過ぎた)");
        else RecordAttempt(outcome, best, f, a, a.PartialNote, a.Info.Count > 0 ? new() { ["info"] = a.Info.ToList() } : null);
        castVerified = true; unverifiedWait = cfg.UnverifiedWait;
        if (outcome == "caught" && !a.Probe && a.PosBefore is { } lp) { landings.Add((lp.X / (double)f.Width, lp.Y / (double)f.Height)); if (landings.Count > 15) landings.RemoveAt(0); }
        if (outcome == "caught" && !a.Probe && castFailures == 0) aim.NoteCatch(f, now);      // 視点が合っていた証拠の画面を、補正の基準にする
        tuner.OnOutcome(outcome);
        attempt = null;
        SetState(FishState.Cooldown, outcome);
    }
}
