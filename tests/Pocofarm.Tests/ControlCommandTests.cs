using Pocofarm.Core;
using Pocofarm.Fishing;

namespace Pocofarm.Tests;

public class ControlCommandTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("pocofarm-ctl").FullName;
    readonly Paths paths;
    readonly ControlChannel ch;
    readonly Config cfg = new() { ControlCheckInterval = 0, PauseVk = 0 };

    public ControlCommandTests()
    {
        paths = new Paths(dir);
        Directory.CreateDirectory(paths.Data);
        ch = new ControlChannel(cfg, paths, new EventLog(paths.Events), new Status(paths.Status), dryRun: false, startPaused: false);
    }

    public void Dispose() => Directory.Delete(dir, true);

    void Send(string name, string? arg = null, int amount = 0)
    {
        Thread.Sleep(30);      // 更新時刻の分解能を超えて変える
        ControlChannel.WriteCommand(paths, name, arg, amount);
        ch.Poll();
    }

    [Fact]
    public void Nudge_IsBoundedAndQueued()
    {
        Send("nudge", "left", 99999);
        Assert.True(ch.Commands.TryDequeue(out var c));
        Assert.Equal(new ControlCommand("nudge", "left", ControlChannel.MaxNudgePx), c);
    }

    [Fact]
    public void InvalidNudgeAndUnknownCommands_AreRejected()
    {
        Send("nudge", "diagonal");
        Send("explode");
        Assert.Empty(ch.Commands);
    }

    [Fact]
    public void ManualCommands_AreRejectedWhilePaused_AndInDryRun()
    {
        Send("recast");
        Assert.Single(ch.Commands);
        ch.Commands.Clear();
        Thread.Sleep(30);
        File.WriteAllText(paths.Control, "{\"paused\":true}");
        ch.Poll();
        Assert.True(ch.Paused);
        Send("recast");
        Assert.Empty(ch.Commands);
    }

    [Fact]
    public void DryRunToggle_RaisesEventAndRejectsClicksCommands()
    {
        bool? seen = null;
        ch.DryRunChanged += v => seen = v;
        Send("dry_run", "on");
        Assert.True(ch.DryRun);
        Assert.True(seen);
        Send("reel_now");
        Assert.Empty(ch.Commands);
        Send("dry_run", "off");
        Assert.False(ch.DryRun);
    }

    [Fact]
    public void PauseAfterResult_WaitsForSafePoint()
    {
        Send("pause_after_result");
        Assert.False(ch.Paused);
        ch.SafePoint();
        Assert.True(ch.Paused);
    }

    [Theory]
    [InlineData("09:30", 8, 0, 2026, 11, 5, 9, 30, 5)]
    [InlineData("07:00", 8, 0, 2026, 11, 5, 7, 0, 6)]
    public void NextOccurrence_RollsToTomorrowWhenPast(string hhmm, int h, int m, int y, int mo, int d, int eh, int em, int ed)
    {
        var next = ControlChannel.NextOccurrence(hhmm, new DateTime(y, mo, d, h, m, 0));
        Assert.Equal(new DateTime(y, mo, ed, eh, em, 0), next);
        Assert.Null(ControlChannel.NextOccurrence("25:00", DateTime.Now));
        Assert.Null(ControlChannel.NextOccurrence("abc", DateTime.Now));
    }
}
