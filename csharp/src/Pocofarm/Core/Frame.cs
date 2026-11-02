namespace Pocofarm.Core;

/// <summary>BGRA 8bit のフレーム。キャプチャ結果と検出処理の共通形式。</summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public Frame(int width, int height, byte[]? data = null)
    {
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 4];
    }

    /// <summary>画素データを複製する(証拠保存や OCR へ渡す用)。</summary>
    public Frame Clone() => new(Width, Height, (byte[])Data.Clone());

    /// <summary>画素を設定する(テスト・描画用)。</summary>
    public void Set(int x, int y, byte b, byte g, byte r)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        int i = (y * Width + x) * 4;
        Data[i] = b; Data[i + 1] = g; Data[i + 2] = r; Data[i + 3] = 255;
    }

    /// <summary>矩形を塗りつぶす。</summary>
    public void Fill(int x0, int y0, int x1, int y1, byte b, byte g, byte r)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) Set(x, y, b, g, r);
    }

    /// <summary>面積平均で縮小する(factor&lt;1)。高解像度の文字を OCR が読みやすい大きさに揃える用。</summary>
    public Frame Downscale(double factor)
    {
        int w = Math.Max(1, (int)(Width * factor)), h = Math.Max(1, (int)(Height * factor));
        var o = new Frame(w, h);
        for (int y = 0; y < h; y++)
        {
            int y0 = (int)((long)y * Height / h), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * Height / h));
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)((long)x * Width / w), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * Width / w));
                int b = 0, g = 0, r = 0, n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0, i = (yy * Width + x0) * 4; xx < x1; xx++, i += 4) { b += Data[i]; g += Data[i + 1]; r += Data[i + 2]; n++; }
                int j = (y * w + x) * 4;
                o.Data[j] = (byte)(b / n); o.Data[j + 1] = (byte)(g / n); o.Data[j + 2] = (byte)(r / n); o.Data[j + 3] = 255;
            }
        }
        return o;
    }

    /// <summary>比率指定の範囲を切り出して新しいフレームにする(境界は切り捨て)。</summary>
    public Frame CropRatio(double l, double t, double r, double b) =>
        Crop((int)(l * Width), (int)(t * Height), (int)(r * Width), (int)(b * Height));

    /// <summary>画素範囲を切り出す。範囲外は丸める。</summary>
    public Frame Crop(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Clamp(x0, 0, Width); x1 = Math.Clamp(x1, x0, Width);
        y0 = Math.Clamp(y0, 0, Height); y1 = Math.Clamp(y1, y0, Height);
        var f = new Frame(x1 - x0, y1 - y0);
        for (int y = y0; y < y1; y++)
            Buffer.BlockCopy(Data, (y * Width + x0) * 4, f.Data, (y - y0) * f.Width * 4, f.Width * 4);
        return f;
    }

    /// <summary>OpenCV 互換の HSV(H=0..179, S/V=0..255)へ変換する。</summary>
    public static (int H, int S, int V) Hsv(byte b, byte g, byte r)
    {
        int v = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = v - mn;
        if (v == 0) return (0, 0, 0);
        int s = (int)Math.Round(255.0 * d / v, MidpointRounding.AwayFromZero);
        double h;
        if (d == 0) h = 0;
        else if (v == r) h = 60.0 * (g - b) / d;
        else if (v == g) h = 120.0 + 60.0 * (b - r) / d;
        else h = 240.0 + 60.0 * (r - g) / d;
        if (h < 0) h += 360;
        return ((int)Math.Round(h / 2, MidpointRounding.AwayFromZero) % 180, s, v);
    }
}
