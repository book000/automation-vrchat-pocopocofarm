using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Cli;

/// <summary>常駐。釣りプロセスを子として起動し、落ちたら再起動し、新しい exe があれば入れ替える。</summary>
public static class Supervisor
{
    /// <summary>run を子プロセスで起動し、落ちたら(restart_delay 秒後に)再起動し続ける(常駐用)。</summary>
    public static int Run(string[] args)
    {
        var self = Environment.ProcessPath!;
        var childArgs = new[] { "run" }.Concat(args.Skip(1)).ToArray();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var paths = CliArgs.Paths(args);
        Log.Setup(paths.Log);
        var cfg = Config.Load(paths.ConfigFile);
        SelfUpdate.CleanupLeftovers(self);
        double lastExeCleanup = Environment.TickCount64 / 1000.0;
        while (!cts.IsCancellationRequested)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(self, childArgs) { UseShellExecute = false, RedirectStandardError = true })!;
            // ネイティブ例外で落ちたときの .NET の致命エラー(スタック)は stderr にしか出ないので、data/crash.log に残す
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Console.Error.WriteLine(e.Data);
                try { File.AppendAllText(Path.Combine(paths.Data, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Data}{Environment.NewLine}"); }
                catch (IOException) { }
            };
            p.BeginErrorReadLine();
            while (!p.WaitForExit(1000))
            {
                if (cts.IsCancellationRequested) { p.Kill(true); return 0; }
                // 設定は毎回読み直す(update_source をダッシュボードから変えても再起動なしで効く)
                var live = Config.Load(paths.ConfigFile);
                double now = Environment.TickCount64 / 1000.0;
                if (live.ExeCleanupInterval > 0 && now - lastExeCleanup >= live.ExeCleanupInterval)
                {
                    lastExeCleanup = now;
                    SelfUpdate.CleanupLeftovers(self);
                }
                if (SelfUpdate.Pending(self, live) is { } next)
                {
                    Log.Info("新しい exe を検出したため、釣りを止めて入れ替えます");
                    p.Kill(true);
                    p.WaitForExit(5000);
                    if (SelfUpdate.Swap(self, next, args)) { Log.Info("入れ替えました。新しい常駐に引き継ぎます"); return 0; }
                }
            }
            Log.Warn($"釣りプロセスが終了(code={p.ExitCode})。{cfg.RestartDelay:0.#}秒後に再起動します");
            cts.Token.WaitHandle.WaitOne((int)(cfg.RestartDelay * 1000));
        }
        return 0;
    }
}
