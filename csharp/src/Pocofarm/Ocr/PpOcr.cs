using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Pocofarm.Core;

namespace Pocofarm.Ocr;

/// <summary>
/// PP-OCRv6(検出 DB + 認識 CTC)を ONNX Runtime で動かす RapidOCR 互換の OCR。トースト用なので横書きだけを扱う。
/// 約0.7秒/回だが誤読が最も少ないため、再確認・救済用に使う。モデルは exe に埋め込んである。
/// </summary>
public sealed class PpOcr : IOcr
{
    const double Thresh = 0.3, BoxThresh = 0.5, UnclipRatio = 1.6, TextScore = 0.5;
    const int LimitSideLen = 736, RecHeight = 48, RecMinWidth = 320;

    readonly InferenceSession det, rec;
    readonly string[] chars;

    public string Name => "rapidocr";

    static PpOcr()
    {
        // System32 にも onnxruntime.dll(Windows 同梱の別版)があり、先に解決されると ABI 不一致で落ちる環境があるため、
        // exe と一緒に展開した DLL を絶対パスで読む
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) =>
        {
            if (!name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            var dirs = ((AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Append(AppContext.BaseDirectory);
            foreach (var d in dirs)
            {
                var path = Path.Combine(d, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
            }
            return IntPtr.Zero;
        });
    }

    public PpOcr()
    {
        det = new InferenceSession(Resource("models.PP-OCRv6_det_small.onnx"));
        rec = new InferenceSession(Resource("models.PP-OCRv6_rec_small.onnx"));
        // 辞書は認識モデルのメタデータに入っている。CTC のブランク(0 番)を先頭に、空白を末尾に足す
        var dict = rec.ModelMetadata.CustomMetadataMap["character"].Split('\n');
        chars = ["blank", .. dict, " "];
    }

    static byte[] Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded model not found: {name}");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public string Read(Frame img)
    {
        if (img.Width < 8 || img.Height < 8) return "";
        var boxes = Detect(img);
        var sb = new System.Text.StringBuilder();
        foreach (var (x0, y0, x1, y1) in boxes)
        {
            var (text, score) = Recognize(img.Crop(x0, y0, x1, y1));
            if (text.Trim().Length > 0 && score >= TextScore) sb.Append(text);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", "");
    }

    /// <summary>検出モデルで文字行の矩形(元画像座標)を上から下・左から右の順に返す。</summary>
    List<(int X0, int Y0, int X1, int Y1)> Detect(Frame img)
    {
        int h = img.Height, w = img.Width;
        double ratio = Math.Min(h, w) < LimitSideLen ? (double)LimitSideLen / Math.Min(h, w) : 1.0;
        int rh = Math.Max(32, (int)Math.Round((int)(h * ratio) / 32.0) * 32), rw = Math.Max(32, (int)Math.Round((int)(w * ratio) / 32.0) * 32);
        var input = ToTensor(img, rh, rw);
        using var res = det.Run([NamedOnnxValue.CreateFromTensor(det.InputNames[0], input)]);
        var prob = res[0].AsTensor<float>();      // [1,1,rh,rw]
        var p = prob.ToArray();

        // 閾値で二値化し、cv2.dilate(2x2) 相当で右下へ 1 画素広げる
        var mask = new bool[rh * rw];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                mask[y * rw + x] = p[y * rw + x] > Thresh || (x > 0 && p[y * rw + x - 1] > Thresh) || (y > 0 && p[(y - 1) * rw + x] > Thresh)
                    || (x > 0 && y > 0 && p[(y - 1) * rw + x - 1] > Thresh);

        var boxes = new List<(int, int, int, int)>();
        var seen = new bool[mask.Length];
        var stack = new Stack<int>();
        for (int s = 0; s < mask.Length; s++)
        {
            if (!mask[s] || seen[s]) continue;
            int minX = rw, minY = rh, maxX = -1, maxY = -1;
            stack.Push(s); seen[s] = true;
            while (stack.Count > 0)
            {
                int q = stack.Pop(), qx = q % rw, qy = q / rw;
                if (qx < minX) minX = qx; if (qx > maxX) maxX = qx;
                if (qy < minY) minY = qy; if (qy > maxY) maxY = qy;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = qx + dx, ny = qy + dy;
                        if ((uint)nx >= (uint)rw || (uint)ny >= (uint)rh) continue;
                        int n = ny * rw + nx;
                        if (mask[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                    }
            }
            double bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (Math.Min(bw, bh) < 3) continue;
            double sum = 0;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++) sum += p[y * rw + x];
            if (sum / (bw * bh) < BoxThresh) continue;
            // DB の unclip: 矩形の面積×比率÷周長だけ四方へ広げる
            double d = bw * bh * UnclipRatio / (2 * (bw + bh));
            double ex0 = minX - d, ey0 = minY - d, ex1 = maxX + 1 + d, ey1 = maxY + 1 + d;
            if (Math.Min(ex1 - ex0, ey1 - ey0) < 5) continue;
            int X(double v, int dest, int src) => (int)Math.Clamp(Math.Round(v / src * dest), 0, dest - 1);
            var box = (X(ex0, w, rw), X(ey0, h, rh), X(ex1, w, rw), X(ey1, h, rh));
            if (box.Item3 - box.Item1 <= 3 || box.Item4 - box.Item2 <= 3) continue;
            boxes.Add(box);
        }
        // RapidOCR の sorted_boxes と同じ: 上から下、同じ行(y 差 10 未満)は左から右
        boxes.Sort((a, b) => a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2) : a.Item1.CompareTo(b.Item1));
        for (int i = 0; i < boxes.Count - 1; i++)
            for (int j = i; j >= 0; j--)
            {
                if (Math.Abs(boxes[j + 1].Item2 - boxes[j].Item2) < 10 && boxes[j + 1].Item1 < boxes[j].Item1)
                    (boxes[j], boxes[j + 1]) = (boxes[j + 1], boxes[j]);
                else break;
            }
        return boxes;
    }

    /// <summary>認識モデルで 1 行を読む。(文字列, 平均信頼度)</summary>
    (string Text, double Score) Recognize(Frame line)
    {
        double ratio = (double)line.Width / line.Height;
        int resizedW = (int)Math.Ceiling(RecHeight * ratio);
        int padW = Math.Max(RecMinWidth, resizedW);
        var t = new DenseTensor<float>([1, 3, RecHeight, padW]);
        var small = ToTensor(line, RecHeight, resizedW);
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < RecHeight; y++)
                for (int x = 0; x < resizedW; x++) t[0, c, y, x] = small[0, c, y, x];
        using var res = rec.Run([NamedOnnxValue.CreateFromTensor(rec.InputNames[0], t)]);
        var o = res[0].AsTensor<float>();         // [1,T,C]
        int steps = o.Dimensions[1], classes = o.Dimensions[2];
        var sb = new System.Text.StringBuilder();
        double conf = 0; int n = 0, prev = -1;
        for (int s = 0; s < steps; s++)
        {
            int best = 0; float bv = float.MinValue;
            for (int c = 0; c < classes; c++) { var v = o[0, s, c]; if (v > bv) { bv = v; best = c; } }
            if (best != prev && best != 0) { sb.Append(chars[best]); conf += bv; n++; }
            prev = best;
        }
        return (sb.ToString(), n > 0 ? conf / n : 0);
    }

    /// <summary>BGR のまま (x/255-0.5)/0.5 で正規化した [1,3,h,w] に双一次補間で縮拡する。</summary>
    static DenseTensor<float> ToTensor(Frame img, int h, int w)
    {
        var t = new DenseTensor<float>([1, 3, h, w]);
        double sx = (double)img.Width / w, sy = (double)img.Height / h;
        for (int y = 0; y < h; y++)
        {
            double fy = Math.Max(0, (y + 0.5) * sy - 0.5);
            int y0 = Math.Min((int)fy, img.Height - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
            double wy = fy - y0;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Max(0, (x + 0.5) * sx - 0.5);
                int x0 = Math.Min((int)fx, img.Width - 1), x1 = Math.Min(x0 + 1, img.Width - 1);
                double wx = fx - x0;
                for (int c = 0; c < 3; c++)       // Frame は BGRA なので 0,1,2 がそのまま B,G,R
                {
                    double v = (Px(img, x0, y0, c) * (1 - wx) + Px(img, x1, y0, c) * wx) * (1 - wy)
                             + (Px(img, x0, y1, c) * (1 - wx) + Px(img, x1, y1, c) * wx) * wy;
                    t[0, c, y, x] = (float)((v / 255.0 - 0.5) / 0.5);
                }
            }
        }
        return t;
    }

    static double Px(Frame f, int x, int y, int c) => f.Data[(y * f.Width + x) * 4 + c];
}
