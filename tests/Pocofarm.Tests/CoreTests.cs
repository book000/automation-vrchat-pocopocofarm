using System.Text.Json.Nodes;
using Pocofarm.Core;
using Pocofarm.Vision;

namespace Pocofarm.Tests;

/// <summary>純粋ロジックのテスト。</summary>
public class CoreTests
{
    static readonly string[] Fish = ["ドジョウ", "フナ", "コイ", "ナマズ", "ニジマス", "金色のコイ"];

    [Fact]
    public void Classify_SeaFish()
    {
        var known = new Config().KnownFish;
        foreach (var name in new[] { "サケ", "エビ", "タイ", "ブリ", "ヒラメ", "マグロ" })
        {
            var t = Toast.Classify($"{name}が釣れた!+20経験値(持っている3匹)", known)!;
            Assert.Equal((Toast.Caught, name, 20, 3), (t.Kind, t.Fish, t.Exp, t.Count));
            Assert.True(new Config().FishCoins.ContainsKey(name));
        }
    }

    [Fact]
    public void Classify_CaughtWithOcrNoise()
    {
        var t = Toast.Classify("ドドジョウが釣れた!+2経験値(持っている28匹)", Fish)!;
        Assert.Equal((Toast.Caught, "ドジョウ", 2, 28), (t.Kind, t.Fish, t.Exp, t.Count));
        t = Toast.Classify("ナマズが釣れた!+6経験価(持っている12匹)", Fish)!;
        Assert.Equal((Toast.Caught, "ナマズ", 6), (t.Kind, t.Fish, t.Exp));
        Assert.Equal("金色のコイ", Toast.Classify("金色のコイが釣れた!+30経験値(持っている5匹)", Fish)!.Fish);
    }

    [Fact]
    public void Classify_OtherKinds()
    {
        Assert.Equal(Toast.Early, Toast.Classify("まだかかっていませんウキがびくびく沈んでから引こう")!.Kind);
        Assert.Equal(Toast.Escaped, Toast.Classify("逃げられた…ウキが沈んだら、すぐに引こう")!.Kind);
        Assert.Equal(Toast.Aim, Toast.Classify("池の水に向けて投げよう池から1~5mくらいの所で、竿の先を水へ向けて")!.Kind);
        Assert.Equal(Toast.LevelUp, Toast.Classify("レベル20になった!")!.Kind);
        Assert.Null(Toast.Classify("釣り竿(Lv3)池を見てクリックで投げる"));
        Assert.Null(Toast.Classify(""));
    }

    [Fact]
    public void SeqRatio_MatchesDifflib()
    {
        // difflib.SequenceMatcher(None, "ドドジョウ", "ドジョウ").ratio() == 2*4/9
        Assert.Equal(8.0 / 9, Toast.SeqRatio("ドドジョウ", "ドジョウ"), 9);
        Assert.Equal(1.0, Toast.SeqRatio("", ""));
        Assert.Equal(0.0, Toast.SeqRatio("abc", "xyz"));
    }

    /// <summary>灰色の水面に赤い棒+白い玉を描いた合成画像(倍率 k)。</summary>
    static Frame BobberImage(double k = 1.0, double dy = 0)
    {
        int R(double v) => (int)Math.Round(v);
        var f = new Frame(R(640 * k), R(480 * k));
        f.Fill(0, 0, f.Width, f.Height, 120, 130, 120);
        int x = R(300 * k), y = R((200 + dy) * k);
        f.Fill(x, y, x + R(4 * k) + 1, y + R(14 * k) + 1, 40, 40, 200);
        f.Fill(x - R(4 * k), y + R(13 * k), x + R(8 * k) + 1, y + R(15 * k) + 1, 40, 40, 200);
        int cx = x + R(2 * k), cy = y + R(24 * k), rr = R(8 * k);
        for (int yy = cy - rr; yy <= cy + rr; yy++)
            for (int xx = cx - rr; xx <= cx + rr; xx++)
                if ((xx - cx) * (xx - cx) + (yy - cy) * (yy - cy) <= rr * rr) f.Set(xx, yy, 235, 235, 235);
        return f;
    }

    [Fact]
    public void Detect_BobberAndExclusion()
    {
        var b = BobberDetector.Detect(BobberImage());
        Assert.NotNull(b);
        Assert.InRange(b!.X, 302 - 8, 302 + 8);
        Assert.InRange(b.Y, 197, 203);
        Assert.Null(BobberDetector.Detect(BobberImage(), exclude: [(280, 190, 330, 240)]));
        var blank = new Frame(200, 200);
        blank.Fill(0, 0, 200, 200, 128, 128, 128);
        Assert.Null(BobberDetector.Detect(blank));
    }

    [Fact]
    public void Dip_AnchoredBobberArmsAfterShortStillnessAtLowFps()
    {
        bool Armed(bool anchored)
        {
            var d = new DipDetector(new DipParams()) { Anchored = anchored };
            for (int i = 0; i < 30; i++) d.Update(100 + i * 0.18, i is >= 20 and < 23 ? new Bobber(970, 381, 9, 9, 0, 0) : null);   // フレーム間隔を学習してから 0.36 秒だけ見えて、あとは見えない
            return d.Armed;
        }
        Assert.False(Armed(false));
        Assert.True(Armed(true));
    }

    [Fact]
    public void Detect_NearSearchFindsSameBobber()
    {
        var b = BobberDetector.Detect(BobberImage(), near: (302, 210));
        Assert.NotNull(b);
        Assert.InRange(b!.X, 294, 310);
    }

    [Fact]
    public void DipDetector_ArmsThenFiresOncePerDip()
    {
        var d = new DipDetector(new DipParams());
        double t = 0;
        var still = new Bobber(100, 200, 10, 20, 100, 100);
        for (int i = 0; i < 60; i++) { t += 0.02; d.Update(t, still); }
        Assert.True(d.Armed);
        var dipped = new Bobber(100, 209, 10, 20, 100, 100);
        int fired = 0;
        for (int i = 0; i < 5; i++) { t += 0.02; if (d.Update(t, dipped) is not null) fired++; }
        Assert.Equal(1, fired);
        for (int i = 0; i < 5; i++) { t += 0.02; d.Update(t, still); }
        for (int i = 0; i < 5; i++) { t += 0.02; if (d.Update(t, dipped) is not null) fired++; }
        Assert.Equal(2, fired);
    }

    [Fact]
    public void DipDetector_LostCountsAsDipButNotBeforeArmed()
    {
        var d = new DipDetector(new DipParams());
        Assert.Null(d.Update(0.0, null));
        double t = 0;
        for (int i = 0; i < 60; i++) { t += 0.02; d.Update(t, new Bobber(100, 200, 10, 20, 100, 100)); }
        int dips = Enumerable.Range(1, 11).Count(i => d.Update(t + 0.02 * i, null) == "dip");
        Assert.Equal(1, dips);
    }

    [Theory]
    [InlineData(0.67)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void DetectAndDip_ScaleWithResolution(double k)
    {
        var b = BobberDetector.Detect(BobberImage(k), scale: k);
        Assert.NotNull(b);
        Assert.True(Math.Abs(b!.X - 302 * k) <= 8 * k);
        var d = new DipDetector(new DipParams(DipMinPx: 4.0 * k, SettleRangePx: 4.0 * k));
        double t = 0;
        int w = (int)Math.Round(10 * k);
        for (int i = 0; i < 60; i++) { t += 0.02; d.Update(t, new Bobber(100, 200, w, 20, 100, 100)); }
        var fired = Enumerable.Range(1, 5).Select(i => d.Update(t + 0.02 * i, new Bobber(100, 200 + (int)Math.Round(8 * k), w, 20, 100, 100)));
        Assert.Contains("dip", fired);
    }

    [Fact]
    public void Detect_RequiresScaleOnLargeImage()
    {
        Assert.Null(BobberDetector.Detect(BobberImage(3.0)));
        Assert.NotNull(BobberDetector.Detect(BobberImage(3.0), scale: 3.0));
    }

    [Fact]
    public void Layout_MatchesReferenceAndFollowsAspect()
    {
        var c = new Config();
        var rf = Layout.Resolve(c, 1920, 1009);
        Assert.All(rf.Toast.Zip(c.ToastRoi), p => Assert.Equal(p.Second, p.First, 6));
        Assert.All(rf.Exclude[0].Zip(c.ExcludeRois[0]), p => Assert.Equal(p.Second, p.First, 6));
        var sq = Layout.Resolve(c, 1280, 953);                 // 5:4 の実測: Lv 表示は x=431..502px
        Assert.True(sq.Level[0] * 1280 < 431 && sq.Level[2] * 1280 > 502);
        Assert.True(sq.Exclude[0][0] * 1280 < 880);            // アバター窓(左端 880px)を覆う
        var big = Layout.Resolve(c, 3840, 2054);               // 4K の実測: 枠は 962..1238px
        Assert.InRange(big.Level[0] * 3840, 962 - 6, 962 + 6);
        Assert.InRange(big.Level[2] * 3840, 1238 - 6, 1238 + 6);
        var tall = Layout.Resolve(c, 3840, 2018);
        Assert.Equal(rf.Level[1], tall.Level[1]);              // 縦位置は高さ比のまま
    }

    [Fact]
    public void Hud_ParseLevelAndXpBar()
    {
        Assert.Equal(22, Hud.ParseLevel("LV22"));
        Assert.Equal(5, Hud.ParseLevel("Lv 5"));
        Assert.Null(Hud.ParseLevel("釣り竿"));
        var img = new Frame(1000, 1000);
        img.Fill(0, 0, 1000, 1000, 60, 60, 60);
        double[] roi = [0.2, 0.8, 0.4, 0.82];
        img.Fill(200, 800, 300, 820, 40, 200, 240);            // BGR の黄色で左半分を塗る
        Assert.InRange(Hud.XpFraction(img, roi)!.Value, 0.471 - 0.02, 0.471 + 0.02);
        var blank = new Frame(1000, 1000);
        blank.Fill(0, 0, 1000, 1000, 60, 60, 60);
        Assert.Equal(0.0, Hud.XpFraction(blank, roi));
    }

    static JsonObject E(double now, double ago, string outcome, Action<JsonObject>? extra = null)
    {
        var o = new JsonObject { ["_t"] = now - ago, ["outcome"] = outcome };
        extra?.Invoke(o);
        return o;
    }

    [Fact]
    public void Stats_Compute()
    {
        const double now = 10_000;
        var ev = new List<JsonObject>
        {
            E(now, 300, "early", o => o["reaction_ms"] = 50),
            E(now, 200, "caught", o => { o["fish"] = "コイ"; o["exp"] = 4; o["count"] = 7; o["reaction_ms"] = 40; o["result_ms"] = 90; o["wait_s"] = 8.0; o["engine"] = "winocr"; o["pos"] = new JsonArray(975, 455); o["bobber_w"] = 12; }),
            E(now, 100, "no_bite_reset"),
            E(now, 50, "caught", o => { o["fish"] = "金色のコイ"; o["exp"] = 30; o["count"] = 1; o["reaction_ms"] = 60; o["result_ms"] = 110; o["wait_s"] = 22.0; o["engine"] = "rapidocr"; o["pos"] = new JsonArray(980, 460); o["bobber_w"] = 12; }),
        };
        var s = Stats.Compute(ev, now, new Dictionary<string, int> { ["コイ"] = 12, ["金色のコイ"] = 150 });
        Assert.Equal(162, s["coins_total"]);
        Assert.Equal(162, s["coins_per_hour"]);
        Assert.Equal(2, s["streak"]);                          // 確認クリックは連続成功を切らない
        Assert.Equal(300, s["since_failure_s"]);
        Assert.Equal(60.0, ((Dictionary<string, object?>)s["reaction"]!)["max"]);
        Assert.Equal(2, ((Dictionary<string, object?>)s["wait"]!)["n"]);
        Assert.Equal(34, s["exp_per_hour"]);
        Assert.Equal(new Dictionary<string, int> { ["コイ"] = 7, ["金色のコイ"] = 1 }, s["inventory"]);
        var fish = (List<Dictionary<string, object?>>)s["fish"]!;
        Assert.Equal(150, fish[1]["coins"]);
        Assert.Equal(2.0, fish[0]["every"]);
        var places = (List<Dictionary<string, object?>>)s["places"]!;
        Assert.Equal(2, places[0]["n"]);                       // 近い位置は同じ場所にまとめる
        Assert.Equal(100.0, places[0]["rate"]);
        Assert.Null(Stats.Percentile([], 50));
        Assert.Equal(0, Stats.Compute([], now)["streak"]);
    }

    [Fact]
    public void NextCatch_ProbabilitiesAndIndependence()
    {
        var rng = new Random(1);
        string Pick() { int r = rng.Next(100); return r < 70 ? "フナ" : r < 98 ? "コイ" : "金色のコイ"; }
        var caught = Enumerable.Range(0, 400).Select(_ => new JsonObject { ["fish"] = Pick() }).ToList();
        var price = new Dictionary<string, int> { ["フナ"] = 3, ["コイ"] = 12, ["金色のコイ"] = 150 };
        var r = Stats.NextCatch(caught, price);
        var probs = (List<Dictionary<string, object?>>)r["probs"]!;
        Assert.Equal("フナ", probs[0]["fish"]);
        Assert.InRange(probs.Sum(p => (double)p["p"]!), 99.5, 100.5);
        Assert.True((bool)r["independent"]!);
        var alt = Enumerable.Range(0, 200).Select(i => new JsonObject { ["fish"] = i % 2 == 0 ? "フナ" : "コイ" }).ToList();
        Assert.False((bool)Stats.NextCatch(alt, new Dictionary<string, int>())["independent"]!);
        var (lo, hi) = Stats.Wilson(2, 400);
        Assert.True(0 < lo && lo < 0.005 && 0.005 < hi && hi < 0.02);
        Assert.Equal((0.0, 1.0), Stats.Wilson(0, 0));
    }

    [Fact]
    public void EventLog_WritesJsonlLinesAndResets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pocofarm-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "events.jsonl");
            var log = new EventLog(path);
            Assert.Equal(1, log.NextAttempt());
            log.Write("attempt", ("attempt", 1), ("outcome", "caught"), ("fish", "コイ"), ("exp", 4));
            var line = File.ReadAllText(path);
            Assert.Contains("\"fish\":\"コイ\"", line);       // 日本語はエスケープしない
            Assert.Matches(@"""ts"":""\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}[+-]\d\d:\d\d""", line);
            var ev = EventLog.Read(path);
            var sum = EventLog.Summarize(ev);
            Assert.Equal(1, sum["caught"]);
            Assert.Equal(4, sum["exp"]);
            Assert.Equal(1, new EventLog(path).NextAttempt() - 1);     // 再起動後も連番を維持
            var dest = log.Reset();
            Assert.NotNull(dest);
            Assert.True(File.Exists(dest));
            Assert.False(File.Exists(path));
            Assert.Equal(1, log.NextAttempt());
            Assert.Null(log.Reset());                                  // 記録が無ければ何も退避しない
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Config_LoadsConfigJson()
    {
        var p = Path.Combine(Path.GetTempPath(), "pocofarm-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, "{\"pause_vk\": 19, \"toast_roi\": [0.1,0.2,0.3,0.4], \"unknown\": 1}");
        try
        {
            var c = Config.Load(p);
            Assert.Equal(19, c.PauseVk);
            Assert.Equal([0.1, 0.2, 0.3, 0.4], c.ToastRoi);
            Assert.Equal(400, c.FishCoins["イトウ"]);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void ExtractNeko_ReadsCatMessageOnlyWhenLabelled()
    {
        Assert.Equal("すいかを植える場所が無くなりました", Toast.ExtractNeko("ねこちゃん: すいかを植える場所が無くなりました"));
        Assert.Equal("すいかを植える場所が無くなりました", Toast.ExtractNeko("ねこちゃん:すいかを植える場所が無くなりました"));
        Assert.Null(Toast.ExtractNeko("フナが釣れた!+2経験値(持っている135匹)"));
        Assert.Null(Toast.ExtractNeko(""));
        Assert.Null(Toast.ExtractNeko("ねこちゃん:"));
        Assert.Equal("ねこちゃんにすいかの種袋をわたしました", Toast.ExtractNeko("ねこちゃんにすいかの種袋をわたしました"));
        Assert.Null(Toast.ExtractNeko("お手伝いさん(ねこちゃん)"));
        Assert.Null(Toast.ExtractNeko("ムいさん(ねこちゃん)つよくするー.てもらう什事(押すと「する」、一種袋をいってねこちゃんをクリンクーると"));
    }

    [Fact]
    public void HudLocator_ReproducesReferenceRoisFromLvTextPosition()
    {
        // 基準画面(1920x1009)で「Lv 29」を読んだ位置(520,843 幅75 高さ21)から、既定のレベル・経験値バー領域に戻る
        var (level, xp) = Pocofarm.Ocr.HudLocator.Apply((520, 843, 75, 21), 1920, 1009);
        var cfg = new Config();
        for (int k = 0; k < 4; k++)
        {
            Assert.InRange(level[k], cfg.LevelRoi[k] - 0.003, cfg.LevelRoi[k] + 0.003);
            Assert.InRange(xp[k], cfg.XpBarRoi[k] - 0.003, cfg.XpBarRoi[k] + 0.003);
        }
        // 画面が 2 倍なら、文字も 2 倍の位置・大きさで見つかり、同じ比率に戻る
        var (level2, _) = Pocofarm.Ocr.HudLocator.Apply((1040, 1686, 150, 42), 3840, 2018);
        Assert.InRange(level2[0], level[0] - 0.002, level[0] + 0.002);
    }

    [Fact]
    public void Classify_RestoresMisreadFishNameFromExp()
    {
        // コイ(+4XP)の「コ」が「ロ」に読み違えられても、経験値から コイ に戻す
        Assert.Equal("コイ", Toast.Classify("ロイが釣れた!+4経験値(持っている116匹)", Fish)!.Fish);
        // 経験値が同じ魚が複数いて名前も遠いときは、読んだまま
        Assert.Equal("ゼゼ", Toast.Classify("ゼゼが釣れた!+2経験値(持っている3匹)", Fish)!.Fish);
        // 未知の経験値は触らない
        Assert.Equal("ロイ", Toast.Classify("ロイが釣れた!+99経験値(持っている3匹)", Fish)!.Fish);
    }

    [Fact]
    public void InferMissingFish_FillsUnknownFromInventoryJump()
    {
        System.Text.Json.Nodes.JsonObject C(string fish, int? count) => new() { ["type"] = "attempt", ["outcome"] = "caught", ["fish"] = fish, ["count"] = count };
        var rows = new List<System.Text.Json.Nodes.JsonObject> { C("フナ", 10), C("コイ", 5), C("", null), C("フナ", 11), C("コイ", 7), C("フナ", 12) };
        Stats.InferMissingFish(rows);
        Assert.Equal("コイ", rows[2].Str("fish"));
        Assert.True(rows[2]["fish_inferred"] is not null);
        // 飛びが無ければ(不明が解決できなければ)読めないまま
        var open = new List<System.Text.Json.Nodes.JsonObject> { C("フナ", 10), C("", null), C("フナ", 11) };
        Stats.InferMissingFish(open);
        Assert.Equal("", open[1].Str("fish"));
    }

    [Fact]
    public void BoxDownscale_AveragesBlocksAndReusesBuffer()
    {
        var f = new Frame(4, 4);
        f.Fill(0, 0, 2, 2, 100, 100, 100);
        f.Fill(2, 0, 4, 2, 200, 200, 200);
        var o = f.BoxDownscale(2);
        Assert.Equal((2, 2), (o.Width, o.Height));
        Assert.Equal(100, o.Data[0]);
        Assert.Equal(200, o.Data[4]);
        Assert.Same(o, f.BoxDownscale(2, o));
    }
}
