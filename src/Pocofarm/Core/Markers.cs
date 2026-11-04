using System.Text.Json.Nodes;

namespace Pocofarm.Core;

/// <summary>グラフに縦線で重ねる「出来事」(設定変更・一時停止/再開・再起動・入れ替え・自動調整・HUD/画角調整)をイベントから拾う(純粋関数)。</summary>
public static class Markers
{
    /// <summary>マーカーになるイベント型。</summary>
    public static readonly string[] Types = ["config_change", "pause", "resume", "session_start", "self_update", "tune", "hud_calibrated", "farm_calibration", "aim_recover", "farm_alert"];

    /// <summary>since(epoch 秒)以降のマーカーを時刻順で返す。各イベントは "_t" を持つか、ts から読める。</summary>
    public static List<Dictionary<string, object?>> Of(IEnumerable<JsonObject> events, double since)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var e in events)
        {
            var type = e.Str("type");
            if (!Types.Contains(type)) continue;
            double? t = e.Num("_t") ?? EventLog.Timestamp(e);
            if (t is not { } tt || tt < since) continue;
            var (kind, label) = Describe(e, type);
            if (kind.Length == 0) continue;
            list.Add(new() { ["t"] = tt, ["kind"] = kind, ["label"] = label });
        }
        return list.OrderBy(m => (double)m["t"]!).ToList();
    }

    static (string Kind, string Label) Describe(JsonObject e, string type) => type switch
    {
        "config_change" => ("config", "設定変更: " + e.Str("keys")),
        "pause" => ("pause", "一時停止(" + e.Str("source") + ")"),
        "resume" => ("resume", "再開(" + e.Str("source") + ")"),
        "session_start" => e.Str("note") == "data_reset" ? ("reset", "データリセット") : ("restart", "再起動"),
        "self_update" => ("update", e["ok"]?.ToString() == "false" ? "exe 入れ替え失敗" : "exe 入れ替え"),
        "tune" => ("tune", $"自動調整 {e.Str("what")}: {e.Str("from")} → {e.Str("to")}"),
        "hud_calibrated" => ("hud", "HUD 領域を自動補正"),
        "farm_calibration" => e["moved"]?.ToString() == "true" ? ("cal", "ファーム画角調整") : ("", ""),
        "aim_recover" => ("aim", "視点補正: " + e.Str("result")),
        "farm_alert" => ("farm", "ファーム: " + e.Str("message")),
        _ => ("", ""),
    };
}
