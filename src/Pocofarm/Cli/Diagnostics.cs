using Pocofarm.Core;
using Pocofarm.Farm;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Cli;

/// <summary>初回点検と、画面・OCR の動作確認用コマンド。いずれもクリックはしない。</summary>
public static class Diagnostics
{
    /// <summary>別マシンでの初回確認。実画面(または --image の画像)で各前提を点検し、問題を日本語で報告する。クリックはしない。</summary>
    public static int Doctor(string[] args)
    {
        var paths = CliArgs.Paths(args);
        var cfg = Config.LoadSafe(paths.ConfigFile);
        Console.WriteLine($"OS: {Environment.OSVersion.Version} / 画面キャプチャの黄色い枠消しは 10.0.22621 以降");
        var report = SelfCheck.Run(cfg, new SelfCheckOptions
        {
            Image = CliArgs.Get(args, "--image"),
            Words = args.Contains("--words"),
            Raw = Console.WriteLine,
            OnResult = r => Console.WriteLine(r.Level switch { CheckLevel.Ok => "  OK   ", CheckLevel.Fail => "  NG   ", CheckLevel.Warn => "  WARN ", _ => "  --   " } + r.Message),
        });
        if (report.Aborted) return 1;
        int bad = report.Results.Count(r => r.Level == CheckLevel.Fail);
        Console.WriteLine(bad == 0 ? "問題は見つかりませんでした。" : $"{bad} 件の問題があります。");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>VRChat を WGC で数秒撮り、フレーム率・サイズ・トースト領域の OCR を表示する。クリックはしない。</summary>
    public static int CaptureTest(string[] args)
    {
        var cfg = Config.Load("config.json");
        var hwnd = WinApi.FindWindow(cfg.WindowTitle);
        if (hwnd == 0) { Console.Error.WriteLine("VRChat window not found"); return 1; }
        using var cap = new WgcCapture(hwnd);
        cap.Start();
        Thread.Sleep(3000);
        Frame? f = null; long seen = 0; cap.CopyLatestTo(ref f, ref seen);
        Console.WriteLine($"frames={cap.FrameCount} ({cap.FrameCount / 3.0:F1}fps) size={f?.Width}x{f?.Height} geo={WinApi.Geometry(hwnd)}");
        if (f is null) return 2;
        var rois = Layout.Resolve(cfg, f.Width, f.Height);
        var crop = f.CropRatio(rois.Toast[0], rois.Toast[1], rois.Toast[2], rois.Toast[3]);
        Console.WriteLine("toast ocr: " + new WinOcr().Read(crop));
        var scale = f.Height / (double)BobberDetector.RefHeight;
        var br = rois.Bobber;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Bobber? b = null;
        int rx = (int)(br[0] * f.Width), ry = (int)(br[1] * f.Height);
        var ex = rois.Exclude.Select(e => ((int)(e[0] * f.Width), (int)(e[1] * f.Height), (int)(e[2] * f.Width), (int)(e[3] * f.Height))).ToList();
        for (int i = 0; i < 20; i++) b = BobberDetector.Detect(f, region: (rx, ry, (int)(br[2] * f.Width), (int)(br[3] * f.Height)), exclude: ex, scale: scale);
        Console.WriteLine($"bobber(roi)={b} offset=({(int)(br[0] * f.Width)},{(int)(br[1] * f.Height)}) {sw.ElapsedMilliseconds / 20.0:F1}ms/detect");
        if (args.Length > 1) Imaging.SavePng(f, args[1]);
        return 0;
    }

    /// <summary>固定サンプル(tests/fixtures/ocr)で各エンジンの分類正答数と平均時間を測る。</summary>
    public static int BenchOcr(string[] args)
    {
        var dir = args.Length > 1 ? args[1] : Path.Combine("tests", "fixtures", "ocr");
        var files = Directory.GetFiles(dir, "*.png").Order().ToList();
        var fish = new Config().KnownFish;
        IOcr[] engines = [new WinOcr(), new PpOcr()];
        foreach (var eng in engines)
        {
            int ok = 0, full = 0, caught = 0; double ms = 0;
            foreach (var f in files)
            {
                var kind = Path.GetFileName(f).Split('_')[0];
                var img = Imaging.Load(f);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var text = eng.Read(img);
                ms += sw.Elapsed.TotalMilliseconds;
                var t = Toast.Classify(text, fish);
                if (t?.Kind == kind) ok++;
                else if (args.Contains("-v")) Console.WriteLine($"  [{eng.Name}] {Path.GetFileName(f)} -> {text}");
                if (kind == "caught") { caught++; if (t is { Fish.Length: > 0, Exp: not null, Count: not null }) full++; }
            }
            Console.WriteLine($"{eng.Name,-9} 分類 {ok}/{files.Count}  魚名/XP/所持 {full}/{caught}  平均 {ms / files.Count:F0}ms");
        }
        return 0;
    }

    /// <summary>画像ファイルを Windows OCR で読む。</summary>
    public static int OcrImage(string[] args)
    {
        var text = new WinOcr().Read(Imaging.Load(args[1]));
        Console.WriteLine(text);
        Console.WriteLine(Toast.Classify(text, new Config().KnownFish));
        return 0;
    }
}
