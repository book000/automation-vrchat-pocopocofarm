# Troubleshooting

Start with the built-in check. With VRChat running (and the bobber in the water if possible):

```powershell
.\pocofarm.exe doctor
```

It reports the OS version, the window and capture rate, the resolution, whether the level and experience regions can be read, the bobber detection, and the firewall rule. Each line is `OK`, `NG` (a problem), or `--` (information). It never clicks. `--image <png>` runs the same checks on a saved screenshot.

The worker also runs these checks once at start; see the self-check card on the dashboard or `/api/selfcheck`. A failed check does not pause the worker. To send a bug report, attach the diagnostics bundle: `.\pocofarm.exe diag` (or the dashboard's diagnostics button, `/api/diag.zip`). See [operations](operations.md#diagnostics-bundle) for what it contains.

A `config.json` that is malformed or has out-of-range values does not stop the worker: bad values are replaced by defaults with a warning in `data/fishing.log`, and a broken file falls back to `data/config.lastgood.json`. If a recent settings change made things worse, use "undo" on the dashboard's settings tools card, or apply the `safe` preset.

Logs: `data/fishing.log` (readable), `data/events.jsonl` (structured), `data/crash.log` (worker crashes). The dashboard shows the log tail and the attention events.

## Nothing happens

| Symptom | Likely cause and fix |
|---|---|
| Log says the VRChat window was not found; state is `wait_window`. | The window title does not match `window_title` (default `VRChat`). Start VRChat in desktop mode. |
| State stays `cooldown` and the message says clicks are on hold. | The window handle was lost. The tool retries after `click_hold_seconds`; check that VRChat is running. |
| The tool is paused. | Press Pause/Break, or use the dashboard button. After a crash restart the worker deliberately starts paused if the previous one was paused. |
| `doctor` reports a low capture frame rate. | VRChat is minimized or hidden. Restore it; the tool needs frames. |
| Clicks are sent but the game does not react. | OSC is not enabled in VRChat (Action Menu -> Options -> OSC -> Enabled), or `osc_port` differs from VRChat's port (default 9000). |

## Casts fail

The game shows a notice asking you to throw towards the pond water when the view is not on the pond. The tool records `cast_failed`, notifies you after `cast_fail_alert_count` failures in a row, and pauses casting for `cast_fail_pause` seconds.

- Face the pond and hold the rod before starting.
- The tool tries to fix a drifted view itself (`aim_recover`). After `cast_fail_alert_count` failures in a row it compares the screen with the picture saved at the last catch (`data/aim_reference.png`) and turns the view back with the mouse, then resumes casting. The notification is sent only if this does not work. Each attempt is logged with `[aim]` lines and an `aim_recover` event whose `result` is one of:
  - `Aligned`: the view was restored.
  - `NoReference`: no catch has been recorded yet, so there is nothing to align to. Catch once with the view on the pond.
  - `NoMatch`: the reference scene is not on the screen. The view turned too far or you moved to another place; the correction only fixes rotation, not a changed position. Move back and face the pond yourself.
  - `GaveUp`: still not aligned after `aim_recover_max_steps` corrections.
  - `InputFailed`: the mouse had no effect or moved the wrong way. VRChat must be able to take the foreground and not have a menu open, and the Windows cursor must be free to move.
  - `Aborted`: the bot was paused during the correction.
- During a correction (a few seconds) VRChat takes the foreground and the mouse moves; the previous window and cursor position are restored afterwards. Set `aim_recover` to `false` if this gets in the way of what you do on the PC.
- Another cause is the bobber staying at the rod tip: the cast click arrived while the game still ignored casts. The auto-tuner raises the reel-to-cast wait on its own after such failures. On slow machines raise `reel_to_cast_min` or `reel_to_cast_max`.

## The state is `needs_attention`

The tool stopped casting on purpose: either `cast_fail_stop_count` casts failed in a row, or the progress watchdog could not recover (`stall` events in `data/events.jsonl` show the tiers it tried). Check that the view is on the pond, the rod is held and nothing covers the screen, then pause and resume from the dashboard (or press "recast") to clear it. A notification is raised when this state starts.

Early pulls caused by the bobber merely disappearing are logged as `lost_dip` events with the recent bobber trace. If they show a flickering detection, `lost_dip_guard` can reject those dips (off by default, not proven on recorded traces).

## The bobber is not detected

- Run `pocofarm capture-test out.png` and open `out.png`; it prints the detection result and its time. The live image on the dashboard also draws the detected bobber.
- Make sure the bobber is not behind a UI element. `bobber_roi` defines the search area and `exclude_rois` removes fixed HUD areas.
- The screen height must be at least about 700 px; below that the bobber is too small (`doctor` reports this).
- Unusual graphics settings or shaders can change the bobber's colors. The thresholds `bobber_red_*` and `bobber_white_*` can be adjusted in the settings.

## Results are wrong or unknown

| Outcome | Meaning and what to check |
|---|---|
| `unknown` | The reel was sent but no result toast could be read. Open the evidence image: is the toast inside `toast_roi`? Is the Windows Japanese OCR language pack installed? Do other overlays cover the toast area? |
| `early` repeatedly | The tool reeled before the fish bit. It raises the number of dips required on its own (up to `max_pecks_to_reel`). If it keeps happening, the bobber may be jittering because of a moving view or another player's bobber nearby. |
| `missed_bite` | A "got away" notice appeared before any reel: the dip was not detected. The record includes the recent bobber trace. Lower `dip_ratio` or `dip_min_px`, or check capture frame rate. |
| `escaped` | The fish got away around the reel; usually a late reel. |
| `cast_failed` | See "Casts fail". |

If the OCR language pack is missing, Windows OCR cannot be created and the log shows an error saying the Japanese language pack is missing. Install the Japanese language (including the OCR component) in Windows settings.

If the program crashes at startup in the PP-OCR engine (ONNX Runtime), set `rapid_ocr` to `false` in `config.json`; Windows OCR is then the only engine and results with missing fish name or experience are no longer re-checked.

## Level and experience are not shown

The HUD regions depend on the window size. If the level cannot be read three times in a row, the tool searches for the "Lv" label and realigns the regions (a `hud_calibrated` event). `doctor` shows whether the label is found. If the HUD is hidden or very small, nothing can be read. Delete `data/calibration.json` to discard a bad realignment.

## Dashboard problems

| Symptom | Fix |
|---|---|
| Cannot open `http://<ip>:10000/` from another device. | Run `pocofarm setup` once (UAC). Check the PC's network profile is Private. A block rule created by cancelling Windows' first "Allow access?" prompt overrides allow rules; `setup` removes it. |
| The dashboard does not start. | The port may be taken: another program uses `dashboard_port`. Change it in `config.json` or pass `--port`. |
| Response 403 or 421. | The client address is not private, or the `Host` header is not an IP, `localhost`, or the machine name. |
| The page says the tool is stopped. | The worker has not updated `data/status.json` for `heartbeat_timeout` seconds. Look at `data/crash.log` and `data/fishing.log`; under `pocofarm all` the worker is restarted automatically. |
| A setting change does not apply. | The setting is marked "Restart" in the [reference](configuration.md); the worker must restart. |

## Farm photos

Photos are taken only when `farm_shot_interval` is greater than 0, the tool is not paused, VRChat is in the foreground (unless `farm_shot_foreground_only` is false), and a gap right after a catch occurs within `farm_shot_wait_seconds`. The sequence aborts without clicking if the screens it expects are not shown. Check the `error` field of `/api/farm`, the log, and that OSC is enabled. Failed attempts save a debug screenshot into `data/`.

## Updates do not apply

- The new file must be named exactly `pocofarm.new.exe` and sit in the same directory as the running `pocofarm.exe`.
- It must be at least 10 MB.
- With `update_source`, the source file must have a later modification time than the running executable, and `pocofarm.new.exe` must not already exist.
- A `pocofarm.new.exe.failed` file means the rename failed; check that the directory is writable.

## Performance

The worker raises its own priority slightly so detection keeps up when VRChat uses the CPU. At 4K the tool processes a frame shrunk by an integer factor (`work_height`). If frames arrive slowly, lower `capture_fps` (restart needed) or the VRChat graphics load. The worker publishes the loop's time per section in the `perf` entry of `data/status.json` every `perf_interval` seconds.
