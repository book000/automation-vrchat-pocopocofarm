using Pocofarm.Vision;

namespace Pocofarm.Tests;

public class LostDipGuardTests
{
    static readonly Bobber Still = new(100, 200, 10, 20, 100, 100);

    /// <summary>trace の形式([t, y, w]、y=-1 は見失い)でフレーム列を流し、"dip" が出た回数を返す。</summary>
    static (int Dips, int Suppressed) Replay(DipParams p, IEnumerable<double[]> trace)
    {
        var d = new DipDetector(p);
        int dips = 0;
        foreach (var f in trace)
            if (d.Update(f[0], f[1] < 0 ? null : new Bobber(100, (int)f[1], (int)f[2], 20, 100, 100)) == "dip") dips++;
        return (dips, d.SuppressedLost);
    }

    static IEnumerable<double[]> Settle()
    {
        for (int i = 1; i <= 60; i++) yield return [i * 0.02, 200, 10];
    }

    // 武装後、visible フレームのうち seen 枚だけ見えて、あとは見失う並びを 30 枚作り、その後も見失い続ける
    static IEnumerable<double[]> ThenLost(int seen)
    {
        double t = 60 * 0.02;
        for (int i = 0; i < 30 - 0; i++) { t += 0.02; yield return [t, i < seen ? 200 : -1, 10]; }
        for (int i = 0; i < 20; i++) { t += 0.02; yield return [t, -1, 0]; }
    }

    [Fact]
    public void GuardOff_LostAlwaysFires()
    {
        Assert.Equal(1, Replay(new DipParams(), Settle().Concat(ThenLost(5))).Dips);
    }

    [Fact]
    public void GuardOn_SteadyBobberThenLost_StillFires()
    {
        var p = new DipParams(LostGuardFrames: 30, LostGuardSeen: 20);
        var steady = Settle().Concat(Enumerable.Range(1, 30).Select(i => new[] { 1.2 + i * 0.02, 200.0, 10.0 })).Concat(
            Enumerable.Range(1, 20).Select(i => new[] { 1.8 + i * 0.02, -1.0, 0.0 }));
        Assert.Equal((1, 0), Replay(p, steady));
    }

    [Fact]
    public void GuardOn_FlickeringBobberThenLost_IsSuppressed()
    {
        var p = new DipParams(LostGuardFrames: 30, LostGuardSeen: 20);
        // 武装後に見えたのは 3 枚だけで、その後ずっと見えない
        var flicker = Settle().Take(28).Concat(Enumerable.Range(1, 3).Select(i => new[] { 0.56 + i * 0.02, 200.0, 10.0 })).Concat(
            Enumerable.Range(1, 40).Select(i => new[] { 0.62 + i * 0.02, -1.0, 0.0 }));
        Assert.Equal((0, 1), Replay(p, flicker));
    }
}
