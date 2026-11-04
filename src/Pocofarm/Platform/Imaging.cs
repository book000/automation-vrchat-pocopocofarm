using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Pocofarm.Core;

namespace Pocofarm.Platform;

/// <summary>画面上に重ねる枠。座標はフレームの画素。</summary>
public readonly record struct Box(int X0, int Y0, int X1, int Y1, string Label);

/// <summary>フレームの JPEG 保存(縮小・枠描き込み)。</summary>
public static class Imaging
{
    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>フレームを幅 width に縮小し、枠を描いて JPEG のバイト列にする。</summary>
    public static byte[] ToJpeg(Frame frame, IEnumerable<Box> boxes, int width, int quality, float fontPx)
    {
        // フレームの配列をそのまま Bitmap の画素領域にする(全画素の複製と大きなネイティブ確保を避ける)
        var pin = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);
        try { return Encode(frame, pin.AddrOfPinnedObject(), boxes, width, quality, fontPx); }
        finally { pin.Free(); }
    }

    [ThreadStatic] static MemoryStream? jpegBuf;

    static byte[] Encode(Frame frame, nint scan0, IEnumerable<Box> boxes, int width, int quality, float fontPx)
    {
        using var src = new Bitmap(frame.Width, frame.Height, frame.Width * 4, PixelFormat.Format32bppArgb, scan0);
        double k = (double)width / frame.Width;
        using var dst = new Bitmap(width, Math.Max(1, (int)Math.Round(frame.Height * k)), PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, dst.Width, dst.Height);
            using var pen = new Pen(Color.Yellow, 2);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            foreach (var b in boxes)
            {
                // 枠は縮小後の座標に直して描く(縮小前に描くと線が細くなる)
                float x0 = (float)(b.X0 * k), y0 = (float)(b.Y0 * k);
                g.DrawRectangle(pen, x0, y0, (float)((b.X1 - b.X0) * k), (float)((b.Y1 - b.Y0) * k));
                if (b.Label.Length > 0) g.DrawString(b.Label, font, Brushes.Yellow, x0, Math.Max(0, y0 - fontPx - 3));
            }
        }
        var ms = jpegBuf ??= new MemoryStream();
        ms.SetLength(0);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        dst.Save(ms, Jpeg, ep);
        return ms.ToArray();
    }

    /// <summary>フレームを PNG で保存する(検証用)。</summary>
    public static void SavePng(Frame frame, string path)
    {
        using var bmp = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(frame.Data, 0, bd.Scan0, frame.Data.Length);
        bmp.UnlockBits(bd);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>画像ファイルを BGRA フレームとして読む(テスト・比較用)。</summary>
    public static Frame Load(string path)
    {
        using var bmp = new Bitmap(path);
        using var conv = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(conv)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
        var bd = conv.LockBits(new Rectangle(0, 0, conv.Width, conv.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var f = new Frame(conv.Width, conv.Height);
        Marshal.Copy(bd.Scan0, f.Data, 0, f.Data.Length);
        conv.UnlockBits(bd);
        return f;
    }
}
