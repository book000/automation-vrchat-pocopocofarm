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

    /// <summary>確認クリックで空中だった位置(追っていたのは自分のウキではなかった)を除外の対象にする。</summary>
    void MarkDecoy((int X, int Y)? pos)
    {
        if (pos is not { } p) return;
        int cell = Math.Max(8, (int)(cfg.BobberDecoyCell * scale));
        decoyTracker.Mark(p.X, p.Y, (int)(cfg.BobberDecoyPad * scale), cell, Now);
        Log.Info($"確認クリックで空中だったため、({p.X}, {p.Y}) の候補をウキではないものとして除外します");
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
        var candidates = BobberDetector.Candidates(f, region, null, exclude: ExcludeRects(f.Width, f.Height), scale: scale, p: BobberParams.From(cfg)).Select(c => (c.X, c.Y));
        decoyTracker.Learn(candidates, tip is null ? null : (tip.X, tip.Y), expectAway, pad, cell, Now, cfg.BobberDecoyKeepSeconds);
        var decoys = decoyTracker.Rects;
        if (decoys.Count > 0 && ++decoyLogTick % 10 == 1) Log.Info($"他人のウキ・アバターの赤白とみなして除外する範囲 {decoys.Count} 升");
    }
}
