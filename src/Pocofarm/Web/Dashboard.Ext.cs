using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pocofarm.Core;
using Pocofarm.Farm;

namespace Pocofarm.Web;

// まとめ・進捗・健康・失敗の整理・ファーム通知・マーカーの組み立て(計算本体は Core/Stats.Summary.cs、Core/Health.cs、Web/Triage.cs、Farm/FarmWatch.cs の純粋関数)
public sealed partial class Dashboard
{
    /// <summary>キャプチャ FPS の標本(ダッシュボードが status を読むたびに足す)。健康判定の FPS 低下の検出に使う。</summary>
    readonly List<double> fpsRing = [];

    /// <summary>グラフに重ねる出来事の対象期間[秒]。</summary>
    const double MarkerSpan = 24 * 3600;

    /// <summary>status の fps を標本に足す。</summary>
    void SampleFps(JsonObject status, bool alive)
    {
        if (!alive || status.Num("fps") is not { } f || f <= 0) return;
        lock (fpsRing)
        {
            fpsRing.Add(f);
            if (fpsRing.Count > Health.BaselineN) fpsRing.RemoveAt(0);
        }
    }

    /// <summary>健康状態を計算して status にも載せる(status.json 自体は釣りプロセスが書くので、ここでは /api/state の status にだけ足す)。</summary>
    Dictionary<string, object?> HealthOf(List<JsonObject> attempts, JsonObject status)
    {
        double[] fps;
        lock (fpsRing) fps = [.. fpsRing];
        var report = Health.Evaluate(attempts, fps);
        Health.Latest = report;
        var json = report.ToJson();
        status["health"] = JsonSerializer.SerializeToNode(json);
        return json;
    }

    /// <summary>畑の通知と状態の推移(farm_scan イベントから)。</summary>
    Dictionary<string, object?> FarmOf(List<JsonObject> events)
    {
        var snaps = FarmWatch.FromEvents(events.Where(e => e.Str("type") == "farm_scan").TakeLast(60));
        var alerts = FarmWatch.Alerts(snaps, Math.Max(1, cfg.FarmAlertScans));
        return new()
        {
            ["alerts"] = alerts.Select(a => new Dictionary<string, object?> { ["kind"] = a.Kind, ["plot"] = a.Plot, ["message"] = a.Message }).ToList(),
            ["timeline"] = FarmWatch.Timeline(snaps), ["last_t"] = snaps.Count > 0 ? snaps[^1].T : null,
            ["note"] = "「完了」は全マス植え済み・水やり済みの意味で、作物が実ったかは判定していません",
        };
    }

    /// <summary>グラフに重ねる出来事(直近 24 時間)。</summary>
    static List<Dictionary<string, object?>> MarkersOf(List<JsonObject> events, double now) => Markers.Of(events, now - MarkerSpan);

    static string ArchiveStampOf(string name) => name.Length >= 24 ? name[7..22] : "";

    /// <summary>[from, to) に重なるイベントを現在の記録と退避した記録から集める(重複する行は 1 件にする)。各イベントに "_t" を足す。</summary>
    public List<JsonObject> LoadRange(double from, double to)
    {
        var files = new List<string>();
        if (Directory.Exists(ArchiveDir))
            foreach (var f in Directory.GetFiles(ArchiveDir, "events_*.jsonl").Order())
            {
                // ファイル名は退避した時刻(=その記録の最後)。from より前に退避したものは範囲に入らない
                if (DateTime.TryParseExact(ArchiveStampOf(Path.GetFileName(f)), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var end)
                    && new DateTimeOffset(end).ToUnixTimeSeconds() < from - 60) continue;
                files.Add(f);
            }
        files.Add(paths.Events);
        var seen = new HashSet<string>();
        var list = new List<JsonObject>();
        foreach (var f in files)
            foreach (var o in EventLog.Read(f))
            {
                if (EventLog.Timestamp(o) is not { } t || t < from || t >= to) continue;
                if (!seen.Add($"{o.Str("ts")}|{o.Str("type")}|{o.Num("attempt")}")) continue;
                o["_t"] = t;
                list.Add(o);
            }
        return [.. list.OrderBy(Tm)];
    }

    /// <summary>
    /// 日次/週次のまとめ。day は yyyy-MM-dd(ローカル日付、省略で今日)、days は期間の日数(1=日次、7=週次)。
    /// 日付が読めないときは null。
    /// </summary>
    public Dictionary<string, object?>? Summary(string? day, int days)
    {
        DateTime d = DateTime.Today;
        if (!string.IsNullOrEmpty(day) && !DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
        days = Math.Clamp(days, 1, 31);
        double from = Stats.DayStart(d), to = Stats.DayStart(d.AddDays(days));
        var evs = LoadRange(from - (to - from), to);
        Stats.InferMissingFish(evs.Where(e => e.Str("type") == "attempt"), cfg.KnownFish);
        var s = Stats.Summarize(evs, from, to, cfg.FishCoins);
        s["day"] = d.ToString("yyyy-MM-dd"); s["days"] = days;
        return s;
    }

    /// <summary>経験値・コインの推移とレベル到達の見込み(直近 8 日分の記録から)。</summary>
    public Dictionary<string, object?> ProgressState()
    {
        double now = EventLog.Now();
        var evs = LoadRange(now - 8 * 86400, now + 1);
        Stats.InferMissingFish(evs.Where(e => e.Str("type") == "attempt"), cfg.KnownFish);
        return Stats.Progress(evs, now, cfg.FishCoins, cfg.LevelGoal);
    }

    /// <summary>直近 hours 時間の失敗を原因別に整理する。</summary>
    public Dictionary<string, object?> TriageState(double hours)
    {
        double since = EventLog.Now() - Math.Clamp(hours, 0.1, 24 * 14) * 3600;
        return Triage.Build(index.Refresh().Where(e => e.Str("type") == "attempt"), since, cfg);
    }
}
