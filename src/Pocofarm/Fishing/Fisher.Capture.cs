using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    /// <summary>4K など高解像度のキャプチャは、整数倍に縮めた作業用フレームで処理する(検出の調子を解像度によらず揃え、処理も軽くする)。</summary>
    Frame ToWorkFrame(Frame f)
    {
        int n = (int)Math.Round(f.Height / (double)cfg.WorkHeight);
        if (n < 2) return f;
        return workFrame = f.BoxDownscale(n, workFrame);
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
            SetState(FishState.WaitWindow, "VRChat ウィンドウを待機中");
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
        if (state == FishState.WaitWindow) SetState(FishState.Cooldown, "キャプチャ開始");
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
            try { Tick(); errCount = 0; sameStateErrors = 0; }
            catch (Exception ex)
            {
                errCount++;
                Log.Exception("ループ内エラー", ex);
                ContainError(ex);
                if (Now - lastErr > 10) { events.Write("error", ("message", ex.Message), ("state", state.Wire())); lastErr = Now; }
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
        ClearAttention("resume");
        resumeCheckPending = true;
        AfterGap(0.0);
    }

    /// <summary>視点が動いている間はウキの位置変化を沈みと誤認しないよう、静止判定をやり直す。</summary>
    void OnViewChange()
    {
        if (state == FishState.WaitBite)
        {
            Log.Info("視点移動を検出。当たり待ちを仕切り直します");
            castOrigin = null; startupAdopt = true;
            lastPos = null;
            SetState(FishState.CastWait, "視点移動→再静止待ち");
        }
        dip = NewDip();
    }

    /// <summary>キャプチャ断(UAC表示・画面切替など)の後は、見失い=沈みの誤検出を避けるため当たり待ちを仕切り直す。</summary>
    void AfterGap(double gap)
    {
        Log.Warn($"処理が{gap:F1}秒途切れました(キャプチャ断の可能性)。ウキの静止判定をやり直します"
            + $" [gc pause={GC.GetTotalPauseDuration().TotalMilliseconds:F0}ms gen0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} frameAge={(cap is null ? -1 : Now - cap.LatestAt):F2}s]");
        events.Write("gap", ("seconds", Math.Round(gap, 1)), ("state", state.Wire()));
        dip = NewDip();
        lastPos = null;
        if (state == FishState.WaitBite)
        {
            castOrigin = null; startupAdopt = true;
            SetState(FishState.CastWait, "キャプチャ断→再静止待ち");
        }
    }

    /// <summary>1 フレーム分の処理。</summary>
    void Tick()
    {
        double now = Now;
        status.Flush();       // どの分岐で抜けても更新が止まらないようにする(常駐が固まりの判定に使う)
        prof.Mark("paused-check");
        if (control.Poll())
        {
            prof.Account(now, "paused");
            lastTick = now;
            status.Flush();
            Thread.Sleep(cfg.IdlePollMs);
            return;
        }
        double gap = now - lastTick;
        lastTick = now;
        if (gap > cfg.GapThreshold) { prof.Account(now, "gap"); AfterGap(gap); }
        prof.Mark("ensure-capture");
        if (!EnsureCapture()) { prof.Account(now, "gap"); status.Flush(); Thread.Sleep(cfg.NoCaptureRetryMs); return; }
        prof.Account(now, "active");

        prof.Mark("copy");
        bool fresh = cap!.CopyLatestTo(ref frame, ref frameSeen);
        if (frame is null || (!fresh && now - Math.Max(cap.LatestAt, capStarted) > cfg.CaptureStallSeconds))
        {
            if (frame is null && now - capStarted < cfg.StartupFrameWait) { Thread.Sleep(10); return; }     // 開始直後は最初のフレーム待ち
            Log.Warn("フレーム取得不可。キャプチャを再初期化します");
            cap.Dispose(); cap = null;
            Thread.Sleep(cfg.CaptureReinitMs);
            return;
        }
        var f = frame;
        if (CaptureSizeStale(f, now)) return;
        f = ToWorkFrame(f);
        cur = f;
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
            if (viewMotion.Update(f, now) && state is FishState.WaitBite or FishState.CastWait) OnViewChange();
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
        prof.Mark("manual");
        HandleManual(now, f);
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
}
