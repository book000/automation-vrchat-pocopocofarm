using Pocofarm.Core;
using Pocofarm.Platform;
using Xunit;

namespace Pocofarm.Tests;

/// <summary>フレーム複製・JPEG 化の確保量が使い回しで減っていることの確認。</summary>
public class AllocationTests
{
    const int Loops = 20;

    static Frame Sample()
    {
        var f = new Frame(1920, 1080);
        f.Fill(100, 100, 900, 700, 10, 200, 240);
        return f;
    }

    [Fact]
    public void RentCopy_AllocatesFarLessThanClone()
    {
        var src = Sample();
        FramePool.Return(FramePool.RentCopy(src));      // 温め
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Loops; i++) src.Clone();
        long clone = GC.GetAllocatedBytesForCurrentThread() - a0;

        a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Loops; i++) FramePool.Return(FramePool.RentCopy(src));
        long pooled = GC.GetAllocatedBytesForCurrentThread() - a0;

        Assert.True(clone >= Loops * src.Data.Length);
        Assert.True(pooled < src.Data.Length, $"pooled={pooled} clone={clone}");
    }

    [Fact]
    public void CopyInto_ReusesBufferAndCopiesPixels()
    {
        var src = Sample();
        Frame? dest = null;
        src.CopyInto(ref dest);
        var first = dest;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Loops; i++) src.CopyInto(ref dest);
        long used = GC.GetAllocatedBytesForCurrentThread() - a0;

        Assert.Same(first, dest);
        Assert.True(used < 1024, $"used={used}");
        Assert.Equal(src.Data, dest!.Data);
    }

    [Fact]
    public void ToJpeg_ProducesJpegAndIsRepeatable()
    {
        var f = Sample();
        var a = Imaging.ToJpeg(f, [], 960, 70, 18);
        var b = Imaging.ToJpeg(f, [], 960, 70, 18);
        Assert.Equal(0xFF, a[0]); Assert.Equal(0xD8, a[1]);
        Assert.Equal(a, b);     // 使い回すバッファに前回の内容が残らない
        // 管理ヒープの確保は出力配列程度(従来は全画素の複製 + ストリーム成長を毎回確保していた)
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Loops; i++) Imaging.ToJpeg(f, [], 960, 70, 18);
        long used = (GC.GetAllocatedBytesForCurrentThread() - a0) / Loops;
        Assert.True(used < f.Data.Length / 4, $"perCall={used}");
    }
}
