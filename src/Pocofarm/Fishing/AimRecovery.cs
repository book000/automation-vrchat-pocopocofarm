using System.Runtime.InteropServices;
using Pocofarm.Core;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

/// <summary>視点補正の結果。</summary>
public enum AimRecoverResult
{
    /// <summary>ずれが許容内に収まった。</summary>
    Aligned,
    /// <summary>基準の画面がまだ無い。</summary>
    NoReference,
    /// <summary>基準の場面を画面から見つけられない(大きく外れた・位置が変わった)。</summary>
    NoMatch,
    /// <summary>最大回数まで補正しても合わなかった。</summary>
    GaveUp,
    /// <summary>マウス入力が効かない(前面でない・反応が逆など)。</summary>
    InputFailed,
    /// <summary>一時停止などで途中でやめた。</summary>
    Aborted,
}

/// <summary>
/// 視点が池からずれたとき、釣れたときの画面(基準)との比較でずれを測り、マウスの相対移動で戻す閉ループ。
/// 回転のずれだけを直す(立ち位置が変わったときは基準の場面と合わず NoMatch になる)。
/// 入出力は <c>look</c> デリゲートに任せ、測って・動かして・撮り直す手順だけをここに持つ。
/// </summary>
public sealed class AimRecovery
{
    readonly Config cfg;
    readonly Paths paths;
    readonly EventLog events;
    byte[]? reference;
    double savedAt;

    /// <summary>基準の画面を持っているか。</summary>
    public bool HasReference => reference is not null;

    public AimRecovery(Config cfg, Paths paths, EventLog events)
    {
        this.cfg = cfg; this.paths = paths; this.events = events;
        try
        {
            if (File.Exists(paths.AimReference)) reference = ViewAligner.ToGrey(Imaging.Load(paths.AimReference));
        }
        catch (Exception e) when (e is IOException or ArgumentException or ExternalException)
        {
            Log.Warn($"視点補正の基準画像を読めませんでした: {e.Message}");
        }
    }

    /// <summary>連続失敗なしで釣れたときの画面を基準にする。ファイルへは 60 秒に 1 回だけ書く(再起動後も使えるように)。</summary>
    public void NoteCatch(Frame f, double now)
    {
        reference = ViewAligner.ToGrey(f);
        if (now - savedAt < 60) return;
        savedAt = now;
        try { Imaging.SavePng(ViewAligner.ToFrame(reference), paths.AimReference); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExternalException)
        {
            Log.Warn($"視点補正の基準画像を保存できませんでした: {e.Message}");
        }
    }

    /// <summary>
    /// start のずれを測り、<paramref name="look"/>(相対マウス移動 dx, dy を送り、撮り直したフレームを返す。送れなければ null)で戻す。
    /// 符号: dx が正で視点が右へ回り、内容は左へ動く。dy が正で視点が下へ向き、内容は上へ動く。よって内容が (+sx, +sy) ずれていれば dx=+gain*sx, dy=+gain*sy。
    /// </summary>
    public AimRecoverResult Recover(Frame start, Func<int, int, Frame?> look, Func<bool> stop)
    {
        if (reference is not { } rf) return Finish(AimRecoverResult.NoReference, 0, null, null, 0, 0);
        double tol = cfg.AimRecoverTolerance, minScore = cfg.AimRecoverMinConfidence;
        // 縦は高さに対する比率なので、同じ回転量でも横の約 9/16 のマウス量で済む
        double gx = cfg.AimRecoverGainInit, gy = gx * 9.0 / 16.0, g0 = gx;
        var cur = ViewAligner.ToGrey(start);
        AimShift? first = null, prev = null;
        (int X, int Y)? lastMove = null;
        int moves = 0, lost = 0, idle = 0;
        bool swept = false;
        while (true)
        {
            if (stop()) return Finish(AimRecoverResult.Aborted, moves, first, prev, gx, gy);
            var m = ViewAligner.Estimate(rf, cur, minScore);
            if (m is null)
            {
                Log.Info($"[aim] 基準の場面が見つかりません(補正{moves}回目)");
                // 空や地面を向いているなど、基準の場面が画面に入っていないときは、縦に少しずつ振って探す(最初の 1 回だけ)
                if (!swept && lastMove is null && moves == 0)
                {
                    swept = true;
                    if (SweepVertical(rf, look, stop, gy, minScore) is { } found) { cur = found; moves++; continue; }
                }
                // 動かしすぎて場面から外れたときだけ、1 回戻してゲインを半分にやり直す
                if (lastMove is not { } lm || lost++ >= 1) return Finish(AimRecoverResult.NoMatch, moves, first, prev, gx, gy);
                gx /= 2; gy /= 2; lastMove = null;
                if (look(-lm.X, -lm.Y) is not { } back) return Finish(AimRecoverResult.InputFailed, moves, first, prev, gx, gy);
                cur = ViewAligner.ToGrey(back);
                continue;
            }
            first ??= m;
            Log.Info($"[aim] ずれ sx={m.Value.Sx:+0.000;-0.000} sy={m.Value.Sy:+0.000;-0.000} 一致度={m.Value.Score:F2} ゲイン={gx:F0}/{gy:F0} 補正={moves}回");
            if (Math.Abs(m.Value.Sx) < tol && Math.Abs(m.Value.Sy) < tol) return Finish(AimRecoverResult.Aligned, moves, first, m, gx, gy);
            if (moves >= cfg.AimRecoverMaxSteps) return Finish(AimRecoverResult.GaveUp, moves, first, m, gx, gy);

            // 直前の移動が意図した量のどれだけ効いたか(1 なら期待どおり)から、ゲインを直す
            if (prev is { } p && lastMove is not null)
            {
                bool ok = true;
                foreach (var (was, now, apply, gain) in new[] { (p.Sx, m.Value.Sx, lastMove.Value.X, 0), (p.Sy, m.Value.Sy, lastMove.Value.Y, 1) })
                {
                    if (apply == 0) continue;
                    double r = (was - now) / was;          // 達成率
                    ref double g = ref (gain == 0 ? ref gx : ref gy);
                    if (r < -0.1) { Log.Warn($"[aim] 反応が逆向きです(達成率{r:F2})。入力の向きが想定と違います"); ok = false; }
                    else if (r < 0.1) { idle++; g = Math.Min(g * 2, g0 * 20); }       // 動かない: 強めてもう一度(2 回続けば入力が届いていない)
                    else { idle = 0; g = Math.Clamp(g / Math.Clamp(r, 0.25, 4.0), g0 / 20, g0 * 20); }
                }
                if (!ok || idle >= 2) return Finish(AimRecoverResult.InputFailed, moves, first, m, gx, gy);
            }

            // 許容の半分に収まった軸は動かさず、ゆれで行ったり来たりしない
            int dx = Math.Abs(m.Value.Sx) < tol / 2 ? 0 : (int)Math.Round(gx * m.Value.Sx);
            int dy = Math.Abs(m.Value.Sy) < tol / 2 ? 0 : (int)Math.Round(gy * m.Value.Sy);
            if (dx == 0 && dy == 0) return Finish(AimRecoverResult.Aligned, moves, first, m, gx, gy);
            Log.Info($"[aim] マウスを動かします dx={dx} dy={dy}");
            if (look(dx, dy) is not { } next) return Finish(AimRecoverResult.InputFailed, moves, first, m, gx, gy);
            moves++;
            prev = m; lastMove = (dx, dy);
            cur = ViewAligner.ToGrey(next);
        }
    }

    const double SweepStep = 0.4;      // 1 回に振る量(画面の高さに対する比率)。基準の一部が画面に残る程度
    const int SweepSteps = 6;
    const double SweepMinScore = 0.85;      // 振っている途中の偶然の似た場面(実測 0.65〜0.67)を拾わないよう、本物の一致(実測 0.95 以上)に近い高さを求める

    /// <summary>
    /// 基準の場面が画面に無いとき、下向き→上向きの順に縦へ振って場面を探す。見つかればその画面を返す。
    /// 見つからなければ振った分だけ戻して null を返す(視点を動かしっぱなしにしない)。
    /// </summary>
    static byte[]? SweepVertical(byte[] rf, Func<int, int, Frame?> look, Func<bool> stop, double gy, double minScore)
    {
        int step = Math.Max(1, (int)Math.Round(gy * SweepStep));
        foreach (int dir in new[] { 1, -1 })
        {
            int total = 0;
            for (int k = 0; k < SweepSteps; k++)
            {
                if (stop() || look(0, dir * step) is not { } fr) return null;
                total += dir * step;
                var g = ViewAligner.ToGrey(fr);
                if (ViewAligner.Estimate(rf, g, Math.Max(minScore, SweepMinScore)) is not null) { Log.Info($"[aim] 縦に振って場面を見つけました(dy={total})"); return g; }
            }
            if (look(0, -total) is null) return null;
        }
        return null;
    }

    AimRecoverResult Finish(AimRecoverResult r, int moves, AimShift? first, AimShift? last, double gx, double gy)
    {
        Log.Info($"[aim] 視点補正の結果: {r}(補正{moves}回)");
        events.Write("aim_recover", ("result", r.ToString()), ("steps", moves),
            ("first_sx", first?.Sx), ("first_sy", first?.Sy), ("last_sx", last?.Sx), ("last_sy", last?.Sy),
            ("score", last?.Score), ("gain_x", Math.Round(gx)), ("gain_y", Math.Round(gy)));
        return r;
    }
}
