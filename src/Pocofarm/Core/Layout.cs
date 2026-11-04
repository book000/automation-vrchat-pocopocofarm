namespace Pocofarm.Core;

/// <summary>フレームサイズに合わせて解決した範囲(いずれも画面に対する比率 l,t,r,b)。</summary>
public sealed record Rois(double[] Bobber, double[] Toast, double[] Level, double[] XpBar, double[][] Exclude);

/// <summary>
/// 解像度・縦横比ごとの画面範囲の解決。VRChat の HUD は「幅に対するアンカー+高さに比例するオフセット」なので、
/// 設定(基準 1920x1009 の比率)を実フレームサイズへ解き直す。
/// </summary>
public static class Layout
{
    public const int RefW = 1920, RefH = 1009;
    // 実測 3 点(1920x1009→171、3840x2054→315、1280x953→102)に合う式
    const double HudCenterOffset = 0.782, HudMinMargin = 0.107;

    static double HudLeft(double w, double h) => Math.Max(w / 2 - HudCenterOffset * h, HudMinMargin * h);

    static double[] BottomLeft(double[] roi, int w, int h)
    {
        double refA = HudLeft(RefW, RefH);
        double X(double r) => (HudLeft(w, h) + (r * RefW - refA) / RefH * h) / w;
        return [X(roi[0]), roi[1], X(roi[2]), roi[3]];
    }

    static double[] EdgeAnchored(double[] roi, int w, int h)
    {
        double X(double r) => r > 0.5 ? (w - (1 - r) * RefW / RefH * h) / w : r * RefW / RefH * h / w;
        double Clamp(double v) => Math.Min(1.0, Math.Max(0.0, v));
        return [Clamp(X(roi[0])), roi[1], Clamp(X(roi[2])), roi[3]];
    }

    public static Rois Resolve(Config cfg, int w, int h) => new(
        (double[])cfg.BobberRoi.Clone(),
        BottomLeft(cfg.ToastRoi, w, h),
        BottomLeft(cfg.LevelRoi, w, h),
        BottomLeft(cfg.XpBarRoi, w, h),
        cfg.ExcludeRois.Select(r => EdgeAnchored(r, w, h)).ToArray());
}
