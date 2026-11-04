# automation-vrchat-pocopocofarm

[![CI](https://github.com/book000/automation-vrchat-pocopocofarm/actions/workflows/ci.yml/badge.svg)](https://github.com/book000/automation-vrchat-pocopocofarm/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A Windows tool that automates fishing in the VRChat world "Pocopoco Farm" (the world's own title is in Japanese). It watches the game window, detects the bite, and sends the click through VRChat's OSC input. It does not read game memory or touch network traffic.

```
cast -> wait for the bobber to settle -> detect a dip and reel in -> read the result toast (OCR) -> record -> cast again
```

> **Disclaimer.** This is an unofficial personal project and is not affiliated with VRChat or the world's creator. Automating input may violate the terms of the world or of VRChat. Check the rules yourself and use the tool at your own risk.

## Features

- **Fast reaction.** Detects the bobber (red stick and white ball) in captured frames and reels in as soon as the first dip is confirmed (by default the first dip; see `pecks_to_reel`).
- **Works in the background.** Frames come from per-window capture (Windows.Graphics.Capture) and clicks are sent over OSC, so VRChat can sit behind other windows without losing focus or moving your cursor. VRChat must not be minimized.
- **Every attempt is recorded.** Each reel ends in exactly one outcome (`caught`, `early`, `escaped`, ...) with evidence images. A catch is only counted when its result toast was actually read.
- **Self-tuning.** Shortens the recast wait, adapts the number of dips before reeling, and adapts the bite-wait timeout from measurements.
- **Web dashboard.** Live view, success rate, per-fish statistics, attempt history, evidence gallery, pause/resume, and an editable settings page.
- **Resident service with self-update.** A supervisor restarts the fishing process if it dies and can swap in a newer executable without stopping manually.
- **Optional farm photos.** Periodically takes a top-down photo of the farm through the in-game camera and analyzes which plots are planted and watered.

## Requirements

- Windows 11 (22H2 or later recommended; on older builds the yellow capture border cannot be hidden, which does not otherwise affect operation).
- VRChat desktop mode with OSC enabled (Action Menu -> Options -> OSC -> Enabled).
- The Japanese language pack for Windows OCR. The game's toasts are Japanese and the primary OCR engine is the Windows one (`ja`).
- A GPU/driver that supports Direct3D 11 (used by the capture).
- To build from source: the .NET 10 SDK. A published executable is self-contained and needs no runtime.

## Quick start

1. Get the executable: download `pocofarm.exe` from the [Releases](../../releases) page (tagged `v*` builds), or [build it yourself](docs/development.md#build-test-publish).
2. Create the working directory for the tool (any folder) and put `pocofarm.exe` there. Runtime files (`config.json`, `data/`) are created relative to the folder passed as `--root`.
3. Start VRChat, join the world, hold the fishing rod, and face the pond.
4. Run a first check (optional but recommended):

   ```powershell
   .\pocofarm.exe doctor
   ```

5. Start fishing and the dashboard:

   ```powershell
   .\pocofarm.exe all --root .
   ```

6. Open `http://localhost:10000/` in a browser.

Press **Pause/Break** to pause and resume. The key works while any window is in the foreground, and no clicks are sent while paused.

## Usage

| Command | Description |
|---|---|
| `pocofarm all [--root <dir>]` | Resident mode. Starts `run` as a child process, restarts it after a crash, and swaps in a newer executable when one appears. |
| `pocofarm run [--root <dir>] [--host <ip>] [--port <n>] [--dry-run] [--paused]` | Fishing and the dashboard in one process. `--dry-run` sends no clicks; `--paused` starts paused. |
| `pocofarm doctor [--image <png>]` | Pre-flight check: OS, capture rate, resolution, HUD regions, bobber detection, firewall rule. Never clicks. |
| `pocofarm setup` | Creates the firewall rule that allows the dashboard to be opened from the LAN (asks for UAC elevation). |
| `pocofarm init-config` | Writes a `config.json` containing all defaults. |
| `pocofarm cleanup` | Removes expired or excess evidence images immediately. |
| `pocofarm capture-test [out.png]` | Captures VRChat for a few seconds and prints size, frame rate, toast OCR, and bobber detection. |
| `pocofarm ocr-image <png>` | Reads one image with Windows OCR and prints the toast classification. |
| `pocofarm bench-ocr [dir] [-v]` | Compares the two OCR engines on a directory of sample images (default `tests/fixtures/ocr`). |

`--root` is the folder that contains `config.json` and `data/`; it defaults to the current directory. See [Operations](docs/operations.md) for running the tool as a long-lived service.

## Dashboard

Open `http://<this-pc-ip>:10000/` from the LAN (or `http://localhost:10000/` on the same machine). The page refreshes every 1.5 seconds and shows:

- run state, success rate, outcome breakdown, per-fish counts, and a catch-rate chart;
- the live view with the detected bobber and the toast region drawn on it, and the most recent result;
- the attempt history with reaction time, time to the result toast, the OCR text, and links to evidence images;
- failure causes over time and success rate by hour;
- tuning statistics (streaks, percentiles of reaction and wait times, experience per hour, drop rates, and more);
- attention events (unlinked or unclassified toasts, errors);
- a settings card to view and edit every setting with its description;
- pause/resume and data-reset buttons (accepted only from private network addresses). A reset moves the records into `data/archive/` rather than deleting them;
- the evidence gallery and the log tail.

The dashboard's text is in Japanese. The HTTP endpoints are documented in [Operations](docs/operations.md#dashboard-http-api).

If the page cannot be opened from another device, run `pocofarm setup` once. See [Operations](docs/operations.md#lan-access-and-the-firewall).

## Configuration

Settings live in `config.json` (snake_case keys; missing keys use defaults). Almost every numeric threshold is a setting, and most can be edited from the dashboard's settings card. Coordinates are stored as ratios of the reference resolution (1920x1009) and re-resolved for the real window size, so no tuning is needed when the resolution changes. See the [configuration reference](docs/configuration.md).

## How it works

1. The capture thread receives frames of the VRChat client area.
2. The bobber detector finds the red stick and white ball; the dip detector decides when the bobber has settled and when it dips.
3. On a dip the tool sends a click over OSC (`/input/UseRight`) and starts waiting for the result toast.
4. A background worker keeps reading the toast region with Windows OCR; the PP-OCR engine re-checks incomplete or unclassifiable text.
5. The result is classified, stored in `data/events.jsonl`, and published to the dashboard; the auto-tuner adjusts timings from the outcome.

Details are in [Architecture](docs/architecture.md).

## Documentation

- [Architecture](docs/architecture.md)
- [Configuration reference](docs/configuration.md)
- [Operations](docs/operations.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Development](docs/development.md)
- [World map](docs/world-map.md): what is where in the world, boards, crops, fish and prices
- [Seed levels](docs/seed-levels.md): what each seed level changes (growth time, price, harvest, requirements)
- [How the world works](docs/world-mechanics.md): bite timing, toasts, OSC and capture facts for automation authors
- [Fishing analysis](docs/analysis-fishing.md) and [farm analysis](docs/analysis-farm.md): statistics from a recorded run
- [Automation guide](docs/automation-guide.md): ranked recommendations and how to reproduce the statistics
- [Contributing](CONTRIBUTING.md)

## Troubleshooting

Quick pointers; the full list is in [docs/troubleshooting.md](docs/troubleshooting.md).

- Casts keep failing: face the pond, keep the rod equipped, and make sure OSC is enabled.
- Nothing is recorded: run `pocofarm doctor` while VRChat is running and fix any `NG` line.
- The dashboard is unreachable from another device: run `pocofarm setup`.

## License

[MIT](LICENSE). Third-party components, including the embedded OCR models, are listed in [NOTICE.md](NOTICE.md).
