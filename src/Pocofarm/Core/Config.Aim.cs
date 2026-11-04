namespace Pocofarm.Core;

/// <summary>視点が池からずれたときの自動復帰(マウスで視点を戻す)に関する設定。</summary>
public sealed partial class Config
{
    [Desc("投げ失敗が続いたとき、視点のずれを推定してマウスで池へ戻す(補正の間だけ VRChat を前面にする)")] public bool AimRecover { get; set; } = true;
    [Desc("視点を戻す補正の最大回数(超えたら通知だけに戻る)")] public int AimRecoverMaxSteps { get; set; } = 8;
    [Desc("視点補正の初期ゲイン: 画面幅ぶんのずれを戻すのに送るマウスの移動量[カウント](実測の応答から自動で修正される)")] public double AimRecoverGainInit { get; set; } = 800.0;
    [Desc("視点のずれの推定を採用する最小の一致度(0〜1。これ未満は場面が違うとして諦める)")] public double AimRecoverMinConfidence { get; set; } = 0.6;
    [Desc("ずれが画面幅・高さに対してこの比率未満なら視点が合ったとみなす")] public double AimRecoverTolerance { get; set; } = 0.02;
    [Desc("マウスを動かしてから画面を撮り直すまでの待ち[秒]")] public double AimRecoverSettle { get; set; } = 0.6;
}
