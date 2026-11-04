using Pocofarm.Core;

namespace Pocofarm.Farm;

/// <summary>画角調整のしきい値。</summary>
public sealed record FarmCalParams(double Margin = 0.04, double Tolerance = 0.03, double Step = 0.5, int Iterations = 6, double MaxShift = 8.0, double ZoomStep = 3.0)
{
    public static FarmCalParams From(Config c) => new(c.FarmCalMargin, c.FarmCalTolerance, c.FarmCalStep, c.FarmCalIterations, c.FarmCalMaxShift, c.FarmCalZoomStep);
}

/// <summary>写真の中の畑の収まり具合。Cx/Cy は 8 面全体の外接矩形の中心、Size は「余白を除いた枠にちょうど収まる大きさ」を 1 とした大きさ。</summary>
public sealed record FarmFit(int Plots, double Cx, double Cy, double Size)
{
    public const int PlotCount = 8;

    public static FarmFit Of(FarmScan scan, double margin)
    {
        if (scan.Plots.Count == 0) return new(0, 0.5, 0.5, 0);
        var xs = scan.Plots.SelectMany(p => new[] { p.Quad[0], p.Quad[2], p.Quad[4], p.Quad[6] }).ToList();
        var ys = scan.Plots.SelectMany(p => new[] { p.Quad[1], p.Quad[3], p.Quad[5], p.Quad[7] }).ToList();
        double w = xs.Max() - xs.Min(), h = ys.Max() - ys.Min();
        return new(scan.Plots.Count, (xs.Min() + xs.Max()) / 2, (ys.Min() + ys.Max()) / 2, Math.Max(w, h) / (1 - 2 * margin));
    }

    /// <summary>8 面そろい、中心が写真の中央に近く、余白を残したまま大きく写っている。</summary>
    public bool Good(double tolerance) => Plots == PlotCount && Math.Abs(Cx - 0.5) <= tolerance && Math.Abs(Cy - 0.5) <= tolerance && Size is <= 1.0 and >= 0.9;

    /// <summary>目標(中央・ちょうど収まる大きさ)からのずれ。小さいほど良い。</summary>
    public double Error => Math.Abs(Cx - 0.5) + Math.Abs(Cy - 0.5) + Math.Abs(Size - 1);

    internal double[] Residual => [Cx - 0.5, Cy - 0.5, Size - 1];
}

/// <summary>
/// カメラの位置(x, z)と画角(Zoom)を動かして、8 面の畑が写真の中央に余白つきで大きく収まる設定を探す。
/// 少し動かしたときの写り方の変化(ヤコビ行列)を実測し、そこからニュートン法で補正する。
/// 高さ y は VRChat がカメラの距離に上限を持つらしく、上げても写りが変わらないので動かさない。向きも動かさない。
/// </summary>
public static class FarmCalibration
{
    /// <summary>姿勢配列の長さ(x, y, z, pitch, yaw, roll, zoom)。</summary>
    public const int PoseLength = 7;

    public const float DefaultZoom = 45f;

    // 調整する姿勢の添字(x, z, zoom)
    static readonly int[] Axes = [0, 2, 6];
    const float MinZoom = 20f, MaxZoom = 150f;   // VRChat の Zoom の範囲(既定 45)

    static double StepOf(int axis, FarmCalParams cp) => axis == 6 ? cp.ZoomStep : cp.Step;

    /// <summary>
    /// start から探す。measure は姿勢を設定して撮影・解析した結果(撮れなければ null)を返す。
    /// 8 面が見つからない間は画角を広げて探し直す。見つからなければ null。
    /// </summary>
    public static async Task<(float[] Pose, FarmFit Fit)?> SolveAsync(float[] start, Func<float[], Task<FarmFit?>> measure, FarmCalParams cp)
    {
        var cur = (float[])start.Clone();
        var fit = await measure(cur);
        for (int i = 0; fit is not { Plots: FarmFit.PlotCount } && i < 6; i++)
        {
            cur[6] -= (float)cp.ZoomStep * 2;   // 8 面見えるまで広げる(実測では値が小さいほど広く写る)
            if (cur[6] < MinZoom) return null;
            fit = await measure(cur);
        }
        if (fit is not { Plots: FarmFit.PlotCount } f0) return null;
        if (f0.Good(cp.Tolerance)) return (cur, f0);

        // 写りの測定にはばらつきがあるので、うまくいかなければ今の最良の位置から測り直して(ヤコビ行列も作り直して)やり直す
        var best = (Pose: cur, Fit: f0);
        for (int round = 0; round < Rounds && !best.Fit.Good(cp.Tolerance); round++)
        {
            var next = await RefineAsync(start, best.Pose, best.Fit, measure, cp);
            if (next.Fit.Error >= best.Fit.Error) break;
            best = next;
        }
        return best;
    }

    const int Rounds = 3;

    static async Task<(float[] Pose, FarmFit Fit)> RefineAsync(float[] start, float[] cur, FarmFit f0, Func<float[], Task<FarmFit?>> measure, FarmCalParams cp)
    {
        // 各軸を少し動かしたときの変化を測る(動かして 8 面が崩れたら逆向きで測る)
        var jac = new double[3, 3];
        for (int c = 0; c < 3; c++)
            if (!await ProbeAsync(cur, f0, Axes[c], StepOf(Axes[c], cp), measure, jac, c)) return (cur, f0);
        Log.Info($"画角調整: 変化の測定 {jac[0,0]:F3} {jac[0,1]:F3} {jac[0,2]:F3} / {jac[1,0]:F3} {jac[1,1]:F3} {jac[1,2]:F3} / {jac[2,0]:F3} {jac[2,1]:F3} {jac[2,2]:F3}");
        if (Solve3(jac, [1, 1, 1]) is null) return (cur, f0);

        var best = (Pose: (float[])cur.Clone(), Fit: f0);
        for (int it = 0; it < cp.Iterations; it++)
        {
            var delta = Solve3(jac, best.Fit.Residual.Select(r => -r).ToArray());
            if (delta is null) break;
            // 写りは位置・画角に対して線形ではないので、1 回に動かす量を探りの幅の数倍までに抑える
            for (int c = 0; c < 3; c++) { double lim = MaxStepFactor * StepOf(Axes[c], cp); delta[c] = Math.Clamp(delta[c], -lim, lim); }
            bool moved = false;
            for (double scale = 1; scale >= 0.06 && !moved; scale /= 2)
            {
                var next = (float[])best.Pose.Clone();
                var dp = new double[3];
                for (int c = 0; c < 3; c++)
                {
                    int ax = Axes[c];
                    double lo = ax == 6 ? MinZoom : start[ax] - cp.MaxShift, hi = ax == 6 ? MaxZoom : start[ax] + cp.MaxShift;
                    next[ax] = (float)Math.Clamp(next[ax] + delta[c] * scale, lo, hi);
                    dp[c] = next[ax] - best.Pose[ax];
                }
                if (await measure(next) is { Plots: FarmFit.PlotCount } nf && nf.Error < best.Fit.Error)
                {
                    UpdateJacobian(jac, dp, nf.Residual, best.Fit.Residual);
                    best = (next, nf);
                    moved = true;
                }
            }
            if (!moved || best.Fit.Good(cp.Tolerance)) break;
        }
        return best;
    }

    const double MaxStepFactor = 4;

    /// <summary>実際に動かした結果でヤコビ行列を更新する(Broyden 法)。</summary>
    static void UpdateJacobian(double[,] jac, double[] dp, double[] after, double[] before)
    {
        double norm = dp.Sum(d => d * d);
        if (norm < 1e-12) return;
        for (int r = 0; r < 3; r++)
        {
            double predicted = 0;
            for (int c = 0; c < 3; c++) predicted += jac[r, c] * dp[c];
            double err = after[r] - before[r] - predicted;
            for (int c = 0; c < 3; c++) jac[r, c] += err * dp[c] / norm;
        }
    }

    static async Task<bool> ProbeAsync(float[] pose, FarmFit f0, int axis, double step, Func<float[], Task<FarmFit?>> measure, double[,] jac, int col)
    {
        // 動かした先で 8 面が見えなくなったら、逆向き、さらに半分の幅でも試す
        foreach (var k in new[] { 1.0, 0.5 })
            foreach (var sign in new[] { 1.0, -1.0 })
            {
                double d = sign * step * k;
                var p = (float[])pose.Clone();
                p[axis] += (float)d;
                if (await measure(p) is { Plots: FarmFit.PlotCount } f)
                {
                    var a = f.Residual; var b = f0.Residual;
                    for (int r = 0; r < 3; r++) jac[r, col] = (a[r] - b[r]) / d;
                    return true;
                }
            }
        return false;
    }

    /// <summary>3 元連立一次方程式 A·x = b(特異に近ければ null)。</summary>
    internal static double[]? Solve3(double[,] a, double[] b)
    {
        var m = new double[3, 4];
        for (int r = 0; r < 3; r++) { for (int c = 0; c < 3; c++) m[r, c] = a[r, c]; m[r, 3] = b[r]; }
        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 3; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-9) return null;
            if (piv != col) for (int c = 0; c < 4; c++) (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
            for (int r = col + 1; r < 3; r++)
            {
                double k = m[r, col] / m[col, col];
                for (int c = col; c < 4; c++) m[r, c] -= k * m[col, c];
            }
        }
        var x = new double[3];
        for (int r = 2; r >= 0; r--)
        {
            double s = m[r, 3];
            for (int c = r + 1; c < 3; c++) s -= m[r, c] * x[c];
            x[r] = s / m[r, r];
        }
        return x;
    }
}
