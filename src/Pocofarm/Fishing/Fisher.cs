using System.Text.Json;
using System.Text.Json.Nodes;
using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

/// <summary>
/// 釣りの状態機械。投げる→着水待ち→当たり待ち→引く→結果確認→投げ直し を無期限に繰り返す。
/// どの試行も必ず結果種別(caught/early/escaped/unknown/…)を持つイベントとして記録する。
/// 引いた後に結果トーストが読めなかった場合も黙って成功扱いにせず "unknown" として証拠を残す。
/// </summary>
public sealed partial class Fisher
{
    const string SWaitWindow = "wait_window", SCooldown = "cooldown", SCastWait = "cast_wait", SWaitBite = "wait_bite", SResult = "result_wait";

    readonly Config cfg;
    readonly Paths paths;
    readonly bool dryRun;
    readonly EventLog events;
    readonly EvidenceStore evidence;
    readonly Status status;

    double scale = 1.0;                 // 画面高さ ÷ RefHeight。ピクセル基準のしきい値に掛ける
    (int W, int H) frameSize = (0, 0);
    Rois rois;
    nint hwnd;
    WgcCapture? cap;
    double capStarted;
    Frame? frame;                       // 現在のフレーム(読み手専用バッファ)
    long frameSeen;
    readonly SingleWorker liveWorker = new("live");
    Task<bool>? liveFuture;
    readonly ToastReader reader;
    readonly HudMonitor hud;
    readonly NekoCollector nekos;
    readonly ClickSender clicks;
    readonly Auditor auditor;
    readonly AutoTuner tuner;
    readonly LoopProfiler prof;
    readonly ControlChannel control;
    readonly AttemptRecorder recorder;
    double lastFind, lastReelAt, castDt = -1;     // castDt: 直近の引きから投げまでの実測間隔(-1 は未計測)
    DipDetector dip;
    string state = SWaitWindow;
    double tState = EventLog.Now();
    Attempt? attempt;
    Bobber? bobber;
    int auditTick, learnTick;
    readonly Dictionary<(int, int), (int Count, double Last)> decoyHeat = [];     // 自分のウキではない候補(他人のウキ・アバターの赤白)が見えた升目と、その回数・最後に見た時刻
    int decoyLogTick;
    (int X, int Y)? castFrom;                        // 投げのクリックを送った時点のウキ位置(竿先にぶら下がったウキ)。ここから動かず静止したなら投げは効いていない
    Frame? workFrame;
    Frame? cur;                                     // いま処理中のフレーム(縮小後)。範囲・倍率はこの大きさに合わせてある
    int staleStreak;
    readonly List<(double X, double Y)> landings = [];     // 釣れたときのウキ位置(画面に対する比率。ウィンドウの大きさが変わっても使える)。自分のウキは毎回ほぼ同じ場所に着水する
    List<(int, int, int, int)> decoys = [];     // 投げる前から画面にあったウキ候補(他人のウキ・アバターの赤白)。投げ後は探索から除く
    (int X, int Y)? lastPos, castOrigin, expectAway, anchorPos;     // anchorPos: 直近に静止を確認した位置(再開後などで本物のウキを優先して探す)
    bool castMoved;
    double castLastSeen;
    int toastNoise;
    static readonly string[] NoiseHints = ["れた", "びくび", "沈んで", "すぐに引", "引こう", "かかって", "経験", "持っ", "匹"];
    int pecks;
    double tArmed;
    readonly Dictionary<string, double> kindLast = [];
    (string Text, double T) unclassifiedLast = ("", 0.0);
    int failStreak;

    /// <summary>投げの連続失敗数。ダッシュボードの警告に出すため状態へも書く。</summary>
    int castFailures
    {
        get => failStreak;
        set { failStreak = value; status.Set("cast_fail_streak", value); }
    }
    bool castAlerted;
    int fpsN;
    double fpsT = EventLog.Now(), fps;
    readonly LinkedList<(double T, int Y, int W)> trace = new();   // 見逃し調査用
    double lastLive, lastCleanup, lastStats;
    List<Box> boxes = [];
    bool startupAdopt = true;
    bool castVerified;                  // 自分の投げ/引きでウキの状態を確認できたか
    double unverifiedWait = 25.0;       // 未確認のまま当たりを待つ上限(再開直後は短くする)
    readonly double startedAt = EventLog.Now();
    double lastTick = EventLog.Now();
    readonly ViewMotion viewMotion;
    readonly AimRecovery aim;
    int aimTriedFor;                    // 視点補正を試した時点の連続失敗数(同じ失敗数では 1 回だけ試す)

    public Fisher(Config cfg, Paths paths, bool dryRun = false, bool startPaused = false)
    {
        this.cfg = cfg; this.paths = paths; this.dryRun = dryRun;
        rois = Layout.Resolve(cfg, Layout.RefW, BobberDetector.RefHeight);   // 実フレームが分かるまでは基準の範囲
        events = new EventLog(paths.Events);
        evidence = new EvidenceStore(paths.Evidence, paths.Live, cfg.EvidenceTtlHours, cfg.EvidenceMaxMb);
        status = new Status(paths.Status);
        clicks = new ClickSender(cfg, status, dryRun);
        auditor = new Auditor(cfg, events, status, clicks);
        nekos = new NekoCollector(cfg, events, SaveEvidence);
        tuner = new AutoTuner(cfg, paths.Tuning);
        prof = new LoopProfiler(cfg, status);
        reader = new ToastReader(cfg);
        hud = new HudMonitor(cfg, paths, events, status);
        dip = NewDip();
        viewMotion = new ViewMotion(cfg.ViewMotionThreshold, cfg.ViewMotionHold);
        aim = new AimRecovery(cfg, paths, events);
        control = new ControlChannel(cfg, paths, events, status, dryRun, startPaused);
        control.Resumed += OnResumed;
        recorder = new AttemptRecorder(cfg, events, status, SaveEvidence, auditor, control);
        control.DataReset += () => lastStats = 0.0;
    }

    /// <summary>ダッシュボード用に共有する状態。</summary>
    public Status Status => status;

    public EventLog Events => events;

    static double Now => EventLog.Now();

    // ---------------------------------------------------------------- 補助

    DipDetector NewDip() => new(DipParams.From(cfg, scale));

    void SetState(string s, string msg = "")
    {
        if (s != state) Log.Info($"状態: {state} → {s} {msg}");
        state = s; tState = Now;
        status.Set("state", s); status.Set("message", msg);
    }

    /// <summary>探索範囲の除外矩形(画素座標)。</summary>
    List<(int, int, int, int)> ExcludeRects(int w, int h) =>
        rois.Exclude.Select(r => ((int)(r[0] * w), (int)(r[1] * h), (int)(r[2] * w), (int)(r[3] * h))).ToList();

    bool Click(string kind) => clicks.Click(hwnd, kind);
}
