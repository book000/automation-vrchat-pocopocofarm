using Pocofarm.Core;

namespace Pocofarm.Fishing;

/// <summary>引き/確認クリックの数と確定した試行の数を突き合わせ、記録漏れを検出する。</summary>
public sealed class Auditor(Config cfg, EventLog events, Status status, ClickSender clicks)
{
    int closed, bad;
    double last;

    /// <summary>クリックを伴う試行が 1 件確定した。</summary>
    public void NoteClosed() => closed++;

    /// <summary>設定の間隔ごとに突き合わせる。attemptOpen は引いて結果待ちの試行があること。</summary>
    public void Tick(double now, bool attemptOpen)
    {
        if (now - last < cfg.AuditInterval) return;
        last = now;
        int reels = clicks.Count(ClickKind.Reel) + clicks.Count(ClickKind.Probe);
        int expected = closed + (attemptOpen ? 1 : 0);
        // 一時的なずれ(確定直前)を誤検出しないよう 2 回連続で一致しないときだけ報告する
        bad = reels != expected ? bad + 1 : 0;
        if (bad == 2)
        {
            Log.Error($"監査不一致: 引き/確認クリック={reels} 確定試行={expected}");
            events.Write("audit_mismatch", ("clicks", new Dictionary<string, int>(clicks.Counts)), ("closed", closed), ("open", attemptOpen));
        }
        status.Set("audit", new Dictionary<string, object?> { ["clicks"] = new Dictionary<string, int>(clicks.Counts), ["closed"] = closed, ["ok"] = bad < 2 });
    }
}
