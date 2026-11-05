using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pocofarm.Core;

/// <summary>釣果・失敗の記録(events.jsonl)。1 行 1 イベントの JSONL。</summary>
public sealed class EventLog
{
    public static readonly string[] Outcomes = ["caught", "early", "escaped", "unknown", "no_bite_reset", "cast_failed", "missed_bite"];

    public static readonly Dictionary<string, string> OutcomeJa = new()
    {
        ["caught"] = "釣果",
        ["early"] = "早すぎ",
        ["escaped"] = "逃げられた",
        ["unknown"] = "結果不明",
        ["no_bite_reset"] = "当たりなし引き直し",
        ["cast_failed"] = "投げ失敗",
        ["missed_bite"] = "当たり見逃し",
    };

    static readonly JsonSerializerOptions Opt = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly object gate = new();
    readonly string path;
    int seq;

    /// <summary>このセッションで書いた試行イベント(集計用)。毎回ファイル全体を読み直さずに済ませる。</summary>
    public List<JsonObject> SessionAttempts { get; } = [];

    /// <summary>メモリ上に持つ試行の上限。超えたら古い 1 割を捨てる(長時間の連続稼働で増え続けないように)。</summary>
    const int MaxSessionAttempts = 50000;

    /// <summary>このセッションの開始時刻(epoch 秒)。リセットで更新される。</summary>
    public double SessionStart { get; private set; } = Now();

    public EventLog(string path) : this(path, true) { }

    EventLog(string path, bool scanSeq)
    {
        this.path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (scanSeq)
        {
            seq = LastSeq();      // 退避しても試行番号は続ける
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                {
                    var dir = Path.Combine(Path.GetDirectoryName(path)!, "archive");
                    Directory.CreateDirectory(dir);
                    File.Move(path, Path.Combine(dir, $"events_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"), true);
                }
            }
            catch (IOException e) { Log.Warn($"events.jsonl の退避に失敗しました: {e.Message}"); }
        }
    }

    /// <summary>起動時にこのサイズを超えていれば data/archive へ退避する。</summary>
    const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>試行番号の走査なしで 1 件追記する。設定変更・入れ替え・自動調整など、釣りの本体以外からの出来事を残す用(全件を読み直さない)。</summary>
    public static void Note(string path, string type, params (string Key, object? Value)[] fields) => new EventLog(path, false).Write(type, fields);

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    int LastSeq()
    {
        int max = 0;
        if (!File.Exists(path)) return 0;
        foreach (var line in ReadLines(path))
        {
            // 途中書き込みで壊れた行や空行は飛ばす(Read と同じ扱い。ここで落とすと起動のたびに失敗する)
            try { if (JsonNode.Parse(line) is JsonObject o && o.Num("attempt") is { } a) max = Math.Max(max, (int)a); }
            catch (JsonException) { }
        }
        return max;
    }

    /// <summary>書き込み中の相手がいてもファイルを共有読みで列挙する。壊れた行は呼び出し側で飛ばす。</summary>
    static IEnumerable<string> ReadLines(string p)
    {
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } line) yield return line;
    }

    /// <summary>試行番号を採番する(再起動後も連番を維持)。</summary>
    public int NextAttempt() { lock (gate) return ++seq; }

    /// <summary>現在の記録を data/archive に退避して空から始める。退避先を返す(記録が無ければ null)。</summary>
    public string? Reset()
    {
        lock (gate)
        {
            string? dest = null;
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                var dir = Path.Combine(Path.GetDirectoryName(path)!, "archive");
                Directory.CreateDirectory(dir);
                dest = Path.Combine(dir, $"events_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
                for (int i = 0; i < 10; i++)      // 読み込み中だと Windows では置換に失敗する
                {
                    try { File.Move(path, dest, true); break; }
                    catch (IOException) { Thread.Sleep(200); }
                }
            }
            seq = 0;
            SessionStart = Now();
            lock (SessionAttempts) { SessionAttempts.Clear(); attemptVersion++; }
            return dest;
        }
    }

    /// <summary>イベントを追記する。</summary>
    public void Write(string type, params (string Key, object? Value)[] fields) => Write(type, fields.Select(f => KeyValuePair.Create(f.Key, f.Value)));

    /// <summary>イベントを追記する(項目を辞書で渡す版)。</summary>
    public void Write(string type, IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var rec = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["type"] = type,
        };
        foreach (var (k, v) in fields) rec[k] = v;
        var line = JsonSerializer.Serialize(rec, Opt);
        lock (gate) Append(line);
        if (type == "attempt" && JsonNode.Parse(line) is JsonObject o) lock (SessionAttempts) { SessionAttempts.Add(o); attemptVersion++; if (SessionAttempts.Count > MaxSessionAttempts) SessionAttempts.RemoveRange(0, MaxSessionAttempts / 10); }
    }

    /// <summary>1 行追記する。ウイルス対策などの一時的なロックは短く待って再試行し、それでも書けなければ記録を諦める(釣りの本線を止めない)。</summary>
    void Append(string line)
    {
        for (int i = 0; ; i++)
        {
            try { File.AppendAllText(path, line + "\n"); return; }
            catch (IOException) when (i < 3) { Thread.Sleep(50); }
            catch (IOException e) { Log.Warn($"イベントを書けませんでした: {e.Message}"); return; }
        }
    }

    /// <summary>イベントを読み出す。壊れた行は飛ばす。since 以降(epoch 秒)だけに絞れる。</summary>
    public static List<JsonObject> Read(string path, double? since = null)
    {
        var list = new List<JsonObject>();
        if (!File.Exists(path)) return list;
        foreach (var line in ReadLines(path))
        {
            JsonObject? o;
            try { o = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
            if (o is null) continue;
            if (since is { } s && (Timestamp(o) is not { } t || t < s)) continue;
            list.Add(o);
        }
        return list;
    }

    /// <summary>イベントの ts を epoch 秒にする。</summary>
    public static double? Timestamp(JsonObject o) =>
        DateTimeOffset.TryParse(o.Str("ts"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToUnixTimeMilliseconds() / 1000.0 : null;

    /// <summary>このセッションの集計。</summary>
    public Dictionary<string, object?> SummarizeSession()
    {
        lock (SessionAttempts)
        {
            // 試行が増えていなければ前回の集計を返す(5 秒ごとの再集計を避ける)
            if (cachedSummary is null || cachedAt != attemptVersion) { cachedSummary = Summarize(SessionAttempts.ToList()); cachedAt = attemptVersion; }
            return cachedSummary;
        }
    }

    Dictionary<string, object?>? cachedSummary;
    int cachedAt, attemptVersion;

    /// <summary>試行イベントから成功率・魚種別・経験値などを集計する。</summary>
    public static Dictionary<string, object?> Summarize(IEnumerable<JsonObject> events)
    {
        var attempts = events.Where(e => e.Str("type") == "attempt").ToList();
        var counts = attempts.GroupBy(e => e.Str("outcome")).ToDictionary(g => g.Key, g => g.Count());
        int C(string k) => counts.GetValueOrDefault(k);
        var caughtEv = attempts.Where(e => e.Str("outcome") == "caught").ToList();
        // 確認クリックは釣りの成否ではなく状態確認なので、成功率の母数から外す
        int total = attempts.Count - C("no_bite_reset"), caught = C("caught");
        return new()
        {
            ["attempts"] = total,
            ["all_events"] = attempts.Count,
            ["caught"] = caught,
            ["success_rate"] = total > 0 ? Math.Round(caught * 100.0 / total, 1) : null,
            ["outcomes"] = Outcomes.ToDictionary(k => k, C),
            ["fish"] = caughtEv.GroupBy(e => e.Str("fish") is { Length: > 0 } f ? f : "不明").OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count()),
            ["exp"] = (int)caughtEv.Sum(e => e.Num("exp") ?? 0),
        };
    }
}
