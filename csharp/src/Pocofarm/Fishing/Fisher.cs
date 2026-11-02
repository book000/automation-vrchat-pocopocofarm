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
public sealed class Fisher
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
    double lastLive, lastCleanup, lastStats, lastFgTry;
    List<Box> boxes = [];
    bool startupAdopt = true;
    bool castVerified;                  // 自分の投げ/引きでウキの状態を確認できたか
    double unverifiedWait = 25.0;       // 未確認のまま当たりを待つ上限(再開直後は短くする)
    readonly double startedAt = EventLog.Now();
    double lastTick = EventLog.Now();
    readonly ViewMotion viewMotion;
    bool wasFg = true;

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

    // ---------------------------------------------------------------- トースト処理

    /// <summary>OCR の読み取りを出現(appearance)単位に集約し、状態に応じて処理する。</summary>
    void DrainSightings(Frame f)
    {
        while (reader.TryDequeue(out var s))
        {
            if (s.Neko is not null)
            {
                nekos.Note(s, f, state);
                if (s.Toast is null) continue;
            }
            if (s.Toast is null)
            {
                if (s.Text.Length >= 5) NoteUnclassified(s, f);
                continue;
            }
            var kind = s.Toast.Kind;
            double last = kindLast.GetValueOrDefault(kind, -99.0);
            bool newAppearance = s.T - last > cfg.ToastNewGap;
            kindLast[kind] = s.T;
            var a = attempt;
            if (kind is Toast.LevelUp or Toast.Achieve)
            {
                if (state == SResult && a is not null && s.T >= a.TClick - 0.1)
                {
                    if (!a.Info.Contains(kind)) a.Info.Add(kind);
                    a.InfoUntil = Now + cfg.InfoWaitSeconds;      // 釣果通知が後から出るのを待つ
                }
                else if (newAppearance) events.Write("toast_info", ("kind", kind), ("text", s.Toast.Text), ("state", state));
                continue;
            }
            if (kind == Toast.Aim)
            {
                if (newAppearance && state is SCastWait or SCooldown) AimError(s, f);
                continue;
            }
            if (state == SResult && a is not null && s.T >= a.TClick - 0.1)
            {
                a.FirstSeen ??= s.T;
                if (a.Best is null || s.Toast.Score >= a.Best.Score) { a.Best = s.Toast; a.Engine = s.Engine; }
                continue;
            }
            if (newAppearance) UnlinkedToast(s, f);
        }
    }

    /// <summary>試行に紐付かないトースト。逃げられた=当たり見逃し、それ以外は紐付け不能として記録する。</summary>
    void UnlinkedToast(Sighting s, Frame f)
    {
        var t = s.Toast!;
        if (recorder.LastFinal is { } fin && fin.Kind == t.Kind && fin.Fish == t.Fish && fin.Exp == t.Exp && fin.Count == t.Count && s.T - fin.T < cfg.ToastDuplicateWindow)
            return;      // 確定済みの結果の再読取り
        if (Now - startedAt < cfg.StartupGrace)
        {
            events.Write("toast_unlinked", ("kind", t.Kind), ("text", t.Text), ("state", "startup_stale"));
            return;      // 起動前から表示されていた古い通知
        }
        if (t.Kind == Toast.Escaped && state is SWaitBite or SCastWait)
        {
            // 一時停止の間は引けないので、停止中に過ぎた当たりは検出の見逃しではない
            if (Now - control.LastResumeAt < cfg.ResumeEscapeGrace)
            {
                RecordAttempt("no_bite_reset", t, f, null, "一時停止中に当たりが過ぎた(逃げられた表示)");
                return;
            }
            RecordAttempt("missed_bite", t, f, null, "引く前に逃げられた(沈み検出の見逃し)",
                new() { ["trace"] = TraceTail(90), ["pecks_seen"] = pecks });
            return;
        }
        var name = SaveEvidence(f, $"unlinked_{t.Kind}", new { text = t.Text, state, engine = s.Engine });
        events.Write("toast_unlinked", ("kind", t.Kind), ("text", t.Text), ("fish", t.Fish), ("state", state), ("evidence", name));
        Log.Warn($"紐付かないトースト: {t.Kind} ({t.Fish}) 状態={state} OCR={t.Text}");
    }

    /// <summary>どの種別にも当てはまらない文字列。見落とし調査のため重複を避けつつ記録する。</summary>
    void NoteUnclassified(Sighting s, Frame f)
    {
        // 確定済みの結果(釣れた/逃げられた/まだかかっていません)の表示が消えていく途中の誤読は、同じ通知の読み損ないなので件数だけ数える
        if (recorder.LastFinal is { } fin && s.T - fin.T < cfg.ToastNoiseWindow && NoiseHints.Any(h => s.Text.Contains(h)))
        {
            status.Set("toast_noise", ++toastNoise);
            return;
        }
        // 釣りと関係のない画面の文字は未分類の警告にせず、種別ごとに数える(本当の取りこぼしを目立たせるため)
        if (ToastNoise.Category(s.Text) is { } other)
        {
            events.Write("toast_other", ("category", other), ("text", s.Text), ("state", state));
            return;
        }
        var (prev, t0) = unclassifiedLast;
        if (s.Text[..Math.Min(8, s.Text.Length)] == prev[..Math.Min(8, prev.Length)] && s.T - t0 < 5) return;
        unclassifiedLast = (s.Text, s.T);
        var name = SaveEvidence(f, "unclassified_toast", new { text = s.Text, engine = s.Engine, state });
        events.Write("toast_unclassified", ("text", s.Text), ("state", state), ("evidence", name));
        Log.Warn($"未分類のトースト領域テキスト: {s.Text}");
    }

    string SaveEvidence(Frame f, string name, object meta) => evidence.Save(f.Clone(), name, meta, boxes);

    List<object> TraceTail(int n) =>
        trace.TakeLast(n).Select(x => (object)new object[] { Math.Round(x.T, 2), x.Y, x.W }).ToList();

    // ---------------------------------------------------------------- 試行の確定

    Dictionary<string, object?> RecordAttempt(string outcome, Toast? toast, Frame? f, Attempt? a, string note, Dictionary<string, object?>? extra = null)
    {
        var fields = recorder.Record(outcome, toast, f, a, note, pecks, extra);
        if (outcome == "caught" && fields["wait_s"] is double ws) tuner.NoteBiteWait(ws);
        return fields;
    }

    // ---------------------------------------------------------------- 状態遷移

    /// <summary>投げクリックを送って着水待ちへ。ウキが引く前の位置に残っていれば引けていないので投げない。</summary>
    void StartCast(double now, Bobber? b)
    {
        if (expectAway is { } ea && b is not null && Math.Abs(b.X - ea.X) < cfg.BobberNearPx * scale && Math.Abs(b.Y - ea.Y) < cfg.BobberNearPx * scale)
        {
            Log.Warn("引いたはずなのにウキが水中の元位置にある。投げずに当たり待ちへ戻る");
            events.Write("desync", ("note", "reel_ineffective"), ("pos", new[] { ea.X, ea.Y }));
            expectAway = null;
            dip = NewDip();
            castOrigin = null;
            startupAdopt = true;
            pecks = 0;
            SetState(SCastWait, "引き未反映→再静止待ち");
            return;
        }
        if (!Click(ClickKind.Cast)) return;
        castDt = Now - lastReelAt;
        expectAway = null;
        dip = NewDip();
        pecks = 0;
        castOrigin = null;
        castMoved = false;
        castLastSeen = Now;
        SetState(SCastWait, "投げクリック送信");
    }

    /// <summary>「池の水に向けて投げよう」通知: 視線が池を向いておらず投げが空振りした。</summary>
    void AimError(Sighting s, Frame f)
    {
        castFailures++;
        RecordAttempt("cast_failed", s.Toast, f, null, $"視線が池を向いていない(連続{castFailures}回)");
        status.Set("message", castFailures >= 3 ? "投げが池に届いていません。視線を池に向けてください" : "");
        SetState(SCooldown, "aim_error");
        if (castFailures >= 3)
        {
            tState = Now + cfg.CastFailPause;      // 連続失敗は待ち時間を延ばす
            AlertCastFailures();
        }
    }

    /// <summary>投げ失敗が続いたら、画面を見ていなくても気づけるよう連続の最初の 1 回だけ通知する。</summary>
    void AlertCastFailures()
    {
        if (castAlerted) return;
        castAlerted = true;
        Notify.Toast("ぽこぽこファーム", "投げが連続で失敗しています。視線を池に向けてください");
    }

    /// <summary>ウキが静止して当たり待ち態勢に入ったとき。投げの成否を確認する。</summary>
    void OnArmed(double now)
    {
        tArmed = now;
        castFailures = 0; castAlerted = false;
        castVerified = castVerified || castMoved;
        if (castVerified) unverifiedWait = cfg.UnverifiedWait;
        startupAdopt = false;
        SetState(SWaitBite, $"ウキ静止 base={dip.BaseY:F0}");
        anchorPos = lastPos;
        Log.Info($"ウキ静止を確認 (x,y)={lastPos} 幅={dip.BaseW:F0}");
        if (castDt >= 0) { Log.Info($"[castlock] dt={castDt:F2} ok"); tuner.OnCastSucceeded(castDt); castDt = -1; }
    }

    /// <summary>引きクリックを送り、結果待ちに入る。</summary>
    void DoReel(double now, Frame f, string why)
    {
        var pos = lastPos;
        if (!Click(ClickKind.Reel)) return;
        double tClick = Now;
        lastReelAt = tClick;
        expectAway = pos;
        attempt = new Attempt(events.NextAttempt(), now, tClick, pos, pecks)
        {
            Trace = trace.TakeLast(60).Select(x => new[] { Math.Round(x.T, 2), x.Y, (double)x.W }).ToList(),
            FrameClick = f.Clone(), TArmed = tArmed, BaseW = dip.BaseW,
        };
        SetState(SResult, why);
    }

    /// <summary>結果待ちの打ち切り判定。トースト確定か、タイムアウト時の再読取りと unknown 記録。</summary>
    void FinishResult(double now, Frame f)
    {
        var a = attempt!;
        double waited = now - a.TClick;
        var best = a.Best;
        bool done = false;
        if (best is not null)
        {
            bool complete = best.Kind != Toast.Caught || (best.Fish.Length > 0 && best.Exp is not null && best.Count is not null);
            done = complete || (a.FirstSeen is { } fs && now - fs > cfg.ResultFirstSeenGrace);
        }
        else if (waited > (a.Probe ? cfg.ProbeResultTimeout : cfg.ResultTimeout) && now > a.InfoUntil)
        {
            best = reader.Recover(a);
            if (best is not null) a.Engine = "rapidocr再読取";
            if (best is null && a.Info.Count > 0)
            {
                // レベルアップ/実績が出ている = 経験値が入った = 釣れている。魚種は読めなかった
                best = new Toast(Toast.Caught, "", "", null, null);
                a.PartialNote = "レベルアップ/実績通知のみ(魚種不明)";
            }
            done = true;
        }
        if (!done) return;
        if (best is null && a.Probe)
        {
            // 水中か空中か不明なまま引いたが何も出なかった = 空中で投げ動作になった
            RecordAttempt("no_bite_reset", null, f, a, "トーストなし: 空中のウキを投げた");
            attempt = null;
            dip = NewDip();
            castOrigin = a.PosBefore;       // 空中の位置から離れたら投げ成功
            castMoved = false; startupAdopt = false; expectAway = null;
            SetState(SCastWait, "確認クリックが投げになった");
            return;
        }
        if (best is null)
        {
            RecordAttempt("unknown", null, f, a, "引いた後に結果トーストを検出できなかった");
            attempt = null;
            // クリックが投げになった/効かなかった可能性がある。ウキの動きで判断するため着水待ちへ(expectAway は維持)
            dip = NewDip();
            castOrigin = a.PosBefore; castMoved = false; startupAdopt = false;
            SetState(SCastWait, "結果不明→ウキの動きで状態確認");
            return;
        }
        var outcome = best.Kind switch { Toast.Caught => "caught", Toast.Early => "early", _ => "escaped" };
        // 確認クリック(再起動直後など)で「まだ早い」「逃げられた」が出ても、停止中に当たりが過ぎただけで検出の失敗ではない
        if (a.Probe && outcome is "early" or "escaped") RecordAttempt("no_bite_reset", best, f, a, outcome == "early" ? "当たりなしで引き直した" : "確認クリックで逃げられた表示(停止中に当たりが過ぎた)");
        else RecordAttempt(outcome, best, f, a, a.PartialNote, a.Info.Count > 0 ? new() { ["info"] = a.Info.ToList() } : null);
        castVerified = true; unverifiedWait = cfg.UnverifiedWait;
        tuner.OnOutcome(outcome);
        attempt = null;
        SetState(SCooldown, outcome);
    }

    // ---------------------------------------------------------------- メインループ

    Bobber? DetectBobber(Frame f)
    {
        var r = rois.Bobber;
        var region = ((int)(r[0] * f.Width), (int)(r[1] * f.Height), (int)(r[2] * f.Width), (int)(r[3] * f.Height));
        var ex = ExcludeRects(f.Width, f.Height);
        // 近くを通る他人のアバターの赤い物体が追跡位置を奪うので、直近に静止を確認した位置の周辺を最優先で探す
        // 水面のウキ(細い)を先に探す。竿先の大きいウキや他人の大きな物体が白玉の多さで勝って、本物を隠すのを防ぐ
        int narrow = (int)(cfg.RodTipBobberWidth * scale);
        var bp = BobberParams.From(cfg);
        var b = anchorPos is { } a ? BobberDetector.Detect(f, region, a, exclude: ex, scale: scale, p: bp, maxWidth: narrow) : null;
        if (b is null && lastPos is { } n) b = BobberDetector.Detect(f, region, n, exclude: ex, scale: scale, p: bp, maxWidth: narrow);
        b ??= BobberDetector.Detect(f, region, null, exclude: ex, scale: scale, p: bp, maxWidth: narrow);
        return b ?? BobberDetector.Detect(f, region, null, exclude: ex, scale: scale, p: bp);
    }

    /// <summary>ウィンドウ探索とキャプチャ開始/再開。成功時 true。</summary>
    bool EnsureCapture()
    {
        // タイトル検索は他プロセスのウィンドウへ問い合わせて詰まることがあるので、ハンドルの生存確認を毎回、検索は 30 秒おきにする
        bool recheck = Now - lastFind >= cfg.WindowRecheckInterval;
        if (recheck) lastFind = Now;
        if (cap is not null && WinApi.IsAlive(hwnd) && (!recheck || WinApi.FindWindow(cfg.WindowTitle) == hwnd))
        {
            if (WinApi.IsMinimized(hwnd))
            {
                status.Set("message", "VRChat が最小化されています");
                if (!dryRun) WinApi.EnsureForeground(hwnd);
                return false;
            }
            return true;
        }
        cap?.Dispose(); cap = null;
        hwnd = WinApi.FindWindow(cfg.WindowTitle);
        if (hwnd == 0)
        {
            SetState(SWaitWindow, "VRChat ウィンドウを待機中");
            return false;
        }
        try
        {
            if (WinApi.IsMinimized(hwnd) && !dryRun) WinApi.EnsureForeground(hwnd);
            cap = new WgcCapture(hwnd);
            cap.Start();
            capStarted = Now;
            frame = null; frameSeen = 0;
            viewMotion.Reset();
            Log.Info($"キャプチャ開始: {WinApi.Geometry(hwnd)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"キャプチャ開始に失敗: {ex.Message}");
            cap?.Dispose(); cap = null;
            status.Set("message", $"キャプチャ失敗: {ex.Message}");
            return false;
        }
        if (state == SWaitWindow) SetState(SCooldown, "キャプチャ開始");
        return true;
    }

    /// <summary>無期限ループ。例外時は記録して続行する。</summary>
    public void Run(CancellationToken stop)
    {
        Log.Info($"釣り開始 (pecks_to_reel={tuner.PecksToReel}{(dryRun ? ", dry-run" : "")})");
        events.Write("session_start", ("pid", Environment.ProcessId), ("engine", "csharp"), ("dry_run", dryRun));
        evidence.Cleanup();
        prof.StartWatchdog(stop);
        int errCount = 0; double lastErr = 0;
        while (!stop.IsCancellationRequested)
        {
            try { Tick(); errCount = 0; }
            catch (Exception ex)
            {
                errCount++;
                Log.Exception("ループ内エラー", ex);
                if (Now - lastErr > 10) { events.Write("error", ("message", ex.Message), ("state", state)); lastErr = Now; }
                if (errCount >= 5 && cap is not null) { cap.Dispose(); cap = null; }
                Thread.Sleep((int)(Math.Min(2.0, 0.2 * errCount) * 1000));
            }
        }
        cap?.Dispose();
    }

    /// <summary>再開時: 停止していた時間を待ちの計時から除き、ウキの状態を確認し直す。</summary>
    void OnResumed(double stopped)
    {
        // 停止中に投げ待ちが時間切れになって失敗扱いになるのを防ぐ
        tState += stopped; tArmed += stopped; castLastSeen += stopped;
        // 停止中に手動で釣り竿を操作されたかもしれない。ウキが水中か空中か分からないので、短い待ちで確認クリックに回す
        castVerified = false; unverifiedWait = cfg.ResumeWait;
        AfterGap(0.0);
    }

    /// <summary>VRChat が前面でない間は他ウィンドウが写り込むため、判定と記録を止めて前面化を試みる。</summary>
    bool ForegroundOk(double now)
    {
        if (hwnd != 0 && WinApi.IsForeground(hwnd))
        {
            if (!wasFg)
            {
                Log.Info("VRChat が前面に戻りました。ウキの静止判定をやり直します");
                AfterGap(0.0);
                wasFg = true;
            }
            return true;
        }
        if (wasFg)
        {
            Log.Warn("VRChat が前面ではありません。判定を一時停止します");
            wasFg = false;
            reader.Clear();
        }
        status.Set("message", "VRChat が前面ではないため一時停止中");
        if (!dryRun && now - lastFgTry > cfg.ForegroundRetry)
        {
            lastFgTry = now;
            WinApi.EnsureForeground(hwnd);
        }
        return false;
    }

    /// <summary>視点が動いている間はウキの位置変化を沈みと誤認しないよう、静止判定をやり直す。</summary>
    void OnViewChange()
    {
        if (state == SWaitBite)
        {
            Log.Info("視点移動を検出。当たり待ちを仕切り直します");
            castOrigin = null; startupAdopt = true;
            lastPos = null;
            SetState(SCastWait, "視点移動→再静止待ち");
        }
        dip = NewDip();
    }

    /// <summary>キャプチャ断(UAC表示・画面切替など)の後は、見失い=沈みの誤検出を避けるため当たり待ちを仕切り直す。</summary>
    void AfterGap(double gap)
    {
        Log.Warn($"処理が{gap:F1}秒途切れました(キャプチャ断の可能性)。ウキの静止判定をやり直します"
            + $" [gc pause={GC.GetTotalPauseDuration().TotalMilliseconds:F0}ms gen0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} frameAge={(cap is null ? -1 : Now - cap.LatestAt):F2}s]");
        events.Write("gap", ("seconds", Math.Round(gap, 1)), ("state", state));
        dip = NewDip();
        lastPos = null;
        if (state == SWaitBite)
        {
            castOrigin = null; startupAdopt = true;
            SetState(SCastWait, "キャプチャ断→再静止待ち");
        }
    }

    /// <summary>1 フレーム分の処理。</summary>
    void Tick()
    {
        double now = Now;
        prof.Mark("paused-check");
        if (control.Poll())
        {
            prof.Account(now, "paused");
            lastTick = now;
            status.Flush();
            Thread.Sleep(100);
            return;
        }
        double gap = now - lastTick;
        lastTick = now;
        if (gap > cfg.GapThreshold) { prof.Account(now, "gap"); AfterGap(gap); }
        prof.Mark("ensure-capture");
        if (!EnsureCapture()) { prof.Account(now, "gap"); status.Flush(); Thread.Sleep(500); return; }
        if (!ForegroundOk(now)) { prof.Account(now, "fg_lost"); status.Flush(); Thread.Sleep(100); return; }
        prof.Account(now, "active");

        prof.Mark("copy");
        bool fresh = cap!.CopyLatestTo(ref frame, ref frameSeen);
        if (frame is null || (!fresh && now - Math.Max(cap.LatestAt, capStarted) > cfg.CaptureStallSeconds))
        {
            if (frame is null && now - capStarted < 3.0) { Thread.Sleep(10); return; }     // 開始直後は最初のフレーム待ち
            Log.Warn("フレーム取得不可。キャプチャを再初期化します");
            cap.Dispose(); cap = null;
            Thread.Sleep(300);
            return;
        }
        var f = frame;
        if (CaptureSizeStale(f, now)) return;
        if ((f.Width, f.Height) != frameSize)
        {
            frameSize = (f.Width, f.Height);
            scale = f.Height / (double)BobberDetector.RefHeight;
            rois = hud.Calibrated(Layout.Resolve(cfg, f.Width, f.Height), f.Width, f.Height);
            Log.Info($"画面 {f.Width}x{f.Height} に合わせて範囲とピクセル基準値(倍率 {scale:F2})を調整します");
            dip = NewDip();
            lastPos = null;
            viewMotion.Reset();
        }
        if (fresh)
        {
            fpsN++;
            if (viewMotion.Update(f, now) && state is SWaitBite or SCastWait) OnViewChange();
            prof.Mark("detect");
            var b = DetectBobber(f);
            bobber = b;
            if (b is not null) { lastPos = (b.X, b.Y); AddTrace(now, b.Y, b.W); }
            else AddTrace(now, -1, 0);
            prof.Mark("pump-ocr");
            reader.Pump(now, f, rois.Toast, scale);
        }
        if (now - fpsT >= 2.0) { fps = fpsN / (now - fpsT); fpsN = 0; fpsT = now; }
        prof.Mark("drain");
        DrainSightings(f);
        UpdateBoxes(f, bobber);
        prof.Mark("step");
        Step(now, f, bobber, fresh);
        prof.Mark("housekeeping");
        Housekeeping(now, f);
        prof.Mark("wait-frame");
        cap.WaitFrame(4);
    }

    double sizeMismatchSince;

    /// <summary>ディスプレイ移動などで WGC の取得サイズが実際のクライアント領域とずれたままなら、キャプチャを作り直す。</summary>
    bool CaptureSizeStale(Frame f, double now)
    {
        var g = WinApi.Geometry(hwnd);
        if (g.Width <= 0 || g.Height <= 0 || (Math.Abs(f.Width - g.Width) <= 4 && Math.Abs(f.Height - g.Height) <= 4)) { sizeMismatchSince = 0; return false; }
        if (sizeMismatchSince == 0) sizeMismatchSince = now;
        if (now - sizeMismatchSince < cfg.CaptureSizeMismatchSeconds) return false;
        Log.Warn($"取得画面 {f.Width}x{f.Height} がウィンドウ {g.Width}x{g.Height} と合いません。キャプチャを作り直します");
        sizeMismatchSince = 0;
        cap?.Dispose(); cap = null;
        return true;
    }

    void AddTrace(double t, int y, int w)
    {
        trace.AddLast((t, y, w));
        if (trace.Count > 120) trace.RemoveFirst();
    }

    void Step(double now, Frame f, Bobber? b, bool fresh)
    {
        switch (state)
        {
            case SCooldown:
            {
                double wait = recorder.SessionAttempts > 0 ? cfg.RecastDelay : 0.5;
                // ゲームは直前の引き(釣果)から約 1.8 秒は投げを受け付けない。結果の確定が早いと RecastDelay だけでは足りないので、引きクリックからも数える
                if (now - tState >= wait && now - lastReelAt >= tuner.ReelToCast && now - recorder.LastCaughtAt >= cfg.AfterCatchCastMin && !StartupProbe(now, b)) StartCast(now, b);
                break;
            }
            case SCastWait:
            {
                if (b is not null) castLastSeen = now;
                if (b is not null && !startupAdopt && fresh)
                {
                    if (castOrigin is null) castOrigin = (b.X, b.Y);      // 投げ後に最初に見えた位置を起点にする
                    else if (!castMoved && Math.Abs(b.X - castOrigin.Value.X) + Math.Abs(b.Y - castOrigin.Value.Y) > cfg.CastMovedPx * scale) castMoved = true;
                }
                dip.Anchored = b is not null && anchorPos is { } ap && Math.Abs(b.X - ap.X) <= cfg.BobberNearPx * scale * 2 && Math.Abs(b.Y - ap.Y) <= cfg.BobberNearPx * scale * 2;
                if (fresh || b is null) dip.Update(now, b);
                // 低 fps だと最初に見えた時点で着水済みで動きを観測できないので、投げから一定時間たった静止も着水とみなす(未確認扱いで当たり待ちに入る)
                if (dip.Armed && (castMoved || startupAdopt || now - tState > cfg.CastSettleFallback))
                {
                    // 竿先にぶら下がったウキ(幅が水面の約 2 倍)は着水していない。投げのクリックが効かなかったので撮り直して投げ直す
                    if (dip.BaseW > cfg.RodTipBobberWidth * scale)
                    {
                        Log.Warn($"ウキが竿先に残っています(幅={dip.BaseW:F0})。投げが不発だったので投げ直します");
                        castFailures++;
                        if (castDt >= 0) { Log.Info($"[castlock] dt={castDt:F2} fail"); tuner.OnCastFailed(castDt); castDt = -1; }
                        RecordAttempt("cast_failed", null, f, null, $"投げが不発(ウキが竿先に残った・連続{castFailures}回)");
                        SetState(SCooldown, "cast_failed");
                        dip = NewDip();
                        // 待ち時間では直らない連続の不発(視線のずれなど)は、間隔を空けて、気づけるよう通知する
                        if (castFailures >= 3)
                        {
                            status.Set("message", "投げが池に届いていません。視線を池に向けてください");
                            tState = Now + cfg.CastFailPause;
                            AlertCastFailures();
                        }
                        return;
                    }
                    OnArmed(now);
                    return;
                }
                if (expectAway is { } ea && b is not null && !startupAdopt && now - tState > cfg.ReelIneffectiveSeconds
                    && Math.Abs(b.X - ea.X) < cfg.BobberNearPx * scale && Math.Abs(b.Y - ea.Y) < cfg.BobberNearPx * scale)
                {
                    Log.Warn("ウキが引く前の位置から動かない。引けていなかったので当たり待ちへ戻る");
                    events.Write("desync", ("note", "reel_ineffective_inplace"), ("pos", new[] { ea.X, ea.Y }));
                    expectAway = null; startupAdopt = true;
                    return;
                }
                if (now - tState > cfg.CastTimeout || (!startupAdopt && now - castLastSeen > cfg.CastLostSeconds)) CastFailed(f);
                break;
            }
            case SWaitBite:
            {
                var ev = fresh || b is null ? dip.Update(now, b) : null;
                if (ev == "dip")
                {
                    pecks++;
                    Log.Info($"沈み検出 #{pecks} (y={(b is null ? "lost" : b.Y.ToString())})");
                    if (pecks >= tuner.PecksToReel) DoReel(now, f, $"沈み{pecks}回目で引く");
                }
                else if (now - tArmed > (castVerified ? tuner.BiteTimeout() : unverifiedWait)) NoBite(now, f);
                break;
            }
            case SResult:
                FinishResult(now, f);
                break;
        }
    }

    /// <summary>起動直後は既存ウキが水中か空中か分からない。見えていれば確認クリックで判定する。</summary>
    bool StartupProbe(double now, Bobber? b)
    {
        if (!startupAdopt || recorder.SessionAttempts > 0 || b is null) return false;
        if (now - tState < cfg.ProbeWatchSeconds) return true;           // 起動直後はウキの様子を見る
        startupAdopt = false;
        Log.Info("起動時にウキが見えているため、確認クリックで水中/空中を判定します");
        tArmed = now;
        NoBite(now, frame!);
        return true;
    }

    /// <summary>投げてもウキが着水しなかった(視線が池でない等)。</summary>
    void CastFailed(Frame f)
    {
        castFailures++;
        RecordAttempt("cast_failed", null, f, null, $"投げ後{cfg.CastTimeout:F0}秒でウキが静止せず(連続{castFailures}回) {dip.Diagnostics} 移動={castMoved} 武装={dip.Armed}",
            new() { ["trace"] = TraceTail(90) });
        if (castFailures >= 3)
        {
            status.Set("message", "投げに連続失敗: 視線が池を向いているか確認してください");
            AlertCastFailures();
            Thread.Sleep((int)(cfg.CastFailPause * 1000));
        }
        SetState(SCooldown, "cast_failed");
        dip = NewDip();
    }

    /// <summary>長時間当たりがない/検出できない。確認クリックを送り、結果で水中だったか空中だったかを判断する。</summary>
    void NoBite(double now, Frame f)
    {
        Log.Info("確認クリックを送ります(当たりなし/起動直後)");
        var pos = lastPos;
        if (!Click(ClickKind.Probe)) return;
        double t = Now;
        lastReelAt = t;
        expectAway = pos;
        attempt = new Attempt(events.NextAttempt(), t, t, pos, pecks)
        {
            Probe = true,
            Trace = trace.TakeLast(60).Select(x => new[] { Math.Round(x.T, 2), x.Y, (double)x.W }).ToList(),
        };
        SetState(SResult, "当たりなし確認クリック");
    }

    // ---------------------------------------------------------------- 表示・保守

    void UpdateBoxes(Frame f, Bobber? b)
    {
        int w = f.Width, h = f.Height;
        Box R(double[] r, string label) => new((int)(r[0] * w), (int)(r[1] * h), (int)(r[2] * w), (int)(r[3] * h), label);
        var label = hud.Level is { } lv ? $"Lv{lv}" : "level";
        if (hud.Xp is { } xp) label += $" {(int)Math.Round(xp * 100)}%";
        var list = new List<Box> { R(rois.Toast, "toast"), R(rois.Level, label), R(rois.XpBar, "") };
        if (b is not null)
        {
            double k = scale;
            list.Add(new Box((int)(b.X - 25 * k), (int)(b.Y - 12 * k), (int)(b.X + 25 * k), (int)(b.Y + 45 * k), $"bobber w{b.W}"));
        }
        boxes = list;
    }

    /// <summary>レベルと経験値バーを定期的に読み、変化時と 1 分ごとに hud イベントとして残す。</summary>

    void Housekeeping(double now, Frame f)
    {
        nekos.Flush(state);
        prof.Mark("hud");
        if (hud.Poll(now, f, rois) is { } adopted) rois = adopted;
        prof.Mark("audit");
        auditor.Tick(now, attempt is not null);
        prof.Mark("live");
        if (now - lastLive >= cfg.LiveInterval && liveFuture is not { IsCompleted: false })
        {
            // JPEG 化と保存は遅い環境で 0.7 秒ほどかかるので、釣りのループを止めないよう別スレッドで行う
            lastLive = now;
            var snap = f.Clone();
            var snapBoxes = boxes.ToList();
            liveFuture = liveWorker.Submit(() => { evidence.WriteLive(snap, snapBoxes); return true; });
        }
        prof.Mark("cleanup");
        if (now - lastCleanup >= cfg.EvidenceCleanupInterval) { lastCleanup = now; evidence.Cleanup(); }
        prof.Mark("stats");
        if (now - lastStats >= 5)
        {
            lastStats = now;
            status.Set("session", events.SummarizeSession());
            status.Set("pecks_to_reel", tuner.PecksToReel);
            status.Set("fps", Math.Round(fps, 1));
            prof.PublishPerf(now);
            status.Set("bobber", bobber is null ? null : new[] { bobber.X, bobber.Y, bobber.W });
            status.Set("state_since", tState);
            status.Set("pecks", pecks);
        }
        prof.Mark("flush");
        status.Flush();
    }
}
