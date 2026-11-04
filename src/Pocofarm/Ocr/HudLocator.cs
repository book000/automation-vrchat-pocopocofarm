using System.Text.RegularExpressions;
using Pocofarm.Core;

namespace Pocofarm.Ocr;

/// <summary>
/// HUD の「Lv 28」の文字位置を OCR で探し、レベル領域と経験値バー領域をその位置・大きさに合わせて補正する。
/// VRChat の HUD サイズ設定や版の違いで、固定比率の領域がずれたときの保険(通常の領域で読めているときは使わない)。
/// </summary>
public static partial class HudLocator
{
    // 基準画面(1920x1009)で正しく読めている状態の、領域の端と「Lv N」外接矩形の中心との差を、その矩形の高さで割った値
    static readonly double[] LevelOffsets = [-3.24, -0.91, 3.16, 2.02];
    static readonly double[] XpOffsets = [-2.62, 1.06, 2.62, 1.54];

    // 「釣り竿 Lv3」のような小さい文字を拾わないための条件(高さは画面の高さに対する比率)
    const double MinHeight = 0.018, MinCenterY = 0.70;

    [GeneratedRegex(@"^[LlＬ][VvＶ]\d{0,3}$")]
    private static partial Regex LvWord();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex Digits();

    /// <summary>画面下部から「Lv N」の外接矩形(フレームのピクセル)を探す。読み取りの揺れに備えて左右をずらした 3 つの帯を読み、最も大きい候補を返す。</summary>
    public static (double X, double Y, double W, double H)? FindLv(Frame f, WinOcr ocr)
    {
        (double X, double Y, double W, double H)? best = null;
        int y0 = (int)(f.Height * MinCenterY), y1 = f.Height;
        foreach (var (a, b) in new[] { (0.0, 0.5), (0.15, 0.65), (0.3, 0.8) })
        {
            int x0 = (int)(f.Width * a), x1 = (int)(f.Width * b);
            var crop = f.CropRatio((double)x0 / f.Width, (double)y0 / f.Height, (double)x1 / f.Width, (double)y1 / f.Height);
            var words = ocr.Words(crop);
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (!LvWord().IsMatch(w.Text)) continue;
                double l = w.X, t = w.Y, r = w.X + w.W, bt = w.Y + w.H;
                if (w.Text.Length <= 2)
                {
                    if (i + 1 >= words.Count || !Digits().IsMatch(words[i + 1].Text)) continue;
                    var n = words[i + 1];
                    l = Math.Min(l, n.X); t = Math.Min(t, n.Y); r = Math.Max(r, n.X + n.W); bt = Math.Max(bt, n.Y + n.H);
                }
                var box = (x0 + l, y0 + t, r - l, bt - t);
                if (box.Item4 < MinHeight * f.Height || (box.Item2 + box.Item4 / 2) < MinCenterY * f.Height) continue;
                if (best is null || box.Item4 > best.Value.H) best = box;
            }
        }
        return best;
    }

    /// <summary>見つけた「Lv N」の位置に合わせたレベル領域・経験値バー領域(画面比 l,t,r,b)。</summary>
    public static (double[] Level, double[] XpBar) Apply((double X, double Y, double W, double H) lv, int w, int h)
    {
        double cx = lv.X + lv.W / 2, cy = lv.Y + lv.H / 2;
        double[] Make(double[] o) => [Math.Clamp((cx + o[0] * lv.H) / w, 0, 1), Math.Clamp((cy + o[1] * lv.H) / h, 0, 1), Math.Clamp((cx + o[2] * lv.H) / w, 0, 1), Math.Clamp((cy + o[3] * lv.H) / h, 0, 1)];
        return (Make(LevelOffsets), Make(XpOffsets));
    }
}
