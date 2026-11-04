using System.Text.Json;
using Pocofarm.Core;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Cli;

/// <summary>点検結果の段階。Info は参考情報(問題ではない)。</summary>
public enum CheckLevel { Ok, Info, Warn, Fail }

/// <summary>点検 1 項目の結果。</summary>
public sealed record CheckResult(string Name, CheckLevel Level, string Message);

/// <summary>点検の入力と出力先。</summary>
public sealed class SelfCheckOptions
{
    /// <summary>実画面の代わりに使う画像ファイル。</summary>
    public string? Image { get; init; }

    /// <summary>OCR の単語一覧も Raw へ出す(doctor --words)。</summary>
    public bool Words { get; init; }

    /// <summary>起動時の簡易点検。fps 計測を省き、フレームが来次第すぐ進む。</summary>
    public bool Quick { get; init; }

    /// <summary>結果が出るたびに呼ぶ(doctor が順に表示する)。</summary>
    public Action<CheckResult>? OnResult { get; init; }

    /// <summary>結果ではない補足行の出力先。</summary>
    public Action<string>? Raw { get; init; }
}

/// <summary>点検の全体結果。Aborted は前提(ウィンドウ・フレーム・OCR)が欠けて途中で止めたことを表す。</summary>
public sealed record SelfCheckReport(List<CheckResult> Results, bool Aborted)
{
    public bool NeedsAttention => Results.Any(r => r.Level == CheckLevel.Fail);
}

/// <summary>画面・OCR・HUD の前提を点検する。クリックは一切しない。doctor と起動時の自己点検が共用する。</summary>
public static class SelfCheck
{
    static readonly JsonSerializerOptions Opt = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    /// <summary>点検を実行する。例外は投げない(失敗は結果に入れる)。</summary>
    public static SelfCheckReport Run(Config cfg, SelfCheckOptions o)
    {
        var results = new List<CheckResult>();
        void Add(string name, CheckLevel level, string msg)
        {
            var r = new CheckResult(name, level, msg);
            results.Add(r);
            o.OnResult?.Invoke(r);
        }
        void Ok(string name, string m) => Add(name, CheckLevel.Ok, m);
        void Ng(string name, string m) => Add(name, CheckLevel.Fail, m);
        void Info(string name, string m) => Add(name, CheckLevel.Info, m);
        try
        {
            if (Environment.OSVersion.Version.Build < 22621) Info("os", "Windows 11 22H2 未満: 黄色い枠が消せない可能性があります(動作には影響しません)");
            Frame? f;
            if (o.Image is not null) f = Imaging.Load(o.Image);
            else
            {
                var hwnd = WinApi.FindWindow(cfg.WindowTitle);
                if (hwnd == 0) { Ng("window", $"VRChat のウィンドウ「{cfg.WindowTitle}」が見つかりません。起動してから再実行してください"); return new(results, true); }
                Ok("window", $"VRChat ウィンドウ {WinApi.Geometry(hwnd)}");
                using var cap = new WgcCapture(hwnd);
                cap.Start();
                f = null; long seen = 0;
                if (o.Quick)
                {
                    for (int i = 0; i < 20 && f is null; i++) { Thread.Sleep(100); cap.CopyLatestTo(ref f, ref seen); }
                }
                else
                {
                    Thread.Sleep(3000);
                    cap.CopyLatestTo(ref f, ref seen);
                }
                var fps = cap.FrameCount / 3.0;
                if (f is null) { Ng("capture", "フレームを取得できません(画面キャプチャが開始できていません)"); return new(results, true); }
                if (!o.Quick)
                {
                    if (fps >= 20) Ok("capture", $"キャプチャ {fps:F0} fps"); else Ng("capture", $"キャプチャが {fps:F0} fps と低いです(VRChat が最小化・非表示の可能性)");
                }
            }
            Ok("screen", $"画面 {f.Width}x{f.Height}(基準 {Layout.RefW}x{Layout.RefH} に対し倍率 {f.Height / (double)Layout.RefH:F2})");
            if (f.Height < 700) Ng("resolution", "高さが 700px 未満です。ウキが小さすぎて検出が不安定になります");

            var rois = Layout.Resolve(cfg, f.Width, f.Height);
            WinOcr ocr;
            try { ocr = new WinOcr(); }
            catch (InvalidOperationException oe) { Ng("ocr", $"{oe.Message}。Windows の設定で日本語の言語パック(OCR を含む)を追加してください"); return new(results, true); }
            var lvText = ocr.Read(f.CropRatio(rois.Level[0], rois.Level[1], rois.Level[2], rois.Level[3]));
            if (Hud.ParseLevel(lvText) is { } lv) Ok("hud_level", $"レベル領域で Lv{lv} を読めました");
            else
            {
                Ng("hud_level", $"レベル領域で「Lv N」を読めません(読み取り結果: 「{lvText}」)");
                if (HudLocator.FindLv(f, ocr) is { } b) Info("hud_locate", $"画面上で「Lv」を {b.X:F0},{b.Y:F0} (幅{b.W:F0} 高さ{b.H:F0}) に発見。実行時は領域を自動補正します");
                else Info("hud_locate", "画面のどこにも「Lv」が見つかりません。HUD が隠れているか、表示サイズが極端に違います");
            }
            if (o.Words) foreach (var w in ocr.Words(f.CropRatio(0, 0.55, 0.75, 1.0))) o.Raw?.Invoke($"      [{w.Text}] x={w.X + 0:F0} y={w.Y + 0.55 * f.Height:F0} w={w.W:F0} h={w.H:F0}");
            if (HudLocator.FindLv(f, ocr) is { } lb)
            {
                var (al, ax) = HudLocator.Apply(lb, f.Width, f.Height);
                double Dev(double[] a, double[] c) => Enumerable.Range(0, 4).Max(k => Math.Abs(a[k] - c[k]) * (k % 2 == 0 ? f.Width : f.Height));
                Info("hud_offset", $"Lv 文字を ({lb.X:F0},{lb.Y:F0}) 高さ{lb.H:F0}px に発見。自動補正した領域と現在の領域のずれ: レベル {Dev(al, rois.Level):F0}px / 経験値バー {Dev(ax, rois.XpBar):F0}px");
            }
            var xp = Hud.XpFraction(f, rois.XpBar);
            if (xp is > 0) Ok("xp_bar", $"経験値バー {xp:P0}"); else Info("xp_bar", "経験値バーの黄色が見つかりません(レベルが上がった直後は 0% のことがあります)");
            var toast = ocr.Read(f.CropRatio(rois.Toast[0], rois.Toast[1], rois.Toast[2], rois.Toast[3]));
            Info("toast", $"通知領域の文字: 「{toast}」(釣れた直後でなければ空で構いません)");
            var scale = f.Height / (double)BobberDetector.RefHeight;
            var br = rois.Bobber;
            var ex = rois.Exclude.Select(e => ((int)(e[0] * f.Width), (int)(e[1] * f.Height), (int)(e[2] * f.Width), (int)(e[3] * f.Height))).ToList();
            var bob = BobberDetector.Detect(f, region: ((int)(br[0] * f.Width), (int)(br[1] * f.Height), (int)(br[2] * f.Width), (int)(br[3] * f.Height)), exclude: ex, scale: scale);
            if (bob is not null) Ok("bobber", $"ウキを検出: {bob}"); else Info("bobber", "ウキは見つかりません(釣り竿を投げて水面に浮いている状態で再実行すると確認できます)");
            if (cfg.DashboardHost == "127.0.0.1" || Firewall.HasRule(Environment.ProcessPath!, cfg.DashboardPort)) Ok("dashboard_lan", "ダッシュボードの LAN 公開設定");
            else Ng("dashboard_lan", $"LAN から {cfg.DashboardPort} 番を開く許可規則がありません → `pocofarm.exe setup` を実行してください");
            return new(results, false);
        }
        catch (Exception e)
        {
            Ng("selfcheck", $"点検中に例外: {e.GetType().Name}: {e.Message}");
            return new(results, true);
        }
    }

    /// <summary>status.json / selfcheck.json / API に載せる形。</summary>
    public static object ToJson(SelfCheckReport r) => new
    {
        ts = EventLog.Now(),
        done = true,
        needs_attention = r.NeedsAttention,
        summary = r.NeedsAttention ? "要確認: " + string.Join(" / ", r.Results.Where(x => x.Level == CheckLevel.Fail).Select(x => x.Message)) : "問題なし",
        checks = r.Results.Select(x => new { name = x.Name, level = x.Level.ToString().ToLowerInvariant(), message = x.Message }).ToList(),
    };

    /// <summary>
    /// 釣りの開始時に 1 回だけ行う簡易点検。結果は status.json の selfcheck と data/selfcheck.json に載せる。
    /// 呼び出しは最大 waitMs ミリ秒だけ待ち、終わらなければ裏で続ける。失敗しても釣りは止めず、一時停止もしない。
    /// </summary>
    public static void RunAtStart(Config cfg, Paths paths, int waitMs = 4000)
    {
        lock (Status.Extra) Status.Extra["selfcheck"] = new { done = false };
        var task = Task.Run(() =>
        {
            try
            {
                var report = Run(cfg, new SelfCheckOptions { Quick = true });
                var json = ToJson(report);
                lock (Status.Extra) Status.Extra["selfcheck"] = json;
                AtomicFile.Write(paths.SelfCheck, JsonSerializer.SerializeToUtf8Bytes(json, Opt));
                foreach (var r in report.Results.Where(x => x.Level is CheckLevel.Fail or CheckLevel.Warn))
                    Log.Warn($"自己点検 [{r.Name}] {r.Message}");
                Log.Info(report.NeedsAttention ? "自己点検: 要確認の項目があります(一時停止はしません)" : "自己点検: 問題なし");
            }
            catch (Exception ex) { Log.Warn($"自己点検に失敗しました: {ex.Message}"); }
        });
        try { task.Wait(waitMs); } catch (AggregateException) { }
    }
}
