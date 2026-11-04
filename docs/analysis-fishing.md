# Fishing Analysis

Statistics from a recorded run of this tool at the pocopoco farm pond. Every number states its dataset and sample size. How to recompute everything is in the [Automation guide](automation-guide.md#reproducing-these-statistics). For the mechanics these numbers support, see [How the World Works](world-mechanics.md).

## Dataset

| Item | Value |
|---|---|
| Source | `data/events.jsonl` plus `data/archive/events_*.jsonl` (two older logs moved aside by data resets), `data/fishing.log`, `data/tuning.json`, `data/status.json` |
| Time span | 2026-11-04 16:56 to 2026-11-05 02:12 (UTC+9), about 9.3 hours of wall time. The cutoff for all numbers is the last event at 02:12:41 |
| Attempts (`type=attempt`) | 3,523 (420 + 279 in the two archives, 2,824 in the live file) |
| Other events | 3,731 `toast_unclassified`, 496 `hud`, 244 `toast_other`, 202 `toast_unlinked`, 196 `gap`, 100 `desync`, 39 `pause`/`resume`, 35 `session_start`, 26 `farm_scan`, 22 `slow_cycle`, 11 `toast_info`, 7 `neko`, 4 `farm_calibration` |
| Setup | One account, desktop-mode VRChat, clicks over OSC, Windows OCR (about 870 catch results) plus PP-OCR (about 1,370) |
| Resolution | 3840x2054 until 18:54, 1920x1009 until 00:46, 3840x2054 until 01:59, then 1920x1009 (see [Failure taxonomy](#failure-taxonomy)) |

Caveats:

1. **One machine, one account, one afternoon and night.** The first 2 hours and the 01:00 hour were active development with many restarts (35 process starts, 12 of them between 00:44 and 01:53). Rates for those periods describe the tool at that moment, not the world.
2. **Success rate** means `caught / (all attempts - no_bite_reset)`, the definition the dashboard uses. `no_bite_reset` (71) is a state check, not a fishing result.
3. `ts` is ISO 8601 with a +09:00 offset; "hours" in tables are local hours of that offset.
4. Periods P1 to P7 are bounded by observed resolution changes and by the times of code fixes in the git history. Fix times are commit times, not necessarily the times the fixed build started running.
5. Evidence images (about 13,000 files, 536 MB at the time) are deleted after 6 hours by design, so most old attempts cannot be re-inspected. Only what the events and the log recorded is analyzed.
6. Not measurable from this data: coin income (the coin counter is not logged), the game's own reaction deadline, the real bite time, and the game's probability table for fish (only observed shares).

## Outcome totals

All 3,523 attempts:

| Outcome | Count | Share of the 3,452 counted attempts |
|---|---|---|
| `caught` | 2,248 | 65.1 % |
| `missed_bite` | 602 | 17.4 % |
| `early` | 273 | 7.9 % |
| `cast_failed` | 142 | 4.1 % |
| `unknown` | 110 | 3.2 % |
| `escaped` | 77 | 2.2 % |
| `no_bite_reset` | 71 | excluded from the denominator |

Steady-state period P4 (19:57 to 00:46, 1080p, 2,145 attempts): caught 79.6 %, missed_bite 9.5 %, early 5.3 %, cast_failed 2.7 %, unknown 1.6 %, escaped 1.3 %. Best period P3 (19:13 to 19:57, 344 attempts): 92.4 %. Best full hour (20:00 to 21:00, 448 attempts): 98.9 % (443 caught).

## Timing

### Time to bite

`wait_s` is the time from the bobber being judged settled to the dip that triggered the click. For catches (n = 2,231 with a value): 5th percentile 0.8 s, 25th 3.6, median 4.8, 75th 5.8, 95th 6.7, mean 4.5.

The shape is flat between 3 and 7 seconds. In the cleanest window (20:00 to 22:00, n = 861 catches):

| Wait bin (s) | 3.0 | 3.5 | 4.0 | 4.5 | 5.0 | 5.5 | 6.0 | 6.5 | below 3 |
|---|---|---|---|---|---|---|---|---|---|
| Catches | 92 | 99 | 112 | 113 | 115 | 131 | 114 | 77 | 8 |

99.1 % of those waits are in [3.0, 7.0). Over all hours 85.7 % of catch waits are in that range and 12.8 % are below 3 s; the below-3 s group is probably re-armed attempts (settle check redone after a candidate switch or view move, restarting the timer), which is why the clean window is used for the shape (this explanation is inferred, not directly logged). Waits do not depend on the fish: mean wait per species is 4.07 to 4.69 s (n = 29 to 585 each).

### Pecks (dips) before the click

`pecks` is the number of dips counted before the click. The tool clicks on dip number `pecks_to_reel` (default 1; the auto-tuner raises it after 2 early results in a row, up to 3, and lowers it after 10 catches in a row; 55 adjustments were logged).

Pecks on `caught` attempts (n = 2,248): 1 -> 1,920, 2 -> 167, 3 -> 132, 4 -> 22, 0 -> 7.

Steady period 19:13 to 00:46, attempts that ended `caught`, `early` or `escaped`:

| Dip number clicked | Attempts | Caught | Early | Escaped |
|---|---|---|---|---|
| 1 | 1,844 | 1,769 (95.9 %) | 71 (3.9 %) | 4 (0.2 %) |
| 2 | 163 | 139 (85.3 %) | 18 (11.0 %) | 6 (3.7 %) |
| 3 | 143 | 92 (64.3 %) | 32 (22.4 %) | 19 (13.3 %) |
| 4 | 13 | 7 | 5 | 1 |

Waiting for more dips was never better than reeling on the first. This is partly confounded (the tuner raises the count only after early reels, which happen when the detector is already confused), but later dips also look like they come too late, matching the "escaped" text.

### Reaction and result latency

| Metric (definition) | n | Median | 5th pct | 95th pct | Other |
|---|---|---|---|---|---|
| `reaction_ms`, catches (dip detected to click sent, inside the tool) | 2,248 | 4 ms | 3 ms | 16 ms | max 151 ms |
| `result_ms`, catches (click to first toast frame seen) | 2,244 | 119 ms | 72 ms | 200 ms | 99th pct 233 ms |
| `reaction_ms`, early | 273 | 10 ms | 4 ms | 51 ms | |
| `reaction_ms`, escaped | 77 | 16 ms | 4 ms | 63 ms | |
| `result_ms`, escaped | 77 | 21 ms | -29 ms | 95 ms | |

A negative or zero `result_ms` means the "got away" toast was already visible before our click (17 of 77 escaped rows). Slower own reactions go with failures (96 % caught when `reaction_ms` is under 5 ms, n = 1,643; 33 % when 20 to 40 ms, n = 69), but slow values mostly occur on multi-dip attempts and around frame stalls, so this is not evidence of a game deadline of 20 ms.

## Early, escaped and missed bites

| Outcome | Toast | What it means in the data |
|---|---|---|
| `early` (273) | "Not hooked yet, reel after the bobber wiggles" | We clicked before the game bit. Median wait 2.1 s, 25 % under 0.7 s. 20 % had a tracked width of at least 20 px |
| `escaped` (77) | "It got away, reel immediately when it sinks" | The reel came too late or at a bad moment. Median wait 4.3 s, median pecks 3. 34 % had a width of at least 20 px |
| `missed_bite` (602) | Same toast, but **no reel click had happened** | The detector reported no dip before the fish left. Median pecks seen 1 |

Evidence:

- Early rate by measured wait, steady period 19:13 to 00:46 (caught + early + escaped): under 3 s: 83 of 329 (25.2 %); 3 to 4 s: 20 of 415 (4.8 %); 4 to 5 s: 1 of 471 (0.2 %); 5 to 7 s: 9 of 895 (1.0 %); 7 s or more: 13 of 51 (25.5 %). A dip before 3 s or after 7 s is very likely not the real bite.
- `missed_bite` traces (last 90 bobber positions, stored in the event) in the steady period (n = 203): in 102 the detector had seen no dip at all, and in 82 the bobber moved less than 4 px in the whole trace, so many are dips the detector did not see rather than slow reactions. The trace spans about 1.5 s (about 50 samples per second). Large amplitudes (75th percentile over 270 px) are cases where the bobber was lost or replaced by another object.
- `missed_bite` by period: P1 303 of 459, P2 51 of 138, P3 6 of 344, P4 203 of 2,145, P5 8 of 57, P6 31 of 290, P7 0 of 90. The 9.5 % in P4 is the floor for this detector in the recorded setup.

## Cast lock and cast failures

- 142 cast failures (4.1 % of counted attempts). By cause: 61 "bobber stayed at the rod tip" (the click was ignored; width at least 15 px), 55 "no settle within 8 s" (35 with a moving bobber seen, 20 with no bobber detected) and 26 "view not facing the pond" (the aim notice; all 26 in one streak at the end, see [Farm analysis](analysis-farm.md#effect-of-farm-photos-on-fishing)).
- Streak lengths of `cast_failed` in a row: 1 (52 times), 2 (16), 3 (1), 4 (4), 5 (1), 6 (1), 28 (1). Without the view-drift streak, no streak exceeded 6.
- Auto-tuner state at the end (`data/tuning.json`): `reel_to_cast = 2.2`, `failed_at = 2.1`. The log shows one shortening to 1.95 s (18:31) and later 2.30 -> 2.25 (01:57), 2.25 -> 2.20 (02:01) and 2.20 -> 2.15 (02:13, just after the cutoff). The cast-lock table is in [How the World Works](world-mechanics.md#cast-lock): about 1 to 2 % of casts are ignored at every interval from 2.0 to 2.7 s.
- Rod-tip failures by period: P4 28 (1.3 % of that period), P1 4, P5 and P6 27 (about 8 % of those 347 attempts), P7 2.

## Fish and experience

### Species table

n = 2,248 catches. Species assigned by name, using the experience value when OCR garbled the name (see [OCR misreads](#ocr-misreads)). Mean wait is `wait_s` in seconds.

| Species | Catches | Share | Exp per catch | Mean wait (s) | Base exp (x2.4 = shown) |
|---|---|---|---|---|---|
| フナ (crucian carp) | 589 | 26.2 % | 5 | 4.61 | 2 |
| ドジョウ (loach) | 450 | 20.0 % | 5 | 4.58 | 2 |
| コイ (carp) | 340 | 15.1 % | 10 | 4.52 | 4 |
| ニジマス (rainbow trout) | 216 | 9.6 % | 19 | 4.67 | 8 |
| ナマズ (catfish) | 201 | 8.9 % | 14 | 4.29 | 6 |
| エビ (shrimp) | 197 | 8.8 % | 19 | 4.41 | 8 |
| サケ (salmon) | 164 | 7.3 % | 24 | 4.69 | 10 |
| 金色のコイ (golden carp) | 33 | 1.5 % | 72 | 4.57 | 30 |
| イトウ (huchen) | 29 | 1.3 % | 144 | 4.07 | 60 |
| ブリ (yellowtail) | 3 | 0.13 % | 480 | -- | 200 |
| ヒラメ (flounder) | 2 | 0.09 % | 432 | -- | 180 |
| タイ (sea bream) | 2 | 0.09 % | 528 | -- | 220 |
| マグロ (tuna) | 1 | 0.04 % | 600 | -- | 250 |
| unresolved | 21 | 0.9 % | -- | -- | -- |

The base column is the shown experience divided by 2.4 and rounded; the tool's own table (`FishExp` in `Core/Toast.cs`) agrees for フナ, ドジョウ, コイ, ナマズ, ニジマス and 金色のコイ. `竿×2.4` appears in 2,175 catch texts (the rest are OCR damage or truncation).

First catches of a species use a different toast ("first time", no inventory count). Six occurred: ヒラメ, エビ, サケ, ブリ, タイ, マグロ. Their experience equals the ordinary experience of that species (ヒラメ was caught again later at 432, ブリ at 480, タイ at 528), so no first-catch bonus is visible.

Saltwater, golden carp and huchen together: 70 of 2,248 catches (3.1 %). The sample is too small to claim time-of-day or session patterns for them. For the common species the three busiest full hours (20:00, 21:00, 23:00) each have フナ + ドジョウ at 44 % to 48 % of catches, so no hourly change in the mix is visible.

### Throughput, experience and coins

Catches counted from `attempt` rows with outcome `caught`. Exp is the toast value with OCR digit loss repaired from the species. "Coins (est.)" is catches times the sell price in the tool's config (`fish_coins`), which was **not measured**.

| Hour | Catches | Exp | Coins (est.) | Notes |
|---|---|---|---|---|
| 19:00 | 379 | 6,746 | 24,171 | includes ブリ, タイ, マグロ (about 15,500 of the est. coins) |
| 20:00 | 443 | 5,859 | 9,615 | best hour |
| 21:00 | 418 | 6,206 | 13,825 | includes one ブリ |
| 22:00 | 150 | 1,789 | 2,616 | high failure rate |
| 23:00 | 371 | 4,672 | 11,983 | |
| 00:00 | 297 | 4,368 | 10,388 | |
| 01:00 | 78 | 961 | 1,435 | 4K and many restarts |
| 02:00 (to 02:12) | 55 | 1,261 | 6,234 | includes one タイ |

- Peak: 443 catches in one hour (20:00 to 21:00), one every 8.1 s, 5,859 exp. The interval between consecutive attempts in that window was 8.0 s median (5th pct 6.1 s, 95th 9.7 s, n = 904). Over all catches the median catch-to-catch interval (gaps above 120 s excluded) is 8.35 s (n = 2,233).
- A cycle is roughly: reel-to-cast wait 2.0 to 2.2 s + cast flight and settle 0.5 to 1 s + wait for the bite 3 to 7 s + click and toast about 0.12 s. The components are from separate measurements and are not summed per attempt.
- Mean exp per catch: 13.2 excluding the four saltwater species, 15.0 including them (n = 2,224 catches with a readable value). Expected sell value is about 22 coins per freshwater catch with the tool's price table.
- Averaged over the whole 9.3 hours: about 242 catches per hour, because of the bad periods.

### Experience and levels

496 `hud` rows. Lv 45 at 30.9 % at 16:56 to Lv 49 at 78.8 % at 02:12. Level-ups at 18:59, 20:16, 21:38 and 23:47.

| Level reached | Time to next level | Catches in between | Exp from catches | Of which saltwater |
|---|---|---|---|---|
| Lv 46 | 77 min | 502 | 8,762 | 1,608 |
| Lv 47 | 83 min | 607 | 8,057 | 480 |
| Lv 48 | 128 min (includes the 22:00 failure hour) | 571 | 7,553 | 0 |

Excluding saltwater catches one level took about 7,150, 7,580 and 7,550 exp, so the requirement near Lv 46 to 48 looks like 7.2k to 7.6k. This is a **lower bound** because attempts whose toast was never read are not counted (110 `unknown` plus missed toasts).

### Inventory counter

The toast's "持っている N匹" counter, per species (n = 567 consecutive フナ reads): +1 in 529 cases, +2 in 28, unchanged in 3, +3 in 2. It resets to 1 after the species is sold or shipped (observed for each of the seven common species between 22:08 and 01:57). A few reads drop by about 100 to 130 (コイ 246 -> 120, 263 -> 132); those are OCR digit loss. No inventory cap is visible (largest value read: 559).

## Success-rate history

### By hour

| Hour | Attempts | Caught | Missed | Early | Escaped | Unknown | Cast failed | Success |
|---|---|---|---|---|---|---|---|---|
| 11-04 16 | 18 | 7 | 7 | 3 | 0 | 0 | 0 | 41.2 % |
| 17 | 91 | 17 | 54 | 10 | 1 | 0 | 1 | 20.5 % |
| 18 | 382 | 33 | 263 | 28 | 15 | 13 | 9 | 9.1 % |
| 19 | 473 | 379 | 36 | 31 | 6 | 9 | 4 | 81.5 % |
| 20 | 448 | 443 | 1 | 2 | 0 | 0 | 2 | 98.9 % |
| 21 | 457 | 418 | 22 | 10 | 2 | 1 | 4 | 91.5 % |
| 22 | 412 | 150 | 112 | 55 | 20 | 25 | 42 | 37.1 % |
| 23 | 460 | 371 | 50 | 26 | 5 | 5 | 3 | 80.7 % |
| 11-05 00 | 353 | 297 | 19 | 20 | 2 | 3 | 8 | 85.1 % |
| 01 | 346 | 78 | 38 | 88 | 26 | 54 | 41 | 24.0 % |
| 02 (to 02:12) | 83 | 55 | 0 | 0 | 0 | 0 | 28 | 66.3 % |

`no_bite_reset` is omitted from the columns and the success denominator. Gaps with no attempts: 26 minutes at 17:10 and 14 minutes at 00:47.

### By period (resolution and fix timeline)

| Period | Window | Resolution | Attempts | Caught | Missed | Early | Escaped | Unknown | Cast failed | Success |
|---|---|---|---|---|---|---|---|---|---|---|
| P1 | 16:56 to 18:54 | 4K, raw | 459 | 56 | 303 | 38 | 14 | 11 | 10 | 13.0 % |
| P2 | 18:54 to 19:13 | 1080p | 138 | 43 | 51 | 21 | 6 | 10 | 0 | 32.8 % |
| P3 | 19:13 to 19:57 | 1080p | 344 | 314 | 6 | 13 | 2 | 1 | 4 | 92.4 % |
| P4 | 19:57 to 00:46 | 1080p | 2,145 | 1,697 | 203 | 113 | 28 | 34 | 58 | 79.6 % |
| P5 | 00:46 to 01:14 | 4K, raw | 57 | 7 | 8 | 5 | 6 | 3 | 21 | 14.0 % |
| P6 | 01:14 to 01:59 | 4K, shrunk by 2 | 290 | 69 | 31 | 83 | 21 | 51 | 21 | 25.0 % |
| P7 | 01:59 to 02:12 | 1080p | 90 | 62 | 0 | 0 | 0 | 0 | 28 | 68.9 % |

Fix timeline from the git history (commit times, local; the running build may have started later):

| Time | Change (summary of the commit message) | Visible effect in the data |
|---|---|---|
| 18:51 | Clicks moved to OSC `Use`, so VRChat can stay in the background | Spans the P1/P2 boundary; the window size also changed then |
| 19:05 | Stop tracking other avatars' red/white as the bobber | P2 success 32.8 % |
| 19:13 | Exclude bobber candidates visible before the cast (other players' bobbers) | P3 success 92.4 %; missed bites 51 -> 6 |
| 19:29 | Added saltwater fish names and prices to the known list | Names resolve afterwards |
| 19:57 | Redo the settle check when the stored base position and the current position differ widely | P4 stays near 80 % for 5 hours |
| 00:37 | Fewer wrong candidate switches; farm photo only when VRChat is in front | not isolated |
| 01:14 | Frames at least twice the work height shrunk by an integer factor | caught width 11 px (work frame) vs 22 px raw; success 25.0 % vs 14.0 % |
| 01:56 | Remember cell counts of other avatars' red/white and track the own bobber regardless of width | P7: no missed bite, early or unknown in 90 attempts |

P7 contains the 28-attempt view-drift streak; excluding it, its other 62 attempts were all catches. That is only 13 minutes, but it is the cleanest result of the run.

## Failure taxonomy

"Seen" means the log or events contain the mechanism directly; "suggested" means the most likely explanation, not isolated.

| Cause | Outcomes affected | Evidence | Strength |
|---|---|---|---|
| **Wrong object tracked** (other players' bobbers, avatar red/white parts) | `missed_bite`, `early`, `escaped` | 906 "switching to a better candidate" log lines (402 in the 23:00 hour), 100 `desync` events (93 "reel ineffective"), and the jump from 32.8 % to 92.4 % in step with the 19:05 and 19:13 fixes | Seen (switching); suggested (as cause of the P2 failures) |
| **4K windows** | `missed_bite` (P1), `unknown` and `early` (P6), rod-tip `cast_failed` (P5, P6) | 13.0 %, 14.0 %, 25.0 % vs 68.9 to 92.4 % at 1080p; loop stalls per active hour 27 to 83 at 4K vs 1.4 to 11 at 1080p | Suggested (confounded by restarts in P5, P6) |
| **Loop stalls** | `missed_bite`, bad settle, desync | 196 `gap` events (median 1.0 s, 95th pct 2.2 s, max 10.1 s), 124 in `cast_wait`; the tool redoes the settle check after a stall. Note: the `gc pause=` value in log lines is the cumulative GC pause since process start, not a per-event pause | Seen |
| **Toast region noisy or obstructed** | `unknown` | The 22:00 hour had 25 `unknown` and 3,654 `toast_unclassified` events reading a stationary `Lv50` text; the 01:00 hour (4K) had 54 `unknown` | Suggested |
| **OCR misreads** | wrong species or exp, `unknown` | [OCR misreads](#ocr-misreads) | Seen |
| **View drift after a farm photo** | `cast_failed` (aim notice) | 26 aim failures plus 2 rod-tip in 5 minutes at 02:03 to 02:09, starting right after a farm photo | Seen |
| **Cast ignored (cast lock)** | `cast_failed` rod-tip | 61 events; about 1 to 2 % of casts at 2.0 to 2.7 s | Seen |
| **Paused during a bite** | `no_bite_reset` | 20 of 71 were "bite passed while paused / at start" | Seen |

Cast-failed streaks are mostly isolated (52 of length 1, 16 of length 2; 7 of length 3 or more apart from the 28-long streak). Rod-tip failures always show a width of at least 15 px at the rod (the threshold).

### OCR misreads

Of the catch toasts, the stored species name (after normalization) was a known name in 76.8 % of Windows OCR results (n = 912) and 95.5 % of PP-OCR results (n = 1,375). The experience was a valid value in 93.2 % and 98.5 %. The count was present in 99.9 % and 99.6 %. PP-OCR is slower (median click-to-result 129 ms vs 103 ms).

Damage patterns among 545 catch toasts whose parsed species was not a known name:

| Intended | OCR result | Seen | Notes |
|---|---|---|---|
| ニジマス | 二ジマス (kanji "two" for katakana "ni") | 106 | Kana confusion |
| コイ | ュイ | 49 | Leading letter lost; many with `+0` exp |
| エビ | 工ビ (kanji "work" for katakana "e") | 47 | |
| ドジョウ | ョウ, ジョウ, ウ, (-ジ,ウ | 32, 9, 8, 8 | Truncated |
| フナ | ブナ, ナ | 15, 11 | |
| any | stray 日, X or ( before the name | about 60 | |
| experience | `+10` read `+0` (28 コイ catches), 19 -> 9, 14 -> 4, 144 -> 44 | about 80 | Leading digit lost |
| inventory | 246 -> 120, 263 -> 132 | about 5 | Digit lost |

The tool first normalizes known names (best match at 0.6 similarity or more), then falls back on the experience value. Experience values are distinct enough for this except フナ/ドジョウ (both 5) and ニジマス/エビ (both 19). Classification text variants seen repeatedly: `まだかかっていません...` with damaged prefixes (about 45 variants of the "not hooked yet" toast) and `逃げられた..ウキが沈んだら、すぐに引こう` with prefix or suffix damage; overlapping toasts produce mixed lines.

## Questions this dataset cannot answer

- **Real bite time.** `wait_s` starts at the tool's settle judgment, and the triggering dip is not necessarily the game's bite.
- **Game deadline after a bite.** The slowest successful reaction was 151 ms, but no experiment used deliberately slow clicks.
- **Rarity tables.** Observed shares only; rare species have 1 to 3 samples.
- **Coins.** Not logged.
- **Time-of-day effects in the world.** None visible; differences between hours are explained by the tool's state.
