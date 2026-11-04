using Pocofarm.Core;
using Pocofarm.Ocr;
using Pocofarm.Platform;

namespace Pocofarm.Fishing;

/// <summary>
/// 画面右のトースト領域を OCR で読み続ける。読み取りは別スレッドで行い、完了した結果(Sighting)を取り出し口に積む。
/// Windows OCR で読み、結果語らしいのに分類できないときだけ RapidOCR 相当で補う。結果不明の試行には直近の画像から再読取りで回復する。
/// </summary>
public sealed class ToastReader(Config cfg)
{
    readonly SingleWorker worker = new("ocr");
    readonly Queue<Sighting> sightings = new();
    readonly LinkedList<(double T, Frame Crop)> crops = new();
    readonly object rapidGate = new();
    WinOcr? winOcr;
    PpOcr? rapidEngine;
    Task<List<Sighting>>? future;
    double lastSubmit;

    PpOcr Rapid { get { lock (rapidGate) return rapidEngine ??= new PpOcr(); } }

    /// <summary>OCR スレッド: Windows OCR で読み、足りなければ RapidOCR で補う。</summary>
    List<Sighting> Job(double t, Frame crop)
    {
        winOcr ??= new WinOcr();
        var text = winOcr.Read(crop);
        var toast = Toast.Classify(text, cfg.KnownFish);
        // 釣果は魚名・経験値・所持数まで欲しいので、不完全なら RapidOCR で補う
        bool incomplete = toast is { Kind: Toast.Caught } && !(toast.Fish.Length > 0 && toast.Exp is not null && toast.Count is not null);
        if (cfg.RapidOcr && ((toast is null && text.Length >= 5) || incomplete))
        {
            try
            {
                var text2 = Rapid.Read(crop);
                var toast2 = Toast.Classify(text2, cfg.KnownFish);
                if (toast2 is not null && (toast is null || toast2.Score >= toast.Score))
                    return [new Sighting(t, text2, toast2, "rapidocr")];
                // ねこちゃんの通知は分類対象ではないが、本文は PP-OCR の方が正確に読める
                if (toast is null && toast2 is null && Toast.ExtractNeko(text2) is not null) return [new Sighting(t, text2, null, "rapidocr")];
            }
            catch (Exception ex) { Log.Exception("RapidOCR 失敗", ex); }
        }
        return [new Sighting(t, text, toast, "winocr")];
    }

    /// <summary>一定間隔でトースト領域(画面比率 roi)を OCR スレッドに投げ、完了した結果を取り込む。scale は画面高さ ÷ 基準高さ。</summary>
    public void Pump(double now, Frame f, double[] roi, double scale)
    {
        if (future is { IsCompleted: true })
        {
            try { foreach (var s in future.Result) sightings.Enqueue(s); }
            catch (Exception ex) { Log.Exception("OCR 例外", ex.GetBaseException()); }
            future = null;
        }
        if (future is not null || now - lastSubmit < cfg.OcrInterval) return;
        lastSubmit = now;
        var crop = f.CropRatio(roi[0], roi[1], roi[2], roi[3]);
        crops.AddLast((now, crop));
        if (crops.Count > cfg.ToastCropHistory) crops.RemoveFirst();
        var ocrCrop = scale > cfg.OcrDownscaleAbove ? crop.Downscale(1 / scale) : crop;     // 基準より大きい画面では文字が大きすぎて誤読が増える
        future = worker.Submit(() => Job(now, ocrCrop));
    }

    /// <summary>取り込み済みの読み取りを 1 件取り出す。</summary>
    public bool TryDequeue(out Sighting s) => sightings.TryDequeue(out s!);

    /// <summary>未処理の読み取りを捨てる(画面が前面でない間の写り込みなど)。</summary>
    public void Clear() => sightings.Clear();

    /// <summary>結果不明の試行を、引いた後の直近の画像から再読取りして回復する。見つからなければ null。</summary>
    /// <remarks>
    /// OCR スレッドで実行する(RapidOCR は約 0.9 秒/枚で、主ループを止めないため)。同じ OCR スレッドに直列化されるので、
    /// RapidOCR のエンジンを複数スレッドから同時に使うことにもならない。読む画像がなければ null。
    /// </remarks>
    public Task<RecoverResult>? RecoverAsync(Attempt a)
    {
        if (!cfg.RapidOcr) return null;
        var window = crops.Where(c => c.T >= a.TClick - 0.1).Select(c => c.Crop).ToList();     // 主スレッドで写しを取る
        if (window.Count == 0) return null;
        bool probe = a.Probe;
        return worker.Submit(() => RecoverJob(window, probe));
    }

    RecoverResult RecoverJob(List<Frame> window, bool probe)
    {
        var info = new List<string>();
        foreach (var crop in window.Where((_, i) => i % Math.Max(1, window.Count / 6) == 0).Take(probe ? 1 : 6))   // 確認クリックは通知なしが普通なので 1 枚だけ
        {
            try
            {
                var tt = Toast.Classify(Rapid.Read(crop), cfg.KnownFish);
                if (tt is null) continue;
                // レベルアップ/実績は結果ではない。釣れた根拠として控えるだけにして、釣果/早すぎ/逃げられた を探し続ける
                if (tt.Kind is Toast.LevelUp or Toast.Achieve) { if (!info.Contains(tt.Kind)) info.Add(tt.Kind); continue; }
                Log.Info($"結果不明を再読取りで回復: {tt.Kind}");
                return new RecoverResult(tt, info);
            }
            catch (Exception ex) { Log.Exception("RapidOCR 再読取り失敗", ex); }
        }
        return new RecoverResult(null, info);
    }
}

/// <summary>再読取りの結果。Info は同時に出たレベルアップ/実績通知。</summary>
public sealed record RecoverResult(Toast? Toast, List<string> Info);
