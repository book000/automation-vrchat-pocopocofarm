using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Cli;

/// <summary>釣りとダッシュボードの本体。常駐の子プロセスとして、または単独で動く。</summary>
public static class RunCommand
{
    /// <summary>釣りとダッシュボードを無期限に実行する。--dry-run ではクリックを送らず前面化もしない(並走中の比較用)。</summary>
    public static int Run(string[] args)
    {
        var paths = CliArgs.Paths(args);
        Log.Setup(paths.Log);
        WinApi.HighResolutionTimer();
        // VRChat と CPU を取り合う環境でも、沈みの検出が間に合うよう優先度を少し上げる
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var cfg = Config.Load(paths.ConfigFile);
        var host = CliArgs.Get(args, "--host") ?? cfg.DashboardHost;
        var port = int.Parse(CliArgs.Get(args, "--port") ?? cfg.DashboardPort.ToString());
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        if (host != "127.0.0.1" && !Firewall.HasRule(Environment.ProcessPath!, port))
            Log.Warn($"LAN からダッシュボードを開く許可規則がありません。 `{Path.GetFileName(Environment.ProcessPath)} setup` を実行してください(UAC が出ます)");
        var web = Web.DashboardServer.StartAsync(paths, cfg, host, port).GetAwaiter().GetResult();
        try { new Fishing.Fisher(cfg, paths, args.Contains("--dry-run"), args.Contains("--paused") || WasPaused(paths)).Run(cts.Token); }
        finally { web.StopAsync().GetAwaiter().GetResult(); }
        return 0;
    }

    /// <summary>前回の実行が一時停止中だったか。手動操作中に再起動(クラッシュ復帰・入れ替え)しても、勝手にクリックしないため。</summary>
    static bool WasPaused(Paths paths)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.Status))?["paused"]?.GetValue<bool>() ?? false; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return false; }
    }
}
