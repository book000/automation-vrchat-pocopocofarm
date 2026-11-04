using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    // ---------------------------------------------------------------- トースト処理

    /// <summary>OCR の読み取りを出現(appearance)単位に集約し、状態に応じて処理する。</summary>
    void DrainSightings(Frame f)
    {
        while (reader.TryDequeue(out var s))
        {
            if (s.Neko is not null)
            {
                nekos.Note(s, f, state);
                if (s.Toast is null) continue;
            }
            if (s.Toast is null)
            {
                if (s.Text.Length >= 5) NoteUnclassified(s, f);
                continue;
            }
            var kind = s.Toast.Kind;
            double last = kindLast.GetValueOrDefault(kind, -99.0);
            bool newAppearance = s.T - last > cfg.ToastNewGap;
            kindLast[kind] = s.T;
            var a = attempt;
            if (kind is Toast.LevelUp or Toast.Achieve)
            {
                if (state == SResult && a is not null && s.T >= a.TClick - 0.1)
                {
                    if (!a.Info.Contains(kind)) a.Info.Add(kind);
                    a.InfoUntil = Now + cfg.InfoWaitSeconds;      // 釣果通知が後から出るのを待つ
                }
                else if (newAppearance) events.Write("toast_info", ("kind", kind), ("text", s.Toast.Text), ("state", state));
                continue;
            }
            if (kind == Toast.Aim)
            {
                if (newAppearance && state is SCastWait or SCooldown) AimError(s, f);
                continue;
            }
            if (state == SResult && a is not null && s.T >= a.TClick - 0.1)
            {
                a.FirstSeen ??= s.T;
                if (a.Best is null || s.Toast.Score >= a.Best.Score) { a.Best = s.Toast; a.Engine = s.Engine; }
                continue;
            }
            if (newAppearance) UnlinkedToast(s, f);
        }
    }

    /// <summary>試行に紐付かないトースト。逃げられた=当たり見逃し、それ以外は紐付け不能として記録する。</summary>
    void UnlinkedToast(Sighting s, Frame f)
    {
        var t = s.Toast!;
        if (recorder.LastFinal is { } fin && fin.Kind == t.Kind && fin.Fish == t.Fish && fin.Exp == t.Exp && fin.Count == t.Count && s.T - fin.T < cfg.ToastDuplicateWindow)
            return;      // 確定済みの結果の再読取り
        if (Now - startedAt < cfg.StartupGrace)
        {
            events.Write("toast_unlinked", ("kind", t.Kind), ("text", t.Text), ("state", "startup_stale"));
            return;      // 起動前から表示されていた古い通知
        }
        if (t.Kind == Toast.Escaped && state is SWaitBite or SCastWait)
        {
            // 一時停止の間は引けないので、停止中に過ぎた当たりは検出の見逃しではない
            if (Now - control.LastResumeAt < cfg.ResumeEscapeGrace)
            {
                RecordAttempt("no_bite_reset", t, f, null, "一時停止中に当たりが過ぎた(逃げられた表示)");
                return;
            }
            RecordAttempt("missed_bite", t, f, null, "引く前に逃げられた(沈み検出の見逃し)",
                new() { ["trace"] = TraceTail(90), ["pecks_seen"] = pecks });
            return;
        }
        var name = SaveEvidence(f, $"unlinked_{t.Kind}", new { text = t.Text, state, engine = s.Engine });
        events.Write("toast_unlinked", ("kind", t.Kind), ("text", t.Text), ("fish", t.Fish), ("state", state), ("evidence", name));
        Log.Warn($"紐付かないトースト: {t.Kind} ({t.Fish}) 状態={state} OCR={t.Text}");
    }

    /// <summary>どの種別にも当てはまらない文字列。見落とし調査のため重複を避けつつ記録する。</summary>
    void NoteUnclassified(Sighting s, Frame f)
    {
        // 確定済みの結果(釣れた/逃げられた/まだかかっていません)の表示が消えていく途中の誤読は、同じ通知の読み損ないなので件数だけ数える
        if (recorder.LastFinal is { } fin && s.T - fin.T < cfg.ToastNoiseWindow && NoiseHints.Any(h => s.Text.Contains(h)))
        {
            status.Set("toast_noise", ++toastNoise);
            return;
        }
        // 釣りと関係のない画面の文字は未分類の警告にせず、種別ごとに数える(本当の取りこぼしを目立たせるため)
        if (ToastNoise.Category(s.Text) is { } other)
        {
            events.Write("toast_other", ("category", other), ("text", s.Text), ("state", state));
            return;
        }
        var (prev, t0) = unclassifiedLast;
        if (s.Text[..Math.Min(8, s.Text.Length)] == prev[..Math.Min(8, prev.Length)] && s.T - t0 < 5) return;
        unclassifiedLast = (s.Text, s.T);
        var name = SaveEvidence(f, "unclassified_toast", new { text = s.Text, engine = s.Engine, state });
        events.Write("toast_unclassified", ("text", s.Text), ("state", state), ("evidence", name));
        Log.Warn($"未分類のトースト領域テキスト: {s.Text}");
    }

    string SaveEvidence(Frame f, string name, object meta) => evidence.Save(f.Clone(), name, meta, boxes);

    List<object> TraceTail(int n) =>
        trace.TakeLast(n).Select(x => (object)new object[] { Math.Round(x.T, 2), x.Y, x.W }).ToList();
}
