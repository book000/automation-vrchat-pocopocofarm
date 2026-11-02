using Pocofarm.Core;

namespace Pocofarm.Vision;

/// <summary>
/// 視点の移動(歩行・マウス)の検出。フレームを 256x144 のグレーに縮め、前フレームとの平行移動をブロックマッチングで推定する。
/// 水や魚の動きでは閾値(1.2px)に届かない
/// </summary>
public sealed class ViewMotion
{
    const int W = 256, H = 144, Range = 6, Margin = 8;
    readonly double threshold, holdSeconds;

    public ViewMotion(double threshold, double holdSeconds)
    {
        this.threshold = threshold;
        this.holdSeconds = holdSeconds;
    }

    byte[]? prev, spare;
    double movingUntil;

    /// <summary>フレームを与え、視点が動いている(直近 0.7 秒以内に動きがあった)なら true。</summary>
    public bool Update(Frame f, double now)
    {
        var small = spare ?? new byte[W * H];
        Shrink(f, small);
        if (prev is not null && ShiftOf(prev, small) > threshold) movingUntil = now + holdSeconds;
        spare = prev; prev = small;
        return now < movingUntil;
    }

    /// <summary>状態を捨てる(キャプチャ再開・解像度変更時)。</summary>
    public void Reset() { prev = null; spare = null; movingUntil = 0; }

    /// <summary>箱平均で 256x144 のグレー画像にする。全画素は重いので箱内を 1 画素おきに間引く。</summary>
    static unsafe void Shrink(Frame f, byte[] o)
    {
        fixed (byte* src = f.Data)
        {
            for (int y = 0; y < H; y++)
            {
                int y0 = y * f.Height / H, y1 = Math.Max(y0 + 1, (y + 1) * f.Height / H);
                for (int x = 0; x < W; x++)
                {
                    int x0 = x * f.Width / W, x1 = Math.Max(x0 + 1, (x + 1) * f.Width / W);
                    int sum = 0, n = 0;
                    for (int yy = y0; yy < y1; yy += 2)
                    {
                        byte* p = src + (yy * f.Width + x0) * 4;
                        for (int xx = x0; xx < x1; xx += 2, p += 8)
                        {
                            sum += (p[0] * 29 + p[1] * 150 + p[2] * 77) >> 8;
                            n++;
                        }
                    }
                    o[y * W + x] = (byte)(sum / n);
                }
            }
        }
    }

    /// <summary>a→b の平行移動量(縮小画素)の大きさ。中央部の絶対差和が最小になるずれを探し、放物線で補間する。</summary>
    static double ShiftOf(byte[] a, byte[] b)
    {
        // ずれ 0 でほぼ一致する(静止画面が大半)なら探索を省く
        int zero = 0, cnt = 0;
        for (int y = Margin; y < H - Margin; y += 2)
            for (int x = Margin; x < W - Margin; x += 2, cnt++) zero += Math.Abs(a[y * W + x] - b[y * W + x]);
        if (zero < cnt * 0.6) return 0;
        var sad = new double[2 * Range + 1, 2 * Range + 1];
        int best = int.MaxValue, bx = 0, by = 0;
        for (int dy = -Range; dy <= Range; dy++)
            for (int dx = -Range; dx <= Range; dx++)
            {
                int s = 0;
                for (int y = Margin; y < H - Margin; y += 2)
                {
                    int ra = y * W, rb = (y + dy) * W;
                    for (int x = Margin; x < W - Margin; x += 2) s += Math.Abs(a[ra + x] - b[rb + x + dx]);
                }
                sad[dy + Range, dx + Range] = s;
                if (s < best) { best = s; bx = dx; by = dy; }
            }
        double Sub(double l, double c, double r) => l + r - 2 * c is var d and > 0 ? (l - r) / (2 * d) : 0;
        double fx = bx > -Range && bx < Range ? Sub(sad[by + Range, bx - 1 + Range], sad[by + Range, bx + Range], sad[by + Range, bx + 1 + Range]) : 0;
        double fy = by > -Range && by < Range ? Sub(sad[by - 1 + Range, bx + Range], sad[by + Range, bx + Range], sad[by + 1 + Range, bx + Range]) : 0;
        return Math.Sqrt((bx + fx) * (bx + fx) + (by + fy) * (by + fy));
    }
}
