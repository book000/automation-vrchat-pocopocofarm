using Pocofarm.Core;
using Pocofarm.Ocr;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

/// <summary>OCR スレッドが返す 1 回分の読み取り。T は画像を取得した時刻。</summary>
public sealed record Sighting(double T, string Text, Toast? Toast, string Engine)
{
    /// <summary>ねこちゃんの通知の本文(無ければ null)。</summary>
    public string? Neko { get; } = Core.Toast.ExtractNeko(Text);
}
