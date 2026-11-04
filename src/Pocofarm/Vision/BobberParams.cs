using Pocofarm.Core;

namespace Pocofarm.Vision;

/// <summary>
/// ウキ検出のしきい値。長さは基準解像度(高さ <see cref="BobberDetector.RefHeight"/>)の画素数で、検出時に画面倍率を掛ける。
/// 既定値は 1920x1009 で調整したもの。
/// </summary>
public sealed record BobberParams(
    int RedMinSaturation = 100, int RedMinValue = 70, int RedHueMax = 8, int RedHueMin = 172,
    double WhiteSatLimit = 45.5, int WhiteMinValue = 140,
    double MinArea = 8, double MaxWidth = 28, double MaxHeight = 60, double MinHeight = 5, double MinAspect = 0.9,
    double MinWhite = 15, double WhiteReachMin = 22, double WhiteReachPad = 10, double WhiteSidePad = 8, double WhiteTopPad = 3, double WhiteCenterTol = 6, double JoinGap = 3)
{
    public static readonly BobberParams Default = new();

    public static BobberParams From(Config c) => new(
        c.BobberRedMinSaturation, c.BobberRedMinValue, c.BobberRedHueMax, c.BobberRedHueMin,
        c.BobberWhiteSatLimit, c.BobberWhiteMinValue,
        c.BobberMinArea, c.BobberMaxWidth, c.BobberMaxHeight, c.BobberMinHeight, c.BobberMinAspect,
        c.BobberMinWhite, c.BobberWhiteReachMin, c.BobberWhiteReachPad, c.BobberWhiteSidePad, c.BobberWhiteTopPad, c.BobberWhiteCenterTol);
}

/// <summary>沈み(ぴくぴく)検出のしきい値。長さの単位は画素(呼び出し側で画面倍率を掛けて渡す)。</summary>
public sealed record DipParams(
    double DipRatio = 0.45, double DipMinPx = 4.0, int DipFrames = 2, double LostSeconds = 0.12,
    double SettleSeconds = 0.5, double SettleRangePx = 4.0,
    double SettleFrames = 3.2, double SettleFramesAnchored = 2.2, double AnchoredSettleScale = 0.6, double InlierRatio = 0.8,
    double FollowRate = 0.9, double MaxLateralPx = 40, double MaxLateralWidths = 4,
    int LostGuardFrames = 0, int LostGuardSeen = 0)
{
    public static DipParams From(Config c, double scale) => new(
        c.DipRatio, c.DipMinPx * scale, c.DipFrames, c.LostSecondsAsDip,
        c.SettleSeconds, c.SettleRangePx * scale,
        c.SettleFrames, c.SettleFramesAnchored, c.AnchoredSettleScale, c.SettleInlierRatio,
        c.DipFollowRate, c.DipMaxLateralPx * scale, c.DipMaxLateralWidths,
        c.LostDipGuard ? c.LostDipGuardFrames : 0, c.LostDipGuardSeen);
}
