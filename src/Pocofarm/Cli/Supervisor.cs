using System.Text.Json;
using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Cli;

/// <summary>常駐。釣りプロセスを子として起動し、落ちたら再起動し、新しい exe があれば入れ替える。</summary>
public static class Supervisor
{
    /// <summary>status.json の更新がこれだけ途絶えたら、釣りプロセスが固まったとみなす(秒)。</summary>
    const double HangSeconds = 90;

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
            double started = Environment.TickCount64 / 1000.0;
            while (!p.WaitForExit(1000))
            {
                if (cts.IsCancellationRequested) { p.Kill(true); Osc.ReleaseUse(cfg.OscPort); return 0; }
                // 主ループが固まると status.json の更新が止まる(プロセスは生きたまま)。起動直後を除き、更新が途絶えたら殺して再起動する
                double sinceStart = Environment.TickCount64 / 1000.0 - started;
                if (sinceStart > HangSeconds && (DateTime.UtcNow - File.GetLastWriteTimeUtc(paths.Status)).TotalSeconds > HangSeconds)
                {
                    Log.Warn($"釣りプロセスの status.json が{HangSeconds:0}秒以上更新されません。固まったとみなして再起動します");
                    p.Kill(true);
                    p.WaitForExit(5000);
                    Osc.ReleaseUse(cfg.OscPort);
                    break;
                }
                // 設定は毎回読み直す(config.json の update_source を編集しても再起動なしで効く。手編集の途中で壊れていても落ちない)
                var live = cfg;
                try { live = Config.Load(paths.ConfigFile); }
                catch (Exception e) when (e is JsonException or IOException) { Log.Warn($"config.json を読めないため、直前の設定で続けます: {e.Message}"); }
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
                    Osc.ReleaseUse(cfg.OscPort);
                    bool swapped = SelfUpdate.Swap(self, next, args);
                    EventLog.Note(paths.Events, "self_update", ("ok", swapped));
                    if (swapped) { Log.Info("入れ替えました。新しい常駐に引き継ぎます"); return 0; }
                }
            }
            Log.Warn($"釣りプロセスが終了(code={p.ExitCode})。{cfg.RestartDelay:0.#}秒後に再起動します");
            cts.Token.WaitHandle.WaitOne((int)(cfg.RestartDelay * 1000));
        }
        return 0;
    }
}
