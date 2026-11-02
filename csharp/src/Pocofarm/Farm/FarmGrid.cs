using Pocofarm.Core;

namespace Pocofarm.Farm;

/// <summary>畑 1 面(3 列×4 行)。Quad は左上・右上・右下・左下の x,y(画像に対する比率)、Cells は行優先 12 文字(植え(緑の有無)と水やり(濃い茶色か)は別: e=植えなし・水やり前 p=植え済・水やり前 b=植えなし・水やり済 w=植え済・水やり済(目標) ?=アバター等で隠れて判別できない)。</summary>
/// <summary>Wet は各マスの「濃い土の画素の割合」(0〜100)。水やりの有無の境目付近の揺れを見分けるために残す。</summary>
public sealed record FarmPlot(double[] Quad, string Cells, int[]? Wet = null);

/// <summary>ファーム写真 1 枚の解析結果。</summary>
public sealed record FarmScan(List<FarmPlot> Plots)
{
    public int Count(char c) => Plots.Sum(p => p.Cells.Count(x => x == c));

    /// <summary>植わっていないマス(植えなし・水やり前 + 植えなし・水やり済)。</summary>
    public int Unplanted => Count('e') + Count('b');

    /// <summary>水やりされていないマス(植えなし・水やり前 + 植え済・水やり前)。</summary>
    public int Dry => Count('e') + Count('p');
}

/// <summary>ファーム解析のしきい値。</summary>
public sealed record FarmScanParams(int EdgeContrast = 14, double EmptyBr = 0.54, double PlantRatio = 0.002,
    double InsetX = 0.05, double InsetY = 0.035, double CellWindow = 0.38, double HiddenRatio = 0.45, int AnalysisWidth = 640)
{
    public static FarmScanParams From(Config c) => new(c.FarmEdgeContrast, c.FarmEmptyBr, c.FarmPlantRatio, c.FarmInsetX, c.FarmInsetY, c.FarmCellWindow, c.FarmHiddenRatio, c.FarmAnalysisWidth);
}

/// <summary>上から見たファーム写真から、畑の枠(8 面)と各マスの植え付け状況を求める。枠は周囲より暗い線として拾う。</summary>
public static class FarmGrid
{
    const int Cols = 3, Rows = 4;

    sealed class Comp
    {
        public int N, MinX = int.MaxValue, MaxX = int.MinValue, MinY = int.MaxValue, MaxY = int.MinValue;
        public (int X, int Y) MinS = (0, 0), MaxS = (0, 0), MinT = (0, 0), MaxT = (0, 0);
        int minS = int.MaxValue, maxS = int.MinValue, minT = int.MaxValue, maxT = int.MinValue;

        public void Add(int x, int y)
        {
            N++;
            MinX = Math.Min(MinX, x); MaxX = Math.Max(MaxX, x); MinY = Math.Min(MinY, y); MaxY = Math.Max(MaxY, y);
            int s = x + y, t = x - y;
            if (s < minS) { minS = s; MinS = (x, y); }
            if (s > maxS) { maxS = s; MaxS = (x, y); }
            if (t < minT) { minT = t; MinT = (x, y); }
            if (t > maxT) { maxT = t; MaxT = (x, y); }
        }

        /// <summary>左上・右上・右下・左下の角。</summary>
        public (int X, int Y)[] Corners() => [MinS, MaxT, MaxS, MinT];
    }

    /// <summary>
    /// 解析する。EdgeContrast は枠線とみなす暗さ(周囲平均との輝度差)、EmptyBr は 青/赤 がこの値以上の明るい地面色を「空き」とする値、
    /// PlantRatio は明るいマスでも緑画素がこの割合以上なら芽があるとみなす。
    /// </summary>
    public static FarmScan Scan(Frame f, FarmScanParams p)
    {
        double factor = Math.Min(1.0, p.AnalysisWidth / (double)f.Width);
        var ds = factor < 1 ? f.Downscale(factor) : f;
        var quads = FindPlots(ds, p.EdgeContrast);
        var plots = quads.Select(q => Classify(f, q, ds.Width, ds.Height, p)).ToList();
        return new FarmScan(plots);
    }

    public static FarmScan Scan(Frame f, int edgeContrast, double emptyBr, double plantRatio) => Scan(f, new FarmScanParams(edgeContrast, emptyBr, plantRatio));

    /// <summary>左上から右下へ(行ごとに左→右)並べた畑の角(縮小画像上の座標)。</summary>
    static List<(int X, int Y)[]> FindPlots(Frame ds, int contrast)
    {
        int w = ds.Width, h = ds.Height;
        var lum = new int[w * h];
        for (int i = 0, j = 0; i < lum.Length; i++, j += 4) lum[i] = (ds.Data[j + 2] * 77 + ds.Data[j + 1] * 151 + ds.Data[j] * 28) >> 8;

        var sum = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long row = 0;
            for (int x = 0; x < w; x++)
            {
                row += lum[y * w + x];
                sum[(y + 1) * (w + 1) + x + 1] = sum[y * (w + 1) + x + 1] + row;
            }
        }

        int r = Math.Max(4, w / 52);
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                long s = sum[y1 * (w + 1) + x1] - sum[y0 * (w + 1) + x1] - sum[y1 * (w + 1) + x0] + sum[y0 * (w + 1) + x0];
                // 育った葉(緑)も暗く写るので、枠(茶色)だけを拾う
                int j = (y * w + x) * 4;
                bool leaf = ds.Data[j + 1] > ds.Data[j + 2] + 5 && ds.Data[j + 1] > ds.Data[j] + 20;
                mask[y * w + x] = !leaf && lum[y * w + x] < s / ((long)(y1 - y0) * (x1 - x0)) - contrast;
            }
        }

        // 欠けた枠線をつなぐため 3×3 で膨らませる
        var grown = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!mask[y * w + x]) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx < (uint)w && (uint)ny < (uint)h) grown[ny * w + nx] = true;
                    }
            }

        var seen = new bool[w * h];
        var stack = new Stack<int>();
        var found = new List<(int X, int Y)[]>();
        var dims = new List<(int W, int H)>();
        for (int i = 0; i < grown.Length; i++)
        {
            if (!grown[i] || seen[i]) continue;
            var c = new Comp();
            seen[i] = true; stack.Push(i);
            while (stack.Count > 0)
            {
                int p = stack.Pop(), x = p % w, y = p / w;
                c.Add(x, y);
                if (x > 0 && grown[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack.Push(p - 1); }
                if (x < w - 1 && grown[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack.Push(p + 1); }
                if (y > 0 && grown[p - w] && !seen[p - w]) { seen[p - w] = true; stack.Push(p - w); }
                if (y < h - 1 && grown[p + w] && !seen[p + w]) { seen[p + w] = true; stack.Push(p + w); }
            }
            int bw = c.MaxX - c.MinX, bh = c.MaxY - c.MinY;
            var cr = c.Corners();
            // 畑は上辺・下辺がほぼ水平(黒板など斜めのものは除く)
            bool level = Math.Abs(cr[0].Y - cr[1].Y) <= 0.12 * bh && Math.Abs(cr[3].Y - cr[2].Y) <= 0.12 * bh;
            if (level && bw >= 0.10 * w && bw <= 0.20 * w && bh >= 0.078 * w && bh <= 0.25 * w && c.N >= 0.002 * w * w)
            {
                found.Add(cr);
                dims.Add((bw, bh));
            }
        }
        return Arrange(found, dims, w);
    }

    /// <summary>行ごとに並べ、1 面だけ欠けた行(隣の畑と融合して大きすぎた等)は等間隔の位置へ補う。</summary>
    static List<(int X, int Y)[]> Arrange(List<(int X, int Y)[]> plots, List<(int W, int H)> dims, int imgW)
    {
        if (plots.Count == 0) return plots;
        double medH = dims.Select(d => (double)d.H).OrderBy(v => v).ElementAt(dims.Count / 2);
        var ordered = plots.OrderBy(Cy).ToList();
        var rows = new List<List<(int X, int Y)[]>> { new() { ordered[0] } };
        foreach (var p in ordered.Skip(1))
        {
            if (Cy(p) - Cy(rows[^1][0]) > 0.5 * medH) rows.Add([]);
            rows[^1].Add(p);
        }
        // 畑は 2 段に並ぶ。机などが紛れて段が増えたら、畑の多い 2 段だけを残す
        if (rows.Count > 2) rows = rows.OrderByDescending(r => r.Count).Take(2).OrderBy(r => Cy(r[0])).ToList();
        var result = new List<(int X, int Y)[]>();
        foreach (var row in rows)
        {
            row.Sort((a, b) => Cx(a).CompareTo(Cx(b)));
            if (row.Count is 2 or 3) FillRow(row, imgW);
            result.AddRange(row);
        }
        return result;
    }

    static double Cx((int X, int Y)[] q) => q.Average(p => p.X);
    static double Cy((int X, int Y)[] q) => q.Average(p => p.Y);

    /// <summary>4 面並ぶはずの行で欠けた畑を、隣の畑を平行移動して補う。端は画像内に収まる側だけに補う。</summary>
    static void FillRow(List<(int X, int Y)[]> row, int imgW)
    {
        var gaps = Enumerable.Range(0, row.Count - 1).Select(i => Cx(row[i + 1]) - Cx(row[i])).ToList();
        double pitch = gaps.Min();
        double width = row.Average(q => q.Max(p => p.X) - q.Min(p => p.X));
        for (int i = row.Count - 2; i >= 0; i--)
            if (gaps[i] > 1.5 * pitch && row.Count < 4) row.Insert(i + 1, Shift(row[i], (int)Math.Round(gaps[i] / 2)));
        // 端の欠け: 画像内に収まる側が一方だけなら、4 面になるまでそちらへ補う(アバターで隣の畑が 2 面つながって落ちた場合など)
        while (row.Count < 4)
        {
            double left = Cx(row[0]) - pitch, right = Cx(row[^1]) + pitch;
            bool fitLeft = left - width / 2 >= 0, fitRight = right + width / 2 <= imgW;
            if (fitLeft && !fitRight) row.Insert(0, Shift(row[0], (int)Math.Round(-pitch)));
            else if (fitRight && !fitLeft) row.Add(Shift(row[^1], (int)Math.Round(pitch)));
            else break;
        }
    }

    static (int X, int Y)[] Shift((int X, int Y)[] q, int dx) => q.Select(p => (p.X + dx, p.Y)).ToArray();

    /// <summary>各マスの中央付近の色で状態を決める。</summary>
    static FarmPlot Classify(Frame f, (int X, int Y)[] q, int dsW, int dsH, FarmScanParams p)
    {
        double sx = (double)f.Width / dsW, sy = (double)f.Height / dsH;
        var cells = new char[Rows * Cols];
        var wet = new int[Rows * Cols];
        for (int rr = 0; rr < Rows; rr++)
            for (int cc = 0; cc < Cols; cc++)
            {
                // 枠の太さの分だけ内側へ寄せる(角は外枠の端なので、そのままだと中心が枠側にずれる)
                double u = p.InsetX + (1 - 2 * p.InsetX) * (cc + 0.5) / Cols, v = p.InsetY + (1 - 2 * p.InsetY) * (rr + 0.5) / Rows;
                double tx = q[0].X + (q[1].X - q[0].X) * u, ty = q[0].Y + (q[1].Y - q[0].Y) * u;
                double bx = q[3].X + (q[2].X - q[3].X) * u, by = q[3].Y + (q[2].Y - q[3].Y) * u;
                double px = (tx + (bx - tx) * v) * sx, py = (ty + (by - ty) * v) * sy;
                double cw = Math.Abs(q[1].X - q[0].X) / Cols * sx, ch = Math.Abs(q[3].Y - q[0].Y) / Rows * sy;
                (cells[rr * Cols + cc], wet[rr * Cols + cc]) = Cell(f, px, py, cw * p.CellWindow, ch * p.CellWindow, p);
            }
        return new FarmPlot(q.SelectMany(p => new[] { Math.Round((double)p.X / dsW, 4), Math.Round((double)p.Y / dsH, 4) }).ToArray(), new string(cells), wet);
    }

    /// <summary>
    /// マス中央の窓を画素ごとに「薄茶(植えなしの地面)・濃茶(水やり済みの土)・緑や黄(株)・その他(アバターや影など)」へ分け、多数決で決める。
    /// その他が多いマスは遮られているので '?' とする。
    /// </summary>
    static (char Kind, int Wet) Cell(Frame f, double cx, double cy, double hx, double hy, FarmScanParams p)
    {
        int x0 = Math.Max(0, (int)(cx - hx)), x1 = Math.Min(f.Width - 1, (int)(cx + hx));
        int y0 = Math.Max(0, (int)(cy - hy)), y1 = Math.Min(f.Height - 1, (int)(cy + hy));
        int n = 0, light = 0, dark = 0, green = 0, other = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0, i = (y * f.Width + x0) * 4; x <= x1; x++, i += 4)
            {
                int b = f.Data[i], g = f.Data[i + 1], r = f.Data[i + 2];
                n++;
                if (g >= r * 0.95 && g > b + 15) { green++; continue; }
                if (r >= 150 && g >= r * 0.72 && b < r * 0.5 && g > b + 50) { green++; continue; }      // 黄色い株(実った・別種の苗)も植えてあるとみなす
                double br = (double)b / Math.Max(1, r);
                if (r >= 140 && br >= p.EmptyBr && br <= 0.75) light++;
                else if (r is >= 60 and < 200 && br >= 0.35 && br < p.EmptyBr) dark++;
                else other++;
            }
        if (n == 0 || other > p.HiddenRatio * n) return ('?', 0);
        // 植え(緑があるか)と水やり(濃い茶色か)は別々に見る
        bool watered = dark > light, planted = green >= p.PlantRatio * n;
        int wetPct = light + dark == 0 ? 0 : (int)Math.Round(100.0 * dark / (light + dark));
        return ((planted, watered) switch { (true, true) => 'w', (true, false) => 'p', (false, true) => 'b', _ => 'e' }, wetPct);
    }
}
