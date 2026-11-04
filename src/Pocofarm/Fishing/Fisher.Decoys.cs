using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    /// <summary>投げる瞬間に見えているウキ候補は自分のものではない(自分のは投げた後に現れる)ので、位置を覚えて投げ後の探索から外す。</summary>
    void RememberDecoys(Bobber? tip)
    {
        if (cur is not { } f) return;
        LearnDecoys(f, tip);
    }

    /// <summary>
    /// 今見えているウキ候補が多い場所(他人のウキ・アバターの赤白)を、升目ごとの回数で覚える(竿先にぶら下がった自分のウキ tip は除く)。
    /// 他人のアバターの小物は映ったり消えたり、少しずれたりするので、投げる瞬間だけでなく、しばらく積み重ねて、周りの升目ごと除外する。
    /// </summary>
    void LearnDecoys(Frame f, Bobber? tip)
    {
        var r = rois.Bobber;
        var region = ((int)(r[0] * f.Width), (int)(r[1] * f.Height), (int)(r[2] * f.Width), (int)(r[3] * f.Height));
        int pad = (int)(cfg.BobberDecoyPad * scale);
        int cell = Math.Max(8, (int)(cfg.BobberDecoyCell * scale));
        foreach (var k in decoyHeat.Where(kv => Now - kv.Value.Last > cfg.BobberDecoyKeepSeconds).Select(kv => kv.Key).ToList()) decoyHeat.Remove(k);
        var seen = new HashSet<(int, int)>();
        foreach (var c in BobberDetector.Candidates(f, region, null, exclude: ExcludeRects(f.Width, f.Height), scale: scale, p: BobberParams.From(cfg)))
        {
            if (tip is { } t && Math.Abs(c.X - t.X) <= pad * 2 && Math.Abs(c.Y - t.Y) <= pad * 4) continue;
            if (expectAway is { } ea && Math.Abs(c.X - ea.X) <= pad * 2 && Math.Abs(c.Y - ea.Y) <= pad * 2) continue;     // 引けずに水中に残った自分のウキ
            seen.Add((c.X / cell, c.Y / cell));
        }
        foreach (var key in seen) decoyHeat[key] = (decoyHeat.TryGetValue(key, out var h) ? Math.Min(h.Count + 1, 50) : 1, Now);
        // 2 回以上見えた升目と、その周囲 1 升を除外の範囲にする
        decoys = decoyHeat.Where(kv => kv.Value.Count >= 2)
            .Select(kv => ((kv.Key.Item1 - 1) * cell, (kv.Key.Item2 - 1) * cell, (kv.Key.Item1 + 2) * cell, (kv.Key.Item2 + 2) * cell)).ToList();
        if (decoys.Count > 0 && ++decoyLogTick % 10 == 1) Log.Info($"他人のウキ・アバターの赤白とみなして除外する範囲 {decoys.Count} 升");
    }
}
