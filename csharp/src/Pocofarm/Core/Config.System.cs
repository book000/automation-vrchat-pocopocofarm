namespace Pocofarm.Core;

/// <summary>ウィンドウ・キャプチャ・ダッシュボード・証拠画像・自動更新など、動作環境に関する設定。</summary>
public sealed partial class Config
{
    [Desc("VRChat のウィンドウ名")] public string WindowTitle { get; set; } = "VRChat";
    [Desc("VRChat ウィンドウを探し直す間隔[秒]")] public double WindowRecheckInterval { get; set; } = 30.0;
    [Desc("画面キャプチャの fps", true)] public int CaptureFps { get; set; } = 60;

    [Desc("ライブ画面の更新間隔[秒]")] public double LiveInterval { get; set; } = 1.0;
    [Desc("証拠画像の掃除を行う間隔[秒]")] public double EvidenceCleanupInterval { get; set; } = 300;
    [Desc("証拠画像の保持時間[時間]", true)] public double EvidenceTtlHours { get; set; } = 6.0;
    [Desc("証拠画像の上限[MB]", true)] public int EvidenceMaxMb { get; set; } = 512;

    [Desc("ダッシュボードの待受アドレス", true)] public string DashboardHost { get; set; } = "0.0.0.0";
    [Desc("ダッシュボードのポート", true)] public int DashboardPort { get; set; } = 10000;
    [Desc("ダッシュボードが「停止」と判定するまでの心拍の途絶[秒]")] public double HeartbeatTimeout { get; set; } = 10;

    [Desc("落ちた釣りプロセスを再起動するまでの待ち[秒]", true)] public double RestartDelay { get; set; } = 5;
    [Desc("自動更新の取得元の exe パス(UNC 可)。これが現在の exe より新しければ、常駐が自動で入れ替わる。config.json を直接編集して設定する(ダッシュボードからは変更できない)。空なら pocofarm.new.exe を置いたときだけ入れ替える")] public string UpdateSource { get; set; } = "";
    [Desc("自動更新の取得元を確認する間隔[秒]")] public double UpdateCheckSeconds { get; set; } = 60;
    [Desc("入れ替え後に残る古い exe(*.old など)を掃除する間隔[秒]。0 で無効")] public double ExeCleanupInterval { get; set; } = 3600;
}
