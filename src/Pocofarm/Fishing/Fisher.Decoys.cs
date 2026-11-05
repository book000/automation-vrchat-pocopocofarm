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

    sealed record SavedDecoys(int Width, int Height, int Cell, List<double[]> Cells);

    /// <summary>起動時に前回の除外の升目を読む(画面の大きさが同じ最初のフレームで使う)。再起動のたびに他人のウキを拾い直して「早すぎ」が増えるのを避ける。</summary>
    void LoadDecoys()
    {
        try
        {
            if (!File.Exists(paths.Decoys) || DateTime.UtcNow - File.GetLastWriteTimeUtc(paths.Decoys) > TimeSpan.FromHours(6)) return;
            pendingDecoys = System.Text.Json.JsonSerializer.Deserialize<SavedDecoys>(File.ReadAllText(paths.Decoys));
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { Log.Warn($"除外位置の記憶を読めませんでした: {e.Message}"); }
    }

    /// <summary>最初のフレームで画面の大きさが前回と同じなら、前回の除外の升目を戻す。</summary>
    void ApplyPendingDecoys(Frame f)
    {
        var p = pendingDecoys; pendingDecoys = null;
        if (p is null || p.Width != f.Width || p.Height != f.Height || p.Cell != Math.Max(8, (int)(cfg.BobberDecoyCell * scale))) return;
        decoyTracker.Import(p.Cells, p.Cell);
        Log.Info($"前回の除外位置を {decoyTracker.Rects.Count} 升引き継ぎました");
    }

    /// <summary>除外の升目を 30 秒に 1 回までファイルへ書く。</summary>
    void SaveDecoys(Frame f)
    {
        if (Now - decoysSavedAt < 30) return;
        decoysSavedAt = Now;
        var rows = decoyTracker.Export();
        if (rows.Count == 0 && !File.Exists(paths.Decoys)) return;
        AtomicFile.Write(paths.Decoys, System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
            new SavedDecoys(f.Width, f.Height, Math.Max(8, (int)(cfg.BobberDecoyCell * scale)), rows))));
    }

    /// <summary>確認クリックで空中だった位置(追っていたのは自分のウキではなかった)を除外の対象にする。</summary>
    void MarkDecoy((int X, int Y)? pos)
    {
        if (pos is not { } p) return;
        if (Now < viewUnsettledUntil) return;
        // 釣れた実績のある着水位置の近くは本物のウキ。確認クリックのトースト取りこぼしなどで空中と誤判定しても、除外しない
        if (cur is { } f && LearnedLanding(f) is { } l && Math.Abs(l.X - p.X) <= 2 * cfg.BobberDecoyCell * scale && Math.Abs(l.Y - p.Y) <= 2 * cfg.BobberDecoyCell * scale) return;
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
        // 再起動直後は、前のプロセスが投げたままの自分のウキが水面に見えている。これを他人のものと覚えると本物の着水位置を除外して投げ失敗が続くので、最初の試行が終わるまでは覚えない
        if (startupAdopt && recorder.SessionAttempts == 0) return;
        var r = rois.Bobber;
        var region = ((int)(r[0] * f.Width), (int)(r[1] * f.Height), (int)(r[2] * f.Width), (int)(r[3] * f.Height));
        int pad = (int)(cfg.BobberDecoyPad * scale);
        int cell = Math.Max(8, (int)(cfg.BobberDecoyCell * scale));
        var candidates = BobberDetector.Candidates(f, region, null, exclude: ExcludeRects(f.Width, f.Height), scale: scale, p: BobberParams.From(cfg)).Select(c => (c.X, c.Y));
        decoyTracker.Learn(candidates, tip is null ? null : (tip.X, tip.Y), expectAway, pad, cell, Now, cfg.BobberDecoyKeepSeconds);
        SaveDecoys(f);
        var decoys = decoyTracker.Rects;
        if (decoys.Count > 0 && ++decoyLogTick % 10 == 1) Log.Info($"他人のウキ・アバターの赤白とみなして除外する範囲 {decoys.Count} 升");
    }
}
