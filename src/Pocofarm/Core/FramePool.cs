using System.Collections.Concurrent;

namespace Pocofarm.Core;

/// <summary>大きなフレームの確保(LOH)を避けるための小さな使い回し置き場。返すのは「もう誰も読まない」フレームだけ。</summary>
public static class FramePool
{
    const int MaxPooled = 6;
    static readonly ConcurrentBag<Frame> Bag = new();

    /// <summary>src と同じ大きさのフレームを借りて中身を写す。</summary>
    public static Frame RentCopy(Frame src)
    {
        Frame? f = null;
        int n = Bag.Count;
        for (int i = 0; i < n && Bag.TryTake(out var c); i++)
        {
            // 大きさが違うものは捨てる(解像度が変わったときに古いサイズを溜めない)
            if (c.Width == src.Width && c.Height == src.Height) { f = c; break; }
        }
        f ??= new Frame(src.Width, src.Height);
        Buffer.BlockCopy(src.Data, 0, f.Data, 0, src.Data.Length);
        return f;
    }

    /// <summary>使い終えたフレームを返す。返した後は呼び出し側も他スレッドも触らないこと。</summary>
    public static void Return(Frame f)
    {
        if (Bag.Count < MaxPooled) Bag.Add(f);
    }
}
