using Pocofarm.Fishing;

namespace Pocofarm.Tests;

public class FishStateTests
{
    // status.json の state 文字列はダッシュボードが参照するので変えない
    [Theory]
    [InlineData(FishState.WaitWindow, "wait_window")]
    [InlineData(FishState.Cooldown, "cooldown")]
    [InlineData(FishState.CastWait, "cast_wait")]
    [InlineData(FishState.WaitBite, "wait_bite")]
    [InlineData(FishState.ResultWait, "result_wait")]
    public void WireNamesAreStable(FishState s, string wire) => Assert.Equal(wire, s.Wire());
}
