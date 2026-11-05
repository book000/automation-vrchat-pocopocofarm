# Configuration reference

Settings live in `config.json` in the root directory (`--root`). The file is optional: missing keys use the defaults below and unknown keys are ignored. `pocofarm init-config` writes a file with every default.

```json
{
  "farm_shot_interval": 900,
  "pecks_to_reel": 1,
  "level_goal": 30
}
```

Most settings can also be viewed and changed from the dashboard's settings card or through [`/api/config`](operations.md#dashboard-http-api). Changes are validated as a whole and saved to `config.json`. A setting marked **Restart** takes effect only after the fishing worker restarts; all others apply immediately. `update_source` can only be edited in the file.

Validation rules: numbers must be finite and within the range of the setting (counts, sizes, ports and anything used as a divisor are at least 1), arrays must have the same length as the default, ROI rectangles are `[left, top, right, bottom]` ratios in 0 to 1 with left < right and top < bottom, `dashboard_host` must be an IP address, and unknown keys are rejected. Relations are checked too: `reel_to_cast_floor <= reel_to_cast_min <= reel_to_cast_max`, `no_bite_min <= no_bite_timeout`, `pecks_to_reel <= max_pecks_to_reel`, `bite_wait_min_samples <= bite_wait_samples`, and `bobber_min_height <= bobber_max_height`. A change that breaks a rule is refused with a message and nothing is applied.

When the worker loads `config.json`, a value of the wrong type or outside its range, or a key in a broken relation, is logged as a warning and replaced by the default (for a relation, all keys involved). A file that is not valid JSON never stops the worker: it falls back to the last file that loaded cleanly (`data/config.lastgood.json`), or to the defaults if there is none, and logs a warning.

### Presets, section reset and undo

The dashboard's "settings tools" card (and the `/api/config/*` endpoints in [operations](operations.md#dashboard-http-api)) offer:

| Preset | Changes | Use when |
|---|---|---|
| `safe` | `tune_reel_to_cast=false`, `reel_to_cast_min=3.0`, `reel_to_cast_max=8.0`, `recast_delay=2.5`, `osc_hold_ms=80`, `farm_shot_interval=0`, `aim_recover=false` | Casts are ignored or the machine is unstable; you want slow, predictable behavior with no mouse or farm camera actions. |
| `fast` | `reel_to_cast_floor=1.0`, `reel_to_cast_min=1.6`, `recast_delay=1.2`, `ocr_interval=0.08`, `no_bite_min=15`, `no_bite_timeout=60` | The frame rate is stable and you want more attempts per hour. Revert if casts start to be ignored. |
| `low-gpu` | `capture_fps=30`, `ocr_interval=0.25`, `live_interval=2.0`, `rapid_ocr=false`, `work_height=720`, `hud_interval=20`, `farm_shot_interval=1800` | The GPU or CPU is shared with VRChat and the PC struggles. Detection is coarser; `capture_fps` needs a restart. |

- **Reset a section** returns every setting of one section (`fishing`, `vision`, `toast`, `farm`, `aim`, `system`; named after the `Config.*.cs` file) to its default. `update_source` is never touched.
- **Differs from default.** The card lists every setting whose value is not the default, with the default next to it.
- **Undo.** Every change made through the dashboard (settings save, preset, section reset) records the previous values of the keys that changed in `data/config.history.jsonl` (the newest 20 entries). Undo restores the newest entry and removes it. Hand edits to `config.json` are not recorded.

Conventions:

- Times are in seconds unless the key ends in `_ms` (milliseconds) or `_hours`.
- Regions (`*_roi`) are `[left, top, right, bottom]` and positions are `[x, y]`, as ratios of the screen (0 to 1) at the reference resolution 1920x1009. They are re-resolved for the real window size, so they stay valid when the resolution or aspect ratio changes.
- Pixel values described as "at reference resolution" scale with the screen height.
- Defaults are shown as written in `src/Pocofarm/Core/Config*.cs`.

## Fishing sequence and auto-tuning

Casting, waiting, reeling, recasting, and the automatic adjustment of those timings.

| Key | Type | Default | Restart | Description |
|---|---|---|---|---|
| `pecks_to_reel` | integer | `1` |  | Which dip triggers the reel. This is the baseline; it rises automatically after repeated early reels and returns to it after a run of catches. |
| `cast_timeout` | number | `8.0` |  | Maximum wait (seconds) after casting for the bobber to settle. |
| `cast_settle_fallback` | number | `3.0` |  | Seconds after a cast at which a motionless bobber is treated as landed, even if its movement was never observed. |
| `cast_lost_seconds` | number | `4.0` |  | Seconds without seeing the bobber after a cast before the cast counts as failed. |
| `cast_fail_pause` | number | `10.0` |  | Seconds to hold off casting after repeated cast failures. |
| `cast_fail_alert_count` | integer | `3` |  | Number of consecutive cast failures before the user is notified. |
| `reel_ineffective_seconds` | number | `2.0` |  | Seconds after reeling before a bobber that has not moved from its old position is judged to be unaffected by the reel. |
| `probe_watch_seconds` | number | `2.0` |  | Seconds to watch an existing bobber at startup before sending a confirmation click. |
| `click_hold_seconds` | number | `1.0` |  | Seconds to wait before trying again after a click was held back (for example, no game window). |
| `result_timeout` | number | `4.0` |  | Maximum wait (seconds) for the result toast after reeling. |
| `probe_result_timeout` | number | `2.2` |  | Maximum wait (seconds) for the result after a confirmation click. |
| `no_bite_timeout` | number | `75.0` |  | Upper limit (seconds) for waiting for a bite before sending a confirmation click. Shortened automatically from measured waits. |
| `no_bite_min` | number | `20.0` |  | Lower limit (seconds) for the bite-wait timeout. |
| `no_bite_factor` | number | `2.5` |  | Bite-wait timeout = longest recent measured wait multiplied by this factor. |
| `bite_wait_samples` | integer | `300` |  | Number of measured bite waits to remember. |
| `bite_wait_min_samples` | integer | `100` |  | Number of measured waits required before the bite-wait timeout starts to shrink. |
| `unverified_wait` | number | `25.0` |  | Maximum bobber wait (seconds) when the cast could not be verified. |
| `resume_wait` | number | `6.0` |  | Maximum bobber wait (seconds) right after resuming. |
| `slow_cycle_seconds` | number | `30.0` |  | Attempt intervals longer than this (seconds) are recorded as `slow_cycle` events. |
| `recast_delay` | number | `1.6` |  | Delay (seconds) before casting again. |
| `after_catch_cast_min` | number | `0.0` |  | Minimum seconds to wait after a catch before casting. For setups where casts are ignored during the catch animation. 0 disables it. |
| `reel_to_cast_min` | number | `2.0` |  | Minimum seconds from the reel click to the next cast (the game's cast lock plus a margin). With auto-tuning enabled it is the starting value and upper bound, which is then shortened. |
| `reel_to_cast_max` | number | `6.0` |  | Auto-tuning: the longest the reel-to-cast wait may be extended to after a failed cast (for slow-rendering setups where the cast lock lasts longer). |
| `early_streak_to_adapt` | integer | `2` |  | Number of consecutive early reels before the number of dips required is increased. |
| `max_pecks_to_reel` | integer | `3` |  | Upper limit for the number of dips required. |
| `caught_streak_to_revert` | integer | `10` |  | Number of consecutive catches before the number of dips required is lowered by one. |
| `tune_reel_to_cast` | boolean | `true` |  | Shorten the reel-to-cast wait automatically as long as casts keep succeeding. |
| `tune_dt_tolerance` | number | `0.3` |  | Auto-tuning: a measured interval within this many seconds of the current setting counts as a success (allowance for loop delay). |
| `resume_escape_grace` | number | `6.0` |  | A "got away" message appearing within this many seconds of resuming is treated as a bite that passed while paused and is not counted as a missed bite. |
| `reel_to_cast_floor` | number | `1.2` |  | Auto-tuning: never shorten the wait below this (seconds). |
| `reel_to_cast_step` | number | `0.05` |  | Auto-tuning: amount (seconds) to shorten the wait per step. |
| `tune_success_streak` | integer | `30` |  | Auto-tuning: number of consecutive successful casts per shortening step. |
| `tune_margin` | number | `0.1` |  | Auto-tuning: when a cast is ignored, the wait is raised to the failing interval plus this margin (seconds). |
| `osc_hold_ms` | integer | `50` |  | How long (milliseconds) the OSC Use input is held down for one click. |
| `gap_threshold` | number | `0.7` |  | Processing pauses longer than this (seconds) are treated as a gap. |
| `capture_stall_seconds` | number | `3.0` |  | Seconds without a new frame before the capture is recreated. |
| `capture_size_mismatch_seconds` | number | `2.0` |  | Seconds the captured size may disagree with the window size before the capture is recreated. |
| `startup_frame_wait` | number | `3.0` |  | Seconds to wait for the first frame at startup. |
| `idle_poll_ms` | integer | `100` |  | Polling interval (milliseconds) of the loop while paused or not in the foreground. |
| `no_capture_retry_ms` | integer | `500` |  | Retry interval (milliseconds) while the capture has not started. |
| `capture_reinit_ms` | integer | `300` |  | Wait (milliseconds) before recreating the capture. |
| `control_check_interval` | number | `0.25` |  | How often (seconds) the pause key and the dashboard request file are checked. |
| `pause_vk` | integer | `0x13` |  | Virtual-key code of the pause key (0x13 = Pause/Break). |
| `audit_interval` | number | `30` |  | Interval (seconds) of the click-versus-attempt reconciliation (audit). |
| `stats_interval` | number | `5` |  | Interval (seconds) at which status values such as the success rate are refreshed. |
| `perf_interval` | number | `10` |  | Interval (seconds) at which per-section processing times are published. |
| `watchdog_seconds` | number | `0.6` |  | A warning is logged when the loop stays in one processing section for at least this many seconds. |
| `trace_length` | integer | `120` |  | Number of frames of bobber-position history kept for missed-bite analysis. |
| `attempt_trace_length` | integer | `60` |  | Number of frames of bobber-position history attached to an attempt record (at reel time). |
| `evidence_trace_length` | integer | `90` |  | Number of frames of bobber-position history attached to a failure record. |
| `state_error_limit` | integer | `5` |  | Number of loop errors in the same state before the current attempt is dropped and the tool returns to `cooldown` (an `error` event with `note: state_reset` is written). |
| `cast_fail_stop_count` | integer | `10` |  | Cast failures in a row after which casting stops and the state becomes `needs_attention` (0 never stops). A notification is raised at every `cast_fail_alert_count` failures before that. |
| `stall_watchdog` | boolean | `true` |  | Enable the progress watchdog (staged recovery when nothing is being caught). |
| `stall_miss_streak` | integer | `15` |  | Non-catch outcomes in a row that count as a stall. |
| `stall_no_catch_minutes` | number | `15` |  | Minutes without a catch that count as a stall (lower bound; `stall_wait_p95_factor` times the observed p95 bite wait is used if longer). |
| `stall_wait_p95_factor` | number | `8` |  | Multiplier on the 95th percentile of observed bite waits for the no-catch limit. |
| `stall_tier_gap_seconds` | number | `120` |  | Minimum seconds before the watchdog moves to the next tier. |
| `resume_check` | boolean | `true` |  | Before the first click after a resume, check that the VRChat window exists, is not minimized and the capture is fresh. |
| `resume_require_foreground` | boolean | `false` |  | Also require VRChat to be the foreground window in the resume check (OSC works in the background, so this is normally unnecessary). |
| `resume_frame_max_age` | number | `1` |  | Resume check: clicks are held while the latest captured frame is older than this many seconds. |
| `lost_dip_guard` | boolean | `false` |  | Experimental. Reject a "dip" that is only the bobber disappearing unless it was visible in enough of the frames before it vanished. Off by default because the recorded traces could not show that it helps. |
| `lost_dip_guard_frames` | integer | `30` |  | Lost-dip guard: number of frames before the loss that are examined. |
| `lost_dip_guard_seen` | integer | `20` |  | Lost-dip guard: minimum number of those frames in which the bobber was visible. |

## View recovery

Automatic correction of a view that has drifted off the pond (for example after the mouse was bumped). See [architecture](architecture.md#view-recovery) for how it works and its limits.

| Key | Type | Default | Restart | Description |
|---|---|---|---|---|
| `aim_recover` | boolean | `true` |  | After `cast_fail_alert_count` casts fail in a row, measure the view's drift against the picture of the last good catch and turn the view back with relative mouse movement. VRChat is brought to the foreground only for the correction. Never runs with `--dry-run` or while paused. |
| `aim_recover_max_steps` | integer | `8` |  | Maximum number of mouse corrections per attempt. After that the tool falls back to notifying you. |
| `aim_recover_gain_init` | number | `800.0` |  | Initial mouse movement (counts) that turns the view by one screen width. The tool refines it from the observed response during an attempt (the vertical gain starts at 9/16 of it). |
| `aim_recover_min_confidence` | number | `0.6` |  | Minimum match score (0 to 1) for a measurement to be trusted. A lower score means the reference scene is not on the screen and the attempt stops. |
| `aim_recover_tolerance` | number | `0.02` |  | The view counts as aligned when the remaining drift is below this fraction of the screen width and height. |
| `aim_recover_settle` | number | `0.6` |  | Seconds to wait after a mouse movement before the screen is captured again. |

## Bobber and dip detection

Color thresholds, size limits, and settling rules for finding the bobber and detecting a bite. Pixel values are expressed at the reference resolution (1920x1009) and scale with the screen height.

| Key | Type | Default | Restart | Description |
|---|---|---|---|---|
| `bobber_roi` | number[] | `[0.05, 0.08, 0.90, 0.75]` | yes | Region searched for the bobber, as [left, top, right, bottom] screen ratios. |
| `exclude_rois` | number[][] | `[[0.77, 0.0, 1.0, 0.36], [0.0, 0.0, 0.17, 0.12]]` | yes | Regions excluded from bobber detection (array of screen-ratio rectangles). |
| `bobber_red_min_saturation` | integer | `100` |  | Bobber red: minimum saturation (0-255). |
| `bobber_red_min_value` | integer | `70` |  | Bobber red: minimum brightness (0-255). |
| `bobber_red_hue_max` | integer | `8` |  | Bobber red: upper hue bound (0-179; red wraps around both ends). |
| `bobber_red_hue_min` | integer | `172` |  | Bobber red: lower hue bound (0-179). |
| `bobber_white_sat_limit` | number | `45.5` |  | Bobber white: maximum saturation (0-255). |
| `bobber_white_min_value` | integer | `140` |  | Bobber white: minimum brightness (0-255). |
| `bobber_min_area` | number | `8` |  | Minimum area (px^2, at reference resolution) of the red stick. |
| `bobber_max_width` | number | `28` |  | Maximum width (px, at reference resolution) of the red stick. |
| `bobber_max_height` | number | `60` |  | Maximum height (px, at reference resolution) of the red stick. |
| `bobber_min_height` | number | `5` |  | Minimum height (px, at reference resolution) of the red stick. |
| `bobber_min_aspect` | number | `0.9` |  | Minimum aspect ratio (height / width) of the red stick. |
| `bobber_min_white` | number | `15` |  | Minimum area (px^2, at reference resolution) of the white ball. |
| `bobber_white_reach_min` | number | `22` |  | Minimum distance (px, at reference resolution) below the stick in which the white ball is searched. |
| `bobber_white_reach_pad` | number | `10` |  | Margin (px, at reference resolution) added to the stick height to get the white-ball search distance. |
| `bobber_white_side_pad` | number | `8` |  | Horizontal margin (px, at reference resolution) of the white-ball search area. |
| `bobber_white_top_pad` | number | `3` |  | How far (px, at reference resolution) the white-ball search area overlaps the bottom of the stick. |
| `bobber_white_center_tol` | number | `6` |  | Minimum horizontal offset (px, at reference resolution) allowed between the white ball's centroid and the stick's center (the stick width is used if larger). |
| `dip_ratio` | number | `0.45` | yes | Bobber drop, relative to its width, that counts as a dip. |
| `dip_min_px` | number | `4.0` | yes | Minimum drop (px, at reference resolution) that counts as a dip. |
| `dip_frames` | integer | `2` | yes | Number of consecutive frames that must show a dip. |
| `lost_seconds_as_dip` | number | `0.12` | yes | Seconds without seeing the bobber after which it counts as a dip. |
| `settle_seconds` | number | `0.5` | yes | Seconds the bobber must be still to count as settled. |
| `settle_range_px` | number | `4.0` | yes | Vertical jitter (px, at reference resolution) still treated as motionless. |
| `settle_frames` | number | `3.2` |  | Lower bound of the settle window in frame intervals (keeps the window long enough at low frame rates). |
| `settle_frames_anchored` | number | `2.2` |  | Lower bound of the settle window, in frame intervals, near the most recent settled position. |
| `anchored_settle_scale` | number | `0.6` |  | Near the most recent settled position the settle time is scaled by this factor. |
| `settle_inlier_ratio` | number | `0.8` |  | Fraction of frames in the settle window that must lie near the median. |
| `dip_follow_rate` | number | `0.9` |  | How quickly (per second) the bobber's reference position follows normal bobbing while waiting for a bite. |
| `dip_max_lateral_px` | number | `40` |  | Objects at least this far (px, at reference resolution) sideways from the settled position are ignored as not being the bobber (the larger of this and the width-based limit applies). |
| `dip_max_lateral_widths` | number | `4` |  | Objects at least this many bobber widths sideways from the settled position are ignored as not being the bobber. |
| `cast_moved_px` | number | `40` |  | Distance (px, at reference resolution) the bobber must travel to count as having moved. |
| `bobber_decoy_pad` | number | `30` |  | Margin (px, at reference resolution) of the area excluded around bobber candidates (other players' bobbers) that were visible before the cast. |
| `bobber_audit_frames` | integer | `24` |  | Frame interval at which the whole screen is searched again for a better candidate than the tracked one. |
| `work_height` | integer | `1080` |  | Reference height for downscaling. When the screen height is at least twice this value (for example 4K) the frame is shrunk by an integer factor before detection and OCR, so behavior matches lower resolutions and processing is lighter. |
| `bobber_switch_ratio` | number | `1.5` |  | Switch to a whole-screen candidate when its score exceeds the tracked candidate's by this factor. |
| `bobber_near_px` | number | `15` |  | Distance (px, at reference resolution) within which the bobber counts as still being at a position. |
| `rod_tip_bobber_width` | number | `15` |  | Width (px, at reference resolution) at or above which a bobber is considered to be still hanging at the rod tip. |
| `view_motion_threshold` | number | `1.2` | yes | Amount of screen change that counts as camera movement. |
| `view_motion_hold` | number | `0.7` | yes | Seconds the view is still treated as moving after camera movement. |

## Toasts, OCR and HUD

Reading the result notifications, the level display, and the experience bar.

| Key | Type | Default | Restart | Description |
|---|---|---|---|---|
| `toast_roi` | number[] | `[0.06, 0.28, 0.36, 0.725]` | yes | Region read for result toasts, as [left, top, right, bottom] screen ratios. |
| `level_roi` | number[] | `[0.255, 0.828, 0.325, 0.889]` | yes | Region of the level display, as [left, top, right, bottom] screen ratios. |
| `xp_bar_roi` | number[] | `[0.2615, 0.869, 0.319, 0.879]` | yes | Region of the experience bar, as [left, top, right, bottom] screen ratios. |
| `rapid_ocr` | boolean | `true` |  | Use the PP-OCR engine (ONNX Runtime) for re-checks and recovery. Set to false on machines where ONNX Runtime crashes at startup. |
| `ocr_interval` | number | `0.12` |  | Interval (seconds) between OCR passes over the toast region. |
| `ocr_downscale_above` | number | `1.3` |  | When the screen is larger than the reference by more than this factor, the image passed to OCR is shrunk (text that is too large is misread more often). |
| `toast_crop_history` | integer | `48` |  | Number of toast-region images kept for re-reading when a result is unknown. |
| `toast_new_gap` | number | `2.5` |  | Seconds after which a toast of the same kind is treated as a new appearance. |
| `toast_duplicate_window` | number | `6.0` |  | Seconds within which an identical catch toast is treated as a duplicate. |
| `toast_noise_window` | number | `10.0` |  | Seconds after a finalized result during which misread toasts are ignored. |
| `startup_grace` | number | `4.0` |  | Seconds after startup during which stale toasts are ignored. |
| `result_first_seen_grace` | number | `0.9` |  | Grace period (seconds) between first seeing a result toast and finalizing it. |
| `info_wait_seconds` | number | `6.0` |  | Seconds to wait for level-up or achievement notices that appear after the catch toast. |
| `neko_merge_gap` | number | `4.0` |  | Interval (seconds) within which readings of the cat notification are merged into one. |
| `neko_flush_seconds` | number | `3.0` |  | Seconds after the cat notification disappears before it is recorded as one entry. |
| `level_goal` | integer | `0` |  | Target level for the dashboard's "estimate to target level". 0 hides it. It disappears automatically once reached. |
| `hud_interval` | number | `10.0` |  | Interval (seconds) at which the HUD (level and experience) is read. |
| `hud_locate_interval` | number | `60` |  | Interval (seconds) at which the HUD position is searched for again. |
| `hud_miss_before_locate` | integer | `3` |  | Number of consecutive failed level reads before the HUD position is searched for again. |
| `hud_recalibrate_min_px` | number | `3.0` |  | If the re-detected position is within this many pixels of the current region, it is not treated as drift and no correction is applied. |
| `hud_write_interval` | number | `60` |  | Interval (seconds) at which the HUD values are recorded even if they did not change. |
| `known_fish` | string[] | built-in list of the 13 fish names |  | List of fish names used to normalize OCR results. |
| `fish_coins` | object | built-in table |  | Sale value in coins per fish name. |

## Window, capture, dashboard and updates

The environment the tool runs in.

| Key | Type | Default | Restart | Description |
|---|---|---|---|---|
| `window_title` | string | `"VRChat"` |  | Title of the VRChat window. |
| `window_recheck_interval` | number | `30.0` |  | Interval (seconds) at which the VRChat window is looked up again. |
| `capture_fps` | integer | `60` | yes | Capture frame rate. |
| `live_interval` | number | `1.0` |  | Update interval (seconds) of the live image. |
| `evidence_cleanup_interval` | number | `300` |  | Interval (seconds) between evidence cleanups. |
| `evidence_ttl_hours` | number | `6.0` | yes | Retention time of evidence images (hours). |
| `evidence_max_mb` | integer | `512` | yes | Size limit of the evidence directory (MB). |
| `dashboard_host` | string | `"0.0.0.0"` | yes | Address the dashboard listens on. |
| `dashboard_port` | integer | `10000` | yes | TCP port of the dashboard. |
| `heartbeat_timeout` | number | `10` |  | Seconds without a heartbeat before the dashboard reports the tool as stopped. |
| `restart_delay` | number | `5` | yes | Seconds the supervisor waits before restarting a fishing process that exited. |
| `update_source` | string | (empty) |  | Path (UNC allowed) of a newer executable for auto-update. If it is newer than the running executable, the resident supervisor swaps to it. Edit `config.json` directly; this key cannot be changed through the dashboard. When empty, only a file named `pocofarm.new.exe` triggers a swap. |
| `update_check_seconds` | number | `60` |  | Interval (seconds) at which `update_source` is checked. |
| `archive_keep_days` | integer | 0 |  | Delete `data/archive/events_*.jsonl` older than this many days, at worker start and in `pocofarm cleanup`. 0 keeps them forever. |
| `local_notify` | boolean | `true` |  | Show a Windows toast notification when fishing stops in the `needs_attention` state. At most one per 10 minutes. |
| `exe_cleanup_interval` | number | `3600` |  | Interval (seconds) at which leftover executables from swaps (`*.old` and similar) are removed. 0 disables it. |

## Farm photos

Periodic photos of the farm through the in-game camera, and their analysis. Requires OSC to be enabled in VRChat.

| Key | Type | Default | Restart | Description |
|---|---|---|---|---|
| `farm_shot_interval` | number | `600` |  | Interval (seconds) between farm photos. 0 disables them. A photo requires VRChat in the foreground. |
| `farm_hide_people` | boolean | `true` |  | While a farm photo is taken, remove avatars and in-camera UI from the camera layers so that no people appear in the picture. They are switched back on afterwards. |
| `farm_alert_scans` | integer | `2` |  | Farm alerts: how many consecutive photos must show unplanted or still-dry cells in a plot before an alert is raised. |
| `farm_alert_toast` | boolean | `true` |  | Also show farm alerts as Windows toast notifications (the dashboard badge is always shown). |
| `farm_shot_foreground_only` | boolean | `true` |  | Take farm photos only while VRChat is already in the foreground (while it is behind other windows the tool just waits and never takes focus or moves the cursor). |
| `farm_shot_wait_seconds` | number | `60` |  | Maximum wait (seconds) for a break right after a catch before fishing is paused for a photo. |
| `farm_shot_crop` | number[] | `[0.0, 0.0, 1.0, 1.0]` |  | Crop of the farm photo as [left, top, right, bottom] screen ratios ([0,0,1,1] is the whole frame). |
| `farm_water_min_change` | integer | `40` |  | Farm change detection: minimum difference in a cell's dark-soil percentage for watering to count as changed (avoids counting jitter near the threshold). |
| `farm_calibrate` | boolean | `true` |  | On the first photo after startup, adjust the camera position automatically if the eight plots do not fit. |
| `farm_cal_margin` | number | `0.04` |  | Calibration: margin (screen ratio) left outside the whole farm. |
| `farm_cal_tolerance` | number | `0.03` |  | Calibration: the farm counts as centered when its center is within this distance (screen ratio) of the photo center. |
| `farm_cal_step` | number | `0.5` |  | Calibration: distance the camera moves to measure the offset. |
| `farm_cal_iterations` | integer | `6` |  | Calibration: maximum number of correction iterations. |
| `farm_cal_zoom_step` | number | `5.0` |  | Calibration: step of the field-of-view (zoom) change. |
| `farm_cal_max_shift` | number | `8.0` |  | Calibration: maximum distance the camera may move from its original position. |
| `farm_cal_settle` | number | `0.35` |  | Calibration: wait (seconds) between moving the camera and grabbing an image. |
| `farm_cal_grab_wait` | number | `0.15` |  | Calibration: interval (seconds) between repeated grabs. |
| `farm_cal_stable_tries` | integer | `10` |  | Calibration: maximum number of grabs while waiting for the image to settle. |
| `farm_keep_shots` | integer | `12` |  | Number of most recent farm photos to keep (so the dashboard can compare before and after). |
| `farm_shot_width` | integer | `1280` |  | Width (px) of the saved farm photo. |
| `farm_shot_quality` | integer | `80` |  | JPEG quality of the farm photo (1-100). |
| `farm_grab_wait` | number | `0.4` |  | Farm photo: wait (seconds) between starting the camera stream and grabbing an image. |
| `farm_pause_settle` | number | `0.3` |  | Farm photo: wait (seconds) between pausing fishing and starting the camera operations. |
| `farm_stream_wait_seconds` | number | `6.0` |  | Farm photo: maximum wait (seconds) for the stream sender to appear. |
| `farm_grab_attempts` | integer | `4` |  | Farm photo: maximum number of grab attempts. |
| `spout_sender` | string | (empty) |  | Name of the Spout sender to read. When empty, a VRChat sender is chosen automatically. |
| `osc_port` | integer | `9000` |  | VRChat OSC receive port. |
| `fast_camera_open` | boolean | `true` |  | Open the camera with color checks only instead of OCR (falls back to the OCR path on failure). |
| `camera_tab` | number[] | `[0.499, 0.872]` |  | Position of the camera tab icon as [x, y] screen ratios. |
| `stream_button` | number[] | `[0.469, 0.486]` |  | Position of the Stream Camera button as [x, y] screen ratios. |
| `farm_edge_contrast` | integer | `14` |  | Farm analysis: darkness (luminance difference from the surroundings) at which a pixel counts as a plot border. |
| `farm_empty_br` | number | `0.54` |  | Farm analysis: light-brown soil whose blue/red ratio is at or above this value counts as not yet watered (dark brown watered soil is about 0.48, light brown about 0.6). |
| `farm_plant_ratio` | number | `0.002` |  | Farm analysis: a light-brown cell with at least this fraction of green pixels counts as planted. |
| `farm_inset_x` | number | `0.05` |  | Farm analysis: horizontal fraction by which cell centers are moved inside the plot border. |
| `farm_inset_y` | number | `0.035` |  | Farm analysis: vertical fraction by which cell centers are moved inside the plot border. |
| `farm_cell_window` | number | `0.38` |  | Farm analysis: size of the window in which a cell's color is examined (fraction of the cell). |
| `farm_hidden_ratio` | number | `0.45` |  | Farm analysis: a cell whose window contains more than this fraction of other colors (avatar, shadow) is reported as hidden/unknown. |
| `farm_analysis_width` | integer | `640` |  | Farm analysis: width (px) the image is shrunk to before analysis. |
