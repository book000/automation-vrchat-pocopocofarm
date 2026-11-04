using System.Globalization;
using System.Net;

namespace Pocofarm.Core;

/// <summary>設定値の検証。項目ごとの範囲(表)と、項目同士の関係を調べる。</summary>
public sealed partial class Config
{
    /// <summary>検証で見つかった問題。Keys は関係する項目(既定値へ戻す対象)。</summary>
    public sealed record Issue(string[] Keys, string Message);

    /// <summary>
    /// 数値項目の範囲(キー 最小 最大)。ここに無い数値は 0 以上 1e9 以下。
    /// 除数・剰余・件数の下限に使う項目は最小を 0 にしない。
    /// </summary>
    const string RangeTable = """
        capture_fps 1 240
        osc_port 1 65535
        dashboard_port 1 65535
        osc_hold_ms 1 5000
        work_height 240 4320
        bobber_decoy_cell 1 400
        trace_length 1 10000
        attempt_trace_length 1 10000
        evidence_trace_length 1 10000
        farm_shot_width 64 7680
        farm_shot_quality 1 100
        farm_analysis_width 64 3840
        farm_keep_shots 1 1000
        bite_wait_samples 1 100000
        tune_success_streak 1 10000
        early_streak_to_adapt 1 1000
        caught_streak_to_revert 1 10000
        pecks_to_reel 1 10
        max_pecks_to_reel 1 10
        dip_frames 1 30
        dip_ratio 0.01 5
        settle_seconds 0.05 10
        settle_frames 0.5 60
        settle_range_px 0.1 100
        settle_inlier_ratio 0.1 1
        bobber_red_min_saturation 0 255
        bobber_red_min_value 0 255
        bobber_red_hue_max 0 179
        bobber_red_hue_min 0 179
        bobber_white_sat_limit 0 255
        bobber_white_min_value 0 255
        bobber_switch_ratio 1 100
        bobber_audit_frames 1 10000
        aim_recover_max_steps 0 100
        aim_recover_gain_init 1 100000
        aim_recover_min_confidence 0 1
        aim_recover_tolerance 0.001 0.5
        aim_recover_settle 0 10
        ocr_interval 0.01 10
        ocr_downscale_above 1 10
        toast_crop_history 1 1000
        hud_miss_before_locate 1 100
        stats_interval 0.1 3600
        perf_interval 0.1 3600
        audit_interval 0.5 3600
        live_interval 0.1 3600
        control_check_interval 0.01 5
        idle_poll_ms 1 5000
        no_capture_retry_ms 10 60000
        heartbeat_timeout 1 3600
        evidence_cleanup_interval 1 86400
        evidence_ttl_hours 0.01 8760
        evidence_max_mb 1 1000000
        no_bite_factor 1 20
        reel_to_cast_floor 0.1 60
        reel_to_cast_min 0.1 60
        reel_to_cast_max 0.1 60
        reel_to_cast_step 0.001 5
        level_goal 0 200
        farm_water_min_change 0 100
        farm_edge_contrast 0 255
        farm_cal_iterations 1 50
        farm_cal_step 0.01 100
        farm_cal_zoom_step 0.01 100
        farm_cal_stable_tries 1 100
        farm_grab_attempts 1 50
        farm_cell_window 0.05 1
        view_motion_threshold 0.01 100
        archive_keep_days 0 36500
        """;

    static readonly Dictionary<string, (double Min, double Max)> Ranges = RangeTable
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(l => l.Split(' '))
        .ToDictionary(a => a[0], a => (double.Parse(a[1], CultureInfo.InvariantCulture), double.Parse(a[2], CultureInfo.InvariantCulture)));

    /// <summary>範囲表のキー(テストで実在を確かめる)。</summary>
    public static IEnumerable<string> RangeKeys() => Ranges.Keys;

    /// <summary>[左, 上, 右, 下] の画面比率として検証する項目。</summary>
    static readonly string[] RectKeys = ["bobber_roi", "toast_roi", "level_roi", "xp_bar_roi", "farm_shot_crop"];

    /// <summary>[x, y] の画面比率として検証する項目。</summary>
    static readonly string[] PointKeys = ["camera_tab", "stream_button"];

    /// <summary>項目同士の関係。</summary>
    static readonly (string[] Keys, Func<Config, bool> Ok, string Message)[] Relations =
    [
        (["reel_to_cast_floor", "reel_to_cast_min", "reel_to_cast_max"], c => c.ReelToCastFloor <= c.ReelToCastMin && c.ReelToCastMin <= c.ReelToCastMax, "reel_to_cast_floor <= reel_to_cast_min <= reel_to_cast_max にしてください"),
        (["no_bite_min", "no_bite_timeout"], c => c.NoBiteMin <= c.NoBiteTimeout, "no_bite_min <= no_bite_timeout にしてください"),
        (["pecks_to_reel", "max_pecks_to_reel"], c => c.PecksToReel <= c.MaxPecksToReel, "pecks_to_reel <= max_pecks_to_reel にしてください"),
        (["bite_wait_min_samples", "bite_wait_samples"], c => c.BiteWaitMinSamples <= c.BiteWaitSamples, "bite_wait_min_samples <= bite_wait_samples にしてください"),
        (["bobber_min_height", "bobber_max_height"], c => c.BobberMinHeight <= c.BobberMaxHeight, "bobber_min_height <= bobber_max_height にしてください"),
    ];

    /// <summary>設定全体を検証する。問題が無ければ空。例外は投げない。</summary>
    public List<Issue> Validate()
    {
        var issues = new List<Issue>();
        foreach (var p in Props)
        {
            var key = KeyOf(p);
            var v = p.GetValue(this);
            var (min, max) = Ranges.GetValueOrDefault(key, (0, 1e9));
            switch (v)
            {
                case int i:
                    if (i < min || i > max) issues.Add(new([key], $"{key} は {min:0.###} 〜 {max:0.###} の整数にしてください(現在 {i})"));
                    break;
                case double d:
                    if (!double.IsFinite(d) || d < min || d > max) issues.Add(new([key], $"{key} は {min:0.###} 〜 {max:0.###} の数値にしてください(現在 {d})"));
                    break;
                case string s when key == "window_title" && s.Trim().Length == 0:
                    issues.Add(new([key], "window_title は空にできません"));
                    break;
                case string s when key == "dashboard_host" && !IPAddress.TryParse(s, out _):
                    issues.Add(new([key], "dashboard_host は IP アドレスにしてください"));
                    break;
                case double[] a when RectKeys.Contains(key):
                    if (!ValidRect(a)) issues.Add(new([key], $"{key} は 0〜1 の [左, 上, 右, 下] で、左<右 かつ 上<下 にしてください"));
                    break;
                case double[] a when PointKeys.Contains(key):
                    if (a.Length != 2 || a.Any(x => !double.IsFinite(x) || x < 0 || x > 1)) issues.Add(new([key], $"{key} は 0〜1 の [x, y] にしてください"));
                    break;
                case double[][] rects when key == "exclude_rois":
                    if (rects.Any(r => !ValidRect(r))) issues.Add(new([key], "exclude_rois の各要素は 0〜1 の [左, 上, 右, 下] で、左<右 かつ 上<下 にしてください"));
                    break;
                case Dictionary<string, int> coins when key == "fish_coins":
                    if (coins.Values.Any(x => x < 0)) issues.Add(new([key], "fish_coins の値は 0 以上にしてください"));
                    break;
            }
        }
        // 単独で不正な項目が絡む関係は、二重に報告しない
        var single = issues.SelectMany(i => i.Keys).ToHashSet();
        foreach (var (keys, ok, message) in Relations)
            if (!keys.Any(single.Contains) && !ok(this)) issues.Add(new(keys, message));
        return issues;
    }

    static bool ValidRect(double[] r) =>
        r.Length == 4 && r.All(x => double.IsFinite(x) && x >= 0 && x <= 1) && r[0] < r[2] && r[1] < r[3];
}
