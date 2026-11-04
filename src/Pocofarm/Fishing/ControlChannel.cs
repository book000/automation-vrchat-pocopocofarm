using System.Collections.Concurrent;
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
    bool dryRun;
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

    /// <summary>true の間はクリックを送らない。実行中にダッシュボードから切り替えられる。</summary>
    public bool DryRun => dryRun;

    /// <summary>ドライランが切り替わったとき。</summary>
    public event Action<bool>? DryRunChanged;

    /// <summary>手動操作(再投げ・今すぐ引く・視点を動かす)の受け口。一時停止中・ドライラン中の要求は受け付けない。</summary>
    public ConcurrentQueue<ControlCommand> Commands { get; } = new();

    bool pauseAtSafePoint;
    DateTime? pauseUntil;
    double lastCommandId;

    /// <summary>視点を動かす 1 回の上限[px]。</summary>
    public const int MaxNudgePx = 200;
    public const int DefaultNudgePx = 40;

    /// <summary>
    /// 手動操作を control.json に書く(ダッシュボード用)。paused は書かないので、一時停止の状態は変わらない。
    /// name: recast / reel_now / nudge(arg=left|right|up|down, amount=px) / dry_run(arg=on|off) / pause_after_result / pause_until(arg=HH:mm)。
    /// </summary>
    public static void WriteCommand(Paths paths, string name, string? arg = null, int amount = 0)
    {
        Directory.CreateDirectory(paths.Data);
        AtomicFile.Write(paths.Control, JsonSerializer.SerializeToUtf8Bytes(new { cmd = name, arg, amount, id = EventLog.Now() }));
    }

    /// <summary>
    /// 結果の確定直後など、安全な位置に来たことを知らせる。「次の安全な位置で一時停止」「HH:MM まで停止」の予約があればここで止める。
    /// </summary>
    public void SafePoint()
    {
        if (!pauseAtSafePoint || Paused) return;
        pauseAtSafePoint = false;
        SetPaused(true, "予約された安全な位置");
    }

    /// <summary>HH:mm の次の時刻(今より前なら翌日)。読めなければ null。</summary>
    public static DateTime? NextOccurrence(string? hhmm, DateTime now)
    {
        if (!TimeSpan.TryParseExact(hhmm, "h\\:mm", null, out var t) || t >= TimeSpan.FromDays(1)) return null;
        var d = now.Date + t;
        return d > now ? d : d.AddDays(1);
    }

    void Handle(string name, string? arg, int amount)
    {
        string? reject = null;
        switch (name)
        {
            case "dry_run":
                bool on = arg == "on";
                if (on != dryRun) { dryRun = on; events.Write("dry_run", ("on", on), ("source", "ダッシュボード")); DryRunChanged?.Invoke(on); }
                break;
            case "pause_after_result":
                pauseAtSafePoint = true; pauseUntil = null;
                events.Write("pause_scheduled", ("kind", "after_result"));
                break;
            case "pause_until":
                if (NextOccurrence(arg, DateTime.Now) is { } until)
                {
                    pauseAtSafePoint = true; pauseUntil = until;
                    events.Write("pause_scheduled", ("kind", "until"), ("until", until.ToString("yyyy-MM-dd'T'HH:mm")));
                }
                else reject = "invalid_time";
                break;
            case "recast" or "reel_now" or "nudge":
                if (Paused) reject = "paused";
                else if (dryRun) reject = "dry_run";
                else if (name == "nudge" && arg is not ("left" or "right" or "up" or "down")) reject = "invalid_direction";
                else Commands.Enqueue(new ControlCommand(name, arg, Math.Clamp(amount <= 0 ? DefaultNudgePx : amount, 1, MaxNudgePx)));
                break;
            default:
                reject = "unknown_command";
                break;
        }
        if (reject is not null)
        {
            Log.Warn($"手動操作を受け付けませんでした: {name} ({reject})");
            events.Write("command_rejected", ("cmd", name), ("reason", reject));
        }
    }

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
        if (on) { while (Commands.TryDequeue(out _)) { } }      // 停止前の未処理の手動操作は捨てる
        else pauseUntil = null;
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
                var root = doc.RootElement;
                if (root.TryGetProperty("paused", out var p)) SetPaused(p.GetBoolean(), "ダッシュボード");
                if (root.TryGetProperty("cmd", out var c) && root.TryGetProperty("id", out var idEl) && idEl.GetDouble() > lastCommandId)
                {
                    lastCommandId = idEl.GetDouble();
                    int amount = root.TryGetProperty("amount", out var am) && am.TryGetInt32(out var n) ? n : 0;
                    string? arg = root.TryGetProperty("arg", out var ar) && ar.ValueKind == JsonValueKind.String ? ar.GetString() : null;
                    Handle(c.GetString() ?? "", arg, amount);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Log.Warn("control.json を読めません");
            }
        }
        if (Paused && pauseUntil is { } u && DateTime.Now >= u) SetPaused(false, "予約時刻");
        if (Paused) status.Set("message", "一時停止中(停止キーかダッシュボードで再開)");
        return Paused;
    }
}
