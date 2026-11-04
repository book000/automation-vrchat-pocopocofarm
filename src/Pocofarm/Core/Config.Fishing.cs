namespace Pocofarm.Core;

/// <summary>釣りの手順(投げる・待つ・引く・投げ直す)と、その自動調整に関する設定。</summary>
public sealed partial class Config
{
    [Desc("何回目の沈みで引くか(基準値。早引きが続くと自動で増え、成功が続くと戻る)")] public int PecksToReel { get; set; } = 1;
    [Desc("投げてからウキが静止するまでの待ち上限[秒]")] public double CastTimeout { get; set; } = 8.0;
    [Desc("投げ後ウキの移動を観測できなくても、静止していれば着水とみなすまでの秒数")] public double CastSettleFallback { get; set; } = 3.0;
    [Desc("投げ後にウキを見失い続けたら投げ失敗とみなす秒数")] public double CastLostSeconds { get; set; } = 4.0;
    [Desc("投げ失敗が続いたときに投げを止める秒数")] public double CastFailPause { get; set; } = 10.0;
    [Desc("投げ失敗が何回続いたら通知するか")] public int CastFailAlertCount { get; set; } = 3;
    [Desc("引いた後にウキが元の位置から動かないと判断するまでの秒数")] public double ReelIneffectiveSeconds { get; set; } = 2.0;
    [Desc("起動直後に既存のウキを観察してから確認クリックするまでの秒数")] public double ProbeWatchSeconds { get; set; } = 2.0;
    [Desc("クリックを保留したあと、次を試すまでの秒数")] public double ClickHoldSeconds { get; set; } = 1.0;

    [Desc("引いてから結果トーストを待つ上限[秒]")] public double ResultTimeout { get; set; } = 4.0;
    [Desc("当たりなし確認クリック時の結果待ち上限[秒]")] public double ProbeResultTimeout { get; set; } = 2.2;
    [Desc("当たり待ちの打ち切り上限[秒](実績から自動で短くなる)")] public double NoBiteTimeout { get; set; } = 75.0;
    [Desc("当たり待ち打ち切りの下限[秒]")] public double NoBiteMin { get; set; } = 20.0;
    [Desc("当たり待ち打ち切り=直近最大待ち時間×この倍率")] public double NoBiteFactor { get; set; } = 2.5;
    [Desc("当たり待ちの実測をこの件数だけ覚える")] public int BiteWaitSamples { get; set; } = 300;
    [Desc("当たり待ちの打ち切りを短くし始めるのに必要な実測件数")] public int BiteWaitMinSamples { get; set; } = 100;
    [Desc("投げの確認ができていないときのウキ待ち上限[秒]")] public double UnverifiedWait { get; set; } = 25.0;
    [Desc("再開直後のウキ待ち上限[秒]")] public double ResumeWait { get; set; } = 6.0;
    [Desc("この秒数を超える試行間隔を slow_cycle として記録する")] public double SlowCycleSeconds { get; set; } = 30.0;

    [Desc("投げ直しまでの待ち[秒]")] public double RecastDelay { get; set; } = 1.6;
    [Desc("釣れた直後に投げを待つ最小秒数(魚の演出中は投げが無視される環境向け。0 で無効)")] public double AfterCatchCastMin { get; set; } = 0.0;
    [Desc("引きクリックから次の投げまでの最小秒数(投げ受付ロック+余裕)。自動調整が有効なら、これを上限として縮める")] public double ReelToCastMin { get; set; } = 2.0;

    [Desc("自動調整: 投げが不発のとき、待ち時間をここまで延ばしてよい上限[秒](描画が遅い環境で投げの受付ロックが長引く場合の備え)")] public double ReelToCastMax { get; set; } = 6.0;

    [Desc("早引きが何回続いたら引く回数を増やすか")] public int EarlyStreakToAdapt { get; set; } = 2;
    [Desc("引く回数の上限")] public int MaxPecksToReel { get; set; } = 3;
    [Desc("成功が何回続いたら引く回数を 1 つ戻すか")] public int CaughtStreakToRevert { get; set; } = 10;

    [Desc("投げの待ち時間(引きから次の投げまで)を成功の続く限り自動で縮める")] public bool TuneReelToCast { get; set; } = true;
    [Desc("自動調整: 実測の間隔が設定値よりこの秒数以内なら成功として数える(ループの遅れ分)")] public double TuneDtTolerance { get; set; } = 0.3;
    [Desc("再開してからこの秒数以内に出た「逃げられた」は、停止中に当たりが過ぎたものとして、見逃しに数えない[秒]")] public double ResumeEscapeGrace { get; set; } = 6.0;
    [Desc("自動調整: これより短くはしない[秒]")] public double ReelToCastFloor { get; set; } = 1.2;
    [Desc("自動調整: 1 回に縮める量[秒]")] public double ReelToCastStep { get; set; } = 0.05;
    [Desc("自動調整: 投げが連続して成功したらこの回数ごとに 1 段縮める")] public int TuneSuccessStreak { get; set; } = 30;
    [Desc("自動調整: 投げが無視されたとき、その値からこの量[秒]だけ余裕を持たせた値まで戻す")] public double TuneMargin { get; set; } = 0.1;

    [Desc("OSC の Use(クリック相当)を押している時間[ミリ秒]")] public int OscHoldMs { get; set; } = 50;
    [Desc("この秒数を超える処理の途切れを gap として扱う")] public double GapThreshold { get; set; } = 0.7;
    [Desc("フレームが来ないときにキャプチャを作り直すまでの秒数")] public double CaptureStallSeconds { get; set; } = 3.0;
    [Desc("取得画面サイズがウィンドウとずれたままのときにキャプチャを作り直すまでの秒数")] public double CaptureSizeMismatchSeconds { get; set; } = 2.0;
    [Desc("起動直後に最初のフレームを待つ秒数")] public double StartupFrameWait { get; set; } = 3.0;
    [Desc("一時停止中・前面でない間にループを回す間隔[ミリ秒]")] public int IdlePollMs { get; set; } = 100;
    [Desc("キャプチャ未開始のときに再試行する間隔[ミリ秒]")] public int NoCaptureRetryMs { get; set; } = 500;
    [Desc("キャプチャを作り直す前の待ち[ミリ秒]")] public int CaptureReinitMs { get; set; } = 300;
    [Desc("一時停止キー・ダッシュボードの要求ファイルを確認する間隔[秒]")] public double ControlCheckInterval { get; set; } = 0.25;
    [Desc("一時停止の仮想キーコード(0x13=Pause)")] public int PauseVk { get; set; } = 0x13;

    [Desc("収支の突き合わせ(audit)を行う間隔[秒]")] public double AuditInterval { get; set; } = 30;
    [Desc("ステータス(成功率など)を更新する間隔[秒]")] public double StatsInterval { get; set; } = 5;
    [Desc("処理区間ごとの所要時間を公開する間隔[秒]")] public double PerfInterval { get; set; } = 10;
    [Desc("同じ処理区間にこの秒数以上留まったら警告する")] public double WatchdogSeconds { get; set; } = 0.6;
    [Desc("ウキ位置の履歴(見逃し調査用)に残すフレーム数")] public int TraceLength { get; set; } = 120;
    [Desc("試行の記録に付けるウキ位置の履歴のフレーム数(引いた時)")] public int AttemptTraceLength { get; set; } = 60;
    [Desc("失敗の記録に付けるウキ位置の履歴のフレーム数")] public int EvidenceTraceLength { get; set; } = 90;

    [Desc("同じ状態でループ内エラーが何回続いたら、試行を捨てて投げ待ちへ戻すか")] public int StateErrorLimit { get; set; } = 5;
    [Desc("投げ失敗がこの回数に達したら投げを止めて needs_attention にする(0 で止めない)")] public int CastFailStopCount { get; set; } = 10;
    [Desc("進行監視(釣れない状態が続いたときの段階的な復旧)を有効にする")] public bool StallWatchdog { get; set; } = true;
    [Desc("進行監視: 釣れない結果がこの回数続いたら停滞とみなす")] public int StallMissStreak { get; set; } = 15;
    [Desc("進行監視: 釣果がない時間の下限[分]。実測の当たり待ち(95 パーセンタイル)× 倍率がこれより長ければそちらを使う")] public double StallNoCatchMinutes { get; set; } = 15;
    [Desc("進行監視: 当たり待ち実測の 95 パーセンタイルに掛ける倍率")] public double StallWaitP95Factor { get; set; } = 8.0;
    [Desc("進行監視: 次の段階へ進むまでの最短秒数")] public double StallTierGapSeconds { get; set; } = 120;
}
