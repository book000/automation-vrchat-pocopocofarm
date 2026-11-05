using Pocofarm.Core;
using Pocofarm.Fishing;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Tests;

/// <summary>視点のずれ推定(ViewAligner)と視点補正の閉ループ(AimRecovery)のテスト。</summary>
public class AimTests : IDisposable
{
    const int VW = 1100, VH = 578;
    readonly string dir = Path.Combine(Path.GetTempPath(), "pocofarm-aim-" + Guid.NewGuid().ToString("N"));

    public AimTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } GC.SuppressFinalize(this); }

    static string Fixture(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return Path.Combine(d!.FullName, "tests", "fixtures", "aim", name);
    }

    /// <summary>視野より広い、低周波の乱数模様の「世界」。視点はこの一部を切り出したもの。</summary>
    static Frame World(int seed)
    {
        const int w = VW * 2, h = VH * 2, cell = 18;
        int gw = w / cell + 2, gh = h / cell + 2;
        var rng = new Random(seed);
        var grid = new double[gw, gh];
        for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++) grid[x, y] = rng.Next(256);
        var f = new Frame(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double gx = x / (double)cell, gy = y / (double)cell;
                int x0 = (int)gx, y0 = (int)gy;
                double fx = gx - x0, fy = gy - y0;
                double v = grid[x0, y0] * (1 - fx) * (1 - fy) + grid[x0 + 1, y0] * fx * (1 - fy) + grid[x0, y0 + 1] * (1 - fx) * fy + grid[x0 + 1, y0 + 1] * fx * fy;
                f.Set(x, y, (byte)v, (byte)v, (byte)v);
            }
        return f;
    }

    static Frame View(Frame world, double ox, double oy) => world.Crop((int)Math.Round(ox), (int)Math.Round(oy), (int)Math.Round(ox) + VW, (int)Math.Round(oy) + VH);

    static readonly Frame WorldA = World(1);
    static readonly Frame WorldB = World(2);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0)]       // 内容が右へ動いた(視点が左へ回った)
    [InlineData(-110, 40)]     // 内容が左・下へ。テンプレートが画面の左へはみ出す量
    [InlineData(250, -60)]
    [InlineData(-200, -40)]
    public void EstimateRecoversSyntheticShift(int shiftX, int shiftY)
    {
        double ox = 500, oy = 300;
        var reference = ViewAligner.ToGrey(View(WorldA, ox, oy));
        var current = ViewAligner.ToGrey(View(WorldA, ox - shiftX, oy - shiftY));
        var s = ViewAligner.Estimate(reference, current, 0.6);
        Assert.NotNull(s);
        Assert.Equal(shiftX / (double)VW, s.Value.Sx, 0.012);
        Assert.Equal(shiftY / (double)VH, s.Value.Sy, 0.012);
        Assert.True(s.Value.Score > 0.9);
    }

    [Fact]
    public void EstimateRejectsUnrelatedAndFlatScenes()
    {
        var reference = ViewAligner.ToGrey(View(WorldA, 500, 300));
        Assert.Null(ViewAligner.Estimate(reference, ViewAligner.ToGrey(View(WorldB, 500, 300)), 0.6));
        var flat = ViewAligner.ToGrey(new Frame(VW, VH));
        Assert.Null(ViewAligner.Estimate(reference, flat, 0.6));
        Assert.Null(ViewAligner.Estimate(flat, reference, 0.6));
    }

    [Fact]
    public void EstimateOnRealFrames()
    {
        var caught = Imaging.Load(Fixture("ref_caught.jpg"));
        var reference = ViewAligner.ToGrey(caught);

        var same = ViewAligner.Estimate(reference, reference, 0.6);
        Assert.NotNull(same);
        Assert.True(Math.Abs(same.Value.Sx) < 0.005 && Math.Abs(same.Value.Sy) < 0.005);

        // 実画面を平行移動(端は黒埋め)しても、ずれ量を取り戻せる
        var moved = new Frame(caught.Width, caught.Height);
        const int dx = 60, dy = 25;
        for (int y = dy; y < caught.Height; y++)
            Buffer.BlockCopy(caught.Data, (y - dy) * caught.Width * 4, moved.Data, (y * caught.Width + dx) * 4, (caught.Width - dx) * 4);
        var s = ViewAligner.Estimate(reference, ViewAligner.ToGrey(moved), 0.6);
        Assert.NotNull(s);
        Assert.Equal(dx / (double)caught.Width, s.Value.Sx, 0.012);
        Assert.Equal(dy / (double)caught.Height, s.Value.Sy, 0.012);

        // 池から大きく外れた実画面(草むら)は、基準の場面として認識しない
        foreach (var name in new[] { "drifted_a.jpg", "drifted_b.jpg" })
            Assert.Null(ViewAligner.Estimate(reference, ViewAligner.ToGrey(Imaging.Load(Fixture(name))), 0.6));
    }

    /// <summary>世界の上を動く仮想の視点。look は dx カウントで内容が左へ dx*kx 画素、dy で上へ dy*ky 画素動く。</summary>
    sealed class FakeView(Frame world, double ox, double oy, double kx, double ky)
    {
        public double Ox = ox, Oy = oy;
        public int Calls;
        public Frame Current => View(world, Ox, Oy);
        public Frame? Look(int dx, int dy)
        {
            Calls++;
            Ox += dx * kx; Oy += dy * ky;
            return Current;
        }
    }

    AimRecovery NewRecovery(Config? cfg = null)
    {
        var paths = new Paths(dir);
        return new AimRecovery(cfg ?? new Config(), paths, new EventLog(paths.Events));
    }

    [Theory]
    [InlineData(400.0)]       // 初期ゲイン(800)より敏感: 行き過ぎる
    [InlineData(800.0)]
    [InlineData(2000.0)]      // 鈍い: 足りない
    public void RecoverConvergesWithWrongGain(double trueGain)
    {
        var rec = NewRecovery();
        double ox0 = 500, oy0 = 300;
        rec.NoteCatch(View(WorldA, ox0, oy0), 1000);
        // 内容が右へ 150 画素・下へ 50 画素ずれた状態(視点が左・上へ回っている)から始める
        var view = new FakeView(WorldA, ox0 - 150, oy0 - 50, VW / trueGain, VH / (trueGain * 9 / 16 * 1.3));
        var result = rec.Recover(view.Current, view.Look, () => false);
        Assert.Equal(AimRecoverResult.Aligned, result);
        Assert.True(Math.Abs(view.Ox - ox0) <= 0.03 * VW && Math.Abs(view.Oy - oy0) <= 0.03 * VH, $"final offset {view.Ox - ox0},{view.Oy - oy0}");
        Assert.True(view.Calls <= 8);
        Assert.Contains(EventLog.Read(Path.Combine(dir, "data", "events.jsonl")), e => e.Str("type") == "aim_recover" && e.Str("result") == "Aligned");
    }

    // 横は 1 周(画面幅 4 つ分 = 4400 画素)で元に戻る世界。世界が見えるのは基準を含む一部の向き(0〜VW 画素)と高さ(0〜VH 画素)だけで、それ以外は平坦な画面(空など)
    static Frame Panorama(double ox, double oy)
    {
        double x = ((ox % 4400) + 4400) % 4400;
        return x > VW || oy < 0 || oy > VH ? new Frame(VW, VH) : View(WorldA, x, oy);
    }

    [Theory]
    [InlineData(500, -300)]        // 空を向いている(高さだけずれている)
    [InlineData(3140, 300)]        // 横へ大きく振ってしまった(高さは合っている)
    [InlineData(2700, -300)]       // 縦も横もずれている
    public void RecoverSweepsToFindReferenceSceneWhenOffScreen(double ox0, double oy0)
    {
        var rec = NewRecovery();
        rec.NoteCatch(View(WorldA, 500, 300), 1000);
        double ox = ox0, oy = oy0;
        Frame Look(int dx, int dy) { ox += dx * VW / 800.0; oy += dy * VH / 450.0; return Panorama(ox, oy); }
        var result = rec.Recover(Panorama(ox, oy), Look, () => false);
        Assert.Equal(AimRecoverResult.Aligned, result);
        Assert.True(Math.Abs(((ox - 500) % 4400 + 4400) % 4400) <= 0.03 * VW && Math.Abs(oy - 300) <= 0.03 * VH, $"final {ox},{oy}");
    }

    [Fact]
    public void RecoverSweepReturnsToStartWhenSceneIsNeverFound()
    {
        var rec = NewRecovery();
        rec.NoteCatch(View(WorldA, 500, 300), 1000);
        double ox = 2000, oy = 250;      // 世界が見える向きが 1 周のどこにも掛からない高さ
        Frame Look(int dx, int dy) { ox += dx * VW / 800.0; oy += dy * VH / 450.0; return new Frame(VW, VH); }
        Assert.Equal(AimRecoverResult.NoMatch, rec.Recover(new Frame(VW, VH), Look, () => false));
        Assert.True(Math.Abs(ox - 2000) < 1 && Math.Abs(oy - 250) < 1, $"final {ox},{oy}");
    }

    [Fact]
    public void RecoverGivesUpWhenInputHasNoEffect()
    {
        var rec = NewRecovery();
        rec.NoteCatch(View(WorldA, 500, 300), 1000);
        var view = new FakeView(WorldA, 350, 300, 0, 0);      // マウスを動かしても視点が動かない(VRChat が前面でない等)
        Assert.Equal(AimRecoverResult.InputFailed, rec.Recover(view.Current, view.Look, () => false));
        Assert.True(view.Calls <= 4);
    }

    [Fact]
    public void RecoverStopsWhenSceneIsUnknownOrPaused()
    {
        var rec = NewRecovery();
        Assert.Equal(AimRecoverResult.NoReference, rec.Recover(View(WorldA, 500, 300), (_, _) => null, () => false));
        rec.NoteCatch(View(WorldA, 500, 300), 1000);
        var other = new FakeView(WorldB, 500, 300, 0, 0);
        Assert.Equal(AimRecoverResult.NoMatch, rec.Recover(other.Current, other.Look, () => false));
        var view = new FakeView(WorldA, 350, 300, 1, 1);
        Assert.Equal(AimRecoverResult.Aborted, rec.Recover(view.Current, view.Look, () => true));
        Assert.Equal(0, view.Calls);
    }

    [Fact]
    public void ReferenceSurvivesRestart()
    {
        NewRecovery().NoteCatch(View(WorldA, 500, 300), 1000);
        Assert.True(File.Exists(new Paths(dir).AimReference));
        var reloaded = NewRecovery();
        Assert.True(reloaded.HasReference);
        var view = new FakeView(WorldA, 450, 300, VW / 800.0, VH / 450.0);
        Assert.Equal(AimRecoverResult.Aligned, reloaded.Recover(view.Current, view.Look, () => false));
    }
}
