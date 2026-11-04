using Pocofarm.Core;

namespace Pocofarm.Vision;

/// <summary>ある視点(基準)に対して、いまの画面が画像上でどれだけずれているか。単位は画面幅・高さに対する比率で、内容が右/下へ動いたときが正。</summary>
/// <param name="Sx">横のずれ(幅に対する比率)</param>
/// <param name="Sy">縦のずれ(高さに対する比率)</param>
/// <param name="Score">一致度(正規化相関。1 に近いほど確か)</param>
public readonly record struct AimShift(double Sx, double Sy, double Score);

/// <summary>
/// 視点のずれの推定。基準フレームの左上寄りの領域(竿・右手・HUD・トーストを避けた池の景色)をテンプレートにして、
/// 現在のフレーム全体から正規化相関(NCC)で探す。ViewMotion のフレーム間の小さな動きと違い、画面の大半が入れ替わるほどのずれも追える。
/// 純粋な計算だけで、入出力は持たない。
/// </summary>
public static class ViewAligner
{
    /// <summary>比較用に縮めるグレー画像の大きさ。</summary>
    public const int W = 128, H = 72;

    // テンプレート領域(画面比率 x 0.08-0.45, y 0.15-0.60)を縮小画像の画素にしたもの
    const int TX0 = 10, TX1 = 58, TY0 = 11, TY1 = 43;

    /// <summary>テンプレートのうち、ずらした先で画面内に残っている面積の下限(これを下回る位置は候補にしない)。</summary>
    const double MinOverlap = 0.6;

    /// <summary>テンプレートの輝度の標準偏差の下限。平坦な領域(空・暗転)では位置が決まらない。</summary>
    const double MinStdDev = 4.0;

    /// <summary>1 位と 2 位(3 画素以上離れた位置)の一致度の差がこれ未満なら、繰り返し模様などで位置が決まらないとして捨てる。</summary>
    const double MinMargin = 0.03;

    /// <summary>フレームを箱平均で 128x72 のグレー画像にする(同じ大きさなら恒等)。</summary>
    public static unsafe byte[] ToGrey(Frame f)
    {
        var o = new byte[W * H];
        fixed (byte* src = f.Data)
        {
            for (int y = 0; y < H; y++)
            {
                int y0 = y * f.Height / H, y1 = Math.Max(y0 + 1, (y + 1) * f.Height / H);
                for (int x = 0; x < W; x++)
                {
                    int x0 = x * f.Width / W, x1 = Math.Max(x0 + 1, (x + 1) * f.Width / W);
                    int sum = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        byte* p = src + (yy * f.Width + x0) * 4;
                        for (int xx = x0; xx < x1; xx++, p += 4, n++) sum += (p[0] * 29 + p[1] * 150 + p[2] * 77) >> 8;
                    }
                    o[y * W + x] = (byte)(sum / n);
                }
            }
        }
        return o;
    }

    /// <summary>グレー画像を PNG 保存用のフレームにする。</summary>
    public static Frame ToFrame(byte[] grey)
    {
        var f = new Frame(W, H);
        for (int i = 0; i < grey.Length; i++) f.Set(i % W, i / W, grey[i], grey[i], grey[i]);
        return f;
    }

    /// <summary>
    /// reference に対する current のずれを返す。見つからない・自信がない(場面が違う)ときは null。
    /// テンプレートは画面外へはみ出してもよく、残った部分(面積の 6 割以上)だけで比べる。
    /// </summary>
    public static AimShift? Estimate(byte[] reference, byte[] current, double minScore)
    {
        int tw = TX1 - TX0, th = TY1 - TY0;
        // 平坦なテンプレートは位置が決まらない
        double tsum = 0, tsq = 0;
        for (int y = TY0; y < TY1; y++)
            for (int x = TX0; x < TX1; x++) { double v = reference[y * W + x]; tsum += v; tsq += v * v; }
        int tn = tw * th;
        if (Math.Sqrt(Math.Max(0, tsq / tn - tsum / tn * (tsum / tn))) < MinStdDev) return null;

        // 位置 (dx, dy) = テンプレートを動かした量。配列の添字は dx + tw, dy + th
        int nx = W + tw, ny = H + th;
        var score = new double[ny, nx];
        double best = -2; int bx = 0, by = 0;
        for (int dy = -th; dy < H; dy++)
            for (int dx = -tw; dx < W; dx++)
            {
                double s = Ncc(reference, current, dx, dy, tw, th);
                score[dy + th, dx + tw] = s;
                if (s > best) { best = s; bx = dx; by = dy; }
            }
        if (best < minScore) return null;

        // 同じくらい似た別の場所があるなら決めない
        double second = -2;
        for (int dy = -th; dy < H; dy++)
            for (int dx = -tw; dx < W; dx++)
                if ((Math.Abs(dx - bx) >= 3 || Math.Abs(dy - by) >= 3) && score[dy + th, dx + tw] > second) second = score[dy + th, dx + tw];
        if (best - second < MinMargin) return null;

        // 放物線で画素未満まで補間する(隣が無効=-2 のときは補間しない)
        double Sub(double l, double c, double r) => l > -1 && r > -1 && l + r - 2 * c is var d and < 0 ? (l - r) / (2 * d) : 0;
        double fx = Sub(At(score, th, tw, by, bx - 1), best, At(score, th, tw, by, bx + 1));
        double fy = Sub(At(score, th, tw, by - 1, bx), best, At(score, th, tw, by + 1, bx));
        return new AimShift((bx + fx) / W, (by + fy) / H, best);
    }

    static double At(double[,] s, int th, int tw, int dy, int dx) =>
        dy + th >= 0 && dy + th < s.GetLength(0) && dx + tw >= 0 && dx + tw < s.GetLength(1) ? s[dy + th, dx + tw] : -2;

    /// <summary>テンプレートを (dx, dy) ずらしたときの重なり部分の正規化相関。重なりが足りない・平坦なら -2。</summary>
    static double Ncc(byte[] r, byte[] c, int dx, int dy, int tw, int th)
    {
        int x0 = Math.Max(TX0, -dx), x1 = Math.Min(TX1, W - dx);
        int y0 = Math.Max(TY0, -dy), y1 = Math.Min(TY1, H - dy);
        if (x1 <= x0 || y1 <= y0 || (double)(x1 - x0) * (y1 - y0) < MinOverlap * tw * th) return -2;
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        for (int y = y0; y < y1; y++)
        {
            int ra = y * W, rb = (y + dy) * W + dx;
            for (int x = x0; x < x1; x++)
            {
                double a = r[ra + x], b = c[rb + x];
                sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
            }
        }
        double n = (x1 - x0) * (y1 - y0);
        double va = n * saa - sa * sa, vb = n * sbb - sb * sb;
        if (va < 1e-6 || vb < 1e-6) return -2;
        return (n * sab - sa * sb) / Math.Sqrt(va * vb);
    }
}
