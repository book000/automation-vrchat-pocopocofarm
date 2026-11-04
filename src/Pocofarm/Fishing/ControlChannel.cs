using System.Text.Json;
using Pocofarm.Core;
using Pocofarm.Platform;

namespace Pocofarm.Fishing;

/// <summary>
/// 外からの操作の受け口。停止キー・ダッシュボード(control.json)による一時停止/再開と、データリセット要求を扱う。
/// 一時停止中はクリックも前面化もしない。
/// </summary>
public sealed class ControlChannel
{
    static double Now => EventLog.Now();

    readonly Config cfg;
    readonly Paths paths;
    readonly EventLog events;
    readonly Status status;
    readonly bool dryRun;
    bool pauseKeyWasDown;
    DateTime controlMtime;
    double resetToken, lastCheck, pausedAt;

    public ControlChannel(Config cfg, Paths paths, EventLog events, Status status, bool dryRun, bool startPaused)
    {
        this.cfg = cfg; this.paths = paths; this.events = events; this.status = status; this.dryRun = dryRun;
        // 起動前の古い要求で停止しないよう、現在の更新時刻を基準にする
        controlMtime = File.Exists(paths.Control) ? File.GetLastWriteTimeUtc(paths.Control) : DateTime.MinValue;
        resetToken = ReadResetToken();
        if (startPaused) { Paused = true; status.Set("paused", true); }     // 手動操作中に再起動してもクリックしない
    }

    public bool Paused { get; private set; }

    /// <summary>最後に一時停止/再開を切り替えた時刻。直後の試行は手動操作の影響を受けうるので集計から外すのに使う。</summary>
    public double LastResumeAt { get; private set; }

    /// <summary>再開したとき。引数は停止していた秒数。</summary>
    public event Action<double>? Resumed;

    /// <summary>ダッシュボードからデータがリセットされたとき。</summary>
    public event Action? DataReset;

    double ReadResetToken()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(paths.Reset));
            return doc.RootElement.TryGetProperty("token", out var t) ? t.GetDouble() : 0.0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return 0.0; }
    }

    /// <summary>ダッシュボードからのデータリセット要求を処理する。記録は削除せず data/archive へ退避する。</summary>
    void CheckReset()
    {
        double token = ReadResetToken();
        if (token <= resetToken) return;
        resetToken = token;
        var dest = events.Reset();
        Log.Warn($"データリセット: 記録を退避しました -> {dest}");
        events.Write("session_start", ("pid", Environment.ProcessId), ("note", "data_reset"));
        DataReset?.Invoke();
    }

    /// <summary>一時停止/再開を切り替え、ログ・イベント・通知に残す。</summary>
    void SetPaused(bool on, string source)
    {
        if (on == Paused) return;
        Paused = on;
        Log.Info($"{source}で{(on ? "一時停止しました" : "再開します")}");
        events.Write(on ? "pause" : "resume", ("source", source));
        if (!dryRun) Notify.Toast("ぽこぽこファーム", on ? "釣りを一時停止しました" : "釣りを再開しました");
        LastResumeAt = Now;
        if (on) pausedAt = Now;
        status.Set("paused", on);
        if (!on) Resumed?.Invoke(Math.Max(0, Now - pausedAt));
    }

    /// <summary>停止キーとコントロールファイルを確認して、いま一時停止中かを返す。</summary>
    public bool Poll()
    {
        bool down = WinApi.KeyDown(cfg.PauseVk);
        if (down && !pauseKeyWasDown) SetPaused(!Paused, "停止キー");
        pauseKeyWasDown = down;
        if (Now - lastCheck < cfg.ControlCheckInterval) return Paused;     // ファイル確認は 4 Hz で足りる
        lastCheck = Now;
        CheckReset();
        var mtime = File.Exists(paths.Control) ? File.GetLastWriteTimeUtc(paths.Control) : DateTime.MinValue;
        if (mtime > controlMtime)
        {
            controlMtime = mtime;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(paths.Control));
                SetPaused(doc.RootElement.GetProperty("paused").GetBoolean(), "ダッシュボード");
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Log.Warn("control.json を読めません");
            }
        }
        if (Paused) status.Set("message", "一時停止中(停止キーかダッシュボードで再開)");
        return Paused;
    }
}
