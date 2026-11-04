using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pocofarm.Core;

/// <summary>JsonObject の緩い型取得。イベントは項目が欠けていても読めるようにする。</summary>
public static class JsonExt
{
    public static string Str(this JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static double? Num(this JsonObject o, string key) => o[key].AsNum();

    /// <summary>JSON の数値ノードを double にする(int/long/double どれで書かれていても読める)。</summary>
    public static double? AsNum(this JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }
}
