using System.Text.Json.Nodes;
using Pocofarm.Core;

namespace Pocofarm.Farm;

/// <summary>1 回の撮影の畑の状態。Cells は畑ごとの 12 マス(e/p/b/w/?)。古い記録には無いので null で、その場合は畑ごとの植えなし数だけ分かる。</summary>
public sealed record FarmSnapshot(double T, string[]? Cells, int[] UnplantedByPlot, int Dry, int Done, int Hidden)
{
    public int Plots => Cells?.Length ?? UnplantedByPlot.Length;
}

/// <summary>畑の通知。Kind は unplanted / dry / complete、Plot は 0 始まりの畑番号。</summary>
public sealed record FarmAlert(string Kind, int Plot, string Message)
{
    public string Key => $"{Kind}:{Plot}";
}

/// <summary>
/// 畑の状態の推移と通知(純粋関数 + 通知済みの記憶)。
/// 「完了」は全 12 マスが植え済み・水やり済み(w)という意味で、作物が実った(収穫できる)かは測っていない。
/// </summary>
public sealed class FarmWatch(int needScans = 2)
{
    public static readonly string[] PlotNames = ["上1", "上2", "上3", "上4", "下1", "下2", "下3", "下4"];

    static string Name(int i) => i < PlotNames.Length ? PlotNames[i] : $"畑{i + 1}";

    readonly List<FarmSnapshot> history = [];
    readonly HashSet<string> active = [];

    /// <summary>farm_scan イベントから推移を作る。畑が 1 面も見つからなかった撮影(カメラがファームを向いていない)は除く。</summary>
    public static List<FarmSnapshot> FromEvents(IEnumerable<JsonObject> events)
    {
        var list = new List<FarmSnapshot>();
        foreach (var e in events.Where(e => e.Str("type") == "farm_scan"))
        {
            if ((e.Num("plots") ?? 0) < 1) continue;
            double t = e.Num("_t") ?? EventLog.Timestamp(e) ?? 0;
            var cells = e["cells"] is JsonArray ca ? ca.Select(n => n?.GetValue<string>() ?? "").ToArray() : null;
            var ub = e["unplanted_by_plot"] is JsonArray ua ? ua.Select(n => (int)(n.AsNum() ?? 0)).ToArray() : [];
            list.Add(new(t, cells, ub, (int)(e.Num("dry") ?? 0), (int)(e.Num("done") ?? 0), (int)(e.Num("hidden") ?? 0)));
        }
        return list.OrderBy(s => s.T).ToList();
    }

    static int Count(string cells, params char[] c) => cells.Count(x => c.Contains(x));

    /// <summary>畑 i の植えなしマス数(セルが無ければ畑ごとの数)。</summary>
    static int Unplanted(FarmSnapshot s, int i) => s.Cells is { } c ? (i < c.Length ? Count(c[i], 'e', 'b') : 0) : i < s.UnplantedByPlot.Length ? s.UnplantedByPlot[i] : 0;

    /// <summary>畑 i の状態コード。R=植え済み・水やり済み(完了) U=植えなしあり D=植え済みで乾いたマスあり ?=隠れて不明 -=その畑が見つからない。</summary>
    public static char State(FarmSnapshot s, int i)
    {
        if (i >= s.Plots) return '-';
        if (Unplanted(s, i) > 0) return 'U';
        if (s.Cells is not { } c || i >= c.Length) return '?';
        if (Count(c[i], 'p') > 0) return 'D';
        return c[i].Length > 0 && c[i].All(x => x == 'w') ? 'R' : '?';
    }

    /// <summary>直近 needScans 回の連続で続いている通知と、最新の撮影で完了した畑。</summary>
    public static List<FarmAlert> Alerts(IReadOnlyList<FarmSnapshot> snaps, int needScans = 2)
    {
        var alerts = new List<FarmAlert>();
        if (snaps.Count == 0) return alerts;
        var last = snaps[^1];
        var recent = snaps.TakeLast(Math.Max(1, needScans)).ToList();
        for (int i = 0; i < last.Plots; i++)
        {
            if (recent.Count >= needScans && recent.All(s => State(s, i) == 'U')) alerts.Add(new("unplanted", i, $"{Name(i)} に植えていないマスが {needScans} 回連続で残っています"));
            else if (recent.Count >= needScans && recent.All(s => State(s, i) == 'D')) alerts.Add(new("dry", i, $"{Name(i)} に水やりが済んでいないマスが {needScans} 回連続で残っています"));
            else if (State(last, i) == 'R') alerts.Add(new("complete", i, $"{Name(i)} は全マス植え済み・水やり済みです(実りは判定していません)"));
        }
        return alerts;
    }

    /// <summary>畑ごとの状態の推移(新しい方から count 回分を古い順)。</summary>
    public static Dictionary<string, object?> Timeline(IReadOnlyList<FarmSnapshot> snaps, int count = 24)
    {
        var s = snaps.TakeLast(count).ToList();
        int n = s.Count == 0 ? 0 : s.Max(x => x.Plots);
        return new()
        {
            ["times"] = s.Select(x => x.T).ToList(),
            ["plots"] = Enumerable.Range(0, n).Select(i => new Dictionary<string, object?> { ["name"] = Name(i), ["states"] = new string(s.Select(x => State(x, i)).ToArray()) }).ToList(),
        };
    }

    /// <summary>撮影のたびに呼ぶ。新しく出た通知(前回は無かったもの)だけを返す。完了は一度出したら、状態が変わるまで再通知しない。</summary>
    public List<FarmAlert> Observe(FarmSnapshot snap)
    {
        if (snap.Plots < 1) return [];
        history.Add(snap);
        if (history.Count > Math.Max(needScans, 2) + 2) history.RemoveAt(0);
        var now = Alerts(history, needScans);
        var fresh = now.Where(a => !active.Contains(a.Key)).ToList();
        active.Clear();
        foreach (var a in now) active.Add(a.Key);
        return fresh;
    }
}
