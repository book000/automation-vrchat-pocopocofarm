using System.Text.Json;
using System.Text.Json.Nodes;
using Pocofarm.Core;

namespace Pocofarm.Fishing;

/// <summary>
/// 引きから次の投げまでの待ち時間を自動で詰める。
/// 上限は設定値(reel_to_cast_min)。投げが連続して成功するたびに少しずつ縮め、竿先にウキが残る不発が出たら
/// そのときの間隔に余裕を足した値へ戻して、そこより短い間隔は試さない。結果は data/tuning.json に残し、再起動後も引き継ぐ。
/// あわせて、引くまでの沈みの回数(早すぎが続いたら増やし、成功が続けば戻す)と当たり待ちの打ち切り秒数も実測から調整する。
/// </summary>
public sealed class AutoTuner
{
    readonly Config cfg;
    readonly string file;
    int streak;
    double? value, failedAt;
    int earlyStreak, caughtStreak;
    double lastBiteTimeout;
    readonly Queue<double> waitSamples = new();

    public AutoTuner(Config cfg, string file)
    {
        this.cfg = cfg; this.file = file;
        PecksToReel = cfg.PecksToReel;
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject o)
            {
                value = (double?)o["reel_to_cast"];
                failedAt = (double?)o["failed_at"];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidCastException) { }
    }

    /// <summary>何回目の沈みで引くか。</summary>
    public int PecksToReel { get; private set; }

    /// <summary>いま使う待ち時間[秒]。自動調整が無効なら設定値そのまま。</summary>
    public double ReelToCast => !cfg.TuneReelToCast ? cfg.ReelToCastMin : Math.Clamp(value ?? cfg.ReelToCastMin, Lower, Upper);

    // 設定値より長い待ちが要る環境(高解像度で描画が遅く、投げの受付ロックが長引くなど)では、不発のたびに上限まで延ばす
    double Upper => Math.Max(cfg.ReelToCastMin, cfg.ReelToCastMax);

    double Lower => Math.Min(Upper, Math.Max(cfg.ReelToCastFloor, failedAt is { } f ? f + cfg.TuneMargin : 0));

    /// <summary>投げが成功した(ウキが着水して静止した)。dt はそのときの実際の間隔。</summary>
    public void OnCastSucceeded(double dt)
    {
        if (!cfg.TuneReelToCast) return;
        // 実測の間隔はループの遅れ分だけ設定値より少し長くなる。それを超えて待たされた回(停止明けなど)は証拠にならないので数えない
        if (dt > ReelToCast + cfg.TuneDtTolerance) return;
        if (++streak < cfg.TuneSuccessStreak) return;
        streak = 0;
        // 設定値を超えて延ばした待ち時間は、長く成功が続いたら不発の記録を薄めて、また縮められるようにする(一時的に遅かっただけの場合に戻れる)
        if (failedAt is { } fa && ReelToCast > cfg.ReelToCastMin) failedAt = fa - cfg.ReelToCastStep < cfg.ReelToCastFloor ? null : fa - cfg.ReelToCastStep;
        double next = Math.Max(Lower, ReelToCast - cfg.ReelToCastStep);
        if (next >= ReelToCast) return;
        Log.Info($"投げの待ち時間を縮めます: {ReelToCast:F2} → {next:F2} 秒");
        value = next;
        Save();
    }

    /// <summary>投げが不発だった(ウキが竿先に残った)。短すぎた可能性が高いので、その間隔より余裕を持たせる。</summary>
    public void OnCastFailed(double dt)
    {
        if (!cfg.TuneReelToCast) return;
        streak = 0;
        // 待ち時間より大きく外れた間隔(視線のずれ・長い停止など)の不発は、待ち時間のせいではないので学習しない
        if (dt > ReelToCast + cfg.TuneDtTolerance) return;
        failedAt = failedAt is { } f ? Math.Max(f, Math.Min(dt, ReelToCast)) : Math.Min(dt, ReelToCast);
        double next = Math.Min(Upper, failedAt.Value + cfg.TuneMargin);
        if (next <= ReelToCast) { Save(); return; }
        Log.Warn($"投げが不発だったため待ち時間を戻します: {ReelToCast:F2} → {next:F2} 秒(間隔 {dt:F2} 秒で失敗)");
        value = next;
        Save();
    }

    /// <summary>釣れるまでの待ち時間[秒]を実測に加える(当たり待ちの打ち切りの根拠)。</summary>
    public void NoteBiteWait(double seconds)
    {
        waitSamples.Enqueue(seconds);
        if (waitSamples.Count > cfg.BiteWaitSamples) waitSamples.Dequeue();
    }

    /// <summary>当たり待ちの打ち切り秒数。実測の最大待ちの数倍(下限と上限は設定値)。打ち切っても確認クリックで引くので、取りこぼしにはならない。</summary>
    public double BiteTimeout()
    {
        if (waitSamples.Count < cfg.BiteWaitMinSamples) return cfg.NoBiteTimeout;
        double t = Math.Clamp(waitSamples.Max() * cfg.NoBiteFactor, Math.Min(cfg.NoBiteMin, cfg.NoBiteTimeout), cfg.NoBiteTimeout);
        if (Math.Abs(t - lastBiteTimeout) >= 2) { lastBiteTimeout = t; Log.Info($"当たり待ちの打ち切りを {t:F0} 秒に調整(直近{waitSamples.Count}件の最大 {waitSamples.Max():F1} 秒)"); }
        return t;
    }

    /// <summary>試行の結果から、引く沈みの回数を調整する。</summary>
    public void OnOutcome(string outcome)
    {
        if (outcome == "early")
        {
            earlyStreak++;
            if (earlyStreak >= cfg.EarlyStreakToAdapt && PecksToReel < cfg.MaxPecksToReel)
            {
                PecksToReel++;
                Log.Warn($"早すぎが続いたため、{PecksToReel}回目の沈みで引くよう調整");
                earlyStreak = 0;
            }
        }
        else if (outcome == "caught")
        {
            earlyStreak = 0;
            // 一時的な誤検知(魚の接近など)で増やした回数は、成功が続けば基準へ戻す(引く回数が多いほど 1 回あたりが遅くなる)
            if (PecksToReel > cfg.PecksToReel && ++caughtStreak >= cfg.CaughtStreakToRevert)
            {
                PecksToReel--;
                caughtStreak = 0;
                Log.Info($"成功が続いたため、{PecksToReel}回目の沈みで引くよう戻しました");
            }
        }
        if (outcome != "caught") caughtStreak = 0;
    }

    void Save()
    {
        try
        {
            var o = new JsonObject { ["reel_to_cast"] = Math.Round(ReelToCast, 3) };
            if (failedAt is { } f) o["failed_at"] = Math.Round(f, 3);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, o.ToJsonString());
        }
        catch (IOException) { }
    }
}
