using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pocofarm.Core;
using Pocofarm.Farm;

namespace Pocofarm.Web;

/// <summary>events.jsonl を追記分だけ読み込んで保持する。全件再読込を避ける。各イベントに "_t"(epoch 秒)を足す。</summary>
public sealed class EventIndex(string path)
{
    readonly object gate = new();
    readonly List<JsonObject> events = [];
    long offset;

    /// <summary>新しい行を取り込んで全イベントのコピーを返す。</summary>
    public List<JsonObject> Refresh()
    {
        lock (gate)
        {
            if (!File.Exists(path)) return [.. events];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset) { offset = 0; events.Clear(); }      // ローテーション/削除
            fs.Seek(offset, SeekOrigin.Begin);
            var data = new byte[fs.Length - offset];
            int read = fs.Read(data, 0, data.Length);
            int end = Array.LastIndexOf(data, (byte)'\n', Math.Max(0, read - 1));
            if (end >= 0)
            {
                foreach (var line in Encoding.UTF8.GetString(data, 0, end + 1).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        if (JsonNode.Parse(line) is not JsonObject o || EventLog.Timestamp(o) is not { } t) continue;
                        o["_t"] = t;
                        events.Add(o);
                    }
                    catch (JsonException) { }
                }
                offset += end + 1;
            }
            return [.. events];
        }
    }
}

/// <summary>ダッシュボードの状態を組み立てる。</summary>
public sealed partial class Dashboard(Paths paths, Config cfg) : IFishingControl
{
    static readonly Regex NameRe = NameRegex();

    readonly EventIndex index = new(paths.Events);
    JsonObject lastStatus = [];
    bool wasAlive = true;
    string? archiveKey;
    List<JsonObject> archiveRows = [];

    public EventIndex Index => index;

    [GeneratedRegex(@"^[\w.\-]+\.(jpg|json)$")]
    private static partial Regex NameRegex();

    /// <summary>ファイル名が安全(パス区切りを含まない jpg/json)か。</summary>
    public static bool SafeName(string name) => NameRe.IsMatch(name);

    static JsonObject Without(JsonObject e, params string[] keys)
    {
        var o = new JsonObject();
        foreach (var (k, v) in e) if (!keys.Contains(k)) o[k] = v?.DeepClone();
        return o;
    }

    /// <summary>データリセットで退避した過去の釣果。魚種の確率推定の母数を増やすためだけに使う(ファイルが変わったときだけ再読込)。</summary>
    List<JsonObject> ArchivedCatches()
    {
        var dir = Path.Combine(paths.Data, "archive");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "events_*.jsonl").Order().ToArray() : [];
        var key = string.Join("|", files.Select(f => $"{Path.GetFileName(f)}:{new FileInfo(f).Length}"));
        if (key != archiveKey)
        {
            archiveRows = files.SelectMany(f => { var a = EventLog.Read(f).Where(e => e.Str("type") == "attempt").ToList(); Stats.InferMissingFish(a, cfg.KnownFish); return a; })
                .Where(e => e.Str("outcome") == "caught")
                .Select(e => new JsonObject { ["outcome"] = "caught", ["fish"] = e.Str("fish") }).ToList();
            archiveKey = key;
        }
        return archiveRows;
    }

    static List<string> Tail(string path, int n)
    {
        if (!File.Exists(path)) return [];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - 60_000);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[fs.Length - start];
        int read = fs.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, read).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(n).ToList();
    }

    static double Tm(JsonObject e) => e.Num("_t") ?? 0;

    /// <summary>10 分刻みの釣果推移。</summary>
    static List<Dictionary<string, object?>> ChartOf(IEnumerable<JsonObject> attempts) =>
        attempts.GroupBy(e => (long)Math.Floor(Tm(e) / 600)).OrderBy(g => g.Key)
            .Select(g =>
            {
                var by = g.Where(e => e.Str("outcome") != "caught").GroupBy(e => e.Str("outcome")).ToDictionary(x => x.Key, x => x.Count());
                return new Dictionary<string, object?>
                {
                    ["t"] = g.Key * 600,
                    ["caught"] = g.Count(e => e.Str("outcome") == "caught"),
                    ["fail"] = by.Values.Sum(),
                    ["by"] = by,
                };
            }).ToList();

    /// <summary>1 時間ごとの成功率。確認クリックの引き直しは分母に入れない。</summary>
    static List<Dictionary<string, object?>> HourlyOf(IEnumerable<JsonObject> attempts) =>
        attempts.Where(e => e.Str("outcome") != "no_bite_reset")
            .GroupBy(e => (long)Math.Floor(Tm(e) / 3600)).OrderBy(g => g.Key)
            .Select(g => new Dictionary<string, object?>
            {
                ["t"] = g.Key * 3600,
                ["caught"] = g.Count(e => e.Str("outcome") == "caught"),
                ["total"] = g.Count(),
            }).ToList();

    static readonly Regex ArchiveNameRe = new(@"^events_\d{8}_\d{6}\.jsonl$");

    string ArchiveDir => Path.Combine(paths.Data, "archive");

    List<JsonObject>? LoadArchive(string name)
    {
        var path = Path.Combine(ArchiveDir, name);
        if (!ArchiveNameRe.IsMatch(name) || !File.Exists(path)) return null;
        var rows = new List<JsonObject>();
        foreach (var o in EventLog.Read(path))
            if (EventLog.Timestamp(o) is { } t) { o["_t"] = t; rows.Add(o); }
        return rows;
    }

    /// <summary>データリセットで退避した記録の一覧(新しい順)。</summary>
    public List<Dictionary<string, object?>> ListArchives()
    {
        if (!Directory.Exists(ArchiveDir)) return [];
        var list = new List<Dictionary<string, object?>>();
        foreach (var f in Directory.GetFiles(ArchiveDir, "events_*.jsonl").OrderByDescending(x => x))
        {
            var name = Path.GetFileName(f);
            var rows = LoadArchive(name) ?? [];
            var attempts = rows.Where(e => e.Str("type") == "attempt").ToList();
            Stats.InferMissingFish(attempts, cfg.KnownFish);
            var sum = EventLog.Summarize(attempts);
            list.Add(new()
            {
                ["name"] = name,
                ["bytes"] = new FileInfo(f).Length,
                ["start"] = rows.Count > 0 ? rows.Min(Tm) : null,
                ["end"] = rows.Count > 0 ? rows.Max(Tm) : null,
                ["attempts"] = sum["attempts"],
                ["caught"] = sum["caught"],
                ["success_rate"] = sum["success_rate"],
                ["exp"] = sum["exp"],
            });
        }
        return list;
    }

    /// <summary>退避した 1 件の中身。画面側で今の記録と同じ部品(グラフ・統計)を使えるよう、同じ形に集計して返す。</summary>
    public Dictionary<string, object?>? ArchiveDetail(string name)
    {
        var rows = LoadArchive(name);
        if (rows is null) return null;
        var attempts = rows.Where(e => e.Str("type") == "attempt").ToList();
        Stats.InferMissingFish(attempts, cfg.KnownFish);
        double start = rows.Count > 0 ? rows.Min(Tm) : 0, end = rows.Count > 0 ? rows.Max(Tm) : 0;
        var huds = rows.Where(e => e.Str("type") == "hud" && e.Num("level") is not null).ToList();
        return new()
        {
            ["name"] = name,
            ["start"] = start,
            ["end"] = end,
            ["total"] = EventLog.Summarize(attempts),
            ["stats"] = Stats.Compute(attempts, end, cfg.FishCoins, []),
            ["chart"] = ChartOf(attempts),
            ["hourly"] = HourlyOf(attempts),
            ["failures"] = attempts.Where(e => e.Str("outcome") != "caught").TakeLast(100).Reverse().Select(e => Without(e, "_t", "trace")).ToList(),
            ["level"] = huds.Count > 0 ? new Dictionary<string, object?> { ["from"] = huds[0].Num("level"), ["to"] = huds[^1].Num("level") } : null,
            ["neko"] = rows.Where(e => e.Str("type") == "neko").TakeLast(40).Reverse().Select(e => Without(e, "_t")).ToList(),
        };
    }

    /// <summary>/api/state の本体。</summary>
    public Dictionary<string, object?> BuildState()
    {
        var events = index.Refresh();
        double now = EventLog.Now();
        // 書き込みと読み込みが重なったときに一瞬だけ読めないことがある。「停止」と誤表示しないよう直前の内容を使う
        JsonObject status = lastStatus;
        try
        {
            // 書き込み側と共有で開く(既定の ReadAllText は書き込み中のファイルを開けず、その間「停止」に見えてしまう)
            using var fs = new FileStream(paths.Status, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            status = lastStatus = JsonNode.Parse(fs) as JsonObject ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 読めなかったときは直前の内容を使い、心拍だけファイルの更新時刻で補う(書き込みが続いていれば「停止」にしない)
            try { lastStatus["heartbeat"] = Math.Max(lastStatus.Num("heartbeat") ?? 0, new DateTimeOffset(File.GetLastWriteTimeUtc(paths.Status)).ToUnixTimeMilliseconds() / 1000.0); }
            catch (IOException) { }
        }
        var attempts = events.Where(e => e.Str("type") == "attempt").ToList();
        Stats.InferMissingFish(attempts, cfg.KnownFish);
        double T(JsonObject e) => e.Num("_t") ?? 0;
        var hour = attempts.Where(e => T(e) >= now - 3600).ToList();
        double sessionStart = status.Num("started") ?? 0;
        var session = attempts.Where(e => T(e) >= sessionStart).ToList();
        double beatAge = now - (status.Num("heartbeat") ?? 0);
        bool alive = status.Count > 0 && beatAge < cfg.HeartbeatTimeout;
        if (alive != wasAlive)
        {
            wasAlive = alive;
            if (alive) Log.Info("釣りの心拍が戻りました");
            else Log.Warn($"釣りの心拍が {beatAge:F1} 秒途絶えました(状態={status.Str("state")})。ダッシュボードは「停止」と表示します");
        }

        var chart = ChartOf(attempts.Where(e => T(e) >= now - 6 * 3600));
        var hourly = HourlyOf(attempts.Where(e => T(e) >= now - 24 * 3600));
        var others = events.Where(e => e.Str("type") is not ("attempt" or "session_start" or "hud" or "neko" or "toast_other")).TakeLast(30).Reverse()
            .Select(e => Without(e, "_t")).ToList();
        // 未確認の判定をブラウザ側でできるよう、通知には内部時刻 _t を残す
        var nekoNotes = events.Where(e => e.Str("type") == "neko").TakeLast(40).Reverse().Select(e => Without(e)).ToList();
        double nekoAck = NekoAck();
        // レベル推移: レベル+経験値バーの割合を連続値にして直近 24 時間を返す
        var huds = events.Where(e => e.Str("type") == "hud" && T(e) >= now - 24 * 3600 && e.Num("level") is not null).ToList();
        var hud = new Dictionary<string, object?>
        {
            ["goal"] = cfg.LevelGoal,
            ["points"] = huds.TakeLast(600).Select(e => new Dictionary<string, object?> { ["t"] = T(e), ["v"] = Math.Round(e.Num("level")!.Value + (e.Num("xp") ?? 0), 3) }).ToList(),
            ["ups"] = huds.Zip(huds.Skip(1)).Where(p => p.Second.Num("level")! > p.First.Num("level")!)
                .Select(p => new Dictionary<string, object?> { ["t"] = T(p.Second), ["level"] = p.Second.Num("level") }).TakeLast(10).ToList(),
            ["now"] = status["hud"]?.DeepClone(),
        };
        // 速度と見落とし防止: 直近の釣果ペース、最後の釣果からの経過、遅延サイクル、再起動の履歴
        var caughtAt = attempts.Where(e => e.Str("outcome") == "caught").Select(T).ToList();
        double Per5(double span) => Math.Round(caughtAt.Count(t => t >= now - span) / (span / 300), 1);
        // 釣果/時: 今回の起動から数える(古いビルドの失敗や停止を含めない)。1 時間以上動いていれば直近 1 時間
        double win = Math.Clamp(now - sessionStart, 60, 3600);
        var pace = new Dictionary<string, object?>
        {
            ["per_hour"] = Math.Round(caughtAt.Count(t => t >= now - win) / win * 3600),
            ["per_hour_window"] = Math.Round(win),
            ["per5_5m"] = Per5(300),
            ["per5_30m"] = Per5(1800),
            ["per5_60m"] = Per5(3600),
            ["last_catch_age"] = caughtAt.Count > 0 ? Math.Round(now - caughtAt[^1], 1) : null,
            ["slow_30m"] = events.Count(e => e.Str("type") == "slow_cycle" && T(e) >= now - 1800),
            ["restarts"] = events.Where(e => e.Str("type") == "session_start" && T(e) >= now - 24 * 3600).Reverse().Select(T).ToList(),
        };
        var recent = attempts.TakeLast(60).Reverse().Select(e => Without(e, "_t", "trace")).ToList();
        SampleFps(status, alive);
        var health = HealthOf(attempts, status);
        return new()
        {
            ["markers"] = MarkersOf(events, now),
            ["health"] = health,
            ["farm"] = FarmOf(events),
            ["now"] = now,
            ["alive"] = alive,
            ["status"] = status,
            ["total"] = EventLog.Summarize(attempts),
            ["hour"] = EventLog.Summarize(hour),
            ["session"] = EventLog.Summarize(session),
            ["recent"] = recent,
            ["chart"] = chart,
            ["hourly"] = hourly,
            ["stats"] = Stats.Compute(attempts, now, cfg.FishCoins, ArchivedCatches()),
            ["hud"] = hud,
            ["pace"] = pace,
            ["other_events"] = others,
            ["neko"] = nekoNotes,
            ["neko_ack"] = nekoAck,
            ["neko_unread"] = events.Count(e => e.Str("type") == "neko" && T(e) > nekoAck),
            ["log"] = Tail(paths.Log, 70),
            ["outcomes"] = EventLog.Outcomes,
            ["evidence_count"] = Directory.Exists(paths.Evidence) ? Directory.EnumerateFiles(paths.Evidence, "*.jpg").Count() : 0,
        };
    }

    /// <summary>新しい順の証拠一覧。同名 JSON の中身も添える。</summary>
    public List<Dictionary<string, object?>> ListEvidence(int limit, string query)
    {
        if (!Directory.Exists(paths.Evidence)) return [];
        var files = new DirectoryInfo(paths.Evidence).EnumerateFiles("*.jpg").OrderByDescending(f => f.LastWriteTimeUtc);
        var outList = new List<Dictionary<string, object?>>();
        foreach (var f in files)
        {
            if (query.Length > 0 && !f.Name.Contains(query, StringComparison.Ordinal)) continue;
            string ocr = "";
            var jp = Path.ChangeExtension(f.FullName, ".json");
            if (File.Exists(jp))
            {
                try
                {
                    var m = JsonNode.Parse(File.ReadAllText(jp)) as JsonObject;
                    ocr = m is null ? "" : m.Str("ocr") is { Length: > 0 } o ? o : m.Str("text");
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            outList.Add(new() { ["name"] = f.Name, ["mtime"] = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000.0, ["ocr"] = ocr });
            if (outList.Count >= limit) break;
        }
        return outList;
    }

    /// <summary>試行履歴(結果種別で絞り込み、新しい順)。</summary>
    public List<JsonObject> ListAttempts(string outcome, int limit) =>
        index.Refresh().Where(e => e.Str("type") == "attempt" && (outcome.Length == 0 || e.Str("outcome") == outcome))
            .TakeLast(limit).Reverse().Select(e => Without(e, "_t")).ToList();

    /// <summary>釣りが今一時停止中か。</summary>
    public bool IsPaused()
    {
        try { return JsonNode.Parse(File.ReadAllText(paths.Status))?["paused"]?.GetValue<bool>() ?? false; }   // 停止キーでも切り替わるので、実際の状態は status.json を見る
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>fisher の現在の状態名(status.json の state)。読めなければ空。</summary>
    public string State()
    {
        try { return JsonNode.Parse(File.ReadAllText(paths.Status))?["state"]?.GetValue<string>() ?? ""; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return ""; }
    }

    /// <summary>一時停止/再開の要求を書く(fisher が更新時刻の変化で読む)。</summary>
    public void RequestPause(bool on)
    {
        Directory.CreateDirectory(paths.Data);
        AtomicFile.Write(paths.Control, JsonSerializer.SerializeToUtf8Bytes(new { paused = on, ts = EventLog.Now() }));
    }

    /// <summary>ねこちゃんの通知を確認した時刻(epoch 秒)。どの端末で確認しても共有される。</summary>
    public double NekoAck()
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(paths.Data, "neko_ack.json")))?["ack"]?.GetValue<double>() ?? 0; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return 0; }
    }

    /// <summary>ねこちゃんの通知を「確認した」ことにする。</summary>
    public void RequestNekoAck()
    {
        Directory.CreateDirectory(paths.Data);
        AtomicFile.Write(Path.Combine(paths.Data, "neko_ack.json"), JsonSerializer.SerializeToUtf8Bytes(new { ack = EventLog.Now() }));
    }

    /// <summary>データリセットの要求を書く(トークンが変わったら fisher が実行する)。</summary>
    public void RequestReset()
    {
        Directory.CreateDirectory(paths.Data);
        AtomicFile.Write(paths.Reset, JsonSerializer.SerializeToUtf8Bytes(new { token = EventLog.Now() }));
    }
}
