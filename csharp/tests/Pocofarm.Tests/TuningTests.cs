using Pocofarm.Core;
using Pocofarm.Fishing;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Tests;

/// <summary>自動調整・古い exe の掃除・ウキの横ずれ棄却のテスト。</summary>
public class TuningTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("pocofarm-tune").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    Config Cfg() => new() { ReelToCastMin = 2.0, ReelToCastFloor = 1.2, ReelToCastStep = 0.1, TuneSuccessStreak = 3, TuneMargin = 0.1 };

    [Fact]
    public void Tuner_ShrinksAfterSuccessStreakDownToFloor()
    {
        var t = new AutoTuner(Cfg(), Path.Combine(dir, "t.json"));
        Assert.Equal(2.0, t.ReelToCast);
        for (int i = 0; i < 3; i++) t.OnCastSucceeded(2.0);
        Assert.Equal(1.9, t.ReelToCast, 3);
        for (int i = 0; i < 100; i++) t.OnCastSucceeded(t.ReelToCast);
        Assert.Equal(1.2, t.ReelToCast, 3);
    }

    [Fact]
    public void Tuner_FailureRaisesAndSetsLowerBound()
    {
        var path = Path.Combine(dir, "t.json");
        var t = new AutoTuner(Cfg(), path);
        for (int i = 0; i < 30; i++) t.OnCastSucceeded(t.ReelToCast);
        t.OnCastFailed(t.ReelToCast);   // 床(1.2)まで縮んだところで不発
        Assert.Equal(1.3, t.ReelToCast, 3);
        for (int i = 0; i < 100; i++) t.OnCastSucceeded(t.ReelToCast);
        Assert.Equal(1.3, t.ReelToCast, 3);
        Assert.Equal(1.3, new AutoTuner(Cfg(), path).ReelToCast, 3);   // 再起動後も引き継ぐ
    }

    [Fact]
    public void Tuner_FailureAtUpperLimitDoesNotThrow()
    {
        var t = new AutoTuner(Cfg(), Path.Combine(dir, "t.json"));
        t.OnCastFailed(t.ReelToCast);   // 上限のまま不発 → 下限が上限を超えても例外にしない
        Assert.Equal(2.0, t.ReelToCast, 3);
    }

    [Fact]
    public void Tuner_FailureFarFromLockIsNotLearned()
    {
        var t = new AutoTuner(Cfg(), Path.Combine(dir, "t.json"));
        for (int i = 0; i < 3; i++) t.OnCastSucceeded(2.0);   // 1.9 まで縮む
        t.OnCastFailed(30.0);   // 視線のずれなど待ち時間のせいではない不発
        Assert.Equal(1.9, t.ReelToCast, 3);
    }

    [Theory]
    [InlineData("MyProfileTOmachi基本無第勢ですが、たまにり一STATUSAskMeMllk-Re-G", "nameplate")]
    [InlineData("おっかい屋さんから肉が届きました!かばんの『材料↓に入っています", "delivery")]
    [InlineData("じゃがいものタネを5個買いました", "farm")]
    [InlineData("PHO0NON・NON", "camera_ui")]
    [InlineData("ノ4第プみ等都", null)]
    public void ToastNoise_Categorizes(string text, string? expected) => Assert.Equal(expected, Pocofarm.Fishing.ToastNoise.Category(text));

    [Fact]
    public void InferMissingFish_TreatsUnknownNameAsMissing()
    {
        var rows = new List<System.Text.Json.Nodes.JsonObject>
        {
            new() { ["outcome"] = "caught", ["fish"] = "フナ", ["count"] = 1 },
            new() { ["outcome"] = "caught", ["fish"] = "?ーカ", ["count"] = 2 },
            new() { ["outcome"] = "caught", ["fish"] = "フナ", ["count"] = 3 },
        };
        Pocofarm.Core.Stats.InferMissingFish(rows, new[] { "フナ", "コイ" });
        Assert.Equal("フナ", rows[1]["fish"]!.GetValue<string>());
    }

    [Fact]
    public void Tuner_DisabledUsesConfigValue()
    {
        var c = Cfg(); c.TuneReelToCast = false;
        var t = new AutoTuner(c, Path.Combine(dir, "t.json"));
        for (int i = 0; i < 50; i++) t.OnCastSucceeded(2.0);
        Assert.Equal(2.0, t.ReelToCast);
    }

    [Fact]
    public void Cleanup_RemovesLeftoversButNotLiveFiles()
    {
        var self = Path.Combine(dir, "pocofarm.exe");
        foreach (var n in new[] { "pocofarm.exe", "pocofarm.new.exe", "pocofarm.exe.old", "pocofarm.exe.20260101000000.old", "pocofarm.new.exe.failed", "other.txt" })
            File.WriteAllText(Path.Combine(dir, n), "x");
        File.WriteAllText(Path.Combine(dir, "pocofarm.new.exe.tmp"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "pocofarm.new.exe.tmp"), DateTime.UtcNow.AddHours(-2));
        Assert.Equal(4, SelfUpdate.CleanupLeftovers(self));
        Assert.Equal(["other.txt", "pocofarm.exe", "pocofarm.new.exe"], Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public void Dip_LateralJumpOfArmedBobberIsIgnored()
    {
        var d = new DipDetector(new DipParams());
        double t = 0;
        for (int i = 0; i < 60; i++) { t += 0.02; d.Update(t, new Bobber(970, 381, 9, 9, 50, 50)); }
        Assert.True(d.Armed);
        int fired = 0;
        for (int i = 0; i < 5; i++) { t += 0.02; if (d.Update(t, new Bobber(1200, 392, 9, 9, 50, 50)) is not null) fired++; }   // 別の物体に乗り移っても沈みとは見なさない
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Tuner_PecksRiseOnEarlyStreakAndRevertOnSuccesses()
    {
        var c = new Config { PecksToReel = 1, MaxPecksToReel = 3, EarlyStreakToAdapt = 2, CaughtStreakToRevert = 3 };
        var t = new AutoTuner(c, Path.Combine(dir, "t.json"));
        t.OnOutcome("early"); t.OnOutcome("early");
        Assert.Equal(2, t.PecksToReel);
        t.OnOutcome("caught"); t.OnOutcome("caught"); t.OnOutcome("caught");
        Assert.Equal(1, t.PecksToReel);
    }

    [Fact]
    public void Tuner_BiteTimeoutShrinksFromMeasuredWaits()
    {
        var c = new Config { NoBiteTimeout = 75, NoBiteMin = 20, NoBiteFactor = 2.5, BiteWaitMinSamples = 5 };
        var t = new AutoTuner(c, Path.Combine(dir, "t.json"));
        Assert.Equal(75, t.BiteTimeout());
        for (int i = 0; i < 5; i++) t.NoteBiteWait(10);
        Assert.Equal(25, t.BiteTimeout());
    }
}
