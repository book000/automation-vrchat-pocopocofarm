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
        var cfg = Config.Load(paths.ConfigFile);
        int bad = 0;
        void Ok(string m) => Console.WriteLine("  OK   " + m);
        void Ng(string m) { Console.WriteLine("  NG   " + m); bad++; }
        void Info(string m) => Console.WriteLine("  --   " + m);

        Console.WriteLine($"OS: {Environment.OSVersion.Version} / 画面キャプチャの黄色い枠消しは 10.0.22621 以降");
        if (Environment.OSVersion.Version.Build < 22621) Info("Windows 11 22H2 未満: 黄色い枠が消せない可能性があります(動作には影響しません)");
        Frame? f;
        if (CliArgs.Get(args, "--image") is { } img) f = Imaging.Load(img);
        else
        {
            var hwnd = WinApi.FindWindow(cfg.WindowTitle);
            if (hwnd == 0) { Ng($"VRChat のウィンドウ「{cfg.WindowTitle}」が見つかりません。起動してから再実行してください"); return 1; }
            Ok($"VRChat ウィンドウ {WinApi.Geometry(hwnd)}");
            using var cap = new WgcCapture(hwnd);
            cap.Start();
            Thread.Sleep(3000);
            f = null; long seen = 0; cap.CopyLatestTo(ref f, ref seen);
            var fps = cap.FrameCount / 3.0;
            if (f is null) { Ng("フレームを取得できません(画面キャプチャが開始できていません)"); return 1; }
            if (fps >= 20) Ok($"キャプチャ {fps:F0} fps"); else Ng($"キャプチャが {fps:F0} fps と低いです(VRChat が最小化・非表示の可能性)");
        }
        Ok($"画面 {f.Width}x{f.Height}(基準 {Layout.RefW}x{Layout.RefH} に対し倍率 {f.Height / (double)Layout.RefH:F2})");
        if (f.Height < 700) Ng("高さが 700px 未満です。ウキが小さすぎて検出が不安定になります");

        var rois = Layout.Resolve(cfg, f.Width, f.Height);
        var ocr = new WinOcr();
        var lvText = ocr.Read(f.CropRatio(rois.Level[0], rois.Level[1], rois.Level[2], rois.Level[3]));
        if (Hud.ParseLevel(lvText) is { } lv) Ok($"レベル領域で Lv{lv} を読めました");
        else
        {
            Ng($"レベル領域で「Lv N」を読めません(読み取り結果: 「{lvText}」)");
            if (HudLocator.FindLv(f, ocr) is { } b) Info($"画面上で「Lv」を {b.X:F0},{b.Y:F0} (幅{b.W:F0} 高さ{b.H:F0}) に発見。実行時は領域を自動補正します");
            else Info("画面のどこにも「Lv」が見つかりません。HUD が隠れているか、表示サイズが極端に違います");
        }
        if (args.Contains("--words")) foreach (var w in ocr.Words(f.CropRatio(0, 0.55, 0.75, 1.0))) Console.WriteLine($"      [{w.Text}] x={w.X + 0:F0} y={w.Y + 0.55 * f.Height:F0} w={w.W:F0} h={w.H:F0}");
        if (HudLocator.FindLv(f, ocr) is { } lb)
        {
            var (al, ax) = HudLocator.Apply(lb, f.Width, f.Height);
            double Dev(double[] a, double[] b) => Enumerable.Range(0, 4).Max(k => Math.Abs(a[k] - b[k]) * (k % 2 == 0 ? f.Width : f.Height));
            Info($"Lv 文字を ({lb.X:F0},{lb.Y:F0}) 高さ{lb.H:F0}px に発見。自動補正した領域と現在の領域のずれ: レベル {Dev(al, rois.Level):F0}px / 経験値バー {Dev(ax, rois.XpBar):F0}px");
        }
        var xp = Hud.XpFraction(f, rois.XpBar);
        if (xp is > 0) Ok($"経験値バー {xp:P0}"); else Info("経験値バーの黄色が見つかりません(レベルが上がった直後は 0% のことがあります)");
        var toast = ocr.Read(f.CropRatio(rois.Toast[0], rois.Toast[1], rois.Toast[2], rois.Toast[3]));
        Info($"通知領域の文字: 「{toast}」(釣れた直後でなければ空で構いません)");
        var scale = f.Height / (double)BobberDetector.RefHeight;
        var br = rois.Bobber;
        var ex = rois.Exclude.Select(e => ((int)(e[0] * f.Width), (int)(e[1] * f.Height), (int)(e[2] * f.Width), (int)(e[3] * f.Height))).ToList();
        var bob = BobberDetector.Detect(f, region: ((int)(br[0] * f.Width), (int)(br[1] * f.Height), (int)(br[2] * f.Width), (int)(br[3] * f.Height)), exclude: ex, scale: scale);
        if (bob is not null) Ok($"ウキを検出: {bob}"); else Info("ウキは見つかりません(釣り竿を投げて水面に浮いている状態で再実行すると確認できます)");
        if (cfg.DashboardHost == "127.0.0.1" || Firewall.HasRule(Environment.ProcessPath!, cfg.DashboardPort)) Ok("ダッシュボードの LAN 公開設定");
        else Ng($"LAN から {cfg.DashboardPort} 番を開く許可規則がありません → `pocofarm.exe setup` を実行してください");
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
        var ex = rois.Exclude.Select(e => ((int)(e[0] * f.Width) , (int)(e[1] * f.Height), (int)(e[2] * f.Width), (int)(e[3] * f.Height))).ToList();
        for (int i = 0; i < 20; i++) b = BobberDetector.Detect(f, region: (rx, ry, (int)(br[2] * f.Width), (int)(br[3] * f.Height)), exclude: ex, scale: scale);
        Console.WriteLine($"bobber(roi)={b} offset=({(int)(br[0] * f.Width)},{(int)(br[1] * f.Height)}) {sw.ElapsedMilliseconds / 20.0:F1}ms/detect");
        if (args.Length > 1) Imaging.SavePng(f, args[1]);
        return 0;
    }

    /// <summary>固定サンプル(tests/fixtures/ocr)で各エンジンの分類正答数と平均時間を測る。</summary>
    public static int BenchOcr(string[] args)
    {
        var dir = args.Length > 1 ? args[1] : Path.Combine("csharp", "tests", "fixtures", "ocr");
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
