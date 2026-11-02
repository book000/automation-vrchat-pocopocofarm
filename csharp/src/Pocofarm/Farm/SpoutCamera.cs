using System.Text.RegularExpressions;
using Pocofarm.Core;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Farm;

/// <summary>
/// VRChat のカメラを人手なしで開く(Esc→カメラ→Stream Camera)。実測した仕様:
/// ・/usercamera/* の OSC はカメラが開いている間だけ効く(Mode でカメラを開くことはできない)
/// ・Spout にファームが映るのはカメラが開いている間だけ(閉じて配信だけ続けると、自分の視点に戻る)
/// ・配信中は VRChat の HUD(レベル・釣果)が消えるので、撮影の間だけ配信する
/// 各段階で画面を確かめ、想定外の画面ならクリックせずに中止する(別ワールドへ移動する事故を防ぐ)。
/// </summary>
public static class SpoutCamera
{
    // カメラタブはアイコンだけで文字が無いので位置は比率(1920x1009 で採寸)。ボタンは OCR で文字を探してから押す

    /// <summary>画面操作は同時に 1 つだけ(手動の入り切りと定点撮影の取り合い防止)。</summary>
    public static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>失敗時の画面を保存するフォルダ(通常は data)。</summary>
    public static string DebugDir { get; set; } = "data";

    public static bool IsStreaming() => SpoutReceiver.ListSenders().Any(n => n.StartsWith("VRC", StringComparison.OrdinalIgnoreCase));

    /// <summary>カメラ(ストリームモード)を開く。失敗時は理由を返し(後始末済み)、成功なら null。呼び出し側で釣りを止めておくこと。</summary>
    public static async Task<string?> OpenAsync(Config cfg)
    {
        var hwnd = WinApi.FindWindow(cfg.WindowTitle);
        if (hwnd == 0) return "VRChat のウィンドウが見つかりません";
        if (!WinApi.EnsureForeground(hwnd)) return "VRChat を前面にできません";
        var saved = WinApi.CursorPos();
        using var cap = new WgcCapture(hwnd);
        cap.Start();
        var ocr = new WinOcr();
        try
        {
            // 速い経路(OCR を使わず色だけで確認)。確認できなければ従来の OCR 経路に回す
            if (cfg.FastCameraOpen && await FastOpenAsync(cfg, hwnd, cap)) return null;
            var err = await RunAsync(cfg, hwnd, cap, ocr);
            if (err is not null) await CleanUpAsync(cfg, hwnd, cap, ocr);
            return err;
        }
        finally { WinApi.MoveCursor(saved.X, saved.Y); }
    }

    /// <summary>配信を止めてカメラを閉じる(カメラは開いている前提)。</summary>
    public static async Task CloseAsync(Config cfg)
    {
        Osc.SetStreaming(false, cfg.OscPort);
        await Task.Delay(300);
        Osc.SetMode(0, cfg.OscPort);
        await Task.Delay(400);
    }

    /// <summary>メニューは閉じている前提で、Esc → カメラタブ(色で確認)→ Stream ボタンだけで開く。確認できなければ false(メニューが開いたままの可能性があるので、呼び出し側は従来経路で後始末する)。</summary>
    static async Task<bool> FastOpenAsync(Config cfg, nint hwnd, WgcCapture cap)
    {
        Frame? frame = null; long seen = 0;
        WinApi.KeyTap(0x1B, 0x01);
        await Task.Delay(500);
        var g = WinApi.Geometry(hwnd);
        // 覚えた位置が外れたときだけ、形で探し直す(探すのに時間がかかるので)
        if (learnedTab is { } lt && await ClickTabAsync(cap, g, [lt]) is not null) { await ClickStreamAsync(cfg, cap, g); return true; }
        var tabs = new List<(double X, double Y)>();
        if (LatestFrame(cap, ref frame, ref seen) && IconFinder.Find(frame!, "ui_tab_camera.png", TabRegion, 0.8) is { } hit) tabs.Add((hit.X, hit.Y));
        tabs.Add((cfg.CameraTab[0], cfg.CameraTab[1]));
        if (await ClickTabAsync(cap, g, tabs) is not null)
        {
            await ClickStreamAsync(cfg, cap, g);
            return true;
        }
        Log.Warn("Spout: 速い経路でカメラタブを確認できません");
        return false;
    }

    static readonly Regex MenuWord = new(@"^(Friends|Profile|Launch|Pad|Worlds|Avatars|Social|Groups|Shop|Respawn|Safety)$", RegexOptions.IgnoreCase);

    static async Task<string?> RunAsync(Config cfg, nint hwnd, WgcCapture cap, WinOcr ocr)
    {
        Frame? frame = null; long seen = 0;
        async Task<List<(string Text, double X, double Y, double W, double H)>> ReadAsync()
        {
            for (int i = 0; i < 20 && !cap.CopyLatestTo(ref frame, ref seen); i++) await Task.Delay(50);
            return frame is null ? [] : await Task.Run(() => ocr.Words(frame));
        }
        async Task<List<(string Text, double X, double Y, double W, double H)>?> WaitAsync(Func<List<(string Text, double X, double Y, double W, double H)>, bool> ok, int ms)
        {
            for (var t = Environment.TickCount64; Environment.TickCount64 - t < ms;)
            {
                var w = await ReadAsync();
                if (ok(w)) return w;
                await Task.Delay(250);
            }
            return null;
        }
        bool MenuOpen(List<(string Text, double X, double Y, double W, double H)> w) => w.Count(x => MenuWord.IsMatch(x.Text)) >= 2;

        // 1. メニューが開いていたら先に閉じる(Esc は開閉の切り替えなので状態を見てから押す)
        Log.Info("Spout: 従来経路を開始");
        if (MenuOpen(await ReadAsync())) { WinApi.KeyTap(0x1B, 0x01); await Task.Delay(700); if (MenuOpen(await ReadAsync())) return "メニューを閉じられません"; }

        // 2. メニューを開く
        WinApi.KeyTap(0x1B, 0x01);
        if (await WaitAsync(MenuOpen, 3000) is null)
        {
            Log.Warn("Spout: クイックメニューが見えません。OCR: " + string.Join(" ", (await ReadAsync()).Select(x => x.Text)));
            if (frame is not null) Imaging.SavePng(frame, Path.Combine(DebugDir, "spout_debug_menu.png"));
            return "クイックメニューが開きません(画面を確認できず中止)";
        }

        // 3. カメラタブを押し、タブが選択色(水色)になったことで「カメラのページが出ている」と確認する(ボタンの文字は小さく OCR で読めない)
        var g = WinApi.Geometry(hwnd);
        var words = await ReadAsync();
        var cands = new List<(double X, double Y)>();
        if (learnedTab is { } lt) cands.Add(lt);
        if (frame is not null && IconFinder.Find(frame, "ui_tab_camera.png", TabRegion, 0.8) is { } iconHit) cands.Add((iconHit.X, iconHit.Y));
        if (TabFromMenu(words, g.Width, g.Height) is { } fromMenu) { cands.Add(fromMenu); foreach (var dy in new[] { -0.02, 0.02, -0.04, 0.04 }) cands.Add((fromMenu.X, fromMenu.Y + dy)); }
        cands.Add((cfg.CameraTab[0], cfg.CameraTab[1]));
        Log.Info("Spout: カメラタブの候補 " + string.Join(" ", cands.Select(c => $"({c.X:F3},{c.Y:F3})")) + " 行の単語: " + string.Join(",", words.Where(x => Regex.IsMatch(x.Text, "^(Home|Respawn|Select|Safety)$", RegexOptions.IgnoreCase)).Select(x => $"{x.Text}@{x.X + x.W / 2:F0},{x.Y + x.H / 2:F0}")));
        bool onCameraPage = await ClickTabAsync(cap, g, cands) is not null;
        if (!onCameraPage)
        {
            Log.Warn("Spout: カメラのページを確認できません。OCR: " + string.Join(" ", (await ReadAsync()).Select(x => x.Text)));
            if (cap.CopyLatestTo(ref frame, ref seen) || frame is not null) Imaging.SavePng(frame!, Path.Combine(DebugDir, "spout_debug.png"));
            return "カメラのメニューを確認できず中止しました";
        }

        // 4. 「Stream Camera」ボタンを押し、メニューが閉じた(カメラが開いた)ことを確認
        await ClickStreamAsync(cfg, cap, g);
        if (await WaitAsync(x => !MenuOpen(x), 3000) is null) return "カメラが開いたことを確認できず中止しました";

        return null;
    }

    static readonly (double X0, double Y0, double X1, double Y1) TabRegion = (0.2, 0.6, 0.8, 1.0), StreamRegion = (0.2, 0.25, 0.8, 0.7);

    static bool LatestFrame(WgcCapture cap, ref Frame? frame, ref long seen)
    {
        for (int k = 0; k < 20 && !cap.CopyLatestTo(ref frame, ref seen); k++) Thread.Sleep(50);
        return frame is not null;
    }

    /// <summary>カメラページの「Stream Camera」ボタンを、アイコンの形で探して押す(見つからなければ設定の比率)。</summary>
    static async Task ClickStreamAsync(Config cfg, WgcCapture cap, ClientGeometry g)
    {
        Frame? frame = null; long seen = 0;
        await Task.Delay(300);
        double x = cfg.StreamButton[0], y = cfg.StreamButton[1];
        if (LatestFrame(cap, ref frame, ref seen) && IconFinder.Find(frame!, "ui_stream_camera.png", StreamRegion, 0.85) is { } hit)
        {
            Log.Info($"Spout: Stream Camera ボタンをアイコンで検出 ({hit.X:F3},{hit.Y:F3}) 一致 {hit.Score:F2} 倍率 {hit.Scale:F2}");
            (x, y) = (hit.X, hit.Y);
        }
        else Log.Warn("Spout: Stream Camera ボタンのアイコンが見つかりません。設定の位置を使います");
        WinApi.ClickAt(g.Left + (int)(g.Width * x), g.Top + (int)(g.Height * y));
        await Task.Delay(600);
    }

    /// <summary>画面の縦横比に応じてメニューの配置が変わるので、見つけたカメラタブの位置(画面比率)を覚えて次から最初に試す。</summary>
    static (double X, double Y)? learnedTab;

    // メニュー下の行(Home / Respawn / Select / Safety)の文字の中心の x を、Home=0・Safety=1 としたときの位置
    static readonly Dictionary<string, double> RowX = new(StringComparer.OrdinalIgnoreCase) { ["Home"] = 0, ["Respawn"] = 0.334, ["Select"] = 0.612, ["Safety"] = 1 };

    // カメラタブの位置(同じ尺度。実測: x は行の 0.47、y は行から 0.158 下)。メニューは 1 枚の板なので縦横比が変わってもこの比は変わらない
    const double TabRowX = 0.47, TabBelowRow = 0.158;

    /// <summary>行の文字(OCR で 2 つ以上読めれば足りる)から、その下にあるカメラタブのおおよその位置(画面比率)を求める。</summary>
    static (double X, double Y)? TabFromMenu(List<(string Text, double X, double Y, double W, double H)> words, int w, int h)
    {
        var row = words.Where(x => RowX.ContainsKey(x.Text)).Select(x => (F: RowX[x.Text], X: x.X + x.W / 2, Y: x.Y + x.H / 2)).ToList();
        if (row.Count < 2) return null;
        var (lo, hi) = (row.MinBy(r => r.F), row.MaxBy(r => r.F));
        if (hi.F - lo.F < 0.2) return null;
        double span = (hi.X - lo.X) / (hi.F - lo.F), home = lo.X - lo.F * span;
        return ((home + TabRowX * span) / w, (row.Average(r => r.Y) + TabBelowRow * span) / h);
    }

    /// <summary>候補の位置を順にクリックし、その場所のタブが選択色(水色)になった位置を返す(ならなければ null)。見つけた位置は覚える。</summary>
    static async Task<(double X, double Y)?> ClickTabAsync(WgcCapture cap, ClientGeometry g, IEnumerable<(double X, double Y)> candidates)
    {
        Frame? frame = null; long seen = 0;
        foreach (var c in candidates)
        {
            WinApi.ClickAt(g.Left + (int)(g.Width * c.X), g.Top + (int)(g.Height * c.Y));
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(250);
                for (int k = 0; k < 20 && !cap.CopyLatestTo(ref frame, ref seen); k++) await Task.Delay(50);
                if (frame is not null && TabSelectedAt(frame, (int)(frame.Width * c.X), (int)(frame.Height * c.Y))) { learnedTab = c; return c; }
            }
        }
        return null;
    }

    /// <summary>(cx, cy) の周り 29x29 画素のうち 2 割以上が選択色(環境の明るさで (0,147,154)〜明るい水色まで変わる。未選択は灰色)か。</summary>
    static bool TabSelectedAt(Frame f, int cx, int cy)
    {
        const int Half = 14;
        int hit = 0, n = 0;
        for (int y = Math.Max(0, cy - Half); y <= Math.Min(f.Height - 1, cy + Half); y++)
            for (int x = Math.Max(0, cx - Half); x <= Math.Min(f.Width - 1, cx + Half); x++)
            {
                int i = (y * f.Width + x) * 4, b = f.Data[i], gr = f.Data[i + 1], r = f.Data[i + 2];
                n++;
                if (b > 100 && gr > 100 && r < 0.4 * Math.Min(b, gr)) hit++;
            }
        return n > 0 && hit >= n * 0.2;
    }

    /// <summary>中止時の後始末: カメラが開いていれば閉じ、メニューが開いていれば Esc で閉じる。</summary>
    static async Task CleanUpAsync(Config cfg, nint hwnd, WgcCapture cap, WinOcr ocr)
    {
        Frame? f = null; long seen = 0;
        for (int i = 0; i < 3; i++)
        {
            for (int k = 0; k < 20 && !cap.CopyLatestTo(ref f, ref seen); k++) await Task.Delay(50);
            var w = f is null ? [] : ocr.Words(f);
            Osc.SetMode(0, cfg.OscPort);                       // カメラが開いていれば閉じる(閉じていれば無害)
            if (w.Count(x => MenuWord.IsMatch(x.Text)) >= 2) { WinApi.KeyTap(0x1B, 0x01); await Task.Delay(700); continue; }
            break;
        }
    }
}
