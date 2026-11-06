using System.Text.RegularExpressions;
using Pocofarm.Core;

namespace Pocofarm.Vision;

/// <summary>画面下 HUD(レベル・経験値バー)の読み取り。</summary>
public static partial class Hud
{
    // 経験値 0% でもバー左端に丸い黄色の端(幅の約5.5%)が描かれるため、その分を差し引いて 0..1 に直す
    const double XpCap = 0.055;

    [GeneratedRegex(@"[LlＬ][VvＶ]\s*(\d{1,3})")]
    private static partial Regex LevelRe();

    /// <summary>OCR 文字列から "Lv 22" 形式のレベルを取り出す。</summary>
    public static int? ParseLevel(string? text)
    {
        var m = LevelRe().Match(text ?? "");
        return m.Success ? Toast.ParseInt(m.Groups[1].Value) : null;
    }

    /// <summary>経験値バーの黄色い部分の長さを 0..1 で返す。黄色が 1 本も見つからない(バーを読めていない)ときと、範囲が空のときは null。</summary>
    public static double? XpFraction(Frame frame, double[] roi)
    {
        var band = frame.CropRatio(roi[0], roi[1], roi[2], roi[3]);
        if (band.Width == 0 || band.Height == 0) return null;
        int last = -1;
        for (int x = 0; x < band.Width; x++)
        {
            int yellow = 0;
            for (int y = 0; y < band.Height; y++)
            {
                int i = (y * band.Width + x) * 4;
                var (h, s, v) = Frame.Hsv(band.Data[i], band.Data[i + 1], band.Data[i + 2]);
                if (h is >= 12 and <= 40 && s >= 110 && v >= 140) yellow++;
            }
            if (yellow / (double)band.Height > 0.5) last = x;
        }
        if (last < 0) return null;      // 0% と区別する。読めていない偽の 0 を記録すると推移が落ちて見込みも狂う
        double raw = (last + 1.0) / band.Width;
        return Math.Round(Math.Min(1.0, Math.Max(0.0, (raw - XpCap) / (1 - XpCap))), 3);
    }
}
