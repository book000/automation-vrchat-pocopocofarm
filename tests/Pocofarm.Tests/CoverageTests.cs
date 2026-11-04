using System.Text.Json;
using System.Text.Json.Nodes;
using Pocofarm.Core;
using Pocofarm.Fishing;
using Pocofarm.Vision;

namespace Pocofarm.Tests;

/// <summary>ViewMotion・Toast 分類の端・ControlChannel・Config・EventLog の読み取り側・Hud・IconFinder の穴を埋めるテスト(現状の挙動を固定する)。</summary>
public sealed class CoverageTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("pocofarm-cov").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    // ---- ViewMotion ----

    static Frame Texture(int w, int h)
    {
        var f = new Frame(w, h);
        var rng = new Random(7);
        for (int y = 0; y < h; y += 8)
            for (int x = 0; x < w; x += 8) f.Fill(x, y, x + 8, y + 8, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        return f;
    }

    static Frame ShiftedDown(Frame src, int dy)
    {
        var o = new Frame(src.Width, src.Height);
        for (int y = 0; y < src.Height; y++)
            Buffer.BlockCopy(src.Data, ((y + src.Height - dy) % src.Height) * src.Width * 4, o.Data, y * src.Width * 4, src.Width * 4);
        return o;
    }

    [Fact]
    public void ViewMotion_StaticFramesNeverMove()
    {
        var vm = new ViewMotion(1.2, 0.7);
        var f = Texture(1280, 720);
        for (int i = 0; i < 5; i++) Assert.False(vm.Update(f, i * 0.1));
    }

    [Fact]
    public void ViewMotion_DetectsVerticalShift_AndResetForgetsIt()
    {
        var vm = new ViewMotion(1.2, 0.7);
        var f = Texture(1280, 720);
        Assert.False(vm.Update(f, 0.0));
        Assert.True(vm.Update(ShiftedDown(f, 40), 0.1));
        vm.Reset();
        Assert.False(vm.Update(f, 0.2));                     // リセット直後は比較相手が無く、保持時間も消える
    }

    [Fact]
    public void ViewMotion_HoldTimeIsConfigurable()
    {
        var vm = new ViewMotion(1.2, 0.2);
        var f = Texture(1280, 720);
        vm.Update(f, 0.0);
        var g = ShiftedDown(f, 40);
        Assert.True(vm.Update(g, 1.0));
        Assert.True(vm.Update(g, 1.1));
        Assert.False(vm.Update(g, 1.3));
    }

    // ---- Toast ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t ")]
    [InlineData("池を見てクリックで投げる")]
    public void Toast_NoMatchIsNull(string? text) => Assert.Null(Toast.Classify(text));

    [Fact]
    public void Toast_PriorityAndWhitespace()
    {
        // レベルアップは釣果の文言を含んでいても優先される。空白は無視して分類する
        Assert.Equal(Toast.LevelUp, Toast.Classify("フナが釣れた! レベル 3 になった")!.Kind);
        Assert.Equal(Toast.Achieve, Toast.Classify("実績 を 達成")!.Kind);
        Assert.Equal(Toast.Escaped, Toast.Classify("逃 げ ら れ た")!.Kind);
        Assert.True(Toast.Classify("逃げられた")!.Strong);
        Assert.False(Toast.Classify("魚が逃げた")!.Strong);
        Assert.True(Toast.Classify("まだかかっていません")!.Strong);
        Assert.False(Toast.Classify("ぴくぴく沈んでから")!.Strong);
    }

    [Fact]
    public void Toast_CaughtWithoutFish_StillCounts_AndScoreRanksCompleteness()
    {
        var weak = Toast.Classify("経験値 +2")!;
        Assert.Equal(Toast.Caught, weak.Kind);
        Assert.Equal("", weak.Fish);
        Assert.Equal(2, weak.Exp);
        var full = Toast.Classify("フナが釣れた!+2経験値(持っている3匹)", ["フナ"])!;
        Assert.True(full.Strong);
        Assert.Equal(3, full.Count);
        Assert.True(full.Score > weak.Score);
    }

    [Fact]
    public void Toast_ParseInt_AcceptsFullWidthDigits() => Assert.Equal(123, Toast.ParseInt("１２３"));

    // ---- ControlChannel ----

    sealed record Rig(Paths Paths, EventLog Events, Status Status, ControlChannel Channel);

    Rig NewChannel(bool startPaused = false)
    {
        var paths = new Paths(dir);
        Directory.CreateDirectory(paths.Data);
        var events = new EventLog(paths.Events);
        var status = new Status(paths.Status);
        // 実機のキー入力に左右されないよう、押されることのない仮想キー(0)を指定する
        var cfg = new Config { PauseVk = 0, ControlCheckInterval = 0 };
        return new Rig(paths, events, status, new ControlChannel(cfg, paths, events, status, dryRun: true, startPaused));
    }

    static void WriteControl(Paths p, string json, int secondsAhead)
    {
        File.WriteAllText(p.Control, json);
        File.SetLastWriteTimeUtc(p.Control, DateTime.UtcNow.AddSeconds(secondsAhead));
    }

    [Fact]
    public void ControlChannel_StartPaused_AndNoFileMeansNoChange()
    {
        var r = NewChannel(startPaused: true);
        Assert.True(r.Channel.Paused);
        Assert.Equal(true, r.Status.Get("paused"));
        Assert.True(r.Channel.Poll());
        Assert.Equal("一時停止中(停止キーかダッシュボードで再開)", r.Status.Get("message"));
    }

    [Fact]
    public void ControlChannel_PauseAndResumeViaControlFile()
    {
        var r = NewChannel();
        double resumedAfter = -1;
        r.Channel.Resumed += s => resumedAfter = s;
        Assert.False(r.Channel.Poll());

        WriteControl(r.Paths, "{\"paused\": true}", 2);
        Assert.True(r.Channel.Poll());
        Assert.True(r.Channel.LastResumeAt > 0);

        WriteControl(r.Paths, "{\"paused\": false}", 4);
        Assert.False(r.Channel.Poll());
        Assert.True(resumedAfter >= 0);

        var types = EventLog.Read(r.Paths.Events).Select(e => e.Str("type")).ToList();
        Assert.Equal(["pause", "resume"], types);
        Assert.Equal("ダッシュボード", EventLog.Read(r.Paths.Events)[0].Str("source"));
    }

    [Fact]
    public void ControlChannel_StaleControlFileAtStartupIsIgnored_AndBrokenJsonDoesNotThrow()
    {
        var p = new Paths(dir);
        Directory.CreateDirectory(p.Data);
        WriteControl(p, "{\"paused\": true}", -60);
        var r = NewChannel();
        Assert.False(r.Channel.Poll());                      // 起動前の古い要求では止まらない
        WriteControl(r.Paths, "{ not json", 3);
        Assert.False(r.Channel.Poll());                      // 読めなくても例外にせず、状態も変えない
    }

    [Fact]
    public void ControlChannel_ResetTokenArchivesEventsOnce()
    {
        var paths = new Paths(dir);
        Directory.CreateDirectory(paths.Data);
        File.WriteAllText(paths.Events, "{\"ts\":\"x\",\"type\":\"attempt\",\"attempt\":4}\n");
        File.WriteAllText(paths.Reset, "{\"token\": 1}");
        var r = NewChannel();                                // 起動時のトークンは基準になり、リセットは起きない
        int resets = 0;
        r.Channel.DataReset += () => resets++;
        r.Channel.Poll();
        Assert.Equal(0, resets);

        File.WriteAllText(paths.Reset, "{\"token\": 2}");
        r.Channel.Poll();
        r.Channel.Poll();
        Assert.Equal(1, resets);
        Assert.Single(Directory.GetFiles(Path.Combine(paths.Data, "archive"), "events_*.jsonl"));
        Assert.Equal(1, r.Events.NextAttempt());             // 連番も 1 から
    }

    // ---- Config ----

    [Fact]
    public void Config_SerializeAndLoad_RoundTrips()
    {
        var c = new Config { PauseVk = 20, ToastRoi = [0.1, 0.2, 0.3, 0.4] };
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(c, Config.Json));
        var back = Config.Load(path);
        Assert.Equal(20, back.PauseVk);
        Assert.Equal([0.1, 0.2, 0.3, 0.4], back.ToastRoi);
        Assert.Equal(new Config().CaptureFps, back.CaptureFps);
        Assert.Contains("\"pause_vk\"", File.ReadAllText(path));
        Assert.Equal(new Config().PauseVk, Config.Load(Path.Combine(dir, "missing.json")).PauseVk);
    }

    [Fact]
    public void Config_Apply_ValidPatchReportsRestartKeys_AndNullRestoresDefault()
    {
        var c = new Config { PauseVk = 99 };
        var restart = new List<string>();
        Assert.Null(c.Apply(new JsonObject { ["capture_fps"] = 30, ["pause_vk"] = null }, restart));
        Assert.Equal(30, c.CaptureFps);
        Assert.Equal(new Config().PauseVk, c.PauseVk);
        Assert.Equal(["capture_fps"], restart);
    }

    [Theory]
    [InlineData("{\"no_such_key\": 1}", "未知のキー")]
    [InlineData("{\"capture_fps\": -1}", "0 以上")]
    [InlineData("{\"capture_fps\": \"x\"}", "形式")]
    [InlineData("{\"toast_roi\": [1, 2]}", "要素数")]
    [InlineData("{\"update_source\": \"x\"}", "update_source")]
    public void Config_Apply_RejectsInvalidPatchWithoutPartialChange(string patch, string reasonPart)
    {
        var c = new Config();
        int before = c.CaptureFps;
        var patchObj = JsonNode.Parse(patch)!.AsObject();
        patchObj["capture_fps"] ??= before + 1;              // 先頭に有効な項目を足し、不正が混じると全体が捨てられることを見る
        var error = c.Apply(patchObj, []);
        Assert.NotNull(error);
        Assert.Contains(reasonPart, error);
        Assert.Equal(before, c.CaptureFps);
    }

    [Fact]
    public void Config_Describe_ListsEveryKeyWithDefault()
    {
        var rows = new Config { PauseVk = 5 }.Describe();
        var json = JsonSerializer.SerializeToNode(rows)!.AsArray();
        var row = json.Single(n => n!["key"]!.GetValue<string>() == "pause_vk")!;
        Assert.Equal(5, row["value"]!.GetValue<int>());
        Assert.Equal(new Config().PauseVk, row["default"]!.GetValue<int>());
        Assert.True(json.Single(n => n!["key"]!.GetValue<string>() == "capture_fps")!["restart"]!.GetValue<bool>());
    }

    // ---- EventLog (read side) ----

    [Fact]
    public void EventLog_Read_SkipsBrokenLinesAndFiltersBySince()
    {
        var path = Path.Combine(dir, "events.jsonl");
        File.WriteAllText(path,
            "{\"ts\":\"2026-01-01T00:00:00.000+09:00\",\"type\":\"old\"}\n" +
            "garbage\n\n" +
            "[1,2]\n" +
            "{\"ts\":\"2026-06-01T00:00:00.000+09:00\",\"type\":\"new\"}\n" +
            "{\"type\":\"no_ts\"}\n");
        Assert.Equal(["old", "new", "no_ts"], EventLog.Read(path).Select(e => e.Str("type")));
        var since = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeSeconds();
        Assert.Equal(["new"], EventLog.Read(path, since).Select(e => e.Str("type")));
        Assert.Empty(EventLog.Read(Path.Combine(dir, "nope.jsonl")));
    }

    [Fact]
    public void EventLog_Summarize_ExcludesConfirmClicksFromRate()
    {
        JsonObject A(string outcome, string fish = "", int exp = 0) => new() { ["type"] = "attempt", ["outcome"] = outcome, ["fish"] = fish, ["exp"] = exp };
        var s = EventLog.Summarize([A("caught", "フナ", 2), A("caught", "", 4), A("early"), A("no_bite_reset"), new JsonObject { ["type"] = "pause" }]);
        Assert.Equal(3, s["attempts"]);
        Assert.Equal(4, s["all_events"]);
        Assert.Equal(66.7, s["success_rate"]);
        Assert.Equal(6, s["exp"]);
        var fish = Assert.IsType<Dictionary<string, int>>(s["fish"]);
        Assert.Equal(1, fish["不明"]);
        Assert.Null(EventLog.Summarize([])["success_rate"]);
    }

    [Fact]
    public void EventLog_SequenceContinuesAfterReopen_AndIgnoresCorruptLines()
    {
        var path = Path.Combine(dir, "events.jsonl");
        File.WriteAllText(path, "{\"type\":\"attempt\",\"attempt\":7}\nbroken\n{\"type\":\"attempt\",\"attempt\":3}\n");
        var log = new EventLog(path);
        Assert.Equal(8, log.NextAttempt());
        log.Write("attempt", ("attempt", 8), ("outcome", "caught"));
        Assert.Single(log.SessionAttempts);
        Assert.Equal(9, new EventLog(path).NextAttempt());
    }

    // ---- Hud ----

    [Fact]
    public void Hud_ParseLevel_FullWidthAndGarbage()
    {
        Assert.Equal(7, Hud.ParseLevel("ＬＶ７"));
        Assert.Equal(120, Hud.ParseLevel("xx lv120 yy"));
        Assert.Null(Hud.ParseLevel(null));
        Assert.Null(Hud.ParseLevel(""));
    }

    [Fact]
    public void Hud_XpFraction_EmptyRoiIsNull_FullBarIsOne_PartialIsBetween()
    {
        var f = new Frame(200, 20);
        Assert.Null(Hud.XpFraction(f, [0.5, 0.5, 0.5, 0.5]));
        f.Fill(0, 0, 200, 20, 0, 200, 255);                  // BGR で黄色(R=255,G=200)
        Assert.Equal(1.0, Hud.XpFraction(f, [0, 0, 1, 1]));
        var half = new Frame(200, 20);
        half.Fill(0, 0, 100, 20, 0, 200, 255);
        Assert.InRange(Hud.XpFraction(half, [0, 0, 1, 1])!.Value, 0.45, 0.50);
    }

    // ---- IconFinder ----

    [Fact]
    public void IconFinder_BlankFrameOrTinyRegion_FindsNothing()
    {
        var blank = new Frame(640, 480);
        Assert.Null(IconFinder.Find(blank, "ui_tab_camera.png", (0, 0, 1, 1)));
        Assert.Null(IconFinder.Find(blank, "ui_tab_camera.png", (0.5, 0.5, 0.51, 0.51)));
    }

    [Fact]
    public void IconFinder_UnknownTemplateThrows() =>
        Assert.Throws<FileNotFoundException>(() => IconFinder.Find(new Frame(640, 480), "ui_nothing.png", (0, 0, 1, 1)));
}
