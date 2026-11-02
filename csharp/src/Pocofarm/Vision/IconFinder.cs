using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using Pocofarm.Core;

namespace Pocofarm.Vision;

/// <summary>画面内からアイコンの見本を探す(大きさ違いも試す正規化相関)。VRChat のメニューは縦横比で大きさが変わるので、文字ではなく形で位置を決める。</summary>
public static class IconFinder
{
    /// <summary>見つけた位置(画面比率)と、一致の度合い(-1〜1)。</summary>
    public readonly record struct Hit(double X, double Y, double Score, double Scale);

    // 見本は 1280x953 の画面で採寸。メニューは画面の高さに比例して大きくなるとは限らないので、幅広く試す
    static readonly double[] Scales = [0.6, 0.7, 0.8, 0.9, 1.0, 1.15, 1.3, 1.5, 1.75, 2.0];

    const int Shrink = 2;                // 照合は半分の解像度で行う(速さのため)

    static readonly Dictionary<string, float[,]> Templates = [];

    /// <summary>埋め込みの見本(Models/ui_*.png)を灰色の配列で返す。</summary>
    static float[,] Template(string name)
    {
        lock (Templates)
        {
            if (Templates.TryGetValue(name, out var t)) return t;
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("models." + name) ?? throw new FileNotFoundException(name);
            using var bmp = new Bitmap(s);
            var g = new float[bmp.Height, bmp.Width];
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); g[y, x] = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B; }
            return Templates[name] = g;
        }
    }

    static readonly Dictionary<string, int> LastScale = [];

    /// <summary>region(画面比率 x0,y0,x1,y1)の中で見本にいちばん合う位置。minScore に届かなければ null。前回見つかった大きさの近くを先に試す(速さのため)。</summary>
    public static Hit? Find(Frame f, string template, (double X0, double Y0, double X1, double Y1) region, double minScore = 0.6)
    {
        int? last; lock (LastScale) last = LastScale.TryGetValue(template, out var l) ? l : null;
        var hit = last is { } i ? FindAt(f, template, region, Math.Max(minScore, 0.85), Scales.Where((_, k) => Math.Abs(k - i) <= 1).ToArray()) : null;
        hit ??= FindAt(f, template, region, minScore, Scales);
        if (hit is { } h) lock (LastScale) LastScale[template] = Array.IndexOf(Scales, h.Scale);
        return hit;
    }

    static Hit? FindAt(Frame f, string template, (double X0, double Y0, double X1, double Y1) region, double minScore, double[] scales)
    {
        int rx0 = (int)(region.X0 * f.Width), ry0 = (int)(region.Y0 * f.Height), rx1 = (int)(region.X1 * f.Width), ry1 = (int)(region.Y1 * f.Height);
        int rw = (rx1 - rx0) / Shrink, rh = (ry1 - ry0) / Shrink;
        if (rw < 4 || rh < 4) return null;
        var img = new float[rh, rw];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                float sum = 0;
                for (int dy = 0; dy < Shrink; dy++)
                    for (int dx = 0; dx < Shrink; dx++)
                    {
                        int i = ((ry0 + y * Shrink + dy) * f.Width + rx0 + x * Shrink + dx) * 4;
                        sum += 0.114f * f.Data[i] + 0.587f * f.Data[i + 1] + 0.299f * f.Data[i + 2];
                    }
                img[y, x] = sum / (Shrink * Shrink);
            }

        var src = Template(template);
        Hit? best = null;
        foreach (var scale in scales)
        {
            var t = Resize(src, scale / Shrink);
            int th = t.GetLength(0), tw = t.GetLength(1);
            if (th < 4 || tw < 4 || th >= rh || tw >= rw) continue;
            var (tz, tn) = Normalize(t);
            for (int y = 0; y + th <= rh; y++)
                for (int x = 0; x + tw <= rw; x++)
                {
                    double score = Correlate(img, x, y, tz, tn);
                    if (score >= minScore && (best is null || score > best.Value.Score))
                        best = new Hit((rx0 + (x + tw / 2.0) * Shrink) / f.Width, (ry0 + (y + th / 2.0) * Shrink) / f.Height, score, scale);
                }
        }
        return best;
    }

    static float[,] Resize(float[,] s, double k)
    {
        int h = Math.Max(1, (int)Math.Round(s.GetLength(0) * k)), w = Math.Max(1, (int)Math.Round(s.GetLength(1) * k));
        var o = new float[h, w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double sy = Math.Min(s.GetLength(0) - 1, (y + 0.5) / k - 0.5), sx = Math.Min(s.GetLength(1) - 1, (x + 0.5) / k - 0.5);
                int y0 = Math.Max(0, (int)sy), x0 = Math.Max(0, (int)sx), y1 = Math.Min(s.GetLength(0) - 1, y0 + 1), x1 = Math.Min(s.GetLength(1) - 1, x0 + 1);
                double fy = sy - y0, fx = sx - x0;
                o[y, x] = (float)((s[y0, x0] * (1 - fx) + s[y0, x1] * fx) * (1 - fy) + (s[y1, x0] * (1 - fx) + s[y1, x1] * fx) * fy);
            }
        return o;
    }

    static (float[,] Zero, double Norm) Normalize(float[,] t)
    {
        double mean = 0; foreach (var v in t) mean += v; mean /= t.Length;
        var z = new float[t.GetLength(0), t.GetLength(1)]; double n = 0;
        for (int y = 0; y < z.GetLength(0); y++)
            for (int x = 0; x < z.GetLength(1); x++) { z[y, x] = (float)(t[y, x] - mean); n += z[y, x] * z[y, x]; }
        return (z, Math.Sqrt(n));
    }

    static double Correlate(float[,] img, int x0, int y0, float[,] tz, double tn)
    {
        int th = tz.GetLength(0), tw = tz.GetLength(1);
        double sum = 0, sq = 0;
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++) { double v = img[y0 + y, x0 + x]; sum += v; sq += v * v; }
        double n = th * tw, mean = sum / n, var = sq - n * mean * mean;
        if (var < 1e-6 || tn < 1e-6) return -1;
        double dot = 0;
        for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++) dot += (img[y0 + y, x0 + x] - mean) * tz[y, x];
        return dot / (Math.Sqrt(var) * tn);
    }
}
