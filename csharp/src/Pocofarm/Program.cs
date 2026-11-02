using Pocofarm.Cli;

namespace Pocofarm;

/// <summary>エントリポイント。サブコマンドで各機能を起動する。</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cmd = args.Length > 0 ? args[0] : "help";
        return cmd switch
        {
            "capture-test" => Diagnostics.CaptureTest(args),
            "ocr-image" => Diagnostics.OcrImage(args),
            "bench-ocr" => Diagnostics.BenchOcr(args),
            "doctor" => Diagnostics.Doctor(args),
            "run" => RunCommand.Run(args),
            "all" => Supervisor.Run(args),
            "cleanup" => Maintenance.Cleanup(args),
            "init-config" => Maintenance.InitConfig(args),
            "setup" => Maintenance.Setup(args),
            _ => Help(),
        };
    }

    static int Help()
    {
        Console.WriteLine("pocofarm setup (LAN公開のファイアウォール設定) | doctor [--image <png>] (初回点検) | all|run [--root <dir>] [--host <ip>] [--port <n>] [--dry-run] [--paused] | capture-test [out.png] | ocr-image <png> | bench-ocr [dir] [-v]");
        return 0;
    }
}
