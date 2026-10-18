# Reproduction plan — forcing the playback-ring descriptor orphan

Goal: turn a rare, spontaneous fault into a **reproducible experiment** — either drive the leak
on demand, or find a trigger that raises the rate enough to measure and confirm the mechanism.

## What we know (mechanism)

From `evidence/onset-window-events.csv`, the fault is:

1. The driver picks the next OUT ring index from the **pairing index** (input side). Normally a
   constant one-slot phase offset: submit OUT `n`, pair with input `n+1`.
2. The phase slips for a few cycles (`unequal_IN_OUT_indices` ≈ 16% — normal).
3. On **resynchronisation** the OUT index advances by two instead of one, skipping a slot
   (`0 → 2`).
4. The skipped slot is never re-armed → **orphaned** → pool 10 → 9 → … → 1.

So the fault needs a **divergence** between the pairing index and the OUT ring index — i.e. an
**anomalous completion ordering on one pipe**, then a resync.

## Hypotheses to test

| # | Trigger hypothesis | How it perturbs the driver |
|---|---|---|
| H1 | DISPATCH_LEVEL / DPC contention | A completion DPC lands inside the submit/pair window → read-index / preempt / write-stale race |
| H2 | Device-level pipe hiccup (error / retry / resume) | Produces a duplicate or out-of-order completion |
| H3 | Stream re-init racing playback | Start/stop or format change resets counters mid-flight |

H1 is the strongest: LatencyMon run 1 (WSL busy) showed `vmbusr.sys` DPCs up to **10.4 ms**.

## Measurement problem to solve first

- The fault is rare (~1 leak / 20–40 min, sometimes zero for a day) — too slow to iterate on.
- The recovery watchdog **restarts the device** on a leak, which rebuilds the ring and destroys
  the observation. Experiments need it **log-only**.
- Onset evidence needs an event window, but the fault is quick — a 2 s probe is too coarse.

## Phase 0 — Instrumentation

1. **Log-only monitor** (`exp-monitor.ps1`): samples the OUT descriptor pool every ~3 s using the
   existing ETW probe; logs `timestamp,pool,indices`; **never restarts**. Disables the recovery
   watchdog for the duration and re-enables it after.
2. **Leak detector**: a "leak event" = pool decreases by ≥1 between samples. Count and timestamp.
3. **Playback driver**: a guaranteed continuous stream for the whole run (so the ring stays
   built — any stream gap rebuilds the ring and masks leaks):
   - a looping local WAV played via a hidden player, or
   - a browser/Audio session held open.
   Stream gaps are themselves logged, and any leak adjacent to a gap is discarded.
4. **Optional continuous ETW capture** for onset windows (bounded, stop-and-analyze), off by
   default to keep costs low; enabled for a confirmed trigger.

## Phase 1 — Baseline (control)

Run 45–60 min of uninterrupted playback, no trigger. Record leaks/hour and the time-to-first-leak.

## Phase 2 — H1: DISPATCH_LEVEL contention

Same playback, plus one of:
- heavy container/VM work (WSL build, Docker build) — reproduces the `vmbusr.sys` DPC spikes;
- a network flood (saturates NDIS DPCs);
- a bounded synthetic DPC/ISR storm.

Measure leaks/hour vs Phase 1. **A jump confirms H1.**

## Phase 3 — H2 / H3 if H1 is inconclusive

- H2: rapid sample-rate / buffer toggles in Focusrite Control; interface alternate-setting
  changes; a suspend/resume cycle mid-stream.
- H3: repeated playback start/stop at random intervals; endpoint switch.

## Phase 4 — Onset capture and confirmation

For any confirmed trigger, capture the full event window and check for the **signature**:

```
OUT Submit index sequence skips by 2 (e.g. 0 -> 2), Pair index advances by 1,
OUT completion for the skipped index arrives but is never resubmitted.
```

Matching that signature = the trigger is the same fault, not a different one.

## Success criteria

- A trigger that produces the OUT-index-skip **within minutes**, repeatably across runs; and
- the onset window shows the pair/OUT phase slip → skip → orphan sequence.

## Risks / limits

- The fault may need **microsecond-level** ordering that no external workload reliably produces.
  If so, the only sure reproduction is a **kernel debugger breakpoint** in the completion routine
  to hold it a controlled interval (needs a second machine or a VM with the Scarlett passed
  through).
- Reaching pool ≤ 2 silences audio; that is expected and acceptable inside an experiment.
- Any stream gap resets the ring — a failed run with gaps is discarded, not counted.
