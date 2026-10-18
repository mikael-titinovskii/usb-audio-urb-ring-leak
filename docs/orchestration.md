# Goal: reproduce the playback-ring fault, find the cause — orchestration

## Objective / acceptance
**Reliably reproduce** the FocusriteUsb playback-descriptor orphan (and thereby prove the cause).
"Reliable" = reproduces more than once with a clear, measured rate increase — not a one-off event.

## Two parallel workstreams

| Stream | Owner | Deliverable |
|---|---|---|
| **A. Decompile the chain** — find the exact code path that advances the OUT ring index by two | subagent `agent-9` | `docs/code-chain.md` |
| **B. Reproduce** — find a trigger that raises the fault rate reliably | subagent `agent-10` | `docs/reproduction-results.md` |

Orchestrator (main thread) owns this file, the shared metrics, and reconciling the two results.

## The fault (established)
Driver `focusriteusb.sys` 4.150.0.432 keeps a **ring of 10 isochronous OUT URBs**. Occasionally
it advances the ring index by **two** instead of one → a slot is never re-armed → stale → audible
chop, then silence. All statuses `0x00000000`; no OS log entry.

**Trigger:** a cycle in which the **OUT completion is delivered before the IN completion**. In
the event stream that appears as **two consecutive completions of the same direction**
(e.g. `Complete OUT 1` then `Complete OUT 2`). Details and the cycle-by-cycle evidence:
the driver-internals analysis (available on request).

## Shared metrics
- **Leaks** — authoritative. Each `,restart` row in
  `C:\Users\mimit\audio-diag\watchdog\pool-history.csv` is exactly one leak (the recovery
  watchdog rebuilds the ring on each detection). Baseline ≈ **3/hour** in ordinary use.
- **Flip rate** — high-sensitivity proxy. `tools/flip-detector.py <queue.csv>` counts
  consecutive same-direction completions. Reference: **106 in the 20-minute onset trace ≈ 5.3/min**
  (versus 1 leak in that same window) — ~100× more frequent, so far faster to iterate on.
- **Orphan indicator** — the same tool reports `advance == +2 (mod 10)` on OUT submissions; that
  means the ring stepped over an index that was never re-armed. Reference run (the 20-min onset
  trace): 394,502 advances of +1 and **6,862 advances of +2, every one skipping index 1** —
  i.e. once a slot is orphaned the rotation produces one +2 per lap for the rest of the session.
  So: `+2 count > 0` in a capture ⇒ an orphan *exists* in that capture, and the reported missing
  index names it. This is a cheap, high-confidence orphan check for any capture.

### Verified reference numbers (tools/flip-detector.py)
| trace | completions | same-dir (trigger) | advance==+2 (orphan) |
|---|---|---|---|
| 15 s onset window (`evidence/onset-window-events.csv`) | 12 | 2 | 1 (index 1) |
| 20-min onset trace (`Queue-…Watch.csv`) | 802,732 | 106 (5.28/min) | 6,862 (all index 1) |

## Tooling status (verified 2026-10-18 ~01:48)
- Recovery watchdog `ScarlettAudioWatchdog`: running (threshold 9, ~8 s poll).
- Phase harness `C:\Users\mimit\audio-diag\exp-phase.ps1 -Phase|-Report`: working.
- ETW queue capture `catch-onset.ps1 -Minutes N`: **verified** — 60 s → 2.2 MB with a stream open
  (~37 KB/s). Note: one 12-minute capture produced only 1.6 MB (anomalous, cause unknown); if a
  capture looks tiny, re-check that a stream was open for its whole duration.
- ETL→CSV via `Analyze-FocusriteUsbQueue.ps1` (heavy, minutes on a 275 MB ETL; seconds on a
  60 s one). Flip rate therefore measured as: capture ~60 s → analyze → `flip-detector.py`.
- Driver parameters backup/restore: `C:\Users\mimit\audio-diag\registry-backup\`.

## THE TRIGGER — settled (2026-10-18)

Full evidence in the driver-internals analysis (available on request). Scanning the whole 20.1-minute baseline trace for
gaps between consecutive completions finds exactly **two** stalls > 5 ms, and they sit exactly at
the state change:

```
t=988.986 s   gap = 14,046 µs
t=989.009 s   gap = 22,288 µs
              -> driver re-primes; pairing offset 0->1 and margin 28->25, both permanent
t=1018.858 s  a flip lands on that skewed state -> ring index +2 -> slot orphaned
```

Chain: **completion-processing stall (>=5 ms) -> re-prime -> skewed state -> later flip -> orphan.**

This supersedes the earlier "system load" framing. The target is a specific measurable event, not
a load level. `tools/flip-detector.py` now reports `stall : completion gaps >5ms` — run it on any
queue CSV. A run with no stall proves nothing.

## Flip timing (measured from the 20-min baseline trace)

The IN and OUT completions **for the same index** are only ~10–25 µs apart, and a flip is those
two swapping order:

```
normal :  Complete IN  idx=N (t)      Complete OUT idx=N (t + 13..16 µs)
flip   :  Complete OUT idx=N (t)      Complete IN  idx=N (t +  8..9  µs)
```

Notes:
- Each flip produces **two** consecutive-same-direction pairs (OUT-OUT, then IN-IN). So the
  reference count of 106 pairs = **53 flips in 20 min ≈ 2.6/min**.
- Of those 53 flips, only **1** became a permanent orphan. So a flip is *necessary but not
  sufficient* — the orphan also needs the pairing state to be vulnerable at that instant.

**Consequence for the trigger search:** the race window is **microseconds**. System-level load
(CPU, disk, DPC contention) operates at *millisecond* scale and is therefore unlikely to be the
lever — consistent with H1 producing 0 leaks in 13.8 min. A trigger must perturb **USB transfer
timing at the µs scale**.

### Stall levers actually present on this machine (surveyed 2026-10-18)

The stall needs a **long DPC/ISR on the CPU running the audio completion DPC**. Sources available here:

- **WSL2 VM-bus** (`vmbusr.sys`) — measured 10.4 ms in the red LatencyMon run. Heavy WSL2 disk I/O
  (`exp-trigger-h1.ps1` / `h1.sh`). Forcing it onto the audio core is the uncertain part; raise the
  number of concurrent I/O streams to raise the odds.
- **USB storage** — this machine has a **Verbatim Vi5000** USB SSD and another USB mass-storage device
  (`VendorCo ProductCode`). Sustained large-file read *and* write on those drives generates storport +
  xHCI DPC traffic and can hold a core for milliseconds. A cheap, easily repeatable lever.
- **Network** — Intel I226-V (link only **100 Mbps**, so throughput alone won't do it) and a Hyper-V
  virtual adapter at 10 Gbps. A loopback/VM-net flood is more promising than the physical NIC.
- **Three xHCI controllers** are present (2× AMD 3.10, 1× 3.20); the Scarlett sits on an AMD 3.10
  (`PCI\VEN_1022&DEV_15B7`). Traffic on *any* controller still competes for CPU time.

Escalation order: WSL I/O → add USB-storage read/write on the Verbatim → add a virtual-NIC flood →
run all three concurrently.

### Recommended µs-scale trigger candidates (for stream B), cheapest first
1. **Split traffic across the same xHCI controller.** The Scarlett is on the AMD xHCI
   (`PCI\VEN_1022&DEV_15B7`), and this machine also exposes a **Realtek USB Audio** device. Driving
   a second isochronous audio stream on that device, or bulk traffic to a USB storage device, changes
   the controller's per-frame scheduling and can shift the IN/OUT completion order.
2. **Format / sample-rate reconfiguration.** Alternating the default endpoint's format between two
   rates (44.1 k ↔ 48 k ↔ 96 k) repeatedly — via `IPolicyConfig::SetDeviceFormat` (the interop is
   already written in `OneDrive\Desktop\Reset-Audio.ps1`) — reconfigures the device and its transfer
   sizing mid-stream.
3. **CPU pinning / affinity perturbation.** Pin the audio work to a core and then force migration
   (`SetThreadAffinityMask` storms, or a high-priority spinning thread on that core). Microsecond
   scheduling effects, not load.
4. **DPC storm tuned to the audio core**, not general load — a periodic timer DPC aimed at the core
   servicing the audio interrupt.

Note for any of these: a format change or stream re-init may rebuild the ring and mask an orphan —
so score the **flip rate** (still visible) and confirm with a capture that contains an
`advance==+2` orphan indicator.

## Method rules (learned the hard way)
1. **Bracket every phase** with `exp-phase.ps1 -Phase`. A leak counts only against the phase that
   was *actually* running — an earlier run wrongly attributed a leak to a window whose trigger had
   already stopped, and the claim had to be retracted.
2. **One variable at a time.**
3. A stream gap rebuilds the ring and hides leaks — confirm `ok: pool=10` (not `idle/no-stream`)
   in `watchdog.log`; discard runs containing idle samples.
4. Concurrent load from the other workstream is roughly constant — compare WITH vs WITHOUT a
   trigger, never an absolute.

## Reconciliation / acceptance criteria

Stream A is done: the mechanism that drops a slot was identified earlier (detail available on request) -
unconditional store into a single-entry pending-OUT mailbox, combined with the pairing gate that only runs when a pending IN also exists.

Stream B must now produce the trigger on demand. **A run counts only if it shows all three, in
order, measured from its own queue CSV:**

| # | criterion | how to check |
|---|---|---|
| 1 | a **completion gap > 5 ms** | `flip-detector.py` → `stall : completion gaps >5ms` line |
| 2 | the **submit margin moving 28 → 25 and staying** | `Pair`/`Submit` events; steady state is always 28 (verified: only the two 14/22 ms stalls ever moved it) |
| 3 | an **`advance == +2`** on OUT submits ~30 s later | `flip-detector.py` → orphan line, and a `,restart` row |

### Capture continuity check — every run showed >500 ms holes (2026-10-18 02:05)

Every short run by stream B reports **many >500 ms holes**, roughly one every 7.5 s:

```
quiet-1 : >500ms holes = 16-19 in 2.0 min
wslio-1 : >500ms holes = 16     in 2.0 min   (its own scan: "investigate before trusting this run")
```

The capture uses `logman ... updateEvery=4s` and logs "armed N times" every 4 s, so the holes are
most likely **created by the capture method** (session re-arm/flush), not by the audio stream — the
watchdog shows `pool=10` continuously throughout, i.e. a stream is open. Holes this size mean the
capture is **not continuous** and cannot prove the absence of a stall, let alone its presence.

**Action:** switch to a single continuous `logman create trace ... -ets` for the whole run (as the
20-minute reference and the `OnsetCatch-*` captures use — those have **0** holes), and treat any run
with holes > 500 ms as invalid. Verify with `flip-detector.py`'s `gaps >200ms` line = 0.

### Flip-rate measurement trap: flips cluster at the start of a capture (2026-10-18 02:04)

Every short run puts essentially **all** of its same-direction pairs in the **first minute**:

```
quiet-1     (2.02 min)  flips=14   -> all in min 0
baseline-A  (6.06 min)  flips=29   -> min1=25, min3=2, min4=2
wslio-1     (2.02 min)  flips=32   -> min1=32   (15.8 flips/min headline)
```

So on a 2-minute capture the headline `flips/min` is really "flips at capture start", and comparing
such numbers between runs measures the start transient, not the trigger. **Fix: report flips/min
computed over the window *after* the first ~30–60 s**, or use long captures. The reference trace
(20 min) is the only one where the distribution is spread (min1=42, min12=16, min19=30, …) and
therefore trustworthy as a baseline.

### Blocking bug found and fixed in the WSL load (2026-10-18 02:03)

`exp-wsl-load.sh` created its work dir under `/root/exp-load`, but the default WSL user is **`mimit`,
not root** (`mkdir: cannot create directory '/root': Permission denied`). The script therefore exited
immediately and **applied no load at all** — so any "load" phase that used it (e.g. `wslio-1`) was a
no-op and its leak count is meaningless. Fixed to `${TMPDIR:-/tmp}/exp-load` and verified:
`pgrep -c dd` = 11 during a run, 0 after, with cleanup working.

**Check before trusting any load phase:** `wsl.exe -e bash -c "pgrep -c dd"` (or `pgrep -c yes`)
while the phase is running — if it returns 0, no load is being applied.

### False-positive traps (found the hard way)

- **Long "gaps" are not stalls.** Capture/flush artifacts and stream-idle periods show up as gaps of
  hundreds of ms to seconds. The real stall is **14–22 ms**. `flip-detector.py` now only counts gaps
  of **5 ms–200 ms** as stalls and reports anything > 200 ms separately as an artifact. A run
  claiming stalls in the 0.3–4 s range is reporting its own capture gaps, not the fault.
- **An `advance==+2` with margin == 28 is not the fault.** After the genuine trigger the margin is
  **25** (28 − one 3 ms slot). Pre-trigger, margin is always 28. So require margin == 25 (or any
  value ≠ 28) for an orphan claim.
- **Buffer/channel changes show as `PrepareOutput` with `value = 0` or `384`** instead of the usual
  `1152`; they are not the fault either.

Criteria 1+2 prove the trigger fired and the driver re-primed into the vulnerable state;
criterion 3 proves the orphan followed. Leak count alone is not enough — a leak without 1+2 in the
same window is not this fault, and 1+2 without a leak is a partial success worth reporting.

Reference values from the 20.1-minute baseline trace: stalls `14046µs@989.0s, 22288µs@989.0s`;
margin 28 → 25 at t=989.011 s; first orphan t=1018.858 s.
