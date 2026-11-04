# Farm Analysis

What the recorded run shows about the farm: the plot layout, how the tool reads plots from a photo, the history of scans, the camera pose that sees all plots, how the Stream Camera and Spout capture works and where it breaks. For the shorter summary see [How the World Works](world-mechanics.md#farm-summary).

Datasets: `data/farm/` (12 photos with analysis JSON kept at the end of the run, the tool keeps the newest 12), `farm_scan` events (26) and `farm_calibration` events (4) in `data/events.jsonl` and the archives, `data/camera_pose.json` and its `.bak`, and `data/fishing.log` (27 "fishing stopped for the photo" lines, 20 scans with all 8 plots found). Period 2026-11-04 16:56 to 2026-11-05 02:12 (UTC+9).

## Layout

- **8 plots in 2 rows of 4.** Each plot is a wooden frame holding **3 columns x 4 rows = 12 cells**, so **96 cells** in total. Seeds are sold in packs matching this ("12 (96)" appears in the shop text read by OCR).
- In a 1280 px wide photo taken from the final pose the plots occupy these fractions of the image (from the analysis JSON; each quad is top-left, top-right, bottom-right, bottom-left):

| Row | Plot columns (x, left to right) | y range |
|---|---|---|
| Top | 0.10-0.26, 0.32-0.47, 0.53-0.68, 0.73-0.89 | 0.04-0.40 |
| Bottom | 0.10-0.26, 0.32-0.47, 0.53-0.68, 0.74-0.90 | 0.59-0.96 |

- A sign with the "how to play" text sits between the two rows, and props (round cushions, a pot) are visible at the edges. A crop setting (`farm_shot_crop`) can remove edges that confuse the analysis such as a red rug.

## How cells look

Seen in the photos (top-down, camera pitch 88 degrees):

| State | Appearance | Tool code |
|---|---|---|
| Unplanted, dry | Light pinkish-tan soil, no green | `e` |
| Planted, dry | Light soil with a small green sprout | `p` |
| Unplanted, watered | Dark brown soil, no green | `b` |
| Planted, watered | Dark brown soil with green leaves (small sprouts to large leafy bushes) | `w` (the target state) |
| Hidden | An avatar or shadow covers the cell | `?` |

- Watered soil is clearly darker. The tool computes for each cell the share of dark-soil pixels (`wet`, 0 to 100). In the final photo the typical values were 95 to 100 for watered and 0 to 2 for dry cells, with a few intermediate values (for example 67 and 87) at cells that were being watered. A change in watering is only counted when `wet` changes by at least 40 points (`farm_water_min_change`) so that edge cases do not flicker.
- The blue/red ratio of a cell separates the two soils: about 0.48 for dark brown, about 0.6 for light brown (documented in the `farm_empty_br` setting, threshold 0.54).
- Plants: a cell counts as planted if at least 0.2 % of its pixels are green (`farm_plant_ratio`). Larger plants are not told apart from sprouts by the tool, so **growth stage and ripeness are not measured**. Ripe crops show golden sparkles in the photo (the in-world toast says a golden watermelon was harvested and sells high in the shipping box); this is visible to a person but is not classified by the code.
- A plant's growth leaves are green and dark, which would also look like a frame edge, so the plot-finding step excludes green pixels before looking for the dark frame lines.

## Scan history

Of 26 `farm_scan` events, 20 found all 8 plots. The 6 failures are from before the camera pose was good: 0 plots at 16:56 and 18:53 (no saved pose yet, so no framing), 1 plot at 00:29, 0 plots at 00:31 and 00:32 and 6 plots at 00:32 (the first poses of the framing search), and 1 plot at 00:32:56 (the calibration step that failed).

| Time (11-05 unless noted) | Plots found | Unplanted | Dry | Watered ("done") | Hidden |
|---|---|---|---|---|---|
| 00:36:53 | 8 | 95 | 96 | 0 | 0 |
| 00:46:17 | 8 | 96 | 96 | 0 | 0 |
| 01:03:37 | 8 | 0 | 20 | 75 | 1 |
| 01:14 | 8 | 0 | 4 | 90 | 2 |
| 01:21 | 8 | 0 | 8 | 88 | 0 |
| 01:25 | 8 | 0 | 12 | 83 | 1 |
| 01:31 | 8 | 0 | 6 | 89 | 1 |
| 01:36 | 8 | 0 | 10 | 85 | 1 |
| 01:38 | 8 | 0 | 15 | 78 | 3 |
| 01:40 | 8 | 1 | 33 | 63 | 0 |
| 01:43 | 8 | 0 | 36 | 60 | 0 |
| 01:48 | 8 | 0 | 8 | 86 | 2 |
| 01:53 | 8 | 0 | 11 | 85 | 0 |
| 02:03 | 8 | 0 | 7 | 89 | 0 |
| 02:13 | 8 | 0 | 16 | 80 | 0 |

(Counts are of the 96 cells; "dry" = unplanted dry + planted dry, "done" = planted and watered; counts can sum to a little under 96 because of hidden cells. The 02:13 row is just after the cutoff for the fishing analysis, so it is the last scan in `data/farm/`.)

What it shows: between 00:46 and 01:03 the field went from fully empty and dry to fully planted and mostly watered (done by hand, not by the tool; the tool only photographs). Afterwards the number of dry cells moved between 4 and 36 as plants dried out and were watered again; for example 8 dry at 01:21, 36 at 01:43 and back to 8 at 01:48. In the 12 saved photos the planted-and-watered cell count ranged from 60 to 90 out of 96 and dry planted cells from 4 to 36. The data contains no growth-time measurement (the photos are too sparse, and growth stage is not classified).

## Camera pose

The pose is stored in `data/camera_pose.json` as 7 numbers: `x, y, z, pitch, yaw, roll, zoom`. The first six are sent over OSC as `/usercamera/Pose`, the seventh as `/usercamera/Zoom`.

| When | Pose | Source |
|---|---|---|
| Before the search at 00:36 | `[0, 10, -3, 70, 0, 0, 45]` | Earlier manual pose, pitch 70 |
| After the first successful search (00:36:52) | `[-0.026, 10, -3.868, 70, 0, 0, 59.28]` | `farm_calibration` event; plot cluster centered at (0.502, 0.519), size 0.94 |
| Before the second search (backup) | `[0, 10, 0.25, 88, 0, 0, 45]` | `data/camera_pose.json.bak` |
| Final (00:46:16) | `[-0.011, 10, -0.626, 88, 0, 0, 56.61]` | `data/camera_pose.json`; centered at (0.500, 0.499), size 0.999 |

Facts from the code and the log:

- The final pose looks straight down (pitch 88) from height 10 with zoom 56.6 (a smaller zoom value shows a wider area; measured by comparing shots at 28, 36 and 56). With it all 8 plots fit in the picture with a margin (the tool targets a 4 % margin and a centre within 3 % of the picture centre).
- The framing search moves only `x`, `z` and zoom; height, yaw and pitch are fixed. (Height is not capped: separate test poses at `y` = 30 and 60 produced higher views.)
- The search measures how the plot cluster moves when the pose changes (a Jacobian), then applies Newton steps, with Broyden updates; it widens the zoom if fewer than 8 plots are found. 4 `farm_calibration` events: 3 ok (2 of them moved the camera), 1 failed, 1 of the 3 ok ones changed nothing ("already fits").
- Calibration runs on the first photo after start-up, and on request from the dashboard. If no pose file exists the tool does not calibrate ("no original camera position", 3 warnings).

## Capturing the farm: Stream Camera and Spout

The farm cannot be seen from the fishing position, so the tool opens the in-game camera, streams it as a Spout texture, grabs one frame, and closes it again.

Sequence implemented in `Farm/FarmShots.cs` and `Farm/SpoutCamera.cs`:

1. Wait (up to `farm_shot_wait_seconds`, 60 s) for fishing to be in `cooldown`, i.e. just after a catch, and pause fishing. VRChat must already be in the foreground (`farm_shot_foreground_only`).
2. Press `Esc`, then click the Camera tab (found by its icon or by remembered ratio, verified by its selected light-blue colour), then click the "Stream Camera" button (found by icon, match 0.96 at scale 1.15 in 14 cases; the icon was not found in 13 cases and the configured position `[0.469, 0.486]` was used). If the expected screen is not seen, the sequence aborts without clicking, which protects against a click that leaves the world.
3. Restore the saved pose over OSC, switch on `/usercamera/Streaming`, wait for the Spout sender (up to 6 s; the command is repeated once at 40 % of the wait).
4. Read the shared D3D11 texture (Spout 2.007 shared memory plus handle) in the tool itself, without the native Spout library, and grab up to 4 frames.
5. Save the JPEG (1,280 px wide, quality 80) and the analysis JSON; keep the newest 12.
6. Stop streaming, set the camera mode to closed, and resume fishing.

Limits seen:

- `/usercamera/*` messages work only while the camera is open; Mode cannot open it. Streaming needs a boolean argument. The HUD is hidden while streaming, so toast and level reads fail during a photo.
- Spout shows the farm only while the camera is open; closing the camera while still streaming returns you to the player's own view.
- The Camera tab position changes with the window's aspect ratio; the tool remembers the found location.
- **Clicks for opening the camera are real mouse and keyboard input**, so this part needs VRChat in front, and the tool restores the cursor position afterwards.
- Fishing stops during a photo: "fishing stopped" was 4.3 to 22.9 s over 27 photos (median 18.5 s). From 01:03 to 01:48 eleven photos in a row took 18.6 to 18.9 s; shorter ones (4.3 to 7.5 s) used the quick path (colour check only, no OCR) and some were the repeated attempts of the framing search.
- Eight of the 27 photos found fewer than 8 plots ("8 plots not found" warnings), all before the pose had been calibrated.

## Effect of farm photos on fishing

- The photo is taken in the gap after a catch, so no bite is lost, but the **bite timer of the next cast is not**: after 11 of 20 full scans (55 %) the very first attempt was a `no_bite_reset` (the cast made before the pause had a bite pass while paused, so the confirmation click shows "got away"). The first attempt was a catch in 8, early in 1.
- The next 5 attempts after each of the 20 scans (n = 100): caught 48, early 24, `no_bite_reset` 16, cast failed 5, missed bite 3, unknown 2, escaped 2. In comparison, P4's steady period caught 79.6 % of counted attempts, so a photo costs about the first 1 to 3 attempts.
- **View drift.** The worst case was the photo at 02:03:49 (7.5 s of stopped fishing). Casting then showed the aim notice ("throw toward the pond water") 26 times between 02:03:59 and 02:09:02, roughly every 11.8 s (the tool waits 10 s after repeated cast failures), with 2 rod-tip failures mixed in. A catch followed at 02:09:20 at a different screen position (bobber at y about 118 to 133, previously 293), so the view had changed and was restored by hand. After the other 19 full scans the pond view was intact. The photo sequence can therefore leave the camera or view state changed; verify the pond is in view before casting after any camera sequence.

## Practical notes for farm automation

1. Use a top-down camera with the framing search, then the plot-finding by dark frame lines is reliable (8 of 8 plots in all 20 scans taken after calibration).
2. Distinguish unplanted from planted by green pixels and dry from watered by a dark-soil share. Both thresholds (0.54, 0.002) were tuned in this world and its lighting.
3. Do not stream while fishing; the HUD disappears.
4. Keep the photo infrequent: the default is every 600 s (observed spacing was 1.6 to 10 minutes because of manual shots from the dashboard, 5 logged, and calibrations).
5. Plan for an avatar or a shadow covering cells (`?`; 0 to 3 of 96 cells in the history above) and use several photos before acting on a cell.
