using Pocofarm.Core;

namespace Pocofarm.Tests;

/// <summary>ログ出力のテスト。</summary>
[Collection("Log")]
public class LogTests
{
    /// <summary>出力が止まったコンソール(Pause キーなど)を模す。</summary>
    sealed class BlockedWriter(ManualResetEventSlim release) : StringWriter
    {
        public override void WriteLine(string? value) => release.Wait();
    }

    [Fact]
    public async Task Log_DoesNotBlockWhenConsoleStalls()
    {
        var file = Path.Combine(Path.GetTempPath(), $"pocofarm-log-{Guid.NewGuid():N}.log");
        var release = new ManualResetEventSlim(false);
        var orig = Console.Out;
        try
        {
            Console.SetOut(new BlockedWriter(release));
            Log.Setup(file, toConsole: true);
            var t = Task.Run(() => { for (int i = 0; i < 50; i++) Log.Info($"line {i}"); }, TestContext.Current.CancellationToken);
            await t.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(50, File.ReadAllLines(file).Count(l => l.Contains(" line ")));
        }
        finally
        {
            release.Set();
            Console.SetOut(orig);
            Log.Setup(file, toConsole: false);
        }
    }
}
