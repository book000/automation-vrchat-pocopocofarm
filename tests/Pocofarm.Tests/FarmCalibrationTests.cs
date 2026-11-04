using Pocofarm.Farm;

namespace Pocofarm.Tests;

public class FarmCalibrationTests
{
    // 位置(x, z)で畑が写真の中を動き、高さ y で大きさが変わる、簡単な写り方のモデル
    static Task<FarmFit?> Model(float[] p)
    {
        double cx = 0.5 + 0.05 * (p[0] - 1.0), cy = 0.5 - 0.04 * (p[2] - 2.0), size = 0.6 * 45 / p[6];
        bool inside = Math.Abs(cx - 0.5) < 0.45 && Math.Abs(cy - 0.5) < 0.45 && size > 0.2;
        return Task.FromResult<FarmFit?>(new(inside ? 8 : 5, cx, cy, size));
    }

    [Fact]
    public async Task Solve_CentersAndFillsFrame()
    {
        var r = await FarmCalibration.SolveAsync([-2f, 11.5f, 4f, 82f, 180f, 0f, 45f], Model, new FarmCalParams());
        Assert.NotNull(r);
        Assert.True(r!.Value.Fit.Good(0.03), $"fit={r.Value.Fit}");
        Assert.Equal(82f, r.Value.Pose[3]);   // 向きは動かさない
    }

    [Fact]
    public async Task Solve_AlreadyGoodKeepsPose()
    {
        float[] pose = [1f, 11.5f, 2f, 82f, 180f, 0f, 45f * 0.6f];
        var r = await FarmCalibration.SolveAsync(pose, Model, new FarmCalParams());
        Assert.Equal(pose, r!.Value.Pose);
    }

    [Fact]
    public async Task Solve_NoPlotsFoundReturnsNull()
    {
        var r = await FarmCalibration.SolveAsync([0f, 11.5f, 0f, 82f, 180f, 0f, 45f], _ => Task.FromResult<FarmFit?>(null), new FarmCalParams());
        Assert.Null(r);
    }
}

public class FarmCalibrationWeakTests
{
    [Fact]
    public async Task Solve_WeakSensitivityStillConverges()
    {
        // 動かしたときの写りの変化が小さく、一部の向きでは 8 面が見えなくなる条件
        static Task<FarmFit?> Model(float[] p)
        {
            double dx = p[0] + 1.5, dz = p[2] + 0.8, dzoom = p[6] - 40;
            double cx = 0.42 + 0.04 * dx - 0.002 * dzoom, cy = 0.70 - 0.06 * dz, size = 0.64 - 0.10 * dz + 0.002 * dzoom + 0.05 * dx;
            int plots = cy > 0.72 || cy < 0.25 ? 6 : 8;
            return Task.FromResult<FarmFit?>(new(plots, cx, cy, size));
        }
        var r = await FarmCalibration.SolveAsync([-1.5f, 11.5f, -0.8f, 82f, 180f, 0f, 40f], Model, new FarmCalParams());
        Assert.NotNull(r);
        Assert.True(r!.Value.Fit.Error < 0.2, $"fit={r.Value.Fit} pose={string.Join(',', r.Value.Pose)}");
    }
}
