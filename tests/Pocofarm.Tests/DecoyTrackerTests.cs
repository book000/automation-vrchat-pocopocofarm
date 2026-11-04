using Pocofarm.Fishing;

namespace Pocofarm.Tests;

public class DecoyTrackerTests
{
    static void Learn(DecoyTracker t, (int, int)[] cands, double now = 0, (int, int)? tip = null, (int, int)? away = null) =>
        t.Learn(cands.Select(c => (c.Item1, c.Item2)), tip, away, pad: 10, cell: 20, now: now, keepSeconds: 100);

    [Fact]
    public void SeenOnceIsNotExcluded()
    {
        var t = new DecoyTracker();
        Learn(t, [(100, 100)]);
        Assert.Empty(t.Rects);
    }

    [Fact]
    public void SeenTwiceExcludesCellAndRing()
    {
        var t = new DecoyTracker();
        Learn(t, [(105, 105)]);
        Learn(t, [(110, 101)]);        // 同じ升目 (5,5)
        Assert.Equal([(80, 80, 140, 140)], t.Rects);     // 升目 4..6 の範囲
    }

    [Fact]
    public void MarkExcludesImmediately_EvenAtTip()
    {
        var t = new DecoyTracker();
        Learn(t, [(105, 105)], tip: (105, 105));      // 追っていた位置は tip 扱いで数えられない
        Assert.Empty(t.Rects);
        t.Mark(105, 105, pad: 10, cell: 20, now: 1);
        Assert.Equal([(80, 80, 140, 140)], t.Rects);
    }

    [Fact]
    public void TipAndExpectAwayAreIgnored()
    {
        var t = new DecoyTracker();
        for (int i = 0; i < 3; i++) Learn(t, [(100, 100), (300, 300)], tip: (105, 120), away: (300, 310));
        Assert.Empty(t.Rects);
    }

    [Fact]
    public void StaleCellsExpire()
    {
        var t = new DecoyTracker();
        Learn(t, [(100, 100)], now: 0);
        Learn(t, [(100, 100)], now: 1);
        Assert.Single(t.Rects);
        Learn(t, [], now: 500);
        Assert.Empty(t.Rects);
        Assert.Equal(0, t.CellCount);
    }

    [Fact]
    public void ForgetsAfterSixNonCatches()
    {
        var t = new DecoyTracker();
        Learn(t, [(100, 100)]);
        Learn(t, [(100, 100)]);
        for (int i = 0; i < 5; i++) Assert.False(t.NoteOutcome("missed_bite"));
        Assert.True(t.NoteOutcome("early"));
        Assert.Equal(0, t.CellCount);
        Assert.Single(t.Rects);        // 除外矩形は次の Learn まで残る
    }

    [Fact]
    public void CatchAndNeutralOutcomesDoNotAdvanceStreak()
    {
        var t = new DecoyTracker();
        for (int i = 0; i < 5; i++) t.NoteOutcome("escaped");
        t.NoteOutcome("caught");
        for (int i = 0; i < 5; i++) Assert.False(t.NoteOutcome("escaped"));
        Assert.False(t.NoteOutcome("no_bite_reset"));
        Assert.False(t.NoteOutcome("cast_failed"));
        Assert.True(t.NoteOutcome("unknown"));
    }
}
