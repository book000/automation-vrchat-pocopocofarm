namespace Pocofarm.Core;

/// <summary>ファーム定点写真の撮影と畑の解析に関する設定。</summary>
public sealed partial class Config
{
    [Desc("ファーム定点写真の撮影間隔[秒]。0 で無効。撮影は VRChat の前面とクリックが要る")] public double FarmShotInterval { get; set; } = 600;
    [Desc("ファーム撮影を、VRChat がすでに前面にあるときだけ行う(背面の間は待つだけで、フォーカスもカーソルも奪わない)")] public bool FarmShotForegroundOnly { get; set; } = true;
    [Desc("撮影のために釣りを止める前に、釣れた直後(区切り)を待つ上限[秒]")] public double FarmShotWaitSeconds { get; set; } = 60;
    [Desc("ファーム写真の切り抜き範囲 [左, 上, 右, 下](画面比率。全体は [0,0,1,1])")] public double[] FarmShotCrop { get; set; } = [0.0, 0.0, 1.0, 1.0];
    [Desc("ファーム通知: 植えなし・水やり前のマスが何回の撮影で連続して残ったら知らせるか")] public int FarmAlertScans { get; set; } = 2;
    [Desc("ファーム通知を Windows のトーストでも出す(ダッシュボードのバッジは常に出る)")] public bool FarmAlertToast { get; set; } = true;
    [Desc("ファームの変化: 水やりの有無が変わったとみなす、マスの濃い土の割合(%)の最小の差。境目付近の揺れを変化と数えないため")] public int FarmWaterMinChange { get; set; } = 40;
    [Desc("ファーム撮影: 起動後の最初の撮影で、畑 8 面が収まっていなければカメラ位置を自動で調整する")] public bool FarmCalibrate { get; set; } = true;
    [Desc("画角調整: 畑全体の外側に残す余白(画面比率)")] public double FarmCalMargin { get; set; } = 0.04;
    [Desc("画角調整: 畑全体の中心が写真の中央からこれ以内(画面比率)なら調整済みとする")] public double FarmCalTolerance { get; set; } = 0.03;
    [Desc("画角調整: ずれを測るためにカメラ位置を動かす幅")] public double FarmCalStep { get; set; } = 0.5;
    [Desc("画角調整: 補正を繰り返す回数の上限")] public int FarmCalIterations { get; set; } = 6;
    [Desc("画角調整: 画角(Zoom)を動かす幅")] public double FarmCalZoomStep { get; set; } = 5.0;
    [Desc("画角調整: 元の位置から動かしてよい上限")] public double FarmCalMaxShift { get; set; } = 8.0;
    [Desc("画角調整: 位置を変えてから画像を取るまでの待ち[秒]")] public double FarmCalSettle { get; set; } = 0.35;
    [Desc("画角調整: 画像を取り直すときの間隔[秒]")] public double FarmCalGrabWait { get; set; } = 0.15;

    [Desc("画角調整: 写りが落ち着くまで撮り直す回数の上限")] public int FarmCalStableTries { get; set; } = 10;
    [Desc("ファーム写真を新しい方から何枚残すか(ダッシュボードで撮影前後を比べるため)")] public int FarmKeepShots { get; set; } = 12;
    [Desc("ファーム写真の幅[px]")] public int FarmShotWidth { get; set; } = 1280;
    [Desc("ファーム写真の JPEG 品質(1〜100)")] public int FarmShotQuality { get; set; } = 80;
    [Desc("ファーム撮影: カメラ配信を始めてから画像を取るまでの待ち[秒]")] public double FarmGrabWait { get; set; } = 0.4;
    [Desc("ファーム撮影: 釣りを止めてから操作を始めるまでの待ち[秒]")] public double FarmPauseSettle { get; set; } = 0.3;
    [Desc("ファーム撮影: 配信の送信元が現れるまで待つ上限[秒]")] public double FarmStreamWaitSeconds { get; set; } = 6.0;
    [Desc("ファーム撮影: 画像を取り直す回数の上限")] public int FarmGrabAttempts { get; set; } = 4;

    [Desc("ダッシュボードに出す Spout 送信元の名前。空なら VRChat の送信元を自動で選ぶ")] public string SpoutSender { get; set; } = "";
    [Desc("撮影中だけカメラのレイヤーから自分・他人のアバターと UI を外し、人が写り込まないようにする")] public bool FarmHidePeople { get; set; } = true;
    [Desc("VRChat の OSC 受信ポート")] public int OscPort { get; set; } = 9000;
    [Desc("カメラを開く操作を、OCR を使わず色の確認だけで速く行う(失敗時は従来の経路に切り替え)")] public bool FastCameraOpen { get; set; } = true;
    [Desc("カメラタブ(アイコン)の位置 [x, y](画面比率)")] public double[] CameraTab { get; set; } = [0.499, 0.872];
    [Desc("Stream Camera ボタンの位置 [x, y](画面比率)")] public double[] StreamButton { get; set; } = [0.469, 0.486];

    [Desc("ファーム解析: 畑の枠線とみなす暗さ(周囲平均との輝度差)")] public int FarmEdgeContrast { get; set; } = 14;
    [Desc("ファーム解析: 青/赤の比がこの値以上の薄茶色を「水やり前」とみなす(濃い茶色=水やり済みは約0.48、薄茶色は約0.6)")] public double FarmEmptyBr { get; set; } = 0.54;
    [Desc("ファーム解析: 薄茶色のマスで緑画素がこの割合以上なら「植えてある」とみなす")] public double FarmPlantRatio { get; set; } = 0.002;
    [Desc("ファーム解析: マスの中心を畑の枠の内側へ寄せる横方向の割合")] public double FarmInsetX { get; set; } = 0.05;
    [Desc("ファーム解析: マスの中心を畑の枠の内側へ寄せる縦方向の割合")] public double FarmInsetY { get; set; } = 0.035;
    [Desc("ファーム解析: マスの色を見る窓の大きさ(マスに対する割合)")] public double FarmCellWindow { get; set; } = 0.38;
    [Desc("ファーム解析: 窓の中の「その他の色」(アバターや影)がこの割合を超えたマスは、隠れて不明とする")] public double FarmHiddenRatio { get; set; } = 0.45;
    [Desc("ファーム解析: 解析用に縮める画像の幅[px]")] public int FarmAnalysisWidth { get; set; } = 640;
}
