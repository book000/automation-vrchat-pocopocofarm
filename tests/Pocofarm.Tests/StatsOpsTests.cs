using System.Globalization;
using System.Text.Json.Nodes;
using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Web;

namespace Pocofarm.Tests;

/// <summary>まとめ・健康・進捗・失敗の整理・ファーム通知・マーカーの純粋ロジック。</summary>
public class StatsOpsTests
{
    static readonly DateTime Day = new(2026, 11, 4, 0, 0, 0, DateTimeKind.Local);
    static double At(double hours) => Stats.DayStart(Day) + hours * 3600;

    static string Iso(double t) => DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    static JsonObject Ev(double t, string type, Action<JsonObject>? f = null)
    {
        var o = new JsonObject { ["ts"] = Iso(t), ["type"] = type, ["_t"] = t };
        f?.Invoke(o);
        return o;
    }

    static JsonObject Attempt(double t, string outcome, string fish = "", int exp = 0, int reaction = 10, string engine = "rapidocr") =>
        Ev(t, "attempt", o => { o["outcome"] = outcome; o["fish"] = fish; o["exp"] = exp; o["reaction_ms"] = reaction; o["engine"] = engine; });

    // ---- 日次/週次のまとめ ----

    [Fact]
    public void Summarize_CountsRatesAgainstPreviousPeriod()
    {
        var evs = new List<JsonObject>();
        for (int i = 0; i < 10; i++) evs.Add(Attempt(At(1) + i * 10, i < 8 ? "caught" : "early", "フナ", 5));      // 当日 80%
        for (int i = 0; i < 10; i++) evs.Add(Attempt(At(-23) + i * 10, i < 5 ? "caught" : "missed_bite", "フナ", 5));   // 前日 50%
        evs.Add(Attempt(At(2), "no_bite_reset"));
        var s = Stats.Summarize(evs, At(0), At(24), new Dictionary<string, int> { ["フナ"] = 10 });
        Assert.Equal(10, s["attempts"]);
        Assert.Equal(8, s["caught"]);
        Assert.Equal(80.0, s["success_rate"]);
        Assert.Equal(30.0, s["success_delta"]);
        Assert.Equal(40, s["exp"]);
        Assert.Equal(80, s["coins_est"]);
        Assert.Contains("見積", (string)s["coins_note"]!);
    }

    [Fact]
    public void Summarize_TopFishRareLevelDowntimeAndFarm()
    {
        var evs = new List<JsonObject>();
        for (int i = 0; i < 30; i++) evs.Add(Attempt(At(1) + i * 20, "caught", i == 0 ? "金色のコイ" : "フナ", 5));
        evs.Add(Ev(At(1), "hud", o => { o["level"] = 40; o["xp"] = 0.5; }));
        evs.Add(Ev(At(5), "hud", o => { o["level"] = 41; o["xp"] = 0.1; }));
        evs.Add(Ev(At(6), "pause")); evs.Add(Ev(At(6.5), "resume"));            // 停止 1800 秒
        evs.Add(Attempt(At(7), "caught", "フナ", 5));                         // 直前の釣果から 6 時間以上空く(停止中の分は除く)
        evs.Add(Ev(At(8), "gap", o => o["seconds"] = 3.0));
        evs.Add(Ev(At(9), "session_start"));
        evs.Add(Ev(At(10), "farm_scan", o => { o["plots"] = 8; o["unplanted"] = 3; o["dry"] = 4; o["done"] = 2; o["hidden"] = 1; }));
        var s = Stats.Summarize(evs, At(0), At(24));
        var top = (List<Dictionary<string, object?>>)s["top_fish"]!;
        Assert.Equal("フナ", top[0]["fish"]);
        var rare = (List<Dictionary<string, object?>>)s["rare"]!;
        Assert.Equal("金色のコイ", Assert.Single(rare)["fish"]);
        var lv = (Dictionary<string, object?>)s["level"]!;
        Assert.Equal(1.0, lv["gained"]);
        var dt = (Dictionary<string, object?>)s["downtime"]!;
        Assert.Equal(1800, dt["paused_s"]);
        Assert.Equal(3, dt["capture_gap_s"]);
        Assert.Equal(1, dt["restarts"]);
        Assert.True((int)dt["offline_s"]! > 3600);
        var farm = (Dictionary<string, object?>)s["farm"]!;
        Assert.Equal(3.0, farm["unplanted"]);
    }

    [Fact]
    public void Summarize_EmptyPeriod_HasNullRate()
    {
        var s = Stats.Summarize([], At(0), At(24));
        Assert.Equal(0, s["attempts"]);
        Assert.Null(s["success_rate"]);
        Assert.Null(s["success_delta"]);
        Assert.Null(s["farm"]);
    }

    [Fact]
    public void Summarize_DayBoundaryUsesLocalMidnight()
    {
        var evs = new List<JsonObject> { Attempt(At(0) - 1, "caught", "フナ"), Attempt(At(0) + 1, "caught", "フナ"), Attempt(At(24) + 1, "caught", "フナ") };
        Assert.Equal(1, Stats.Summarize(evs, At(0), At(24))["attempts"]);
    }

    // ---- 健康 ----

    static List<JsonObject> Series(int n, Func<int, string> outcome, Func<int, int>? reaction = null, Func<int, string>? engine = null)
    {
        var l = new List<JsonObject>();
        for (int i = 0; i < n; i++) l.Add(Attempt(At(0) + i * 10, outcome(i), "フナ", 5, reaction?.Invoke(i) ?? 10, engine?.Invoke(i) ?? "rapidocr"));
        return l;
    }

    [Fact]
    public void Health_GoodPeriod_IsOk()
    {
        // 記録の良好な時間帯相当: 成功 98%、反応 p95 は 13ms 前後、結果不明・見逃しは時々
        var r = Health.Evaluate(Series(400, i => i % 50 == 0 ? "missed_bite" : "caught", i => 8 + i % 7));
        Assert.Equal("ok", r.Level);
        Assert.Empty(r.Causes);
    }

    [Fact]
    public void Health_ChronicallyBadPeriod_IsFlaggedEvenWhenBaselineIsBad()
    {
        // ずっと見逃し 60%(基準も悪い)でも絶対値で鳴る
        var r = Health.Evaluate(Series(400, i => i % 5 < 3 ? "missed_bite" : "caught"));
        Assert.Equal("bad", r.Level);
        Assert.Contains(r.Causes, c => c.Key == "missed_bite");
    }

    [Fact]
    public void Health_SuddenUnknownAndSlowReaction_NamesTheCauses()
    {
        var s = Series(300, _ => "caught");
        s.AddRange(Series(30, i => i % 3 == 0 ? "unknown" : "caught", _ => 90, i => i % 3 == 0 ? "" : "rapidocr"));
        var r = Health.Evaluate(s);
        Assert.Contains(r.Causes, c => c.Key == "reaction");
        Assert.Contains(r.Causes, c => c.Key == "unknown");
        Assert.Equal("bad", r.Level);
    }

    [Fact]
    public void Health_FpsDrop_IsReportedAndFewAttemptsAreUnknown()
    {
        var fps = Enumerable.Repeat(45.0, 270).Concat(Enumerable.Repeat(10.0, 30)).ToList();
        var r = Health.Evaluate(Series(100, _ => "caught"), fps);
        Assert.Equal("warn", r.Level);
        Assert.Equal("fps", Assert.Single(r.Causes).Key);
        Assert.Equal("unknown", Health.Evaluate(Series(10, _ => "caught")).Level);
    }

    // ---- 進捗 ----

    [Fact]
    public void Progress_EstimatesExpPerLevelFromLevelUps()
    {
        var evs = new List<JsonObject>();
        // Lv10→11→12 の間にそれぞれ 100 経験値(20 匹 × 5)
        evs.Add(Ev(At(0), "hud", o => { o["level"] = 10; o["xp"] = 0.0; }));
        for (int i = 0; i < 20; i++) evs.Add(Attempt(At(0.1) + i, "caught", "フナ", 5));
        evs.Add(Ev(At(1), "hud", o => { o["level"] = 11; o["xp"] = 0.0; }));
        for (int i = 0; i < 20; i++) evs.Add(Attempt(At(1.1) + i, "caught", "フナ", 5));
        evs.Add(Ev(At(2), "hud", o => { o["level"] = 12; o["xp"] = 0.5; }));
        double now = At(2.5);
        for (int i = 0; i < 10; i++) evs.Add(Attempt(now - 600 + i, "caught", "フナ", 5));      // 直近 1 時間 50 経験値
        var p = Stats.Progress(evs, now, new Dictionary<string, int> { ["フナ"] = 10 }, 14);
        Assert.Equal(100.0, p["exp_per_level"]);
        Assert.Equal("level_ups", p["exp_per_level_source"]);
        Assert.Equal(50.0, p["exp_per_hour"]);
        var eta = (Dictionary<string, object?>)p["eta"]!;
        Assert.Equal(13, eta["next_level"]);
        Assert.Equal(1.0, eta["hours"]);            // 残り 50% × 100 / 50 経験値毎時
        Assert.Equal(3.0, eta["goal_hours"]);       // (残り 50 + Lv13→14 の 100) / 50 経験値毎時
    }

    [Fact]
    public void Progress_DailyTrendHasSevenDays()
    {
        var p = Stats.Progress([Attempt(At(1), "caught", "フナ", 5)], At(2));
        var daily = (List<Dictionary<string, object?>>)p["daily"]!;
        Assert.Equal(7, daily.Count);
        Assert.Equal(5, daily[^1]["exp"]);
    }

    // ---- 失敗の整理 ----

    [Fact]
    public void Triage_GroupsByOutcomeWithEvidenceAndAdvice()
    {
        var a = new List<JsonObject>
        {
            Attempt(At(1), "missed_bite"), Attempt(At(2), "missed_bite"), Attempt(At(3), "unknown", engine: ""), Attempt(At(4), "caught"), Attempt(At(5), "no_bite_reset"),
        };
        a[1]["evidence"] = "r.jpg"; a[1]["evidence_click"] = "c.jpg";
        a[1]["trace"] = new JsonArray(new JsonArray(1.0, 100, 10), new JsonArray(1.1, 112, 10));
        var t = Triage.Build(a, 0, new Config());
        Assert.Equal(3, t["total"]);
        var groups = (List<Dictionary<string, object?>>)t["groups"]!;
        Assert.Equal("missed_bite", groups[0]["outcome"]);
        Assert.NotEmpty((string[])groups[0]["advice"]!);
        var ex = (List<Dictionary<string, object?>>)groups[0]["examples"]!;
        Assert.Equal("r.jpg", ex[0]["evidence"]);
        Assert.Equal(12.0, ex[0]["trace_range_px"]);
        Assert.Contains(((List<string>)groups[1]["hints"]!), h => h.Contains("OCR"));
    }

    // ---- ファーム ----

    static FarmSnapshot Snap(double t, params string[] plots) =>
        new(t, plots, plots.Select(p => p.Count(c => c is 'e' or 'b')).ToArray(), 0, 0, 0);

    const string Full = "wwwwwwwwwwww", Empty = "eeeeeeeeeeee", Dry = "pppppppppppp";

    [Fact]
    public void FarmWatch_AlertsOnlyAfterConsecutiveScans()
    {
        var one = new[] { Snap(1, Empty, Full) };
        Assert.DoesNotContain(FarmWatch.Alerts(one), a => a.Kind == "unplanted");
        var two = new[] { Snap(1, Empty, Dry), Snap(2, Empty, Dry) };
        var al = FarmWatch.Alerts(two);
        Assert.Contains(al, a => a is { Kind: "unplanted", Plot: 0 });
        Assert.Contains(al, a => a is { Kind: "dry", Plot: 1 });
        // 間に植え済みを挟めば連続ではない
        Assert.DoesNotContain(FarmWatch.Alerts([Snap(1, Empty), Snap(2, Full), Snap(3, Empty)]), a => a.Kind == "unplanted");
    }

    [Fact]
    public void FarmWatch_CompleteMeansAllPlantedAndWatered_AndObserveDoesNotRepeat()
    {
        Assert.Contains(FarmWatch.Alerts([Snap(1, Full)]), a => a.Kind == "complete");
        Assert.DoesNotContain(FarmWatch.Alerts([Snap(1, "wwwwwwwwwww?")]), a => a.Kind == "complete");
        var w = new FarmWatch();
        Assert.Empty(w.Observe(Snap(1, Empty)));
        Assert.Single(w.Observe(Snap(2, Empty)));
        Assert.Empty(w.Observe(Snap(3, Empty)));
        Assert.Single(w.Observe(Snap(4, Full)));        // 完了は 1 回だけ
        Assert.Empty(w.Observe(Snap(5, Full)));
    }

    [Fact]
    public void FarmWatch_FromEventsAndTimeline()
    {
        var e1 = Ev(At(1), "farm_scan", o => { o["plots"] = 2; o["dry"] = 0; o["done"] = 0; o["hidden"] = 0; o["unplanted_by_plot"] = new JsonArray(12, 0); o["cells"] = new JsonArray(Empty, Full); });
        var old = Ev(At(2), "farm_scan", o => { o["plots"] = 2; o["unplanted_by_plot"] = new JsonArray(0, 3); });      // cells の無い古い記録
        var none = Ev(At(3), "farm_scan", o => o["plots"] = 0);
        var snaps = FarmWatch.FromEvents([e1, old, none]);
        Assert.Equal(2, snaps.Count);
        var tl = FarmWatch.Timeline(snaps);
        var plots = (List<Dictionary<string, object?>>)tl["plots"]!;
        Assert.Equal("U?", plots[0]["states"]);
        Assert.Equal("RU", plots[1]["states"]);
    }

    // ---- マーカー ----

    [Fact]
    public void Markers_PickConfigPauseRestartResetAndSkipsOldOnes()
    {
        var evs = new List<JsonObject>
        {
            Ev(At(1), "config_change", o => o["keys"] = "dip_ratio"), Ev(At(2), "pause", o => o["source"] = "停止キー"), Ev(At(3), "resume"),
            Ev(At(4), "session_start"), Ev(At(5), "session_start", o => o["note"] = "data_reset"), Ev(At(6), "self_update", o => o["ok"] = true),
            Ev(At(7), "tune", o => { o["what"] = "reel_to_cast"; o["from"] = 2; o["to"] = 1.9; }), Ev(At(8), "hud_calibrated"),
            Ev(At(9), "farm_calibration", o => o["moved"] = false), Ev(At(10), "attempt"), Ev(At(-5), "pause"),
        };
        var m = Markers.Of(evs, At(0));
        Assert.Equal(["config", "pause", "resume", "restart", "reset", "update", "tune", "hud"], m.Select(x => (string)x["kind"]!));
    }

    [Fact]
    public void EventLogNote_AppendsReadableEvent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pocofarm-ops-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "events.jsonl");
            EventLog.Note(path, "config_change", ("keys", "a, b"));
            var e = Assert.Single(EventLog.Read(path));
            Assert.Equal("config_change", e.Str("type"));
            Assert.Equal("a, b", e.Str("keys"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
