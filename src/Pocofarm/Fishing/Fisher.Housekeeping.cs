using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    void AddTrace(double t, int y, int w)
    {
        trace.AddLast((t, y, w));
        if (trace.Count > 120) trace.RemoveFirst();
    }
    // ---------------------------------------------------------------- 表示・保守

    void UpdateBoxes(Frame f, Bobber? b)
    {
        int w = f.Width, h = f.Height;
        Box R(double[] r, string label) => new((int)(r[0] * w), (int)(r[1] * h), (int)(r[2] * w), (int)(r[3] * h), label);
        var label = hud.Level is { } lv ? $"Lv{lv}" : "level";
        if (hud.Xp is { } xp) label += $" {(int)Math.Round(xp * 100)}%";
        var list = new List<Box> { R(rois.Toast, "toast"), R(rois.Level, label), R(rois.XpBar, "") };
        if (b is not null)
        {
            double k = scale;
            list.Add(new Box((int)(b.X - 25 * k), (int)(b.Y - 12 * k), (int)(b.X + 25 * k), (int)(b.Y + 45 * k), $"bobber w{b.W}"));
        }
        boxes = list;
    }

    void Housekeeping(double now, Frame f)
    {
        nekos.Flush(state.Wire());
        prof.Mark("hud");
        if (hud.Poll(now, f, rois) is { } adopted) rois = adopted;
        prof.Mark("audit");
        auditor.Tick(now, attempt is not null);
        prof.Mark("live");
        if (now - lastLive >= cfg.LiveInterval && liveFuture is not { IsCompleted: false })
        {
            // JPEG 化と保存は遅い環境で 0.7 秒ほどかかるので、釣りのループを止めないよう別スレッドで行う
            lastLive = now;
            var snap = f.Clone();
            var snapBoxes = boxes.ToList();
            liveFuture = liveWorker.Submit(() => { evidence.WriteLive(snap, snapBoxes); return true; });
        }
        prof.Mark("cleanup");
        if (now - lastCleanup >= cfg.EvidenceCleanupInterval) { lastCleanup = now; evidence.CleanupAsync(); }
        prof.Mark("stats");
        if (now - lastStats >= 5)
        {
            lastStats = now;
            status.Set("session", events.SummarizeSession());
            status.Set("pecks_to_reel", tuner.PecksToReel);
            status.Set("fps", Math.Round(fps, 1));
            prof.PublishPerf(now);
            status.Set("bobber", bobber is null ? null : new[] { bobber.X, bobber.Y, bobber.W });
            status.Set("state_since", tState);
            status.Set("pecks", pecks);
        }
        prof.Mark("flush");
        status.Flush();
    }
}
