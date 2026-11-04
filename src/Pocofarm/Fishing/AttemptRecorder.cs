using Pocofarm.Core;

namespace Pocofarm.Fishing;

/// <summary>直近に確定した結果トースト(同じ通知の再読取りや消えかけの誤読を見分けるために覚えておく)。</summary>
public readonly record struct FinalToast(string Kind, string Fish, int? Exp, int? Count, double T);

/// <summary>
/// 試行の結果をイベント・ログ・証拠画像に確定する。どの試行も必ず結果種別を持つ 1 件のイベントになる。
/// 前回の試行からの所要時間が異常に長ければ slow_cycle も残す。
/// </summary>
public sealed class AttemptRecorder(Config cfg, EventLog events, Status status, EvidenceSaver saveEvidence, Auditor auditor, ControlChannel control)
{
    static double Now => EventLog.Now();

    double lastAttemptAt;

    public FinalToast? LastFinal { get; private set; }
    public double LastCaughtAt { get; private set; } = -1e9;
    public int SessionAttempts { get; private set; }
    public int SessionCaught { get; private set; }

    /// <summary>釣れないまま続いた失敗の数(釣れたら 0。当たりなしの引き直しは数えない)。視点やウキの見え方が変わったときに気づくため。</summary>
    public int MissStreak { get; private set; }

    /// <summary>試行結果を記録して、書き込んだ項目を返す。pecksSeen は試行に紐付かない記録(a が null)で使う沈みの回数。</summary>
    public Dictionary<string, object?> Record(string outcome, Toast? toast, Frame? f, Attempt? a, string note, int pecksSeen, Dictionary<string, object?>? extra = null)
    {
        int aid = a?.Id ?? events.NextAttempt();
        if (outcome == "caught") LastCaughtAt = Now;
        var fields = new Dictionary<string, object?>
        {
            ["attempt"] = aid, ["outcome"] = outcome, ["note"] = note, ["fish"] = toast?.Fish ?? "",
            ["exp"] = toast?.Exp, ["count"] = toast?.Count, ["ocr"] = toast?.Text ?? "", ["pecks"] = a?.Pecks ?? pecksSeen,
            ["reaction_ms"] = a is null ? null : (int)Math.Round((a.TClick - a.TDip) * 1000),
            ["result_ms"] = a?.FirstSeen is { } fs ? (int)Math.Round((fs - a.TClick) * 1000) : null,
            ["engine"] = a?.Engine ?? "",
            // 着水静止から最初の沈みまでの秒数。確認クリックの引き直しは当たりを待った時間ではないので除く
            ["wait_s"] = a is { TArmed: > 0, Probe: false } && a.TDip > a.TArmed ? Math.Round(a.TDip - a.TArmed, 1) : null,
            ["pos"] = a?.PosBefore is { } p ? new[] { p.X, p.Y } : null,
            ["bobber_w"] = a is { BaseW: > 0 } ? (int)Math.Round(a.BaseW) : null,
        };
        if (extra is not null) foreach (var kv in extra) fields[kv.Key] = kv.Value;
        if (f is not null) fields["evidence"] = saveEvidence(f, $"{outcome}_{aid}", new { attempt = aid, outcome, ocr = fields["ocr"] });
        if (a?.FrameClick is { } fc && outcome != "caught")
            fields["evidence_click"] = saveEvidence(fc, $"{outcome}_{aid}_click", new { attempt = aid, trace = a.Trace });
        events.Write("attempt", fields);
        // 失敗扱いにならないまま時間だけ失う異常(投げ無視などで当たり待ちが長引く)を拾う。一時停止明けは数えない
        double since = Now - Math.Max(lastAttemptAt, control.LastResumeAt);
        if (lastAttemptAt > 0 && since > cfg.SlowCycleSeconds)
            events.Write("slow_cycle", ("seconds", Math.Round(since)), ("outcome", outcome), ("note", $"前回の試行から{since:F0}秒かかった(通常は約10秒)"));
        lastAttemptAt = Now;
        if (toast is not null) LastFinal = new FinalToast(toast.Kind, toast.Fish, toast.Exp, toast.Count, Now);
        SessionAttempts++;
        if (a is not null) auditor.NoteClosed();
        if (outcome == "caught") { SessionCaught++; MissStreak = 0; }
        else if (outcome != "no_bite_reset") MissStreak++;
        status.Set("miss_streak", MissStreak);
        var detail = outcome == "caught" ? $" {fields["fish"]} +{fields["exp"]}XP 所持{fields["count"]}" : "";
        var msg = $"#{aid} {EventLog.OutcomeJa[outcome]}{detail} 反応={fields["reaction_ms"]}ms 結果={fields["result_ms"]}ms {note}";
        if (outcome is "caught" or "no_bite_reset") Log.Info(msg); else Log.Warn(msg);
        status.Set("last_result", new Dictionary<string, object?> { ["attempt"] = aid, ["outcome"] = outcome, ["fish"] = fields["fish"], ["exp"] = fields["exp"], ["ts"] = Now });
        return fields;
    }
}
