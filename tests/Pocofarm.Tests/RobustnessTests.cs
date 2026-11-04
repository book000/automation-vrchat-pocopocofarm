using Pocofarm.Core;
using Pocofarm.Fishing;

namespace Pocofarm.Tests;

/// <summary>壊れたファイル・書き込み競合・掃除に対する頑健さのテスト。</summary>
public class RobustnessTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("pocofarm-robust").FullName;

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void Tuner_CorruptFile_FallsBackToDefaults()
    {
        var file = Path.Combine(dir, "tuning.json");
        File.WriteAllText(file, "{ not json");
        var cfg = new Config { ReelToCastMin = 2.0 };
        var t = new AutoTuner(cfg, file);
        Assert.Equal(2.0, t.ReelToCast);
    }

    [Fact]
    public void AtomicFile_OverwritesExistingFile()
    {
        var path = Path.Combine(dir, "a.json");
        Assert.True(AtomicFile.Write(path, [1, 2, 3]));
        Assert.True(AtomicFile.Write(path, [4, 5]));
        Assert.Equal([4, 5], File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void EventLog_Write_AppendsOneLinePerEvent()
    {
        var log = new EventLog(Path.Combine(dir, "events.jsonl"));
        log.Write("a", ("n", 1));
        log.Write("b", ("n", 2));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "events.jsonl")).Length);
    }

    [Fact]
    public void EvidenceCleanup_RemovesOldestBeyondCapAndExpired()
    {
        var old = Path.Combine(dir, "old.jpg");
        File.WriteAllBytes(old, new byte[10]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-10));
        for (int i = 0; i < 3; i++)
        {
            var f = Path.Combine(dir, $"n{i}.jpg");
            File.WriteAllBytes(f, new byte[100]);
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-3 + i));
        }
        int removed = EvidenceStore.CleanupDir(dir, 6 * 3600, 150);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(Path.Combine(dir, "n2.jpg")));
        Assert.Equal(3, removed);
    }
}
