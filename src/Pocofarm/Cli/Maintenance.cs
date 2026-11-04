using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Cli;

/// <summary>掃除・ファイアウォール設定・既定設定の書き出し。</summary>
public static class Maintenance
{
    /// <summary>証拠画像を今すぐ掃除する。</summary>
    public static int Cleanup(string[] args)
    {
        var paths = CliArgs.Paths(args);
        var cfg = Config.LoadSafe(paths.ConfigFile);
        Console.WriteLine("削除: " + Fishing.EvidenceStore.CleanupDir(paths.Evidence, cfg.EvidenceTtlHours * 3600, cfg.EvidenceMaxMb * 1024L * 1024L));
        if (cfg.ArchiveKeepDays > 0) Console.WriteLine($"古いアーカイブの削除: {Housekeeping.PruneArchive(paths, cfg)}");
        return 0;
    }

    /// <summary>診断 zip を書き出す(ダッシュボードが落ちているとき用)。既定の出力先は --root 直下。</summary>
    public static int Diag(string[] args)
    {
        var paths = CliArgs.Paths(args);
        var out_ = CliArgs.Get(args, "--out") ?? Path.Combine(paths.Root, $"pocofarm-diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using (var fs = File.Create(out_)) DiagBundle.Build(paths, fs);
        Console.WriteLine(Path.GetFullPath(out_));
        return 0;
    }

    /// <summary>LAN からダッシュボードを開くためのファイアウォール規則を作る(UAC が出る)。</summary>
    public static int Setup(string[] args)
    {
        var paths = CliArgs.Paths(args);
        var port = int.Parse(CliArgs.Get(args, "--port") ?? Config.LoadSafe(paths.ConfigFile).DashboardPort.ToString());
        var exe = CliArgs.Get(args, "--exe") ?? Environment.ProcessPath!;
        if (CliArgs.Get(args, "--log") is { } log)
        {
            using var w = new StreamWriter(log, false, System.Text.Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(w);
            return Firewall.Setup(exe, port, true);
        }
        return Firewall.Setup(exe, port, args.Contains("--elevated"));
    }

    /// <summary>既定値の config.json を書き出す。</summary>
    public static int InitConfig(string[] args)
    {
        var paths = CliArgs.Paths(args);
        File.WriteAllText(paths.ConfigFile, System.Text.Json.JsonSerializer.Serialize(new Config(), Config.Json));
        Console.WriteLine(paths.ConfigFile);
        return 0;
    }
}
