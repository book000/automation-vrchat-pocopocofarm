using System.Text.Json.Nodes;
using Pocofarm.Core;
using Pocofarm.Fishing;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;
using Pocofarm.Web;

namespace Pocofarm.Tests;

/// <summary>補助クラス・ダッシュボード・OCR のテスト。</summary>
public class MoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "pocofarm-test-" + Guid.NewGuid().ToString("N"));

    public MoreTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } GC.SuppressFinalize(this); }

    /// <summary>リポジトリ直下(pocofarm/ と tests/ がある所)を上へたどって探す。</summary>
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    /// <summary>低周波の乱数模様。全体が一様でなく、平行移動すれば位置が分かる画像。</summary>
    static Frame Texture(int w, int h)
    {
        var rng = new Random(0);
        var grid = new double[26, 16];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 26; x++) grid[x, y] = rng.Next(256);
        var f = new Frame(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double gx = x * 25.0 / w, gy = y * 15.0 / h;
                int x0 = (int)gx, y0 = (int)gy;
                double fx = gx - x0, fy = gy - y0;
                double v = grid[x0, y0] * (1 - fx) * (1 - fy) + grid[x0 + 1, y0] * fx * (1 - fy) + grid[x0, y0 + 1] * (1 - fx) * fy + grid[x0 + 1, y0 + 1] * fx * fy;
                f.Set(x, y, (byte)v, (byte)v, (byte)v);
            }
        return f;
    }

    [Fact]
    public void ViewMotion_DetectsShiftNotNoise()
    {
        var vm = new ViewMotion(1.2, 0.7);
        var baseF = Texture(1920, 1080);
        Assert.False(vm.Update(baseF, 0.0));
        var noisy = baseF.Clone();
        var rng = new Random(1);
        for (int i = 0; i < noisy.Data.Length; i += 4)
            for (int c = 0; c < 3; c++) noisy.Data[i + c] = (byte)Math.Clamp(noisy.Data[i + c] + rng.Next(-3, 4), 0, 255);
        Assert.False(vm.Update(noisy, 0.1));                 // 水面のちらつき程度では動き扱いしない
        var shifted = new Frame(1920, 1080);
        for (int y = 0; y < 1080; y++)
            for (int x = 0; x < 1920; x++)
                Buffer.BlockCopy(baseF.Data, (y * 1920 + (x + 1920 - 60) % 1920) * 4, shifted.Data, (y * 1920 + x) * 4, 4);
        Assert.True(vm.Update(shifted, 0.2));                // 視点が動けば検出する
        Assert.True(vm.Update(shifted, 0.5));                // 0.7 秒は動き扱いを保つ
        Assert.False(vm.Update(shifted, 1.5));
    }

    [Fact]
    public void Notify_EscapesQuotesWithoutRunningPowerShell()
    {
        var script = Notify.BuildScript("a'b", "<c>&");
        Assert.Contains("a''b", script);
        Assert.Contains("&lt;c&gt;&amp;", script);
    }

    [Fact]
    public void Evidence_CleanupByTtlAndSize()
    {
        var old = Path.Combine(dir, "old.jpg"); var nw = Path.Combine(dir, "new.jpg");
        File.WriteAllBytes(old, new byte[100]); File.WriteAllBytes(nw, new byte[100]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        Assert.Equal(1, EvidenceStore.CleanupDir(dir, 3600, 10_000));
        Assert.True(File.Exists(nw)); Assert.False(File.Exists(old));
        for (int i = 0; i < 3; i++) { var p = Path.Combine(dir, $"s{i}.jpg"); File.WriteAllBytes(p, new byte[600]); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-3 + i)); }
        Assert.Equal(2, EvidenceStore.CleanupDir(dir, 3600, 1000));       // 古い順に容量上限まで削除される
        Assert.Equal(["new.jpg", "s2.jpg"], Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void EventIndex_ReadsOnlyCompleteLines()
    {
        var path = Path.Combine(dir, "events.jsonl");
        var ts = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
        File.WriteAllText(path, $"{{\"ts\":\"{ts}\",\"type\":\"a\"}}\n{{\"ts\":\"");
        var idx = new EventIndex(path);
        Assert.Single(idx.Refresh());                       // 書きかけの行は次回に回す
        File.AppendAllText(path, $"{ts}\",\"type\":\"b\"}}\n");
        Assert.Equal(["a", "b"], idx.Refresh().Select(e => e.Str("type")));
    }

    [Fact]
    public void BuildState_HourlyExcludesProbeResets()
    {
        var paths = new Paths(dir);
        Directory.CreateDirectory(paths.Data);
        var now = DateTimeOffset.Now;
        var rows = new[] { ("caught", 10), ("caught", 20), ("early", 30), ("no_bite_reset", 40) };
        File.WriteAllText(paths.Events, string.Join("\n", rows.Select(r =>
            $"{{\"ts\":\"{now.AddMinutes(-r.Item2):yyyy-MM-dd'T'HH:mm:ss.fffzzz}\",\"type\":\"attempt\",\"outcome\":\"{r.Item1}\"}}")) + "\n");
        var st = new Dashboard(paths, new Config()).BuildState();
        var hourly = (List<Dictionary<string, object?>>)st["hourly"]!;
        Assert.Equal(3, hourly.Sum(h => (int)h["total"]!));
        Assert.Equal(2, hourly.Sum(h => (int)h["caught"]!));
        var chart = (List<Dictionary<string, object?>>)st["chart"]!;
        Assert.Equal(1, chart.Sum(c => ((Dictionary<string, int>)c["by"]!).GetValueOrDefault("early")));
    }

    [Fact]
    public void Dashboard_RejectsUnsafeEvidenceNames()
    {
        Assert.True(Dashboard.SafeName("20261025_052923_801_caught_3.jpg"));
        Assert.False(Dashboard.SafeName("../config.json"));
        Assert.False(Dashboard.SafeName("a/b.jpg"));
        Assert.False(Dashboard.SafeName("x.exe"));
    }

    [Fact]
    public void PpOcr_ReadsEveryFixtureKind()
    {
        var ocr = new PpOcr();
        var fish = new Config().KnownFish;
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "fixtures", "ocr"), "*.png").Order().ToList();
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var kind = Path.GetFileName(f).Split('_')[0];
            var t = Toast.Classify(ocr.Read(Imaging.Load(f)), fish);
            Assert.True(t is not null && t.Kind == kind, f);
        }
    }

    [Fact]
    public void WinOcr_ReadsEveryFixtureKind()
    {
        Assert.SkipUnless(WinOcr.IsAvailable(), "日本語の Windows OCR 言語パックがない環境ではスキップする");
        var ocr = new WinOcr();
        var fish = new Config().KnownFish;
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "fixtures", "ocr"), "*.png").Order().ToList();
        foreach (var f in files)
        {
            var kind = Path.GetFileName(f).Split('_')[0];
            var t = Toast.Classify(ocr.Read(Imaging.Load(f)), fish);
            Assert.True(t is not null && t.Kind == kind, f);
        }
    }

    [Fact]
    public void Fisher_ConstructsWithDefaultConfig()
    {
        var paths = new Paths(dir);
        var fisher = new Fisher(new Config(), paths, dryRun: true);
        Assert.Equal("starting", fisher.Status.Get("state"));
        Assert.NotNull(fisher.Events);
    }

    [Fact]
    public void DownscaleAveragesBlocks()
    {
        var f = new Pocofarm.Core.Frame(4, 2);
        f.Fill(0, 0, 2, 2, 200, 100, 0);
        f.Fill(2, 0, 4, 2, 0, 0, 100);
        var d = f.Downscale(0.5);
        Assert.Equal((2, 1), (d.Width, d.Height));
        Assert.Equal(200, d.Data[0]);
        Assert.Equal(100, d.Data[6]);
    }

    [Fact]
    public void ConfigApplyValidatesAndReportsRestart()
    {
        var c = new Config();
        var restart = new List<string>();
        Assert.Null(c.Apply(JsonNode.Parse("{\"farm_shot_interval\":300,\"toast_roi\":[0.1,0.3,0.4,0.7],\"pecks_to_reel\":2}")!.AsObject(), restart));
        Assert.Equal(300, c.FarmShotInterval);
        Assert.Equal(2, c.PecksToReel);
        Assert.Equal(["toast_roi"], restart);
        Assert.Null(c.Apply(JsonNode.Parse("{\"pecks_to_reel\":null}")!.AsObject(), []));
        Assert.Equal(1, c.PecksToReel);
        Assert.NotNull(c.Apply(JsonNode.Parse("{\"no_such_key\":1}")!.AsObject(), []));
        Assert.NotNull(c.Apply(JsonNode.Parse("{\"cast_timeout\":-1}")!.AsObject(), []));
        Assert.NotNull(c.Apply(JsonNode.Parse("{\"camera_tab\":[0.5]}")!.AsObject(), []));
        Assert.NotNull(c.Apply(JsonNode.Parse("{\"cast_timeout\":\"x\"}")!.AsObject(), []));
        Assert.Equal(8.0, c.CastTimeout);   // 不正な更新は何も変えない
    }

    [Fact]
    public void ConfigApplyRejectsUpdateSource()
    {
        var c = new Config();
        Assert.NotNull(c.Apply(JsonNode.Parse("{\"update_source\":\"//host/share/x.exe\"}")!.AsObject(), []));
        Assert.Equal("", c.UpdateSource);
    }

    [Fact]
    public void EventLog_SkipsCorruptLinesWhenResumingSequence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pocofarm-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "events.jsonl");
            File.WriteAllText(path, "{\"type\":\"attempt\",\"attempt\":7}\n{\"type\":\"att\n\n");   // 途中で切れた行と空行
            Assert.Equal(8, new EventLog(path).NextAttempt());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
