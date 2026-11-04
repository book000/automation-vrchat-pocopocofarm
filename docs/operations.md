# Operations

How to run the tool unattended, update it, expose the dashboard, and watch it.

## Running as a resident service

`pocofarm all --root <dir>` is the mode intended for long-term use. A supervisor process starts the fishing worker (`pocofarm run`) as a child, restarts it `restart_delay` seconds (default 5) after it exits, and writes the worker's stderr to `data/crash.log`.

```powershell
.\pocofarm.exe all --root .
```

`scripts/start.bat` starts the published executable minimized, using the repository root as `--root`. To start the tool at logon, run the executable (or a shortcut to `start.bat`) from the Windows Startup folder or a Task Scheduler task that runs at logon in the interactive session. The tool needs a desktop session because it captures a window and shows notifications.

The root directory (`--root`, default: the current directory) holds `config.json` and `data/`. It does not have to be next to the executable.

If the previous worker was paused, the restarted worker starts paused as well (it reads `paused` from `data/status.json`). This avoids clicking on its own after a crash while you were playing manually.

### Pausing

| Method | Notes |
|---|---|
| **Pause/Break** key (`pause_vk`) | Works whichever window is in the foreground. Shows a Windows toast notification. |
| Dashboard pause/resume button | Writes `data/control.json`, which the worker polls every `control_check_interval` seconds. |
| `pocofarm run --paused` | Starts paused. |

While paused no click is sent, the farm photo sequence is skipped, and the dashboard shows the paused state. A "got away" notice within `resume_escape_grace` seconds of resuming is treated as a bite that passed while paused and recorded as `no_bite_reset`, not as a missed bite.

## Auto-update

The supervisor swaps itself for a newer executable without a manual stop.

1. A new executable appears as `pocofarm.new.exe` **in the same directory as the running `pocofarm.exe`**. Files smaller than 10 MB are ignored so a half-copied file is never swapped in.
2. The supervisor stops the worker, renames the running `pocofarm.exe` to `pocofarm.exe.old` (Windows allows renaming a running executable), moves `pocofarm.new.exe` into place, and starts the new executable with the same arguments. The old supervisor then exits.
3. If the swap fails, the new file is kept as `pocofarm.new.exe.failed` and the old executable stays in service.
4. At startup and every `exe_cleanup_interval` seconds (default 3600; 0 disables) the supervisor deletes leftovers: `pocofarm.exe.old`, `pocofarm.exe.*.old`, `pocofarm.new.exe.failed`, and a `pocofarm.new.exe.tmp` older than one hour. Files still in use are retried later.

There are two ways to deliver the new file:

- **Manually.** Copy the new executable next to the running one as `pocofarm.new.exe`. When the copy takes long, copy it as `pocofarm.new.exe.tmp` and rename it when finished, so the supervisor never sees a partial file.
- **Automatically.** Set `update_source` in `config.json` to the path of a newer executable (UNC paths work). Every `update_check_seconds` (default 60) the supervisor compares modification times; if the source is newer than the running executable it copies the file to `pocofarm.new.exe.tmp`, renames it to `pocofarm.new.exe`, and the swap above follows. `update_source` can only be edited in the file, not from the dashboard, because it names an executable to run.

The only layout assumption is that `pocofarm.new.exe` lives in the directory of the running executable (`Environment.ProcessPath`); the repository layout does not matter. The supervisor re-reads `config.json` every second, so changing `update_source` needs no restart.

To publish a build and deploy it to a running instance:

```powershell
dotnet publish src/Pocofarm -c Release -r win-x64 --self-contained -o artifacts/publish
Copy-Item artifacts\publish\pocofarm.exe <install-dir>\pocofarm.new.exe
```

## LAN access and the firewall

By default the dashboard listens on `0.0.0.0:10000` (`dashboard_host`, `dashboard_port`). To open it from another device on the LAN, Windows Firewall needs an inbound rule for the executable:

```powershell
.\pocofarm.exe setup
```

This asks for UAC elevation and creates an inbound allow rule for TCP on the dashboard port, restricted to the Private profile and the local subnet. It also removes a block rule named `pocofarm.exe` for the same program, because a block rule takes precedence over an allow rule and Windows creates one when the "Allow access?" prompt is cancelled. The rule is tied to the executable's path, so it does not need repeating when a rebuilt executable keeps the same path. `scripts/setup_firewall.ps1 -Exe <path-to-exe>` does the same from PowerShell.

At startup the worker logs a warning if no matching allow rule exists. To avoid the firewall prompt entirely on a single machine, bind to loopback: `pocofarm run --host 127.0.0.1` (or set `dashboard_host`).

Access control:

- Every request is accepted only from private addresses (loopback, 10/8, 172.16/12, 192.168/16, 169.254/16, IPv6 link-local and unique-local); others receive 403.
- The `Host` header must be an IP address, `localhost`, or the machine name (DNS-rebinding protection); otherwise 421.
- `POST` requests carrying an `Origin` header from a different origin are rejected with 403 (CSRF protection). Requests without `Origin`, such as `curl`, are accepted.
- There is no authentication. Do not expose the port beyond a trusted network.

## Dashboard HTTP API

All endpoints are served by the worker on the dashboard port. Responses are JSON unless noted. The paths below are the full list.

| Method | Path | Description |
|---|---|---|
| GET | `/` | Dashboard page. |
| GET | `/api/state` | Everything the page shows: status, totals, recent attempts, statistics, log tail. |
| GET | `/api/events?outcome=<outcome>&limit=<n>` | Attempt history (default limit 200), optionally filtered by outcome. |
| GET | `/api/evidence?limit=<n>&q=<text>` | Evidence image list (default limit 48). |
| GET | `/evidence/<file>` | One evidence image or its JSON file. |
| GET | `/live.jpg` | Latest live image with detection overlays. |
| GET | `/api/archives`, `/api/archive?name=<file>` | List archived event logs, and read one. |
| GET | `/api/summary?day=YYYY-MM-DD&days=<n>` | Daily (`days=1`, default) or weekly (`days=7`) summary starting at local midnight of `day` (default today): catches, success rate against the previous period, experience, estimated coins, level change, top fish, rare drops, downtime, last farm scan. Reads archived logs too. Coins are estimated from `fish_coins`, not read from the game. |
| GET | `/api/progress` | Experience needed per level (estimated from observed level-ups), experience per hour, time to the next level (and `level_goal`), and the last 7 days of experience and estimated coins per day and per active hour. |
| GET | `/api/triage?hours=<h>` | Failed attempts grouped by outcome with evidence images, the bobber trace, and advisory suggestions taken from [Troubleshooting](troubleshooting.md). Nothing is changed automatically. |
| GET | `/api/health` | Health verdict (`ok`, `warn`, `bad`, `unknown`) and its causes; the same object is in `/api/state` as `health` and as `status.health`. |
| GET | `/api/farm?limit=<n>` | Farm photo list with analysis, schedule, and camera state. |
| GET | `/api/farm/phase` | Lightweight farm photo progress. |
| GET | `/farm/<file>`, `/farm_live.jpg` | A farm photo; the live view while a photo is being taken. |
| GET | `/api/config` | Every setting with key, description, current value, default, and whether a restart is needed. |
| GET | `/api/selfcheck` | Result of the start-up self-check (also under `selfcheck` in `status.json`). |
| GET | `/api/diag.zip` | Diagnostics bundle (see [Diagnostics bundle](#diagnostics-bundle)). Private networks only. |
| GET | `/api/config/tools` | Presets, sections, settings that differ from the default, and whether undo is available. |
| POST | `/api/config/preset?name=<preset>` | Apply a preset (`safe`, `fast`, `low-gpu`). |
| POST | `/api/config/reset-section?name=<section>` | Restore one section's settings to their defaults. |
| POST | `/api/config/undo` | Undo the newest recorded settings change. |
| POST | `/api/config` | Change settings. Body: a JSON object of `{ "key": value }`; `null` restores the default. Validated as a whole: on any error nothing changes and the response is 400. Saved to `config.json`. The response lists keys that need a restart. `update_source` is refused. |
| POST | `/api/pause?on=1` or `on=0` | Pause or resume (default pause). |
| POST | `/api/farm/shoot` | Take a farm photo now. |
| POST | `/api/farm/calibrate` | Take a farm photo now and calibrate the camera framing. |
| POST | `/api/neko_ack` | Mark cat notices as read. |
| POST | `/api/reset` | Move the current event log to `data/archive/` and count from zero. |

Examples:

```bash
curl http://localhost:10000/api/config
curl -X POST http://localhost:10000/api/config -d '{"farm_shot_interval":900,"pecks_to_reel":null}'
curl -X POST "http://localhost:10000/api/pause?on=1"
```

Settings marked "Restart" in the [reference](configuration.md) are saved immediately but take effect after the fishing worker restarts (under the supervisor, stop the worker process and it is restarted automatically).

## Start-up self-check

When the worker starts it runs the non-invasive checks of `pocofarm doctor` once, in quick mode (no frame-rate measurement, never a click, at most about 4 seconds of waiting; a slower check finishes in the background). The result is written to `data/selfcheck.json`, published under `selfcheck` in `status.json`, served at `/api/selfcheck`, and shown in the dashboard's self-check card. Failures are logged as warnings and the card says "needs attention" (window not found, resolution under 700 px, HUD level unreadable, missing Japanese OCR language pack, no capture frames, missing firewall rule). The worker is **not** paused. OSC reachability cannot be detected and is not checked. If VRChat was not running yet, restart the worker (or run `doctor`) to check again.

## Diagnostics bundle

`GET /api/diag.zip` from the dashboard, or `pocofarm diag [--root <dir>] [--out <zip>]` when the dashboard is down (default output: `pocofarm-diag-<timestamp>.zip` in the root), writes a zip with:

- `fishing.log.tail.txt` and `crash.log.tail.txt` (last 500 lines each)
- `status.json` and `config.redacted.json` (`update_source` and every key whose name contains `url`, `token`, `secret` or `password` removed, at any depth)
- `selfcheck.json` (the latest start-up self-check)
- `evidence/` (the 10 newest failure images with their JSON, at most about 20 MB)
- `system.txt` (exe version, OS, .NET, CPU count, GPU names from the registry) and `README.txt` (notes about anything that could not be read)

Check the bundle before sharing it: logs can contain window titles and file paths.

## Monitoring

- **Dashboard.** The header shows running, paused, or stopped. The page marks the tool as stopped when `data/status.json` has had no heartbeat for `heartbeat_timeout` seconds (default 10).
- **Attention events.** Unlinked or unclassified toasts, errors, gaps, slow cycles, and desynchronization appear in the attention list.
- **Health badge.** Next to the state badge the dashboard shows a health verdict computed from the last 30 attempts compared with the last 300: reaction time p95, share of `unknown`, share of `missed_bite`, share of attempts without an OCR engine (a stand-in for OCR confidence, which is not recorded per attempt), success rate, and the capture FPS. FPS samples are taken each time the dashboard reads `status.json`, so they exist only while a dashboard page is open. Relative thresholds (for example reaction p95 above twice the baseline and above 40 ms) catch sudden changes; absolute floors (missed bite 30% or more, unknown 12% or more, success 50% or less, reaction p95 60 ms or more, FPS below 15) catch long bad periods whose baseline is bad too. Fewer than 20 recent attempts gives `unknown`. The thresholds are constants in `Core/Health.cs`, tuned on recorded data (a good evening and several bad hours). The verdict is computed in the dashboard layer and is added to the `status` object served by `/api/state`; `data/status.json` itself is written only by the fishing loop.
- **Event markers.** The trend charts (failures per 10 minutes, hourly success rate, level) show dashed vertical lines for configuration changes, pause/resume, restarts and data resets, executable swaps (`self_update`), auto-tuner changes (`tune`), HUD and farm calibration, view recovery, and farm alerts. Hover a line for the time and details.
- **Summary, progress, failure triage.** Cards on the dashboard backed by `/api/summary`, `/api/progress` and `/api/triage`.
- **Farm alerts.** After each farm photo the plot states are compared with the previous scans. A plot with unplanted cells, or planted cells that are still dry, for `farm_alert_scans` consecutive scans (default 2) raises an alert: a `farm_alert` event, a dashboard badge, and a Windows toast (`farm_alert_toast`). A plot whose 12 cells are all planted and watered is reported as "complete". **Ripeness is not measured**: "complete" only means every cell looks planted and watered, not that the crops can be harvested. The farm card also shows a per-plot state timeline and a strip of the latest photos.
- **Live view.** The live card can be zoomed (wheel or buttons), panned by dragging, and reset by double-click, all in the browser. The detection boxes are drawn into `live.jpg` by the worker and cannot be hidden from the page; the page offers a rule-of-thirds guide overlay instead. A card lists the per-section loop timings and the uptime breakdown that the worker publishes as `perf` and `uptime` in `status.json`.
- **Windows notifications.** Toasts are shown for pause/resume and when casts fail repeatedly (`cast_fail_alert_count` times in a row).
- **Audit.** If clicks and recorded attempts disagree twice in a row, an `audit_mismatch` event is written and the dashboard warns.
- **Logs.** `data/fishing.log` (human readable), `data/events.jsonl` (structured), `data/crash.log` (crashes of the worker). `fishing.log` and `crash.log` both rotate at 5 MB and keep 5 older generations.
- **Success rate.** It is computed over attempts excluding `no_bite_reset`. A run of failures (`miss_streak` in the status) usually means the view or bobber appearance changed.

## Resolution and window handling

- The capture follows the window: resizing, moving between monitors, or changing resolution is picked up from the frames. The tool recomputes its regions and pixel thresholds when the frame size changes (see [Architecture](architecture.md#resolution-handling)).
- Frames of 4K and larger are shrunk by an integer factor before processing.
- Do not minimize VRChat. A minimized window produces no frames; the tool restores it, which brings it to the foreground.
- Keep the HUD visible: the level and experience readings depend on it. If it cannot be read, the tool tries to realign the regions by finding the "Lv" label.
- The window is found by its title (`window_title`, default `VRChat`).

## Data retention

Evidence images are removed after `evidence_ttl_hours` (default 6 hours) or when the directory exceeds `evidence_max_mb` (default 512 MB), oldest first. `pocofarm cleanup` applies the same rule immediately. The event log is never deleted automatically. Archived event logs in `data/archive/` are kept forever unless `archive_keep_days` is set (then `pocofarm cleanup` and worker start delete older ones). Farm photos keep the newest `farm_keep_shots`.

To reset statistics, use the dashboard's reset button or `POST /api/reset`; the old log is moved to `data/archive/`.
