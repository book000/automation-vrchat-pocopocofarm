using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pocofarm.Core;

/// <summary>設定項目の説明(ダッシュボードの設定画面に出す)。restart は変更に再起動が要るもの。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DescAttribute(string text, bool restart = false) : Attribute
{
    public string Text { get; } = text;
    public bool Restart { get; } = restart;
}

/// <summary>
/// 動作パラメータ。座標は基準解像度(1920x1009)に対する比率。config.json のキーは snake_case。
/// 項目は領域ごとの partial ファイル(Config.*.cs)に分けてある。ここには読み書きと検証だけを置く。
/// </summary>
public sealed partial class Config
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly PropertyInfo[] Props = typeof(Config).GetProperties();

    /// <summary>config.json を読む。無ければ既定値。未知のキーは無視する。</summary>
    public static Config Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) ?? new Config() : new Config();

    static string KeyOf(PropertyInfo p) => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name);

    static DescAttribute? DescOf(PropertyInfo p) => (DescAttribute?)Attribute.GetCustomAttribute(p, typeof(DescAttribute));

    /// <summary>画面表示用の項目一覧(キー・説明・現在値・既定値・再起動の要否)。</summary>
    public List<object> Describe()
    {
        var def = new Config();
        return Props.Select(p => (object)new
        {
            key = KeyOf(p),
            desc = DescOf(p)?.Text ?? "",
            restart = DescOf(p)?.Restart ?? false,
            value = p.GetValue(this),
            @default = p.GetValue(def),
        }).ToList();
    }

    /// <summary>
    /// {キー: 値} を検証して反映する(null は既定値へ戻す)。一つでも不正なら何も変えずに理由を返し、成功なら null。
    /// 反映したキーのうち、再起動が要るものを restart に入れる。
    /// </summary>
    public string? Apply(JsonObject patch, List<string> restart)
    {
        var def = new Config();
        var staged = new List<(PropertyInfo P, object V)>();
        foreach (var (key, node) in patch)
        {
            var p = Props.FirstOrDefault(x => KeyOf(x) == key);
            if (p is null) return $"未知のキーです: {key}";
            if (key == "update_source") return "update_source は config.json を直接編集してください(ここからは変更できません。任意の exe を実行させる経路になるため)";
            object? v;
            try { v = node is null ? p.GetValue(def) : JsonSerializer.Deserialize(node.ToJsonString(), p.PropertyType, Json); }
            catch (JsonException) { return $"{key}: 値の形式が違います"; }
            if (v is null) return $"{key}: 値が空です";
            if (v is double d && (!double.IsFinite(d) || d < 0)) return $"{key}: 0 以上の数値にしてください";
            if (v is int i && i < 0) return $"{key}: 0 以上の整数にしてください";
            if (v is double[] arr && (arr.Length != ((double[])p.GetValue(def)!).Length || arr.Any(x => !double.IsFinite(x)))) return $"{key}: 要素数 {((double[])p.GetValue(def)!).Length} の数値配列にしてください";
            staged.Add((p, v));
        }
        foreach (var (p, v) in staged)
        {
            p.SetValue(this, v);
            if (DescOf(p)?.Restart == true) restart.Add(KeyOf(p));
        }
        return null;
    }
}
