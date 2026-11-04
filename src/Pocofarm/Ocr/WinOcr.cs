using System.Text.RegularExpressions;
using Pocofarm.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Pocofarm.Ocr;

/// <summary>OCR エンジン共通の入口。</summary>
public interface IOcr
{
    string Name { get; }

    /// <summary>画像内の文字列を返す(空白は除く)。</summary>
    string Read(Frame img);
}

/// <summary>Windows 標準 OCR。約45ms/回で日本語に強い常用エンジン。スレッドごとにインスタンスを作って使う。</summary>
public sealed partial class WinOcr : IOcr
{
    readonly OcrEngine engine;

    public string Name => "winocr";

    /// <summary>この環境で日本語の Windows OCR を使えるか(言語パックの有無。CI の Windows Server には無いことがある)。</summary>
    public static bool IsAvailable() => OcrEngine.TryCreateFromLanguage(new Language("ja")) is not null;

    public WinOcr()
    {
        engine = OcrEngine.TryCreateFromLanguage(new Language("ja"))
            ?? throw new InvalidOperationException("Windows OCR の日本語言語パックがありません");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>単語ごとの文字列と位置(画像内のピクセル)。HUD の位置合わせ用。</summary>
    public List<(string Text, double X, double Y, double W, double H)> Words(Frame img)
    {
        var list = new List<(string, double, double, double, double)>();
        if (img.Width < 2 || img.Height < 2) return list;
        using var writer = new DataWriter();
        writer.WriteBytes(img.Data);
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, img.Width, img.Height, BitmapAlphaMode.Premultiplied);
        var res = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
        foreach (var line in res.Lines)
            foreach (var w in line.Words) list.Add((w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height));
        return list;
    }

    /// <inheritdoc />
    public string Read(Frame img)
    {
        if (img.Width < 2 || img.Height < 2) return "";
        using var writer = new DataWriter();
        writer.WriteBytes(img.Data);
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, img.Width, img.Height, BitmapAlphaMode.Premultiplied);
        var res = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
        return Spaces().Replace(res.Text, "");
    }
}
