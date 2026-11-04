using System.Text.Json.Nodes;
using Pocofarm.Core;
using Pocofarm.Fishing;

namespace Pocofarm.Tests;

/// <summary>進行監視・証拠の間引き・イベント記録の上限まわりのテスト。</summary>
public class ResilienceTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("pocofarm-resil").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void EvidenceThrottle_KeepsFirstThenEveryTenth_AndAlwaysCaught()
    {
        var t = new EvidenceThrottle();
        var kept = Enumerable.Range(1, 21).Select(_ => t.Keep("missed_bite")).ToList();
        Assert.Equal([1, 10, 20], kept.Select((k, i) => (k, i + 1)).Where(x => x.k).Select(x => x.Item2));
        Assert.True(t.Keep("caught"));
        Assert.True(t.Keep("caught"));
        Assert.True(t.Keep("early"));      // 種別が変われば最初の 1 回として残す
    }

    [Fact]
    public void WaitP95_NeedsTenSamples_ThenReturnsTail()
    {
        var t = new AutoTuner(new Config(), Path.Combine(dir, "t.json"));
        Assert.Null(t.WaitP95());
        for (int i = 1; i <= 100; i++) t.NoteBiteWait(i);
        Assert.Equal(95, t.WaitP95());
    }

    [Fact]
    public void UnclassifiedEvidence_IsCapped()
    {
        var store = new EvidenceStore(Path.Combine(dir, "ev"), Path.Combine(dir, "live.jpg"), 24, 100);
        using var f = new MemoryFrame();
        int saved = 0;
        for (int i = 0; i < EvidenceStore.UnclassifiedCap + 20; i++)
            if (store.Save(f.Frame, "unclassified_toast", new { i }).Length > 0) saved++;
        Assert.Equal(EvidenceStore.UnclassifiedCap, saved);
        Assert.NotEqual("", store.Save(f.Frame, "caught_1", new { }));      // ほかの種別は対象外
    }

    [Fact]
    public void SessionSummary_ReflectsNewAttempts_AndIsCached()
    {
        var path = Path.Combine(dir, "events.jsonl");
        var log = new EventLog(path);
        log.Write("attempt", ("attempt", 1), ("outcome", "caught"), ("fish", "A"), ("exp", 5));
        Assert.Equal(1, log.SummarizeSession()["caught"]);
        log.Write("attempt", ("attempt", 2), ("outcome", "caught"), ("fish", "A"), ("exp", 5));
        Assert.Equal(2, log.SummarizeSession()["caught"]);
        Assert.Same(log.SummarizeSession(), log.SummarizeSession());      // 変化がなければ再集計しない
    }

    [Fact]
    public void Decoys_Clear_DropsRects()
    {
        var t = new DecoyTracker();
        for (int i = 0; i < 2; i++) t.Learn([(100, 100)], null, null, 10, 20, i, 100);
        Assert.NotEmpty(t.Rects);
        t.Clear();
        Assert.Empty(t.Rects);
        Assert.Equal(0, t.CellCount);
    }

    sealed class MemoryFrame : IDisposable
    {
        public Pocofarm.Core.Frame Frame { get; } = new(64, 64);
        public void Dispose() { }
    }
}
