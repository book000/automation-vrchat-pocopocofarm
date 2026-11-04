namespace Pocofarm.Core;

/// <summary>ウキ・沈みの検出に関する設定。</summary>
public sealed partial class Config
{
    [Desc("ウキを探す範囲 [左, 上, 右, 下](画面比率)", true)] public double[] BobberRoi { get; set; } = [0.05, 0.08, 0.90, 0.75];
    [Desc("ウキ検出から除外する範囲(画面比率の矩形の配列)", true)] public double[][] ExcludeRois { get; set; } = [[0.77, 0.0, 1.0, 0.36], [0.0, 0.0, 0.17, 0.12]];

    [Desc("ウキの赤: 最小の彩度(0〜255)")] public int BobberRedMinSaturation { get; set; } = 100;
    [Desc("ウキの赤: 最小の明るさ(0〜255)")] public int BobberRedMinValue { get; set; } = 70;
    [Desc("ウキの赤: 色相の上限(0〜179、赤は両端にまたがる)")] public int BobberRedHueMax { get; set; } = 8;
    [Desc("ウキの赤: 色相の下限(0〜179)")] public int BobberRedHueMin { get; set; } = 172;
    [Desc("ウキの白: 彩度の上限(0〜255)")] public double BobberWhiteSatLimit { get; set; } = 45.5;
    [Desc("ウキの白: 最小の明るさ(0〜255)")] public int BobberWhiteMinValue { get; set; } = 140;
    [Desc("ウキの赤い棒の最小面積[px²](基準解像度)")] public double BobberMinArea { get; set; } = 8;
    [Desc("ウキの赤い棒の最大幅[px](基準解像度)")] public double BobberMaxWidth { get; set; } = 28;
    [Desc("ウキの赤い棒の最大高さ[px](基準解像度)")] public double BobberMaxHeight { get; set; } = 60;
    [Desc("ウキの赤い棒の最小高さ[px](基準解像度)")] public double BobberMinHeight { get; set; } = 5;
    [Desc("ウキの赤い棒の最小の縦横比(高さ÷幅)")] public double BobberMinAspect { get; set; } = 0.9;
    [Desc("ウキの白玉の最小面積[px²](基準解像度)")] public double BobberMinWhite { get; set; } = 15;
    [Desc("ウキの白玉を棒の下へ探す最小の距離[px](基準解像度)")] public double BobberWhiteReachMin { get; set; } = 22;
    [Desc("ウキの白玉を探す距離に棒の高さへ足す余裕[px](基準解像度)")] public double BobberWhiteReachPad { get; set; } = 10;
    [Desc("ウキの白玉を探す左右の余裕[px](基準解像度)")] public double BobberWhiteSidePad { get; set; } = 8;
    [Desc("ウキの白玉を探す範囲の上端を棒の下端から食い込ませる量[px](基準解像度)")] public double BobberWhiteTopPad { get; set; } = 3;
    [Desc("白玉の重心が棒の中心からずれてよい最小の距離[px](基準解像度。棒の幅の方が大きければそちら)")] public double BobberWhiteCenterTol { get; set; } = 6;

    [Desc("沈みと判定するウキの下がり量(幅に対する比)", true)] public double DipRatio { get; set; } = 0.45;
    [Desc("沈みと判定する最小の下がり量[px](基準解像度)", true)] public double DipMinPx { get; set; } = 4.0;
    [Desc("沈みと判定する連続フレーム数", true)] public int DipFrames { get; set; } = 2;
    [Desc("ウキが見えなくなって沈みとみなす秒数", true)] public double LostSecondsAsDip { get; set; } = 0.12;
    [Desc("ウキが静止したとみなす秒数", true)] public double SettleSeconds { get; set; } = 0.5;
    [Desc("静止とみなす上下の揺れ幅[px](基準解像度)", true)] public double SettleRangePx { get; set; } = 4.0;
    [Desc("静止判定の窓の下限(フレーム間隔の何倍か。低 fps で窓が短すぎないように)")] public double SettleFrames { get; set; } = 3.2;
    [Desc("直近の静止位置の近くでの静止判定の窓の下限(フレーム間隔の何倍か)")] public double SettleFramesAnchored { get; set; } = 2.2;
    [Desc("直近の静止位置の近くでは、静止判定の秒数をこの倍率に縮める")] public double AnchoredSettleScale { get; set; } = 0.6;
    [Desc("静止判定の窓の中で、中央値の近くに集まっているべきフレームの割合")] public double SettleInlierRatio { get; set; } = 0.8;
    [Desc("当たり待ち中にウキの基準位置が通常の揺れへ追従する速さ(1 秒あたり)")] public double DipFollowRate { get; set; } = 0.9;
    [Desc("静止位置から横にこの距離[px](基準解像度)以上離れた物体は、ウキではないとして無視する(ウキ幅の倍数と大きい方)")] public double DipMaxLateralPx { get; set; } = 40;
    [Desc("静止位置から横にウキ幅のこの倍数以上離れた物体は、ウキではないとして無視する")] public double DipMaxLateralWidths { get; set; } = 4;

    [Desc("ウキが元の位置から動いたとみなす距離[px](基準解像度)")] public double CastMovedPx { get; set; } = 40;
    [Desc("投げる前から見えていたウキ候補(他人のウキ)を、投げ後の探索から除く範囲の余白[px](基準解像度)")] public double BobberDecoyPad { get; set; } = 8;
    [Desc("追跡中のウキ候補より本物らしい物がないか、画面全体を探し直すフレーム間隔")] public int BobberAuditFrames { get; set; } = 24;
    [Desc("画面全体の候補の点数が追跡中の候補のこの倍率を超えたら、そちらへ乗り換える")] public double BobberSwitchRatio { get; set; } = 1.5;
    [Desc("ウキが元の位置に残っているとみなす距離[px](基準解像度)")] public double BobberNearPx { get; set; } = 15;
    [Desc("ウキが竿先に残っているとみなす幅[px](基準解像度)")] public double RodTipBobberWidth { get; set; } = 15;
    [Desc("視点が動いたとみなす画面の変化量", true)] public double ViewMotionThreshold { get; set; } = 1.2;
    [Desc("視点が動いた後、動き中とみなし続ける秒数", true)] public double ViewMotionHold { get; set; } = 0.7;
}
