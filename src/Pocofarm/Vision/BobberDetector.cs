using Pocofarm.Core;

namespace Pocofarm.Vision;

/// <summary>検出したウキ。座標は渡した画像内のもの。</summary>
public sealed record Bobber(int X, int Y, int W, int H, int White, int Score);

/// <summary>ウキ(赤い棒+白い玉)の検出。画素しきい値は画面高さ ÷ RefHeight の倍率で拡縮する。</summary>
public static class BobberDetector
{
    /// <summary>ピクセル基準値を決めた画面の高さ(1920x1009 で調整)。</summary>
    public const int RefHeight = 1009;

    const byte Red = 1, White = 2, Scanned = 4;

    [ThreadStatic] static byte[]? flags;

    static int R(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    /// <summary>
    /// 画像中のウキを返す(座標は画像全体のもの)。region で探す範囲を絞り、near を渡すとその周辺だけ探す。
    /// exclude は除外矩形(x0,y0,x1,y1)で、固定 HUD を避ける。maxWidth を超える幅の候補は捨てる(水面のウキは細い)。
    /// </summary>
    public static Bobber? Detect(Frame img, (int X0, int Y0, int X1, int Y1)? region = null, (int X, int Y)? near = null, int radius = 160,
        IReadOnlyList<(int X0, int Y0, int X1, int Y1)>? exclude = null, double scale = 1.0, int maxWidth = int.MaxValue, BobberParams? p = null)
    {
        Bobber? best = null;
        foreach (var c in Candidates(img, region, near, radius, exclude, scale, maxWidth, p))
            if (best is null || c.Score > best.Score) best = c;
        return best;
    }

    /// <summary>条件に合うウキ候補をすべて返す(他人のウキなど、本物でないものを先に把握するため)。</summary>
    public static List<Bobber> Candidates(Frame img, (int X0, int Y0, int X1, int Y1)? region = null, (int X, int Y)? near = null, int radius = 160,
        IReadOnlyList<(int X0, int Y0, int X1, int Y1)>? exclude = null, double scale = 1.0, int maxWidth = int.MaxValue, BobberParams? p = null)
    {
        var found = new List<Bobber>();
        p ??= BobberParams.Default;
        var (x0, y0, x1, y1) = region ?? (0, 0, img.Width, img.Height);
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(img.Width, x1); y1 = Math.Min(img.Height, y1);
        radius = R(radius * scale);
        if (near is { } n)
        {
            x0 = Math.Max(x0, n.X - radius); y0 = Math.Max(y0, n.Y - radius);
            x1 = Math.Min(x1, n.X + radius); y1 = Math.Min(y1, n.Y + radius);
        }
        int ox = x0, oy = y0, w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return found;

        int n0 = w * h;
        var fl = Classify(img, x0, y0, w, h, p);
        foreach (var (ex0, ey0, ex1, ey1) in exclude ?? [])
        {
            int a = Math.Max(0, ex0 - ox), b2 = Math.Max(0, ey0 - oy), c = Math.Min(w, ex1 - ox), e = Math.Min(h, ey1 - oy);
            if (ex1 - ox <= 0 || ey1 - oy <= 0) continue;
            for (int y = b2; y < e; y++)
                for (int x = a; x < c; x++) fl[y * w + x] &= unchecked((byte)~Red);
        }

        var stack = new Stack<int>();
        for (int start = 0; start < n0; start++)
        {
            if (fl[start] != Red) continue;
            var (minX, minY, maxX, maxY, area) = Flood(fl, w, h, start, stack, Math.Max(1, R(p.JoinGap * scale)));
            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            if (area < p.MinArea * scale * scale || cw > p.MaxWidth * scale || cw > maxWidth || ch > p.MaxHeight * scale || ch < p.MinHeight * scale || ch < p.MinAspect * cw) continue;

            // ウキが大きく写る(手前)ほど白玉も大きいので、棒の高さに比例して探す
            int side = R(p.WhiteSidePad * scale), top = R(p.WhiteTopPad * scale);
            int reach = Math.Max(R(p.WhiteReachMin * scale), ch + R(p.WhiteReachPad * scale));
            int py0 = minY + ch - top, py1 = Math.Min(h, minY + ch + reach);
            int px0 = Math.Max(0, minX - side), px1 = Math.Min(w, minX + cw + side);
            int wc = 0; long colSum = 0; int cols = 0;
            for (int x = px0; x < px1; x++)
            {
                bool any = false;
                for (int y = Math.Max(0, py0); y < py1; y++)
                    if ((fl[y * w + x] & White) != 0) { wc++; any = true; }
                if (any) { colSum += x - px0; cols++; }
            }
            if (wc < p.MinWhite * scale * scale || cols == 0) continue;
            // 白玉の重心が赤い棒の真下付近にあること(髪飾りなど横にずれた白を除く)
            if (Math.Abs(colSum / (double)cols + px0 - (minX + cw / 2.0)) > Math.Max(p.WhiteCenterTol * scale, cw)) continue;

            // 白だけで競うと、髪飾りや服の白が多い他人のアバターが本物に勝つ。赤い棒の面積も加点して、ウキらしい(白+太い赤)ものを選ぶ
            var cand = new Bobber(minX + ox + cw / 2, minY + oy, cw, ch, wc, wc + 8 * area);
            found.Add(cand);
        }
        return found;
    }

    /// <summary>画素ごとのフラグ(Red / White)を作る。毎フレーム確保すると LOH を食うので使い回す。</summary>
    static byte[] Classify(Frame img, int x0, int y0, int w, int h, BobberParams p)
    {
        int n0 = w * h;
        if (flags is null || flags.Length < n0) flags = new byte[n0];
        else Array.Clear(flags, 0, n0);
        var fl = flags;
        var d = img.Data;
        for (int y = 0; y < h; y++)
        {
            int src = ((y + y0) * img.Width + x0) * 4, dst = y * w;
            for (int x = 0; x < w; x++, src += 4, dst++)
            {
                byte b = d[src], g = d[src + 1], r = d[src + 2];
                // 彩度・明度で早期に弾く(HSV への変換は赤の候補だけ)
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx >= p.WhiteMinValue && (mx - mn) * 255.0 < p.WhiteSatLimit * mx) { fl[dst] = White; continue; }
                if (mx < p.RedMinValue || mx == 0 || (mx - mn) * 255 < p.RedMinSaturation * mx - 127) continue;
                var (hh, s, v) = Frame.Hsv(b, g, r);
                if (s >= p.RedMinSaturation && v >= p.RedMinValue && (hh <= p.RedHueMax || hh >= p.RedHueMin)) fl[dst] = Red;
            }
        }
        return fl;
    }

    /// <summary>赤画素の連結成分を走査して外接矩形と面積を得る。遠くの小さなウキは棒が細く、縦に 1〜2 画素途切れて 2 つに割れるので、縦方向は gap 画素まで飛ばして繋ぐ。</summary>
    static (int MinX, int MinY, int MaxX, int MaxY, int Area) Flood(byte[] fl, int w, int h, int start, Stack<int> stack, int gap)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1, area = 0;
        stack.Push(start); fl[start] |= Scanned;
        while (stack.Count > 0)
        {
            int q0 = stack.Pop(), px = q0 % w, py = q0 / w;
            area++;
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
            for (int dy = -gap; dy <= gap; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int qx = px + dx, qy = py + dy;
                    if ((uint)qx >= (uint)w || (uint)qy >= (uint)h) continue;
                    int q = qy * w + qx;
                    if (fl[q] == Red) { fl[q] |= Scanned; stack.Push(q); }
                }
        }
        return (minX, minY, maxX, maxY, area);
    }
}
