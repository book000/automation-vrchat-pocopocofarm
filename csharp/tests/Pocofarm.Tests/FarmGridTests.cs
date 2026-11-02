using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Platform;

namespace Pocofarm.Tests;

/// <summary>ファーム写真の畑・マス検出。実際の撮影画像(tests/fixtures/farm)で確かめる。</summary>
public class FarmGridTests
{
    static string Fixture(string name)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return Path.Combine(d!.FullName, "tests", "fixtures", "farm", name);
    }

    static FarmScan Scan(string name) => FarmGrid.Scan(Imaging.Load(Fixture(name)), 14, 0.54, 0.002);

    [Fact]
    public void AvatarJoiningTwoPlots_StillFindsAllEight()
    {
        var s = Scan("avatar_between_plots.jpg");
        Assert.Equal(8, s.Plots.Count);
    }

    [Fact]
    public void HalfPlanted_UpperEmptyLowerWatered()
    {
        var s = Scan("half_planted.jpg");
        Assert.Equal(8, s.Plots.Count);
        Assert.Equal(48, s.Count('e'));
        Assert.Equal(1, s.Count('p'));
        Assert.True(s.Count('w') >= 44);
        Assert.All(s.Plots.Take(4), p => Assert.Equal(new string('e', 12), p.Cells));
        Assert.All(s.Plots.Skip(4), p => Assert.DoesNotContain('e', p.Cells));
        Assert.Equal(s.Count('e'), s.Unplanted - s.Count('b'));
    }

    [Fact]
    public void YellowPlants_CountAsPlanted()
    {
        var s = Scan("yellow_plants.jpg");
        Assert.Equal(8, s.Plots.Count);
        Assert.Equal(0, s.Unplanted);
        Assert.Equal(0, s.Count('e') + s.Count('b'));
    }

    [Fact]
    public void AllEmpty_EveryCellEmpty()
    {
        var s = Scan("all_empty.jpg");
        Assert.Equal(8, s.Plots.Count);
        Assert.Equal(96, s.Count('e'));
    }

    [Fact]
    public void AvatarOnPlot_CoveredCellsAreUnknownNotEmpty()
    {
        var s = Scan("avatar_on_plot.jpg");
        Assert.Equal(8, s.Plots.Count);
        Assert.Equal(0, s.Count('e'));
        Assert.True(s.Count('?') is >= 1 and <= 6);
        Assert.All(s.Plots.Take(2), p => Assert.Equal(new string('w', 12), p.Cells));
    }

    [Fact]
    public void GrownLeaves_StillFindsAllPlots()
    {
        var s = Scan("grown_leaves.jpg");
        Assert.Equal(8, s.Plots.Count);
        Assert.Equal(0, s.Count('e'));
        Assert.True(s.Count('p') >= 6);
    }
}
