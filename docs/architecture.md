# Architecture

This document describes how the tool is put together: the processes, the modules, the fishing state machine, the vision and OCR pipelines, and the files it writes. Source paths are relative to `src/Pocofarm/`.

## Processes

The tool is a single executable (`pocofarm.exe`, .NET 10, Windows-only). It runs in one of two shapes:

| Shape | Command | What it does |
|---|---|---|
| Supervisor | `pocofarm all` | Starts `pocofarm run` as a child process, restarts it after `restart_delay` seconds if it exits, copies its stderr to `data/crash.log`, and performs executable swaps (see [Operations](operations.md#auto-update)). |
| Worker | `pocofarm run` | The fishing loop and the dashboard web server in one process. Can also be started directly. |

The worker runs the Kestrel dashboard on its own threads and the fishing loop (`Fishing/Fisher.cs`) on the main thread. OCR, HUD reading, live-image writing and evidence saving each use a single background worker so the loop is never blocked by slow work.

## Modules

| Directory | Responsibility |
|---|---|
| `Program.cs`, `Cli/` | Entry point and sub-commands: `Supervisor` (resident mode), `RunCommand` (worker), `Diagnostics` (`doctor`, `capture-test`, `ocr-image`, `bench-ocr`), `Maintenance` (`cleanup`, `init-config`, `setup`). |
| `Core/` | Parts that do not depend on the screen: `Config*.cs` (settings, split by area), `Layout` (per-resolution regions), `Toast` (classification of OCR text), `Events` (the event log), `Stats`, `Status`, `AtomicFile`, `Paths`, `Frame`. |
| `Vision/` | `BobberDetector` (red stick plus white ball), `DipDetector` (settle and dip decision), `BobberParams`, `ViewMotion` (camera movement detection), `ViewAligner` (view drift measurement for [view recovery](#view-recovery)), `Hud` and `IconFinder`. |
| `Fishing/` | `Fisher` (the state machine, a partial class; `FishState`, `DecoyTracker`) and its collaborators: `AutoTuner`, `AttemptRecorder`, `ToastReader`, `HudMonitor`, `ControlChannel`, `ClickSender`, `AimRecovery`, `Auditor`, `NekoCollector`, `LoopProfiler`, `EvidenceStore`. |
| `Farm/` | Farm photos: `FarmShots` (scheduling and the capture sequence), `SpoutCamera` (opens the in-game camera), `SpoutReceiver` (reads the shared texture), `FarmGrid` (plot analysis), `FarmCalibration` (camera framing), `Osc` (OSC sender). |
| `Platform/` | Windows integration: `WgcCapture` (Windows.Graphics.Capture), `WinApi` (window lookup, foreground handling, key state), `Imaging`, `Notify` (toast notifications), `Firewall`, `SelfUpdate`. |
| `Ocr/` | `WinOcr` (Windows OCR), `PpOcr` (PP-OCRv6 through ONNX Runtime), `HudLocator` (finds the "Lv" label to realign the HUD regions). |
| `Web/` | `DashboardServer` (Kestrel endpoints), `Dashboard` (state building), and `Static/index.html`, embedded into the executable. |

Design rule: thresholds and intervals are `Config` properties with a `[Desc]` description (detectors receive them through records such as `BobberParams`). `Fisher` only owns state transitions; recording, OCR, tuning and control are delegated to the collaborator classes.

## Capture

`Platform/WgcCapture.cs` captures the VRChat client area with Windows.Graphics.Capture, per window. That is why the game can be partly or fully covered by other windows and still be captured, and why monitor changes need no re-enumeration. Frames arrive only when the picture changes; the latest frame is copied to the fishing loop as BGRA. A minimized window produces no frames, so the loop restores it (unless `--dry-run`).

The loop recreates the capture when no frame arrives for `capture_stall_seconds` or when the captured size disagrees with the window for `capture_size_mismatch_seconds`.

### Resolution handling

- Regions are stored as ratios of the reference resolution 1920x1009 (`Core/Layout.cs`). The game's HUD is anchored to the horizontal center plus a height-proportional offset, so `Layout.Resolve` recomputes the toast, level and experience-bar regions for the actual width and height. Edge-anchored exclusion regions are resolved separately.
- Pixel-based thresholds are multiplied by `height / 1009`.
- When the frame height is at least twice `work_height` (default 1080, for example 4K), the frame is shrunk by an integer factor before detection and OCR so that behavior matches lower resolutions and processing stays light.
- OCR input is shrunk when the screen is larger than the reference by more than `ocr_downscale_above`, because text that is too large is misread more often.

## Fishing state machine

`Fishing/Fisher.cs` cycles through five states, published as `state` in `data/status.json`:

| State | Meaning | Leaves when |
|---|---|---|
| `wait_window` | The VRChat window was not found. | The window appears and capture starts, then `cooldown`. |
| `cooldown` | Waiting to cast. | `recast_delay` has elapsed, the tuned reel-to-cast wait has elapsed since the last reel click, and `after_catch_cast_min` has elapsed since the last catch. The cast click is sent and the state becomes `cast_wait`. |
| `cast_wait` | The bobber is flying or settling. | The dip detector reports the bobber settled (`wait_bite`), or the cast fails (`cooldown`). |
| `wait_bite` | The bobber is still; waiting for a dip. | A dip count reaches the required number (reel click, `result_wait`), or the bite-wait timeout expires (confirmation click, `result_wait`). |
| `result_wait` | The reel click was sent; waiting for the result toast. | The toast is finalized (`cooldown`), or the result is unknown and the state is re-checked from the bobber's motion (`cast_wait`). |

```
wait_window -> cooldown -> cast_wait -> wait_bite -> result_wait -> cooldown ...
                    ^           |  \_ cast failed ______________________/
                    |___________|
```

A sixth state, `needs_attention`, is entered from any state when the tool gives up: `cast_fail_stop_count` casts failed in a row, or the progress watchdog reached its last tier. Nothing is cast until you pause and resume (or press "recast" on the dashboard), which clears it. The code is split into `partial class Fisher` files by concern (`Fisher.Capture.cs`, `.Bobber.cs`, `.Decoys.cs`, `.Sightings.cs`, `.Steps.cs`, `.Result.cs`, `.Aim.cs`, `.Watchdog.cs`, `.Manual.cs`, `.Housekeeping.cs`); the state names are the `FishState` enum, serialized in snake_case.

### Robustness

- **Error containment.** An exception in the loop is logged and the loop continues. After `state_error_limit` errors in a row in the same state the attempt, dip detector and bobber memory are dropped, the state becomes `cooldown`, and an `error` event with `note: state_reset` is written. An attempt is detached before it is finalized, so a failing event write cannot record it twice.
- **Progress watchdog** (`Fisher.Watchdog.cs`). It triggers when `stall_miss_streak` non-catch outcomes happen in a row, or no fish is caught for `max(stall_no_catch_minutes, stall_wait_p95_factor x p95 bite wait)`. Each trigger writes a `stall` event and moves one tier up, at least `stall_tier_gap_seconds` apart: tier 1 recreates the capture and resets the dip detector, the remembered decoys, learned landings and the peck adaptation; tier 2 sends one confirmation click; tier 3 notifies and enters `needs_attention`. A catch resets the tiers.
- **Cast failures.** The notification is re-armed at every `cast_fail_alert_count` failures in a row. At `cast_fail_stop_count` casting stops (`needs_attention`).
- **Result re-read.** When the result toast is not read in time, the RapidOCR re-read runs on the OCR worker thread and `result_wait` polls the task (up to 12 seconds). Running it on the same single worker as normal reads also means the ONNX engine is never used from two threads at once.
- **Evidence.** Repeated identical failures save evidence for the first and then every 10th occurrence (the events are still written, with `evidence_skipped`). `unclassified_toast` evidence is capped at 500 files.

### Manual control

`data/control.json` accepts `{"cmd": ..., "arg": ..., "amount": ..., "id": <unique number>}` besides `{"paused": bool}`. Commands: `recast`, `reel_now`, `nudge` (arg `left|right|up|down`, at most 200 px, VRChat is brought to the foreground only for the movement), `dry_run` (`on|off`), `pause_after_result` (pause at the next cooldown, i.e. after a result, never mid-reel) and `pause_until` (arg `HH:mm`: pause at the next safe point and resume automatically at that time). `recast`, `reel_now` and `nudge` are rejected (`command_rejected` event) while paused or in dry-run. After a resume the first click is held until a quick check passes (see `resume_check`); whether the rod is held cannot be read from the screen, so it is not checked. The dashboard sends these through `POST /api/control?cmd=...&arg=...`.

Details worth knowing:

- **Casting.** A cast counts as successful when the bobber moves away from where it first appeared and then settles. If a settled bobber is wider than `rod_tip_bobber_width` it is still hanging at the rod tip, so the click had no effect: the attempt is recorded as `cast_failed` and the tool casts again. A "look at the water" notice from the game also records `cast_failed`. Repeated failures first trigger [view recovery](#view-recovery); if that is off or fails, casting pauses for `cast_fail_pause` seconds and a Windows notification is raised once.
- **Cast lock.** The game ignores casts for a short time after a catch. The wait between the reel click and the next cast is `reel_to_cast_min`, shortened by the auto-tuner and extended after a failed cast (bounded by `reel_to_cast_floor` and `reel_to_cast_max`).
- **Confirmation click.** When no bite arrives within the bite-wait timeout, or when the tool starts with a bobber already visible, it sends a "probe" click. If no toast appears, the click was a cast made with the bobber in the air, which is recorded as `no_bite_reset`.
- **Startup.** With a bobber visible at startup the tool watches it for `probe_watch_seconds` and then decides whether it is in the water or in the air by the confirmation click.
- **Desync recovery.** If the bobber is still at its old position after a reel, the reel had no effect and the tool returns to waiting instead of casting. If the tracked candidate is much worse than another one on screen, it switches to the better candidate and waits for it to settle again. Camera movement or a capture gap also sends the loop back to the settle wait.
- **Learned landing position.** After enough catches the median bobber position is used to prefer candidates near where the player's own bobber usually lands (stored as ratios of the screen so it survives window resizing).

## Vision pipeline

1. **Classification.** `BobberDetector` converts each pixel of the search region (`bobber_roi`, minus `exclude_rois`) to a red or white flag using the HSV thresholds in the settings.
2. **Candidates.** Red regions are grown by flood fill and kept if their area, width, height and aspect ratio fit a bobber stick. A white ball is then searched for below each stick; its size scales with the stick height so bobbers that are close to the camera also match. Candidates are scored.
3. **Decoys.** Bobbers visible before the cast (other players') are remembered and excluded after the cast.
4. **Tracking.** The loop searches near the tracked position and periodically (`bobber_audit_frames`) re-searches the whole region, switching when another candidate scores `bobber_switch_ratio` times higher.
5. **Settling.** `DipDetector` collects positions after a cast. When most frames in a short window lie within `settle_range_px` of their median the bobber is armed, with a base position and width.
6. **Dip.** A dip is a drop of at least `max(dip_min_px, width * dip_ratio)` below the base for `dip_frames` consecutive frames (one frame at low frame rates), or the bobber being lost for `lost_seconds_as_dip`. The base position follows normal bobbing slowly. Objects far to the side of the settled position are ignored.
7. **View motion.** `ViewMotion` detects camera movement with block matching on a shrunken image, so a turning view is not mistaken for a dip.

## View recovery

When the view drifts off the pond every cast fails. `Vision/ViewAligner.cs` and `Fishing/AimRecovery.cs` correct this automatically; `Fisher` calls them from the `cooldown` state.

- **Reference.** On a catch with no recent failures (`AimRecovery.NoteCatch`) the frame is shrunk to a 128x72 grey image and kept as the reference. It is written to `data/aim_reference.png` (at most once a minute) and loaded at start, so it survives restarts.
- **Measuring.** `ViewAligner.Estimate` takes a template from the reference's upper-left area (x 0.08-0.45, y 0.15-0.60), which avoids the diagonal rod, the hand, the HUD and the toast zone, and searches it over the whole current image with normalized cross-correlation (a template may hang off the edge as long as 60% of it remains). The result is the shift as a fraction of width and height plus the match score, interpolated below one pixel. It returns null when the score is below `aim_recover_min_confidence`, the template is flat, or a second place matches almost as well (repeating texture), i.e. the scene is not recognized.
- **Closed loop.** After `cast_fail_alert_count` consecutive failures (`cast_failed` from a toast, a bobber at the rod tip, or a timeout), and before the notification, `AimRecovery.Recover` runs at most `aim_recover_max_steps` times: measure the shift, send a relative mouse movement `dx = gain * sx`, `dy = gain * sy` (positive dx turns the view right so content moves left; positive dy turns it down so content moves up), wait `aim_recover_settle`, capture again and re-measure. The gains (counts per unit shift) start at `aim_recover_gain_init` and are corrected after every step from the fraction of the intended shift that was actually achieved. If a move loses the scene it is undone and the gains are halved; if the mouse has no effect twice, or the response has the wrong sign, the attempt stops. The view is aligned when both shifts are below `aim_recover_tolerance`; the failure counter is then reset and casting resumes. Otherwise the existing path (notification and back-off) continues. Each step is logged (`[aim]`) and the outcome is written as an `aim_recover` event.
- **Focus.** Relative mouse input only reaches the foreground window, so `Fisher.TryAimRecover` brings VRChat to the foreground for the correction and then restores the previous window and the cursor position. It is skipped with `--dry-run`, while paused (also checked between steps), and when `aim_recover` is off. Clicks and photos are unaffected; they keep using OSC.

Limits: it fixes rotation only. If the player's position changed, or the view turned so far that the reference scene is no longer visible, it stops with `NoMatch` and the tool falls back to the notification. Large changes of the scene (other players walking through the template area, lighting) lower the score and can also cause `NoMatch`. The initial gain is a guess for the default VRChat mouse sensitivity, so the first step may under- or overshoot.

## OCR and result classification

- **Region.** `ToastReader` crops the toast region (`toast_roi`, resolved per resolution) every `ocr_interval` seconds and hands it to a background worker. Recent crops are kept in a ring (`toast_crop_history`).
- **Engines.** Windows OCR (Japanese) reads every crop. Its text is classified by `Core/Toast.cs` into `caught`, `early`, `escaped`, `aim` (the "look at the water" notice), `levelup` or `achieve`; for catches the fish name, experience and owned count are extracted. If the text is long enough to be a toast but cannot be classified, or a catch lacks the fish name, experience or count, the same crop is re-read with PP-OCR (`rapid_ocr`) and the higher-scoring reading wins. PP-OCR is slower (a fraction of a second per read) but misreads less.
- **Fish names** are normalized against `known_fish`, with a fallback that uses the experience value when the name is garbled.
- **Recovery.** If no toast is read after a reel, the saved crops since the click are re-read with PP-OCR. If that fails too, the attempt is recorded as `unknown` with evidence. Level-up and achievement toasts are not results; they extend the wait (`info_wait_seconds`) and, if no catch toast ever appears, show that experience was gained.
- **Unlinked toasts.** Toasts that do not belong to an open attempt are recorded as `toast_unlinked`, unclassifiable text as `toast_unclassified`, and known noise as `toast_other`.
- **Cat notices.** The in-game cat's messages are collected separately from catches and recorded as `neko` events with evidence.
- **Engine comparison** (`pocofarm bench-ocr`) runs both engines over the fixture images in `tests/fixtures/ocr` and reports classification accuracy, completeness of fish/experience/count, and average time.

PP-OCR runs the PP-OCRv6 detection and recognition models on ONNX Runtime. Both models are embedded in the executable (`Models/*.onnx`) and only horizontal text is handled.

## HUD reading

`HudMonitor` reads the level with OCR and the experience bar by color every `hud_interval` seconds. If the level cannot be read `hud_miss_before_locate` times in a row it looks for the "Lv" label in the lower part of the screen, recomputes the level and experience regions from the label's position and size, and stores them per screen size in `data/calibration.json`. A `hud_calibrated` event records each correction.

## Input: OSC

Clicks are the OSC input `/input/UseRight` sent over UDP to `127.0.0.1:osc_port` (default 9000), held down for `osc_hold_ms` milliseconds. Because OSC is independent of window focus, VRChat can be in the background. The same sender is used for the camera messages (`/usercamera/Pose`, `/usercamera/Zoom`, `/usercamera/Streaming`, `/usercamera/Mode`) of the farm photo sequence.

## Auto-tuning

`Fishing/AutoTuner.cs` adapts three things from measured outcomes:

1. **Reel-to-cast wait.** Starting at `reel_to_cast_min`, it shortens the wait by `reel_to_cast_step` after every `tune_success_streak` consecutive successful casts, down to `reel_to_cast_floor`. A cast that fails because the bobber stayed at the rod tip raises the wait to the failing interval plus `tune_margin` (up to `reel_to_cast_max`) and remembers that value as a floor. State is persisted in `data/tuning.json`. Disable with `tune_reel_to_cast`.
2. **Dips before reeling.** `early_streak_to_adapt` consecutive early reels raise the number by one (up to `max_pecks_to_reel`); `caught_streak_to_revert` consecutive catches lower it again to the baseline `pecks_to_reel`.
3. **Bite-wait timeout.** Once `bite_wait_min_samples` waits have been measured, the timeout becomes the longest recent wait times `no_bite_factor`, clamped between `no_bite_min` and `no_bite_timeout`.

## Recording and the audit

`AttemptRecorder` turns every attempt into exactly one `attempt` event with an `outcome`, saves evidence images, writes the log line, and updates the status. Every 30 seconds (`audit_interval`) `Auditor` compares the number of reel and confirmation clicks with the number of finalized attempts; two mismatches in a row produce an `audit_mismatch` event and a dashboard warning.

### Outcome types

| Outcome | Meaning | Counts as failure |
|---|---|---|
| `caught` | A fish was caught (name, experience and owned count recorded). | No |
| `early` | The game said the fish had not bitten yet: reeled too early. | Yes |
| `escaped` | The fish got away around the time of the reel. | Yes |
| `missed_bite` | A "got away" message appeared before any reel: a dip was not detected (the recent bobber trace is attached). | Yes |
| `unknown` | The reel was sent but no result toast could be read, even on re-read. | Yes |
| `cast_failed` | The cast did not work (look-at-the-water notice, the bobber never settled, or it stayed at the rod tip). | Yes |
| `no_bite_reset` | A long wait ended with a confirmation click. A state check rather than a fishing result. | No (excluded from the success-rate denominator) |

### Other event types

`session_start`, `pause`, `resume`, `stall`, `needs_attention`, `attention_cleared`, `lost_dip`, `command`, `command_rejected`, `dry_run`, `pause_scheduled`, `resume_check`, `hud`, `hud_calibrated`, `slow_cycle` (an attempt took longer than `slow_cycle_seconds`), `gap` (the loop stalled), `desync` (state mismatch such as an ineffective reel), `audit_mismatch`, `error`, `neko`, `toast_info`, `toast_unlinked`, `toast_unclassified`, `toast_other`, `farm_scan` and `farm_calibration`. Every line of `data/events.jsonl` has `ts` (ISO 8601 with offset) and `type`.

## Farm photos

When `farm_shot_interval` is greater than 0 and VRChat is in the foreground (`farm_shot_foreground_only`), `Farm/FarmShots.cs`:

1. waits for a gap right after a catch (state `cooldown`), pauses fishing, and opens the in-game camera (`Esc`, the camera tab, the Stream Camera button, each step verified on screen; it aborts without clicking if the screen is unexpected);
2. restores the camera pose from `data/camera_pose.json`, switches on OSC `Streaming`, and reads the Spout shared texture itself (`SpoutReceiver`);
3. on the first photo after startup (and when requested), calibrates the camera pose so that all eight plots fit with margin (`FarmCalibration`), saving the new pose and a `.bak` of the old one;
4. saves a JPEG into `data/farm/` and a JSON file with the plot analysis next to it, writes a `farm_scan` event, keeps the newest `farm_keep_shots` photos;
5. stops the stream, closes the camera, and resumes fishing.

`FarmGrid` finds each plot's border as a darker line, derives the 3x4 cell grid, and classifies each cell: `e` not planted and dry, `p` planted and dry, `b` not planted and watered, `w` planted and watered, `?` hidden by an avatar or shadow. OSC `/usercamera/*` messages only take effect while the camera is open, and the HUD disappears while streaming, so streaming runs only during the photo.

## Data files

Everything the tool writes lives under the root directory (`--root`).

| Path | Content |
|---|---|
| `config.json` | Settings (optional; missing keys use defaults). |
| `data/events.jsonl` | One JSON event per line. `type=attempt` always carries an `outcome`. |
| `data/fishing.log` | Human-readable log. Rotated at about 5 MB; five older files (`fishing.log.1` to `.5`) are kept. |
| `data/status.json`, `data/live.jpg` | Current state and live image for the dashboard. |
| `data/evidence/` | Evidence images (JPEG plus a same-named JSON file). Deleted after `evidence_ttl_hours` or beyond `evidence_max_mb`, oldest first. |
| `data/tuning.json` | Persisted auto-tuning state. |
| `data/calibration.json` | HUD regions realigned for a screen size. |
| `data/camera_pose.json` | Camera pose for farm photos (6 numbers, or 7 with the zoom added by calibration). |
| `data/farm/` | Farm photos and their analysis files. |
| `data/control.json`, `data/reset.json`, `data/neko_ack.json` | Requests from the dashboard to the worker (pause state, data reset token, cat-notice acknowledgement). |
| `data/archive/` | Event logs moved aside by a data reset. |
| `data/crash.log` | stderr of a crashed worker, written by the supervisor. |

Files that other processes read while they are written (status, live image, control files) are replaced atomically (`Core/AtomicFile.cs`).
