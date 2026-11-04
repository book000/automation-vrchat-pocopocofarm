# How the World Works

This page describes the behavior of the VRChat world "pocopoco farm" as observed while building and running this tool. It is written for anyone who wants to automate the world (with this tool or their own) or just understand how it behaves. The numbers come from a recorded run; the full statistics, sample sizes and reproduction steps are in [Fishing analysis](analysis-fishing.md), [Farm analysis](analysis-farm.md) and the [Automation guide](automation-guide.md).

Conventions used throughout:

- **Observed** means measured in the recorded data. **Implemented** means the tool's code assumes or relies on it. **Reported** means stated by the maintainer but not exercised by this repository's code or data.
- The world's text is Japanese. Toast texts are given in Japanese with an English gloss.
- Screen coordinates are ratios of the frame, or pixels at the 1920x1009 reference resolution unless stated.
- The recorded run is one account, one desktop-mode client, 2026-11-04 16:56 to 2026-11-05 02:12 (UTC+9), 3,523 fishing attempts. See [the dataset](analysis-fishing.md#dataset).

## The pond and the rod

You stand at the pond holding a fishing rod. The in-world hint at the bottom left reads (OCR of the live image, rod level 5 in the recorded account): "Look at the pond and click to cast -> when the bobber sinks, click to reel in."

| Step | What happens | How the tool observes it |
|---|---|---|
| Cast | A click (right-hand "Use" input) throws the bobber toward where you look. It must be thrown at the pond water, 1 to 5 m from the shore. | A red-and-white bobber moves, then settles on the water. |
| Landing | The bobber splashes down and bobs very slightly. A bobber at rest on the recorded setup was about 10 px wide (1920x1009 frame) at the usual landing spot. | Position and width stabilize within a few pixels. |
| Bite | After a random delay the fish bites and the bobber sinks sharply (see below). | The bobber jumps down 8 to 10 px, about one bobber width, in one or two frames. |
| Reel | A second click reels in. It must come soon after the sink. | -- |
| Result | A toast (text notification) appears on the left side of the screen. | OCR of the toast region. |

### Bite timing (observed)

- The first bite arrives **3.0 to 7.0 seconds** after the bobber has settled. In the cleanest 2 hours of data (861 catches), 99.1 % of waits fall inside that range; the minimum was 0.1 s and the maximum 6.9 s. The distribution inside the range is roughly flat (about 90 to 130 catches per half-second bin). Median 5.0 s, 5th percentile 3.2 s, 95th percentile 6.6 s.
- Which fish you get does not depend on how long you wait: mean wait per species is 4.1 to 4.7 s for all nine common species (n = 29 to 585 per species).
- A bite shows as a **single sharp drop** (median 9 px, interquartile range 8 to 10 px at a 10 px wide bobber) rather than a gradual sink. There is a short "wiggle" in the game's own wording ("びくびく沈んでから引こう": reel after the bobber wiggles and sinks), but with a frame rate around 45 to 50 fps 85.4 % of catches (1,920 of 2,248) were made by reeling on the first detected drop (the tool's default); among those reeled in the steady period, 96 % of first-drop reels were catches (1,769 of 1,844).
- Dips detected **before 3 s** are mostly not bites. In the steady period (19:13 to 00:46) reeling on a dip with a measured wait under 3 s was an "early" result in 27 % of cases (83 of 329); with a wait of 3 to 5 s the early rate was 2.4 % (21 of 886). See [Early, escaped, missed](analysis-fishing.md#early-escaped-and-missed-bites).

### Reaction window

The world does not show how long you have after the sink. What was measured:

- Our own latency from detecting the sink to sending the click (`reaction_ms`) was 4 ms median, 16 ms at the 95th percentile, 151 ms maximum among 2,248 successful catches. No catch was recorded with more than 151 ms of own latency, so the true deadline is **not** known to be anything longer.
- The click to first-visible-toast latency (`result_ms`) was 119 ms median (72 ms 5th, 200 ms 95th percentile), and it is mostly the OCR polling interval (0.12 s) plus the toast fade-in.
- When the fish gets away before the click, the "got away" toast arrives with no reel click at all (`missed_bite`, 602 cases, nearly all in the first 2 hours when the detector followed the wrong object, see [the analysis](analysis-fishing.md#failure-taxonomy)). So there is a point at which the fish gives up on its own; the data cannot say how many seconds that is.

### Cast lock

After a reel the game ignores casts for about two seconds ("cast lock"). Observed from 2,626 cast attempts (54 failures) where the interval from the reel click to the next cast click was logged:

| Interval reel -> cast | Casts | Cast ignored (bobber stays at the rod tip) |
|---|---|---|
| 1.95 to 2.00 s | 5 | 1 |
| 2.00 to 2.05 s | 1,371 | 11 (0.8 %) |
| 2.05 to 2.10 s | 466 | 9 (1.9 %) |
| 2.10 to 2.20 s | 247 | 4 (1.6 %) |
| 2.20 to 2.35 s | 333 | 4 (1.2 %) |
| 2.35 to 2.70 s | 82 | 1 (1.2 %) |
| 2.70 to 4 s | 6 | 1 |
| 4 s and longer | 115 | 23 (long intervals follow retries and view problems, so they say nothing about the lock) |

There is no sharp threshold: the failure rate is about 1 to 2 % over the whole range from 2.0 to 2.7 s and does not fall to zero at longer intervals. Below 2.0 s there are too few samples to say (the tuner lowered the wait once to 1.95 s at 18:31). The tool's auto-tuner ended at `reel_to_cast = 2.2` with `failed_at = 2.1` (`data/tuning.json`). A cast click during the lock simply does nothing, so the cost of a too-early cast is one wasted cycle, not a penalty. Casting at 2.0 s and re-casting after a failure is cheaper than waiting longer for every cycle.

## Results and toasts

All results are text toasts in a box on the left of the screen. The tool reads the box, which is at `[0.06, 0.28, 0.36, 0.725]` of the reference frame (`toast_roi`). The HUD hint, the coin counter and the level indicator sit below it.

| Meaning | Toast text (Japanese) | Gloss |
|---|---|---|
| Caught | `<Fish>が釣れた！+<exp>経験値(竿×2.4・持っている<N>匹)` | "<Fish> caught! +exp experience (rod x2.4, you have N)" |
| First catch of a species | `<Fish>がはじめて釣れた！+<exp>経験値(竿×2.4・かばんの『魚』で見られるよ)` | Same, plus "you can see it in your bag's Fish page"; no inventory count is shown |
| Reeled too early | `まだかかっていません ウキがびくびく沈んでから引こう` | "Not hooked yet; reel after the bobber wiggles and sinks" |
| Fish got away | `逃げられた.. ウキが沈んだら、すぐに引こう` | "It got away.. reel immediately when the bobber sinks" |
| Aim notice | `池の水に向けて投げよう` and `池から1~5mくらいの所で、竿の先を水へ向けて` | "Throw toward the pond water" / "Within 1 to 5 m of the pond, point the rod tip at the water" |
| Level up | `レベル<N>になった!` | "Became level N" |
| Achievement | `実績「<name>」を達成!じっせきの看板で報酬を受け取れます` | "Achievement reached; collect the reward at the achievements sign" |
| Species unlocked for the mini-garden | `箱庭に<Fish>を置けるようになりました 小さな池で泳ぐよ` | "You can now place <Fish> in the mini-garden; it swims in the small pond" |
| Cat helper | `ねこちゃんが<item>を見つけてきた!×N` | "The cat found <item>" (seeds or coins) |

Notes that matter for automation:

1. **The aim notice is the only signal that a cast failed because of where you look.** It is shown when the view is not on the pond or the rod tip is not pointed at water. In the recorded run the view drifted off the pond once, after a farm photo, and produced 26 consecutive aim failures in 5 minutes before it was fixed by hand (see [Farm analysis](analysis-farm.md#effect-of-farm-photos-on-fishing)).
2. **Toasts stack and overlap.** A catch toast can be partly covered by the aim notice, a level-up or an achievement toast, so OCR text sometimes contains two sentences. Classify by priority: level up, achievement, aim, early, escaped, caught (the order the tool uses).
3. **The "early" and "escaped" texts contain each other's hint.** "Not hooked yet ..." and "It got away .. when the bobber sinks ..." both mention the sinking bobber, so match on the leading phrase (`かかっていません` or `逃げられた`), never on the shared hint text.
4. **Only the catch toast carries the fish and the exp.** There is no separate hook toast. Experience in the toast is already multiplied by the rod ("竿×2.4").
5. A sign or name tag containing "Lv50" is read as unclassified noise thousands of times (3,731 `toast_unclassified` events, 74 % containing `v50`, 3,654 of them in the 22:00 hour). One reading is "Unlocks at Lv 50: HUD and volume settings are at the stand next to the mirror" (OCR of a sign, garbled). Ignore them.
6. Farm and shop notices ("seed", "coin", "shipping box", "delivery", name plates) also appear in the toast region when you stand near them. The tool files them under `toast_other` with categories `farm` (209), `nameplate` (20) and `delivery` (15).

## HUD

| Element | Location (ratio of the 1920x1009 reference) | What it shows |
|---|---|---|
| Level text "Lv N" | `level_roi = [0.255, 0.828, 0.325, 0.889]` | Player level; read by OCR |
| Experience bar | `xp_bar_roi = [0.2615, 0.869, 0.319, 0.879]` | Fill fraction of the current level; read by color |
| Coin counter | Left of the level text | Total coins; not read or logged by the tool |
| Rod hint | Just above the coin counter | "Fishing rod (Lv N): look at the pond and click to cast..." |

The HUD is anchored to the horizontal center plus a height-proportional offset, so its regions must be recomputed for each window size; the tool also realigns them by finding the "Lv" label when the level cannot be read three times in a row (see [Architecture](architecture.md#hud-reading)). While the in-game camera is **streaming**, the HUD (level, toasts) disappears; this is why the tool only streams for the few seconds of a farm photo.

Level progression observed: Lv 45 (30.9 %) at 16:56 to Lv 49 (78.8 %) at 02:12. For the three full levels measured (46, 47 and 48), recorded catches add up to about 7,150 to 7,580 experience per level when the four saltwater catches are excluded (a lower bound, because attempts whose toast was not read are not counted); see [the analysis](analysis-fishing.md#experience-and-levels).

## Fish

Experience per catch (as shown in the toast, rod multiplier already applied): **5, 10, 14, 19, 24, 72, 144** for the common species and **432 to 600** for four saltwater species. All of them are consistent with a base value times 2.4 rounded (for example 2 x 2.4 = 4.8 -> 5, 8 x 2.4 = 19.2 -> 19, 180 x 2.4 = 432).

| Species (Japanese) | Exp per catch | Share of 2,248 catches | Sell price used by the tool (config assumption) |
|---|---|---|---|
| Crucian carp (フナ) | 5 | 26.2 % | 3 |
| Loach (ドジョウ) | 5 | 20.0 % | 5 |
| Carp (コイ) | 10 | 15.1 % | 12 |
| Rainbow trout (ニジマス) | 19 | 9.6 % | 28 |
| Catfish (ナマズ) | 14 | 8.9 % | 20 |
| Shrimp (エビ) | 19 | 8.8 % | 30 |
| Salmon (サケ) | 24 | 7.3 % | 50 |
| Golden carp (金色のコイ) | 72 | 1.5 % | 150 |
| Japanese huchen (イトウ) | 144 | 1.3 % | 400 |
| Yellowtail (ブリ) | 480 | 0.13 % (n = 3) | 4,500 |
| Flounder (ヒラメ) | 432 | 0.09 % (n = 2) | 4,000 |
| Sea bream (タイ) | 528 | 0.09 % (n = 2) | 5,000 |
| Tuna (マグロ) | 600 | 0.04 % (n = 1) | 6,000 |
| Unresolved (garbled OCR) | -- | 0.9 % (21) | -- |

Sell prices come from the tool's `fish_coins` setting, not from the recorded data (the coin counter is not logged), so treat the coin columns as assumptions. Species with the same experience (フナ/ドジョウ, ニジマス/エビ) can only be told apart by the name in the toast. English names are translations and may differ from the world's own wording.

Inventory: the toast says "you have N" for the species just caught; N increases by exactly 1 per catch (for フナ: 529 of 567 consecutive reads differ by +1, 28 by +2 where a catch was not read, 3 are unchanged). It drops back to 1 after the species is sold or shipped (observed for every species between 22:08 and 01:57), so it is not a cap. A few single-read drops (for example コイ 246 -> 120) are OCR digit loss, not sales.

## Camera, capture and input

### Input

| Purpose | Mechanism | Needs focus? |
|---|---|---|
| Cast / reel | OSC `/input/UseRight` = 1, hold 50 ms, then 0 (UDP to 127.0.0.1:9000) | No |
| Farm camera pose | OSC `/usercamera/Pose` (6 floats: x, y, z, pitch, yaw, roll) | No, but only while the camera is open |
| Farm camera zoom | OSC `/usercamera/Zoom` (1 float) | No, but only while the camera is open |
| Spout stream | OSC `/usercamera/Streaming` (bool) | No, but only while the camera is open |
| Open the camera | Keyboard `Esc`, then mouse click on the Camera tab, then the "Stream Camera" button | **Yes**: foreground window and real input |

OSC must be enabled in VRChat (Action Menu -> Options -> OSC -> Enabled). All fishing runs on OSC only, so VRChat can sit behind other windows.

Findings about OSC (implemented in `src/Pocofarm/Farm/Osc.cs` and `SpoutCamera.cs`):

- `/usercamera/*` messages only have an effect **while the in-game camera is already open**. `/usercamera/Mode` cannot open the camera, only close it (0).
- `/usercamera/Streaming` requires a boolean OSC argument; an integer is ignored.
- Spout shows the farm only while the camera is open. Closing the camera while still streaming returns the shared texture to your own view.
- The tool's framing search changes only `x`, `z` and zoom. The camera height is not limited to 10: poses at `y` = 30 and 60 gave higher views of the world (see [World map](world-map.md)).
- Reported (not exercised by this repository): `/input/LookHorizontal` has no effect in desktop mode, while `/input/LookVertical` works. The tool therefore never turns the view over OSC; the view must already face the pond.

### Capture

- The tool uses Windows Graphics Capture (WGC) on the VRChat window. WGC captures a window even if it is covered by other windows, so you can keep working on other things. A **minimized** window produces no frames; the tool restores it.
- WGC delivers a frame only when the picture changes, and a loop that waits for frames can stall for a second or more when nothing changes. In the recorded run 196 loop stalls longer than 0.7 s were logged (median 1.0 s, 95th percentile 2.2 s, maximum 10.1 s); 63 % (124) happened while waiting for the bobber to settle after a cast (`cast_wait`). Stalls were 3 to 7 times more frequent in the 4K windows (see below).
- Typical processing speed on the recorded machine: about 46 frames per second at 1080p, with bobber detection taking 2.4 ms on average (`status.json` `perf` section).
- The farm photo uses a different path: Spout (shared D3D11 texture) from the in-game camera in stream mode, not WGC.

### Resolution handling

- The recorded run started at 3840x2054 (4K window), switched to 1920x1009 for about 6 hours, went back to 4K for about 1.2 hours and ended at 1920x1009.
- At raw 4K the bobber is about twice as wide as at 1080p (median 22 px vs 10 px) and the toast text is larger. Frames at least twice the work height (4K) are shrunk by an integer factor before detection and OCR so thresholds match; other sizes are scaled by `height / 1009`. The integer downscale was added during the recorded run (01:14), so the earlier 4K windows ran at raw 4K.
- In the recorded data all three 4K windows had a far worse success rate (13.0 %, 14.0 % and 25.0 %) than the 1080p windows after the decoy fixes (92.4 %, 79.6 % and 68.9 %, the last one dominated by one view-drift streak), and loop stalls were 3 to 7 times more frequent (27 to 83 per active hour at 4K vs 1.4 to 11 at 1080p). The 4K windows also coincide with heavy development churn (twelve restarts between 00:44 and 01:53), so the effect of resolution is **suggestive, not isolated**. See [Failure taxonomy](analysis-fishing.md#failure-taxonomy).
- The window can change size by itself (for example 1920x998, 1920x1046, 1280x953 within 3 s during a window move); the tool recreates the capture when the captured size disagrees with the window for 2 s.

## Decoys and the bobber

- The bobber is a **red stick with a white ball** below it, about 10 px wide at 1080p (median of 2,231 catches; 95th percentile 11 px). Other players' bobbers, red or white avatar parts and world props look similar. The log records 906 "switched to a better candidate" events, 402 of them in the 23:00 hour alone, and the developer's commit messages at 19:05 and 19:13 name other avatars' red/white parts and other players' bobbers as the cause of tracking the wrong object.
- The player's own bobber lands in a very small area: in the 1080p period 19:57 to 00:46, 1,658 of 1,697 catches (97.7 %) landed within 10 px of the same screen position (980, 390) of the 1920x1009 frame. Learn the landing position and prefer candidates near it.
- Decoys such as other players' bobbers are often visible **before** your cast, so everything red-and-white visible before the cast can be excluded afterwards (the tool does this, plus a memory of cells where decoys were seen).

## Farm summary

Eight plots (2 rows x 4 columns), each 3 columns x 4 rows of cells = 96 cells. A top-down photo through the in-game camera at pitch 88 degrees shows all of them. Wet soil is dark brown, dry soil is light pinkish-tan; a planted cell shows green leaves, and ripe cells show golden sparkles. Details: [Farm analysis](analysis-farm.md).

## Pitfalls summary

1. Decoy bobbers and avatar parts: track by learned landing position, exclude what is already visible before the cast.
2. A frame stall of 1 s or more during the wait looks like a dip: restart the settle check after a gap.
3. OCR misreads digits and kana of toasts (for example ニジマス -> 二ジマス, エビ -> 工ビ, "+10" -> "+0"); use a second engine for incomplete reads and map experience values back to species.
4. The world drops camera or view state after the farm photo sequence: verify the pond is in view before casting again.
5. A paused tool misses bites: the first confirmation click after a pause usually shows the "got away" toast (71 `no_bite_reset` events: 20 were exactly that, 26 were a cast made with the bobber in the air, 25 a re-pull with no bite).
6. 4K windows gave poor results in all three recorded windows, including the one with integer downscaling.
7. Streaming hides the HUD; never stream while fishing.
