using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pocofarm.Core;
using Pocofarm.Platform;

namespace Pocofarm.Fishing;

/// <summary>画面証拠(JPEG+JSON)の非同期保存と、期限・容量による自動掃除。</summary>
public sealed class EvidenceStore
{
    static readonly JsonSerializerOptions Opt = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly string dir, livePath;
    readonly double ttlSeconds;
    readonly long maxBytes;
    readonly BlockingCollection<Action> queue = new(64);
    readonly object cleanupGate = new();

    public EvidenceStore(string dir, string livePath, double ttlHours, int maxMb)
    {
        this.dir = dir; this.livePath = livePath;
        ttlSeconds = ttlHours * 3600;
        maxBytes = maxMb * 1024L * 1024L;
        Directory.CreateDirectory(dir);
        var t = new Thread(() => { foreach (var job in queue.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = "evidence" };
        t.Start();
    }

    /// <summary>フレームを縮小して非同期保存し、ファイル名を返す。frame は呼び出し側が以後触らないコピーを渡すこと。pooled なら保存後に FramePool へ返す。</summary>
    public string Save(Frame frame, string name, object meta, IReadOnlyList<Box>? boxes = null, bool pooled = false)
    {
        if (name == "unclassified_toast" && !TryReserveUnclassified()) return "";
        var fname = $"{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}_{name}.jpg";
        var b = boxes?.ToList() ?? [];
        // キューが詰まっているときは古い証拠を優先して新しいものを諦める(釣りの処理を止めない)
        if (!queue.TryAdd(() => { Write(frame, fname, meta, b); if (pooled) FramePool.Return(frame); })) Log.Warn($"証拠保存が詰まっているため破棄: {fname}");
        return fname;
    }

    /// <summary>unclassified_toast の証拠の上限。誤読の連発でディスクと掃除の負荷が増えるのを防ぐ。</summary>
    internal const int UnclassifiedCap = 500;

    int unclassifiedCount = -1;
    readonly object capGate = new();

    bool TryReserveUnclassified()
    {
        lock (capGate)
        {
            // 初回だけ既存の枚数を数える(掃除で減った分は次の起動で戻る)
            if (unclassifiedCount < 0)
                unclassifiedCount = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*_unclassified_toast.jpg").Count() : 0;
            if (unclassifiedCount >= UnclassifiedCap) return false;
            unclassifiedCount++;
            return true;
        }
    }

    void Write(Frame img, string fname, object meta, List<Box> boxes)
    {
        try
        {
            var path = Path.Combine(dir, fname);
            File.WriteAllBytes(path, Imaging.ToJpeg(img, boxes, 1100, 78, 14));
            File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(meta, Opt));
        }
        catch (Exception ex) { Log.Exception($"証拠保存に失敗: {fname}", ex); }
    }

    /// <summary>ダッシュボード用のライブ画像を原子的に更新する。</summary>
    public void WriteLive(Frame frame, IReadOnlyList<Box> boxes) =>
        AtomicFile.Write(livePath, Imaging.ToJpeg(frame, boxes, 960, 70, 18));

    /// <summary>掃除を保存と同じ作業スレッドへ回す(ファイルが多いと列挙が遅く、釣りの本線で呼ぶと止まるため)。詰まっていれば次の機会に回す。</summary>
    public void CleanupAsync() => queue.TryAdd(() => Cleanup());

    /// <summary>期限切れと容量超過の証拠を古い順に削除し、削除数を返す。</summary>
    public int Cleanup() { lock (cleanupGate) return CleanupDir(dir, ttlSeconds, maxBytes); }

    /// <summary>ディレクトリ内の jpg/json を TTL と総容量で掃除する。</summary>
    public static int CleanupDir(string directory, double ttlSeconds, long maxBytes)
    {
        if (!Directory.Exists(directory)) return 0;
        int removed = 0;
        var cutoff = DateTime.UtcNow.AddSeconds(-ttlSeconds);
        var keep = new List<FileInfo>();
        foreach (var f in new DirectoryInfo(directory).EnumerateFiles())
        {
            if (f.Extension is not (".jpg" or ".json")) continue;
            try
            {
                if (f.LastWriteTimeUtc < cutoff) { f.Delete(); removed++; }
                else keep.Add(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"証拠削除に失敗 {f.Name}: {ex.Message}"); }
        }
        keep.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        long total = keep.Sum(f => f.Length);
        foreach (var f in keep)
        {
            if (total <= maxBytes) break;
            try { long size = f.Length; f.Delete(); total -= size; removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (removed > 0) Log.Info($"証拠を{removed}件掃除しました");
        return removed;
    }
}
