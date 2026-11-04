using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Tests;

public class BobberFarTests
{
    /// <summary>遠くの小さなウキは赤い棒が縦に 2 つに割れて写ることがあり、片方だけを拾うと位置が 12 画素飛んで偽の沈みになる。</summary>
    [Fact]
    public void 割れて写る遠くのウキを1つにつなぐ()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        var f = Imaging.Load(Path.Combine(d!.FullName, "tests", "fixtures", "menu", "bobber_far_split.png"));
        var b = BobberDetector.Detect(f, scale: 953 / 1009.0);
        Assert.NotNull(b);
        Assert.InRange(b!.H, 18, 24);
    }
}
