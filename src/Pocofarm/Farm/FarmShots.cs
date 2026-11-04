using System.Text.RegularExpressions;
using Pocofarm.Core;
using Pocofarm.Platform;

namespace Pocofarm.Farm;

/// <summary>釣りの一時停止と状態の問い合わせ。ファーム撮影が、釣りの区切りを待って止めるのに使う。</summary>
public interface IFishingControl
{
    bool IsPaused();
    string State();
    void RequestPause(bool pause);
}

/// <summary>Spout 配信中、一定間隔でファームの様子を JPEG に保存する(直近 farm_keep_shots 枚だけ残す)。</summary>
public sealed partial class FarmShots(Paths paths, Config cfg, IFishingControl dash)
{
    [GeneratedRegex(@"^farm_\d{8}_\d{6}\.jpg$")]
    private static partial Regex NameRe();

    public static bool SafeName(string name) => NameRe().IsMatch(name);

    const int StreamPollMs = 300, NextPollMs = 5000, GatePollMs = 100;

    public string? LastError { get; private set; }

    bool calChecked;

    /// <summary>いま行っていること(「撮影」「画角調整」)。釣りを止めている間だけ入る。ダッシュボードの一時停止の表示に使う。</summary>
    public string Phase { get; private set; } = "";

    /// <summary>画角調整の途中経過(ダッシュボードのリアルタイム表示に出す)。</summary>
    public string Note { get; private set; } = "";

    /// <summary>次の定期撮影の予定時刻(epoch 秒)。無効または撮影中は 0。</summary>
    public double NextAt { get; private set; }

    /// <summary>停止されるまで撮影し続ける。配信は撮影の間だけ ON にする(配信中は HUD が消えるため)。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Farm);
        using var rx = new SpoutReceiver();
        try
        {
            do
            {
                if (cfg.FarmShotInterval <= 0 || !await SpoutCamera.Gate.WaitAsync(0, ct)) continue;
                var wasPaused = dash.IsPaused();
                if (wasPaused) { SpoutCamera.Gate.Release(); continue; }     // 一時停止中は手動操作中の可能性が高く、画面操作を割り込ませない
                try { await ShootAsync(rx, wasPaused); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    LastError = ex.Message;
                    Log.Warn("ファーム写真の撮影に失敗: " + ex.Message);
                }
                finally
                {
                    if (!wasPaused) dash.RequestPause(false);
                    SpoutCamera.Gate.Release();
                }
            } while (await WaitNextAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>次の撮影時刻まで待つ。間隔は設定の変更にすぐ追従し、0 の間は撮らずに待ち続ける。</summary>
    async Task<bool> WaitNextAsync(CancellationToken ct)
    {
        var since = EventLog.Now();
        while (!ct.IsCancellationRequested)
        {
            NextAt = cfg.FarmShotInterval > 0 ? since + cfg.FarmShotInterval : 0;
            await Task.Delay(NextPollMs, ct);
            if (cfg.FarmShotInterval <= 0) since = EventLog.Now();
            else if (EventLog.Now() - since >= cfg.FarmShotInterval && (!cfg.FarmShotForegroundOnly || WinApi.IsForeground(WinApi.FindWindow(cfg.WindowTitle)))) return true;   // 前面でなければ、前面になるまで撮影を待つ(フォーカスを奪わない)
        }
        return false;
    }

    /// <summary>今すぐ 1 枚撮る(ダッシュボードの「今すぐ撮影」)。他の撮影中なら null を返す。</summary>
    public async Task<string?> ShootNowAsync(bool calibrate = false)
    {
        if (!await SpoutCamera.Gate.WaitAsync(0)) return "撮影中です";
        var wasPaused = dash.IsPaused();
        try
        {
            using var rx = new SpoutReceiver();
            return await ShootAsync(rx, wasPaused, calibrate);
        }
        finally
        {
            if (!wasPaused) dash.RequestPause(false);
            SpoutCamera.Gate.Release();
        }
    }

    /// <summary>釣りを止め、カメラを開いて配信し、1 枚保存して元に戻す。失敗時は理由を返す。</summary>
    async Task<string?> ShootAsync(SpoutReceiver rx, bool wasPaused, bool calibrate = false)
    {
        if (!wasPaused)
        {
            // 釣果の取りこぼしを避けるため、釣れた直後(cooldown)の短い隙間でだけ止める。来なければ今回は見送る
            for (int i = 0; dash.State() != "cooldown"; i++)
            {
                if (i >= cfg.FarmShotWaitSeconds * 1000 / GatePollMs) return "釣りの区切りを待てず見送りました";
                await Task.Delay(GatePollMs);
            }
            dash.RequestPause(true);
            await Task.Delay((int)(cfg.FarmPauseSettle * 1000));
        }
        var stopped = System.Diagnostics.Stopwatch.StartNew();
        Phase = "撮影"; Note = "";
        SpoutCamera.DebugDir = paths.Data;
        if (await SpoutCamera.OpenAsync(cfg) is { } err) { Phase = ""; LastError = err; Log.Warn("ファーム写真: " + err); return err; }
        string? result = "フレームを取得できませんでした";
        try
        {
            RestorePose();
            Osc.SetStreaming(true, cfg.OscPort);
            if (cfg.FarmHidePeople) Osc.SetCameraLayers(false, cfg.OscPort);
            int waitSteps = (int)(cfg.FarmStreamWaitSeconds * 1000 / StreamPollMs);
            for (int i = 0; i < waitSteps && !SpoutCamera.IsStreaming(); i++)
            {
                await Task.Delay(StreamPollMs);
                if (i == waitSteps * 2 / 5) Osc.SetStreaming(true, cfg.OscPort);       // 送信元が現れない時は配信の指示をもう一度送る
            }
            if (await GrabShotAsync(rx, cfg.FarmGrabAttempts) is { } shot)
            {
                if (calibrate || (cfg.FarmCalibrate && !calChecked))
                {
                    calChecked = true;
                    var cp = FarmCalParams.From(cfg);
                    if (calibrate || !FarmFit.Of(FarmGrid.Scan(shot, FarmScanParams.From(cfg)), cp.Margin).Good(cp.Tolerance))
                        shot = await CalibrateAsync(rx, cp) ?? shot;
                }
                var path = Path.Combine(paths.Farm, $"farm_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                AtomicFile.Write(path, Imaging.ToJpeg(shot, [], cfg.FarmShotWidth, Math.Clamp(cfg.FarmShotQuality, 1, 100), 18));
                SaveScan(shot, path);
                PruneOld();
                result = null;
            }
        }
        finally
        {
            Phase = ""; Note = "";
            if (cfg.FarmHidePeople) Osc.SetCameraLayers(true, cfg.OscPort);
            await SpoutCamera.CloseAsync(cfg);
            if (SpoutCamera.IsStreaming()) { await SpoutCamera.CloseAsync(cfg); if (SpoutCamera.IsStreaming()) Log.Warn("ファーム写真: 配信を止められません(釣果が読めない状態のままです)"); }
        }
        Log.Info($"ファーム写真: 釣りを止めた時間 約{stopped.Elapsed.TotalSeconds:F1}秒");
        if (result is not null) Log.Warn("ファーム写真: " + result);
        return result;
    }

    /// <summary>フレームを取って切り抜く。取れなければ attempts 回まで取り直して null。</summary>
    async Task<Frame?> GrabShotAsync(SpoutReceiver rx, int attempts, double? wait = null)
    {
        for (int i = 0; i < attempts; i++)
        {
            await Task.Delay((int)((wait ?? cfg.FarmGrabWait) * 1000));
            var f = rx.Grab(cfg.SpoutSender, out var e);
            LastError = e;
            if (f is null) continue;
            var shot = CropShot(f);
            PublishLive(shot);
            return shot;
        }
        return null;
    }

    /// <summary>撮影・画角調整の間の映像を、ダッシュボードがリアルタイムで見られるよう 1 枚書き出す。</summary>
    void PublishLive(Frame shot)
    {
        try { AtomicFile.Write(paths.FarmLive, Imaging.ToJpeg(shot, [], 640, 60, 18)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// カメラ位置を動かして、畑 8 面が写真の中央に余白つきで大きく収まる位置を探し、見つかれば保存して(新しい位置の写真を返す)。
    /// 見つからなければ元の位置へ戻して null。配信中のカメラで行うので、撮影と同じ停止の間に済ませる。
    /// </summary>
    async Task<Frame?> CalibrateAsync(SpoutReceiver rx, FarmCalParams cp)
    {
        var file = paths.CameraPose;
        if (LoadPose() is not { } start)
        {
            Log.Warn("画角調整: 元のカメラ位置(camera_pose.json)がないため行いません");
            return null;
        }
        var scanParams = FarmScanParams.From(cfg);
        Phase = "画角調整";
        async Task<FarmFit?> Measure(float[] pose)
        {
            ApplyPose(pose);
            await Task.Delay((int)(cfg.FarmCalSettle * 1000));
            // カメラが動き終わる前の写りを拾わないよう、連続 3 回の結果が一致するまで撮り直す
            FarmFit? prev = null;
            int same = 0;
            for (int i = 0; i < cfg.FarmCalStableTries; i++)
            {
                var fit = await GrabShotAsync(rx, 2, cfg.FarmCalGrabWait) is { } f ? FarmFit.Of(FarmGrid.Scan(f, scanParams), cp.Margin) : null;
                same = fit is not null && prev is not null && fit.Plots == prev.Plots && Math.Abs(fit.Cx - prev.Cx) < 0.008 && Math.Abs(fit.Cy - prev.Cy) < 0.008 && Math.Abs(fit.Size - prev.Size) < 0.015 ? same + 1 : 0;
                if (same >= 2 && fit is not null)
                {
                    Note = $"畑 {fit.Plots} 面 / 中心 {fit.Cx:F2},{fit.Cy:F2} / 大きさ {fit.Size:F2}";
                    Log.Info($"画角調整: x={pose[0]:F2} z={pose[2]:F2} 画角={pose[6]:F1} → {Note}");
                    return fit;
                }
                prev = fit;
            }
            Log.Info($"画角調整: x={pose[0]:F2} z={pose[2]:F2} 画角={pose[6]:F1} → 写りが落ち着きませんでした");
            return null;
        }
        Log.Info("画角調整: 畑が収まるカメラ位置を探します");
        var found = await FarmCalibration.SolveAsync(start, Measure, cp);
        var events = new EventLog(paths.Events);
        if (found is not { } r || r.Pose.SequenceEqual(start))
        {
            ApplyPose(start);
            if (found is { } same && same.Fit.Good(cp.Tolerance)) Log.Info("画角調整: 今の位置のままで収まっています");
            else Log.Warn("画角調整: 収まる位置が見つからなかったので元の位置のままにします");
            events.Write("farm_calibration", ("ok", found is { } g && g.Fit.Good(cp.Tolerance)), ("moved", false));
            return null;
        }
        AtomicFile.Write(file + ".bak", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(start));
        AtomicFile.Write(file, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(r.Pose));
        Log.Info($"画角調整: カメラ位置を保存しました x={r.Pose[0]:F2} z={r.Pose[2]:F2} 画角={r.Pose[6]:F1}(畑 {r.Fit.Plots} 面、中心 {r.Fit.Cx:F2},{r.Fit.Cy:F2}、大きさ {r.Fit.Size:F2})");
        events.Write("farm_calibration", ("ok", r.Fit.Good(cp.Tolerance)), ("moved", true), ("pose", r.Pose), ("from", start), ("cx", r.Fit.Cx), ("cy", r.Fit.Cy), ("size", r.Fit.Size));
        ApplyPose(r.Pose);
        await Task.Delay((int)(cfg.FarmCalSettle * 1000));
        Phase = "撮影";
        return await GrabShotAsync(rx, cfg.FarmGrabAttempts);
    }

    /// <summary>畑の植え付け状況を解析し、写真と同名の .json に保存する(失敗しても写真は残す)。</summary>
    void SaveScan(Frame shot, string jpgPath)
    {
        try
        {
            var scan = FarmGrid.Scan(shot, FarmScanParams.From(cfg));
            // 植え(緑)と水やり(濃い茶色)は別々に数える。目標は「植え済・水やり済」
            int unplanted = scan.Unplanted, dry = scan.Dry, done = scan.Count('w'), hidden = scan.Count('?');
            AtomicFile.Write(Path.ChangeExtension(jpgPath, ".json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new { plots = scan.Plots.Select(p => new { quad = p.Quad, cells = p.Cells, wet = p.Wet }), unplanted, dry, done, hidden }));
            new EventLog(paths.Events).Write("farm_scan", ("plots", scan.Plots.Count), ("unplanted", unplanted), ("dry", dry), ("done", done), ("hidden", hidden),
                ("unplanted_by_plot", scan.Plots.Select(p => p.Cells.Count(c => c is 'e' or 'b')).ToArray()), ("cells", scan.Plots.Select(p => p.Cells).ToArray()));
            Alert(scan);
            Log.Info($"ファーム解析: 畑 {scan.Plots.Count} 面、植えなし {unplanted}(水やり前 {dry}・水やり済 {done})・隠れて不明 {hidden}" + (scan.Plots.Count == 8 ? "" : "(畑が 8 面見つかりません)"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("ファーム解析に失敗: " + ex.Message);
        }
    }

    FarmWatch? watch;

    /// <summary>畑の通知(植えなし・乾きの連続、全マス完了)。新しく出たものをイベントとトーストに出す。</summary>
    void Alert(FarmScan scan)
    {
        watch ??= new FarmWatch(Math.Max(1, cfg.FarmAlertScans));
        var snap = new FarmSnapshot(EventLog.Now(), scan.Plots.Select(p => p.Cells).ToArray(), scan.Plots.Select(p => p.Cells.Count(c => c is 'e' or 'b')).ToArray(), scan.Dry, scan.Count('w'), scan.Count('?'));
        foreach (var a in watch.Observe(snap))
        {
            new EventLog(paths.Events).Write("farm_alert", ("kind", a.Kind), ("plot", a.Plot), ("message", a.Message));
            if (cfg.FarmAlertToast) Notify.Toast("ぽこぽこファーム", a.Message);
        }
    }

    /// <summary>撮影前後の比較に使うため、新しい方から farm_keep_shots 枚(と同名の解析結果)だけ残して消す。</summary>
    void PruneOld()
    {
        var names = Directory.EnumerateFiles(paths.Farm, "farm_*.jpg").Select(Path.GetFileName).Where(n => n is not null && SafeName(n)).OrderByDescending(n => n, StringComparer.Ordinal);
        foreach (var n in names.Skip(Math.Max(1, cfg.FarmKeepShots)))
            foreach (var f in Directory.EnumerateFiles(paths.Farm, Path.GetFileNameWithoutExtension(n) + ".*")) File.Delete(f);
    }

    /// <summary>写したくない端(赤い敷物など)を除くため、farm_shot_crop(左・上・右・下の画面比率)で切り抜く。</summary>
    Frame CropShot(Frame f)
    {
        var c = cfg.FarmShotCrop;
        return c.Length == 4 && c[0] < c[2] && c[1] < c[3] && (c[0] > 0 || c[1] > 0 || c[2] < 1 || c[3] < 1) ? f.CropRatio(c[0], c[1], c[2], c[3]) : f;
    }

    /// <summary>カメラの位置を保存した値(data/camera_pose.json。位置と向きの 6 個 + 画角の 7 個目は画角調整が書く)へ戻す。無ければ何もしない。OSCQuery の読み値は更新が遅れるので、読み戻しでは確かめない。</summary>
    void RestorePose()
    {
        if (!File.Exists(paths.CameraPose) || System.Text.Json.JsonSerializer.Deserialize<float[]>(File.ReadAllText(paths.CameraPose)) is not { } pose) return;
        if (pose.Length == 6) Osc.SetPose(pose, cfg.OscPort);
        else if (pose.Length == FarmCalibration.PoseLength) ApplyPose(pose);
    }

    /// <summary>保存したカメラ設定(位置・向きの 6 個 + 画角の 7 個目)。画角が無ければ既定値を補う。無ければ null。</summary>
    float[]? LoadPose()
    {
        if (!File.Exists(paths.CameraPose) || System.Text.Json.JsonSerializer.Deserialize<float[]>(File.ReadAllText(paths.CameraPose)) is not { } pose) return null;
        return pose.Length switch { 6 => [.. pose, FarmCalibration.DefaultZoom], FarmCalibration.PoseLength => pose, _ => null };
    }

    void ApplyPose(float[] pose)
    {
        Osc.SetPose(pose[..6], cfg.OscPort);
        Osc.SetZoom(pose[6], cfg.OscPort);
    }

    /// <summary>新しい順のファイル名と撮影時刻(epoch 秒)。</summary>
    public List<object> List(int limit)
    {
        if (!Directory.Exists(paths.Farm)) return [];
        return Directory.EnumerateFiles(paths.Farm, "farm_*.jpg")
            .Select(p => new FileInfo(p)).Where(i => SafeName(i.Name))
            .OrderByDescending(i => i.Name).Take(limit)
            .Select(i => (object)new { name = i.Name, ts = new DateTimeOffset(i.LastWriteTime).ToUnixTimeMilliseconds() / 1000.0, scan = ReadScan(i.FullName) }).ToList();
    }

    static System.Text.Json.Nodes.JsonNode? ReadScan(string jpgPath)
    {
        var file = Path.ChangeExtension(jpgPath, ".json");
        try { return File.Exists(file) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) : null; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }
}
