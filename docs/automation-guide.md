# Automation Guide

Practical guidance for building your own automation of the pocopoco farm world, based on what worked and what did not in this project's recorded run, plus how to reproduce the published statistics. Numbers cite [Fishing analysis](analysis-fishing.md) and [Farm analysis](analysis-farm.md); mechanics are in [How the World Works](world-mechanics.md).

## Recommendations, ranked

Ranked by how much each one changed the success rate or removed a failure class in the recorded data.

1. **Track your own bobber, not "a red thing".** Decoys (other players' bobbers, red or white avatar parts) were the largest controllable failure: success went from 32.8 % to 92.4 % when decoy fixes landed (P2 to P3, 138 and 344 attempts). Exclude every red-and-white candidate that is visible before you cast, learn where your bobber lands (97.7 % of catches landed within 10 px of one point in the 1080p period, n = 1,697) and prefer candidates near it, and switch candidates only when another one is clearly better.
2. **Reel on the first sharp drop.** First-drop reels caught 95.9 % (1,769 of 1,844) vs 64.3 % for third-drop reels (n = 143). A bite is one drop of about one bobber width (median 9 px at a 10 px bobber) within one or two frames; require at most 2 consecutive frames and a minimum of about 4 px (at 1080p).
3. **Ignore dips outside the 3 to 7 s window.** After the bobber has settled, the bite comes 3.0 to 7.0 s later (99.1 % of 861 waits). Dips before 3 s led to an early reel in 25 % of cases; 4 to 5 s only 0.2 %. If you have another way to know the bobber is settled, start a timer there and treat earlier dips with suspicion. This is an inference from `wait_s`, not a documented game rule.
4. **Run at 1080p-class work resolution.** All three 4K windows were bad (13 %, 14 %, 25 %; loop stalls 27 to 83 per active hour vs 1.4 to 11 at 1080p). The evidence is confounded with development churn, but there is no reason to pay for 4K. Shrink frames by an integer factor, or set the VRChat window to about 1920x1080 and scale thresholds by `height / 1009`.
5. **Use OSC for input and Windows Graphics Capture for the picture.** Cast and reel are `/input/UseRight` 1 then 0 after 50 ms, which needs no focus, so you can use other windows. WGC captures a covered window but gives no frames for a minimized one.
6. **Read the toast with two OCR engines.** One engine alone gave a known fish name in 76.8 % (Windows OCR) or 95.5 % (PP-OCR) of catches. Use the faster engine first, re-read with the stronger one when the fish name, experience or count is missing, and repair garbled names from the experience value (5, 10, 14, 19, 24, 72, 144, 432 to 600 map to species except for the two pairs that share a value).
7. **Verify the pond view before every cast, especially after any camera or menu sequence.** The aim notice (`池の水に向けて投げよう`) is the signal; after the farm photo at 02:03 it appeared 26 times before manual repair. Do not just keep casting: stop and alert after 3 failures in a row (the tool also backs off).
8. **Cast 2.0 to 2.2 s after the reel, and treat a failed cast as cheap.** About 1 to 2 % of casts are ignored at those intervals (2,626 casts); there is no sharp lock edge, so tuning the wait to the last decimal gains little. Adapt upward after a failure, shorten slowly after a streak of 30 successes.
9. **After a stall, redo the settle check.** Loop stalls of 0.7 s or more occurred 196 times (95th percentile 2.2 s, maximum 10.1 s). A frame gap looks like a dip or a lost bobber. Use frame timestamps and treat a gap as "unknown", not as evidence.
10. **Pause for the farm photo only right after a catch.** Expect the next attempt to be a recovery (55 % were `no_bite_reset`), and costs of 4 to 23 s per photo. Do not stream while fishing, since streaming hides the HUD.
11. **Record structured events with evidence.** One line per attempt with outcome, wait, pecks, reaction and OCR text was what made every number here computable. Save a few evidence images, but delete them on a schedule.

What does not need effort: the aim of the cast (the view is fixed when you start), the cooldown after a catch beyond about 2 s, and tuning the number of dips to wait for.

## Things that did not work

| Idea | Result |
|---|---|
| Waiting for 2 or 3 dips before reeling | Success fell from 95.9 % to 85.3 % (2 dips, n = 163) and 64.3 % (3 dips, n = 143); only useful as a short-lived workaround when the detector is confused |
| Looking up the species from the toast name only | 23 % of Windows OCR names were not a known name; use the experience value as a second key |
| Running at raw 4K | Poor results in two windows (13.0 %, 14.0 %) and a third with half-size frames (25.0 %) |
| Opening the camera by OSC alone | `/usercamera/Mode` cannot open it; opening needs `Esc`, the Camera tab and the "Stream Camera" button by real input |
| Integer OSC argument for `/usercamera/Streaming` | Ignored; it must be a boolean |
| Turning the view by OSC (reported by the maintainer) | `/input/LookHorizontal` has no effect in desktop mode; `/input/LookVertical` works. Not exercised by this repository |
| Streaming while fishing | The HUD and toasts disappear |
| Treating every unclassified toast as a problem | 3,731 events, 74 % from one stationary `Lv50` text; filter them |

## Pitfalls checklist

- Treat "early" and "escaped" toast texts by their first words; both mention the sinking bobber.
- Toasts overlap; classify by priority (level up, achievement, aim, early, escaped, caught).
- `wait_s` in this project's data starts at the settle judgment and ends at the dip that triggered the click, not at the game's bite.
- The HUD must be located relative to window size; a "Lv" label search repairs it.
- Windows can change size by themselves during a move (1920x998, 1920x1046, 1280x953 within 3 s): recreate the capture when the captured size disagrees with the window.
- The `gc pause=` value in the log is cumulative since start, not a per-event measure.
- Sell prices in the tool's config are assumptions; the coin counter is not logged, so coins per hour in the analysis are estimates.

## Reproducing these statistics

All statistics come from `data/events.jsonl`, `data/archive/events_*.jsonl` and `data/fishing.log`. Each event line has `ts` and `type`; attempts have `outcome`, `fish`, `exp`, `count`, `wait_s`, `pecks`, `reaction_ms`, `result_ms`, `bobber_w`, `pos`, `engine`, `ocr` and sometimes `trace`. Run the snippets from the folder that contains `data/` with Python 3 and no extra packages. The published numbers use the cutoff `2026-11-05T02:12:41+09:00`, so later data will give larger counts.

### Load the events

```python
import json, glob, collections, statistics as st

CUT = "2026-11-05T02:12:41.450"          # drop this line's filter to include newer data
ev = []
for f in sorted(glob.glob("data/archive/events_*.jsonl")) + ["data/events.jsonl"]:
    for line in open(f, encoding="utf8"):
        try:
            d = json.loads(line)
        except ValueError:
            continue
        if d["ts"] <= CUT + "+09:00":
            ev.append(d)
ev.sort(key=lambda d: d["ts"])
A = [e for e in ev if e["type"] == "attempt"]
C = [e for e in A if e["outcome"] == "caught"]
```

### Outcome totals and success rate

```python
oc = collections.Counter(e["outcome"] for e in A)
den = len(A) - oc["no_bite_reset"]
print(len(A), dict(oc), "success %.1f%%" % (100 * oc["caught"] / den))   # 3523, caught 2248, 65.1 %
```

### Success by hour

```python
by = collections.defaultdict(collections.Counter)
for e in A:
    by[e["ts"][5:13]][e["outcome"]] += 1
for k in sorted(by):
    c = by[k]; d = sum(c.values()) - c["no_bite_reset"]
    print(k, sum(c.values()), c["caught"], "%.1f%%" % (100 * c["caught"] / d))
```

### Bite wait distribution (clean window)

```python
W = [e["wait_s"] for e in C
     if e.get("wait_s") is not None and "2026-11-04T20" <= e["ts"][:13] <= "2026-11-04T21"]
h = collections.Counter(int(w * 2) / 2 for w in W)
print(len(W), sorted(h.items()))      # 861 waits; about 99 % between 3.0 and 7.0 s
```

### Species shares (name, with experience as fallback)

```python
FIX = {0: 10, 4: 14, 9: 19, 44: 144}                  # leading-digit losses
BYEXP = {432: "ヒラメ", 480: "ブリ", 528: "タイ", 600: "マグロ", 72: "金色のコイ",
         144: "イトウ", 24: "サケ", 14: "ナマズ", 10: "コイ"}
def species(e):
    x = FIX.get(e["exp"], e["exp"]) if e["exp"] is not None else None
    n = e["fish"] or ""
    if x in BYEXP: return BYEXP[x]
    if x == 19: return "エビ" if any(c in n for c in "エ工ビェ") else "ニジマス" if any(c in n for c in "ニ二ジマス") else "?"
    if x == 5:  return "フナ" if any(c in n for c in "フブナ") else "ドジョウ" if any(c in n for c in "ドジョウ") else "?"
    return n or "?"
print(collections.Counter(species(e) for e in C).most_common())
```

### Reel-to-cast lock table

The interval is only in the log (`[castlock] dt=2.03 ok` or `fail`):

```python
import re
rows = []
for line in open("data/fishing.log", encoding="utf8"):
    m = re.search(r"\[castlock\] dt=([\d.]+) (ok|fail)", line)
    if m: rows.append((float(m.group(1)), m.group(2)))
for lo, hi in [(1.95, 2.0), (2.0, 2.05), (2.05, 2.1), (2.1, 2.2), (2.2, 2.35), (2.35, 2.7)]:
    x = [r for d, r in rows if lo <= d < hi]
    print(lo, hi, len(x), sum(r == "fail" for r in x))
```

The log is rotated at about 5 MB; the recorded run kept all lines in one file. Older lines are in `data/fishing.log.1` to `.5`.

### Level and experience

```python
H = [e for e in ev if e["type"] == "hud"]
print(H[0]["level"], H[0]["xp"], H[-1]["level"], H[-1]["xp"])   # 45 0.309 ... 49 0.788
```

### Farm scans

```python
import glob, json
for f in sorted(glob.glob("data/farm/*.json")):
    d = json.load(open(f))
    cells = "".join(p["cells"] for p in d["plots"])
    print(f[-11:-5], len(d["plots"]), collections.Counter(cells))
```

Cell codes: `e` unplanted and dry, `p` planted and dry, `b` unplanted and watered, `w` planted and watered, `?` hidden.

### Commands and endpoints in this repository

| Purpose | Command |
|---|---|
| Check a running setup (window, capture rate, HUD, bobber, firewall) | `pocofarm doctor` (add `--image <png>` for a saved screenshot) |
| Test bobber detection on the live window | `pocofarm capture-test out.png` |
| OCR one image | `pocofarm ocr-image <png>` |
| Compare the two OCR engines on the fixture images | `pocofarm bench-ocr [dir] [-v]` |
| Run the loop without clicking | `pocofarm run --dry-run` (or `--paused` to start paused) |
| Run unattended with restart | `pocofarm all` |

The dashboard (default port 10000) serves the same data live: `/api/state` (state and statistics), `/api/events?outcome=<name>&limit=<n>` (attempts), `/api/evidence`, `/api/archives`, `/api/farm` (photos and scans), `POST /api/farm/shoot` and `POST /api/farm/calibrate`, and `/api/config`. See [Operations](operations.md#dashboard-http-api) for the full list.

### What you cannot reproduce from the repository data

- Coins per hour (the coin counter is not logged; the published estimates use the config price table).
- Anything before 2026-11-04 16:56 or after the cutoff.
- Per-attempt screenshots older than 6 hours (evidence is deleted on a schedule).
