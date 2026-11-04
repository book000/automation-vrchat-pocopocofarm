using Pocofarm.Core;

namespace Pocofarm.Fishing;

/// <summary>
/// メインループの計測。区間ごとの所要時間と稼働率の内訳を status に出し、同じ区間に留まり続けたらログに警告する
/// (遅い環境でどこに時間がかかっているか、何で止まったかを調べるため)。
/// </summary>
public sealed class LoopProfiler(Config cfg, Status status)
{
    const int WatchdogPollMs = 100;

    static double Now => EventLog.Now();

    readonly Dictionary<string, (double Sum, int N, double Max)> perf = [];
    readonly Dictionary<string, double> uptime = new() { ["active"] = 0, ["paused"] = 0, ["fg_lost"] = 0, ["gap"] = 0 };
    volatile string section = "";
    double sectionAt, lastPerf, acctT = Now, lastUptimeSet;

    /// <summary>いま処理中の区間を記録する。直前の区間の所要時間を集計に足す。</summary>
    public void Mark(string name)
    {
        double now = Now;
        if (section.Length > 0)
        {
            var (sum, n, max) = perf.GetValueOrDefault(section);
            double ms = (now - sectionAt) * 1000;
            perf[section] = (sum + ms, n + 1, Math.Max(max, ms));
        }
        section = name; sectionAt = now;
    }

    /// <summary>区間ごとの平均・最大(ms)を一定間隔で status の perf に出す。</summary>
    public void PublishPerf(double now)
    {
        if (now - lastPerf < cfg.PerfInterval) return;
        lastPerf = now;
        status.Set("perf", perf.ToDictionary(kv => kv.Key, kv => (object)new { avg_ms = Math.Round(kv.Value.Sum / kv.Value.N, 1), max_ms = Math.Round(kv.Value.Max, 1), n = kv.Value.N }));
        perf.Clear();
    }

    /// <summary>前回からの経過秒を稼働率の区分(active/paused/fg_lost/gap)に加える。</summary>
    public void Account(double now, string category)
    {
        uptime[category] += now - acctT;
        acctT = now;
        if (now - lastUptimeSet < 1.0) return;
        lastUptimeSet = now;
        status.Set("uptime", uptime.ToDictionary(kv => kv.Key, kv => (object?)Math.Round(kv.Value)));
    }

    /// <summary>メインループが同じ区間で止まっていたら、その区間名をログに出す監視スレッドを起動する。</summary>
    public void StartWatchdog(CancellationToken stop) =>
        new Thread(() => Watch(stop)) { IsBackground = true, Name = "watchdog" }.Start();

    void Watch(CancellationToken stop)
    {
        string reported = "";
        double reportedAt = 0;
        while (!stop.IsCancellationRequested)
        {
            Thread.Sleep(WatchdogPollMs);
            var sec = section; var at = sectionAt;
            if (Now - at > cfg.WatchdogSeconds && (sec != reported || at != reportedAt))
            {
                reported = sec; reportedAt = at;
                Log.Warn($"処理が止まっています: 区間={sec} ({Now - at:F1}秒経過)");
            }
        }
    }
}
