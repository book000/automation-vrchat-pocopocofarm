using System.Text.Json;
using System.Text.Json.Nodes;
using Pocofarm.Core;

namespace Pocofarm.Web;

/// <summary>設定のプリセット・区分ごとのリセット・直前の変更の取り消し。変更の履歴は data/config.history.jsonl に少しだけ残す。</summary>
public static class ConfigPresets
{
    const int HistoryMax = 20;
    static readonly object Gate = new();

    /// <summary>名前付きの設定パッチ。キーは実在する設定名(テストで確かめる)。</summary>
    public static readonly IReadOnlyDictionary<string, (string Desc, string Patch)> All = new Dictionary<string, (string, string)>
    {
        ["safe"] = ("Conservative timing: auto-tuning off, longer waits, no farm photo shoots, no mouse view recovery.",
            """{"tune_reel_to_cast":false,"reel_to_cast_min":3.0,"reel_to_cast_max":8.0,"recast_delay":2.5,"osc_hold_ms":80,"farm_shot_interval":0,"aim_recover":false}"""),
        ["fast"] = ("Shorter waits and quicker OCR. Needs a stable frame rate; revert if casts start to be ignored.",
            """{"reel_to_cast_floor":1.0,"reel_to_cast_min":1.6,"recast_delay":1.2,"ocr_interval":0.08,"no_bite_min":15,"no_bite_timeout":60}"""),
        ["low-gpu"] = ("Lower load: 30 fps capture, slower OCR and live view, smaller working size. Detection is coarser.",
            """{"capture_fps":30,"ocr_interval":0.25,"live_interval":2.0,"rapid_ocr":false,"work_height":720,"hud_interval":20,"farm_shot_interval":1800}"""),
    };

    /// <summary>設定を反映して config.json へ保存し、変更前の値を履歴に残す。成功なら null、不正なら理由。</summary>
    public static string? ApplyAndSave(Paths paths, Config cfg, JsonObject patch, List<string> restart)
    {
        lock (Gate)
        {
            var before = new JsonObject();
            foreach (var (key, _) in patch) if (cfg.ValueOf(key) is { } v) before[key] = v;
            var err = cfg.Apply(patch, restart);
            if (err is not null) return err;
            // 実際に変わった項目だけを履歴に残す
            var changed = new JsonObject();
            foreach (var (key, old) in before)
                if (cfg.ValueOf(key)?.ToJsonString() != old?.ToJsonString()) changed[key] = old?.DeepClone();
            if (changed.Count > 0) PushHistory(paths, changed);
            AtomicFile.Write(paths.ConfigFile, JsonSerializer.SerializeToUtf8Bytes(cfg, Config.Json));
            return null;
        }
    }

    /// <summary>プリセットを適用する。未知の名前は理由を返す。</summary>
    public static string? ApplyPreset(Paths paths, Config cfg, string name, List<string> restart) =>
        All.TryGetValue(name, out var p) ? ApplyAndSave(paths, cfg, (JsonObject)JsonNode.Parse(p.Patch)!, restart) : $"未知のプリセットです: {name}";

    /// <summary>区分(fishing / vision / toast / farm / aim / system)の全項目を既定値へ戻す。update_source は対象外。</summary>
    public static string? ResetSection(Paths paths, Config cfg, string section, List<string> restart)
    {
        var patch = new JsonObject();
        foreach (var k in Config.AllKeys())
            if (Config.SectionOf(k) == section && k != "update_source") patch[k] = null;
        return patch.Count == 0 ? $"未知の区分です: {section}" : ApplyAndSave(paths, cfg, patch, restart);
    }

    /// <summary>直前の変更を取り消す(変更前の値へ戻す)。取り消せる履歴が無ければ理由を返す。</summary>
    public static string? Undo(Paths paths, Config cfg, List<string> restart, out List<string> keys)
    {
        lock (Gate)
        {
            keys = [];
            var lines = ReadHistory(paths);
            if (lines.Count == 0) return "取り消せる変更がありません";
            var last = lines[^1];
            var before = (JsonObject)last["before"]!.DeepClone();
            var err = cfg.Apply(before, restart);
            if (err is not null) return "取り消せません: " + err;
            keys = before.Select(kv => kv.Key).ToList();
            lines.RemoveAt(lines.Count - 1);
            WriteHistory(paths, lines);
            AtomicFile.Write(paths.ConfigFile, JsonSerializer.SerializeToUtf8Bytes(cfg, Config.Json));
            return null;
        }
    }

    /// <summary>画面用: 既定値と違う項目・区分・プリセット・取り消しの可否。</summary>
    public static object Tools(Paths paths, Config cfg)
    {
        List<JsonObject> hist;
        lock (Gate) hist = ReadHistory(paths);
        var def = new Config();
        return new
        {
            presets = All.Select(kv => new { name = kv.Key, desc = kv.Value.Desc, patch = JsonNode.Parse(kv.Value.Patch) }).ToList(),
            sections = Config.AllKeys().Select(k => Config.SectionOf(k)).Where(s => s is not null).Distinct().Order().ToList(),
            changed = cfg.DifferingKeys().Select(k => new { key = k, section = Config.SectionOf(k), value = cfg.ValueOf(k), @default = def.ValueOf(k) }).ToList(),
            undo = new { available = hist.Count > 0, keys = hist.Count > 0 ? hist[^1]["before"]!.AsObject().Select(kv => kv.Key).ToList() : [] },
        };
    }

    static List<JsonObject> ReadHistory(Paths paths)
    {
        var list = new List<JsonObject>();
        if (!File.Exists(paths.ConfigHistory)) return list;
        try
        {
            foreach (var line in File.ReadLines(paths.ConfigHistory))
            {
                try { if (JsonNode.Parse(line) is JsonObject o && o["before"] is JsonObject) list.Add(o); }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        return list;
    }

    static void PushHistory(Paths paths, JsonObject before)
    {
        var lines = ReadHistory(paths);
        lines.Add(new JsonObject { ["ts"] = EventLog.Now(), ["before"] = before });
        WriteHistory(paths, lines.TakeLast(HistoryMax).ToList());
    }

    static void WriteHistory(Paths paths, List<JsonObject> lines)
    {
        Directory.CreateDirectory(paths.Data);
        AtomicFile.Write(paths.ConfigHistory, System.Text.Encoding.UTF8.GetBytes(string.Concat(lines.Select(o => o.ToJsonString() + "\n"))));
    }
}
