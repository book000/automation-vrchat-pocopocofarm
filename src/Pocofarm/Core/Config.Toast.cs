namespace Pocofarm.Core;

/// <summary>画面の通知(トースト)・OCR・HUD(レベル/経験値)に関する設定。</summary>
public sealed partial class Config
{
    [Desc("釣果トーストを読む範囲 [左, 上, 右, 下](画面比率)", true)] public double[] ToastRoi { get; set; } = [0.06, 0.28, 0.36, 0.725];
    [Desc("レベル表示の範囲 [左, 上, 右, 下](画面比率)", true)] public double[] LevelRoi { get; set; } = [0.255, 0.828, 0.325, 0.889];
    [Desc("経験値バーの範囲 [左, 上, 右, 下](画面比率)", true)] public double[] XpBarRoi { get; set; } = [0.2615, 0.869, 0.319, 0.879];

    [Desc("RapidOCR(ONNX Runtime)での再確認・救済を使う。ONNX Runtime が起動時に落ちる環境では false にする")] public bool RapidOcr { get; set; } = true;
    [Desc("トーストを OCR する間隔[秒]")] public double OcrInterval { get; set; } = 0.12;
    [Desc("基準より画面がこの倍率を超えて大きいとき、OCR に渡す画像を縮める(文字が大きすぎると誤読が増える)")] public double OcrDownscaleAbove { get; set; } = 1.3;
    [Desc("結果不明時の再読取り用に残すトースト領域の枚数")] public int ToastCropHistory { get; set; } = 48;
    [Desc("同じ種類のトーストを別の出現とみなす間隔[秒]")] public double ToastNewGap { get; set; } = 2.5;
    [Desc("同じ内容の釣果トーストを重複とみなす秒数")] public double ToastDuplicateWindow { get; set; } = 6.0;
    [Desc("確定済みの結果の直後に出る誤読トーストを無視する秒数")] public double ToastNoiseWindow { get; set; } = 10.0;
    [Desc("起動直後、古いトーストを無視する秒数")] public double StartupGrace { get; set; } = 4.0;
    [Desc("結果トーストを最初に見てから確定するまでの猶予[秒]")] public double ResultFirstSeenGrace { get; set; } = 0.9;
    [Desc("レベルアップ等の通知が釣果の後から出るのを待つ秒数")] public double InfoWaitSeconds { get; set; } = 6.0;
    [Desc("ねこちゃん通知の読み取りを 1 件にまとめる間隔[秒]")] public double NekoMergeGap { get; set; } = 4.0;
    [Desc("ねこちゃん通知の表示が途切れてから、1 件として記録するまでの秒数")] public double NekoFlushSeconds { get; set; } = 3.0;

    [Desc("ダッシュボードに「目標レベルまでの目安」を出す目標レベル。0 で出さない。到達したら自動で消える")] public int LevelGoal { get; set; } = 0;
    [Desc("HUD(レベル・経験値)を読む間隔[秒]")] public double HudInterval { get; set; } = 10.0;
    [Desc("HUD の位置を再探索する間隔[秒]")] public double HudLocateInterval { get; set; } = 60;
    [Desc("レベルが続けてこの回数読めなかったら、HUD の位置を再探索する")] public int HudMissBeforeLocate { get; set; } = 3;
    [Desc("再探索した位置が現在の領域とこの画素数以内なら、ずれではないとして補正しない")] public double HudRecalibrateMinPx { get; set; } = 3.0;
    [Desc("HUD の値が変わらなくても記録する間隔[秒]")] public double HudWriteInterval { get; set; } = 60;

    [Desc("魚の名前の一覧")] public List<string> KnownFish { get; set; } = ["ドジョウ", "フナ", "コイ", "ナマズ", "ニジマス", "金色のコイ", "イトウ", "サケ", "エビ", "タイ", "ブリ", "ヒラメ", "マグロ"];
    [Desc("魚ごとの売値コイン")] public Dictionary<string, int> FishCoins { get; set; } = new()
    {
        ["フナ"] = 3, ["ドジョウ"] = 5, ["コイ"] = 12, ["ナマズ"] = 20, ["ニジマス"] = 28, ["金色のコイ"] = 150, ["イトウ"] = 400, ["サケ"] = 50, ["エビ"] = 30, ["ブリ"] = 4500, ["ヒラメ"] = 4000, ["タイ"] = 5000, ["マグロ"] = 6000,
    };
}
