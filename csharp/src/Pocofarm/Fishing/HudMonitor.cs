using System.Text.Json;
using System.Text.Json.Nodes;
using Pocofarm.Core;
using Pocofarm.Ocr;
using Pocofarm.Platform;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

/// <summary>
/// 画面左上の HUD(レベルと経験値バー)を読む。レベルは OCR、経験値は色で測る。
/// レベルが読めない状態が続いたら「Lv」の文字位置から領域を割り出し直し、画面サイズごとに data/ へ保存する。
/// </summary>
public sealed class HudMonitor
{
    static double Now => EventLog.Now();

    readonly Config cfg;
    readonly Paths paths;
    readonly EventLog events;
    readonly Status status;
    readonly SingleWorker worker = new("hud");
    WinOcr? ocr;
    Task<int?>? levelFuture;
    Task<(double X, double Y, double W, double H)?>? locateFuture;
    int miss;                                       // レベルが続けて読めなかった回数(領域ずれの疑い)
    double lastLocate, lastRead, lastWrite;
    (int W, int H, double[] Level, double[] Xp)? cal;   // 「Lv」の文字位置から割り出した領域(画面サイズごと)

    public HudMonitor(Config cfg, Paths paths, EventLog events, Status status)
    {
        this.cfg = cfg; this.paths = paths; this.events = events; this.status = status;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(paths.Calibration)) is JsonObject o)
                cal = ((int)o["w"]!, (int)o["h"]!, o["level"]!.AsArray().Select(x => (double)x!).ToArray(), o["xp"]!.AsArray().Select(x => (double)x!).ToArray());
        }
        catch (Exception ex) when (ex is IOException or JsonException or NullReferenceException or InvalidOperationException) { }
    }

    /// <summary>直近に読めたレベル。</summary>
    public int? Level { get; private set; }

    /// <summary>経験値バーの充填率(0〜1)。</summary>
    public double? Xp { get; private set; }

    /// <summary>保存済みの補正があれば、その画面サイズの領域に差し替える。</summary>
    public Rois Calibrated(Rois r, int w, int h) => cal is { } c && c.W == w && c.H == h ? r with { Level = c.Level, XpBar = c.Xp } : r;

    /// <summary>一定間隔でレベルを読み、必要なら領域を補正する。領域が変わったときだけ新しい Rois を返す。</summary>
    public Rois? Poll(double now, Frame f, Rois rois)
    {
        Rois? adopted = null;
        if (levelFuture is { IsCompleted: true })
        {
            int? lv = null;
            try { lv = levelFuture.Result; }
            catch (Exception ex) { Log.Exception("HUD OCR 失敗", ex.GetBaseException()); }
            levelFuture = null;
            // 誤読で推移が乱れないよう、直前から 0..2 上がるまでの値だけ採用する
            if (lv is { } v && (Level is null || (v - Level) is >= 0 and <= 2))
            {
                bool changed = v != Level;
                if (changed && Level is not null) Log.Info($"レベルアップ: Lv{Level} -> Lv{v}");
                Level = v;
                miss = 0;
                Xp = Hud.XpFraction(f, rois.XpBar);
                status.Set("hud", new Dictionary<string, object?> { ["level"] = v, ["xp"] = Xp, ["ts"] = Now });
                if (changed || now - lastWrite >= cfg.HudWriteInterval)
                {
                    lastWrite = now;
                    events.Write("hud", ("level", v), ("xp", Xp));
                }
            }
            else miss++;
        }
        if (locateFuture is { IsCompleted: true })
        {
            try { if (locateFuture.Result is { } box) adopted = Adopt(box, f, rois); }
            catch (Exception ex) { Log.Exception("HUD 位置の探索に失敗", ex.GetBaseException()); }
            locateFuture = null;
        }
        if (locateFuture is null && miss >= cfg.HudMissBeforeLocate && now - lastLocate >= cfg.HudLocateInterval)
        {
            lastLocate = now;
            var snap = f.Clone();
            locateFuture = worker.Submit(() => { ocr ??= new WinOcr(); return HudLocator.FindLv(snap, ocr); });
        }
        if (levelFuture is null && now - lastRead >= cfg.HudInterval)
        {
            lastRead = now;
            var l = (adopted ?? rois).Level;
            var crop = f.CropRatio(l[0], l[1], l[2], l[3]);
            levelFuture = worker.Submit(() => { ocr ??= new WinOcr(); return Hud.ParseLevel(ocr.Read(crop)); });
        }
        return adopted;
    }

    /// <summary>「Lv」の文字位置から割り出した領域を採用する(現在の領域とほぼ同じなら読めない原因は別なので何もしない)。</summary>
    Rois? Adopt((double X, double Y, double W, double H) box, Frame f, Rois rois)
    {
        var (lvl, xp) = HudLocator.Apply(box, f.Width, f.Height);
        double dev = Enumerable.Range(0, 4).Max(k => Math.Abs(lvl[k] - rois.Level[k]) * (k % 2 == 0 ? f.Width : f.Height));
        if (dev <= cfg.HudRecalibrateMinPx) return null;
        cal = (f.Width, f.Height, lvl, xp);
        miss = 0;
        Log.Warn($"HUD のレベル表示が既定の位置で読めないため、画面上の「Lv」の位置({box.X:F0},{box.Y:F0} 高さ{box.H:F0}px)に合わせて領域を自動補正しました(ずれ {dev:F0}px)");
        events.Write("hud_calibrated", ("w", f.Width), ("h", f.Height), ("lv_x", Math.Round(box.X)), ("lv_y", Math.Round(box.Y)), ("lv_h", Math.Round(box.H)), ("dev_px", Math.Round(dev)));
        try { File.WriteAllText(paths.Calibration, new JsonObject { ["w"] = f.Width, ["h"] = f.Height, ["level"] = new JsonArray(lvl.Select(x => (JsonNode)x).ToArray()), ["xp"] = new JsonArray(xp.Select(x => (JsonNode)x).ToArray()) }.ToJsonString()); }
        catch (IOException) { }
        return rois with { Level = lvl, XpBar = xp };
    }
}
