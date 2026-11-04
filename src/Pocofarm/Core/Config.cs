using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pocofarm.Core;

/// <summary>設定項目の説明(ダッシュボードの設定画面に出す)。restart は変更に再起動が要るもの。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DescAttribute(string text, bool restart = false, [CallerFilePath] string file = "") : Attribute
{
    public string Text { get; } = text;
    public bool Restart { get; } = restart;

    /// <summary>項目の区分。宣言されたファイル名(Config.Fishing.cs なら "fishing")から決まる。コンパイラが呼び出し元のパスを渡す。</summary>
    public string Section { get; } = Path.GetFileNameWithoutExtension(file).Split('.').Last().ToLowerInvariant();
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

    /// <summary>
    /// config.json を読む。無ければ既定値。未知のキーは無視する。値の型が違う項目・範囲外の項目は警告して既定値に戻す(例外にしない)。
    /// JSON 全体が壊れているときだけ JsonException を投げる(呼び出し側が直前の設定を使い続けられるように)。
    /// </summary>
    public static Config Load(string path, bool warn = true)
    {
        var cfg = new Config();
        if (!File.Exists(path)) return cfg;
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("config.json が JSON オブジェクトではありません");
        foreach (var (key, node) in root)
        {
            var p = Props.FirstOrDefault(x => KeyOf(x) == key);
            if (p is null || node is null) continue;
            try { if (JsonSerializer.Deserialize(node.ToJsonString(), p.PropertyType, Json) is { } v) p.SetValue(cfg, v); }
            catch (JsonException) { if (warn) Log.Warn($"config.json: {key} の値の形式が違うため既定値を使います"); }
        }
        cfg.AddMissingFish();
        cfg.Sanitize(warn);
        return cfg;
    }

    /// <summary>古い config.json は釣れる魚の一覧が当時の分しか無いので、既定にある魚と売値のうち足りないものを補う(消された魚は戻る)。</summary>
    void AddMissingFish()
    {
        var d = new Config();
        foreach (var f in d.KnownFish) if (!KnownFish.Contains(f)) KnownFish.Add(f);
        foreach (var (f, c) in d.FishCoins) FishCoins.TryAdd(f, c);
    }

    /// <summary>
    /// 起動時用の読み込み。壊れた JSON でも落とさず、直近に正常に読めた設定(data/config.lastgood.json)、無ければ既定値で続ける。
    /// 正常に読めたら、その内容を lastgood に控える。
    /// </summary>
    public static Config LoadSafe(string path)
    {
        var good = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "data", "config.lastgood.json");
        try
        {
            var cfg = Load(path);
            if (File.Exists(path))
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(good)!); File.Copy(path, good, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return cfg;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"config.json を読めません({ex.Message})。直近に正常だった設定か既定値で続けます");
            try { if (File.Exists(good)) return Load(good); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
            return new Config();
        }
    }

    /// <summary>検証に通らない項目を既定値へ戻す(関係式に絡む項目は、関係するキーすべて)。</summary>
    void Sanitize(bool warn)
    {
        var def = new Config();
        for (int pass = 0; pass < 3; pass++)
        {
            var issues = Validate();
            if (issues.Count == 0) return;
            foreach (var (keys, message) in issues)
            {
                if (warn) Log.Warn($"config.json: {message} → {string.Join(", ", keys)} を既定値に戻します");
                foreach (var k in keys)
                    if (Props.FirstOrDefault(x => KeyOf(x) == k) is { } p) p.SetValue(this, p.GetValue(def));
            }
        }
    }

    /// <summary>浅いコピー(配列・辞書は共有。検証用に項目を差し替えるだけの用途)。</summary>
    public Config Clone() => (Config)MemberwiseClone();

    /// <summary>全項目のキー。</summary>
    public static IEnumerable<string> AllKeys() => Props.Select(KeyOf);

    /// <summary>項目の区分(fishing / vision / toast / farm / aim / system)。未知のキーは null。</summary>
    public static string? SectionOf(string key) => Props.FirstOrDefault(x => KeyOf(x) == key) is { } p ? DescOf(p)?.Section : null;

    /// <summary>項目の現在値を JSON にする。未知のキーは null。</summary>
    public JsonNode? ValueOf(string key) => Props.FirstOrDefault(x => KeyOf(x) == key) is { } p ? JsonSerializer.SerializeToNode(p.GetValue(this), p.PropertyType, Json) : null;

    /// <summary>既定値と違う項目のキー。</summary>
    public List<string> DifferingKeys()
    {
        var def = new Config();
        return Props.Where(p => JsonSerializer.Serialize(p.GetValue(this), Json) != JsonSerializer.Serialize(p.GetValue(def), Json)).Select(KeyOf).ToList();
    }

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
        // 反映後の設定を検証する(項目単独の範囲と、項目同士の関係)。今回触れていない項目の問題は無視する
        var trial = Clone();
        foreach (var (p, v) in staged) p.SetValue(trial, v);
        var touched = staged.Select(x => KeyOf(x.P)).ToHashSet();
        var bad = trial.Validate().Where(i => i.Keys.Any(touched.Contains)).Select(i => i.Message).ToList();
        if (bad.Count > 0) return string.Join(" / ", bad);
        foreach (var (p, v) in staged)
        {
            p.SetValue(this, v);
            if (DescOf(p)?.Restart == true) restart.Add(KeyOf(p));
        }
        return null;
    }
}
