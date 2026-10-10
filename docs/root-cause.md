# Playback-ring descriptor leak: mechanism, evidence and mitigation

## Symptom
After running `Reset-Audio.bat`, audio works and then **stops completely** within minutes.
Progression: clean → intermittent crackle → total silence. Nothing appears in any Windows
event log, and no device re-enumeration, service failure or crash occurs.

## Root cause (evidence-backed)
The Focusrite driver `FocusriteUsb.sys` 4.150.0.432 keeps a ring of **10 output (playback)
isochronous URB descriptors**. Under a pairing race it **orphans one descriptor**: the
completion is processed, but the following `PrepareOutput`/`Submit`/`Pair` for that index
never happens, so the descriptor is permanently lost.

Captured onset (trace window 14:30:54–14:50:54, `Watch-20261010-1432`):

| Time | Pool | Ring period | Margin |
|---|---|---|---|
| 14:30:54 → 14:47:53 | 10 of 10 | 30 ms | +28 |
| **14:47:53** | **9 of 10 (index 1 lost)** | 27 ms | +25 |
| 14:47:53 → 14:50:54 | 9 of 10 | 27 ms | +25 |

The user reported crackle at ~14:47:30–14:48 — i.e. at the moment of the loss.

Event ordering at the loss (from the driver's own ETW queue trace):

```
HEALTHY (index 2)                     FAILING (index 1)
OUT Complete  idx=2                    IN  Complete  idx=1
IN  Complete  idx=2                    IN  Submit    idx=1
IN  Submit    idx=2                    OUT PrepareOutput idx=0
OUT PrepareOutput idx=2                OUT Submit    idx=0
OUT Submit    idx=2                    OUT Pair      idx=0  pair=1   <-- mispaired
OUT Pair      idx=2  pair=2            OUT Complete  idx=1   <-- never resubmitted
```

At the failure the driver paired **input index 1 with output index 0** instead of output 1.
Output 1's completion is then never followed by a resubmission. All statuses remain
`0x00000000` ("success") throughout.

### Why it escalates to silence
Each orphaned descriptor shortens the ring period by one 3 ms slot and leaves that slot's
audio stale — the audible crackle. Repeat leaks: 10 → 9 → 6 → … → 1. Measured collapsed
state (14:20, before the test reset): **1 descriptor, margin −185**, every transfer returning
**0 bytes** → complete silence while Windows believes playback is fine.

### Why nothing shows in the logs
- The driver's own "margin below 10" warning uses an **unsigned** comparison, so a −185 margin
  (0xFFFFFF47) bypasses it (verified in the disassembled driver).
- No re-enumeration, no PnP event, no service restart: `audiodg` PID was unchanged at the drop.
- The audio engine keeps rendering real audio (peak −11.7 dBFS measured during a previous
  silent state) while the device receives zeros.

### Why Reset-Audio.bat "fixes" it, and why it returns
Restarting the device rebuilds the ring to 10 descriptors (verified twice). The leak
mechanism is unchanged, so new orphans accumulate and it fails again.

## Applied fix: pool watchdog (automatic recovery)
`C:\Users\<user>\audio-diag\pool-watchdog.ps1`, installed as scheduled task
**ScarlettAudioWatchdog** (at logon, elevated, hidden).

- Every 30 s while a playback stream is open (every 60 s otherwise) it probes the driver's own ETW queue provider (`slot-probe.ps1`, bounded 2 s
  capture) and counts circulating output descriptors.
- If the pool is measurable and **≤ 2 descriptors** (audio silent or imminent), it restarts
  the Scarlett device and re-verifies. Healthy pools are never touched.
- When nothing is playing the pool is not measurable and no action is taken.

End-to-end test (14:57, threshold raised to force the path):
```
FAULT detected: pool=10 ... -> restarting Scarlett
restart: Device restarted successfully.
after restart: pool=10 indices=0,1,2,3,4,5,6,7,8,9
```

Log: `C:\Users\<user>\audio-diag\watchdog\watchdog.log`

## Not a cure
The defect is in the driver's pairing and resubmission logic. The watchdog converts indefinite silence into automatic
recovery within about a minute. A correction requires an update to the driver module: request retention/resubmission
after a completion, correct input/output pairing under index slip, and a working (signed)
margin check.

What was ruled out by measurement:
- **Disk I/O / DPC bursts as the trigger**: two bounded heavy-read tests (21–22 Sep) did not
  reproduce any pool reduction; storage DPCs stayed under 0.5 ms in those runs.
- **Power management**: USB selective suspend, PCIe ASPM, per-device idle flags remain
  disabled; both AMD HDMI-audio devices remain disabled.
- **CPU scheduling**: audio ready-to-run delays and DPC/ISR durations were sub-millisecond in
  the 18 Sep elevated trace.

## Key evidence files
- `Watch-20261010-1432\queue.usb-queue-Watch.csv` — per-event queue trace incl. the onset
- `Watch-20261010-1432\queue.usb-queue-Watch.txt` — aggregate report
- `Onset-20261010-1440\` — device reset then 8 min trace (healthy 10/10, bytes 1152)
- `CurrentState-20261010-1432\` — collapsed pool (1 of 10, margin −185)
- `slot-probe.ps1`, `pool-watchdog.ps1`, `install-pool-watchdog.ps1`, `queue-timeline.py`
- Prior measurements referenced in this document are retained under `docs/`.

---

## Update: unassisted recurrence and automatic recovery (same day)

After the recovery watchdog was deployed, the ring collapsed on its own and was repaired
without any user action. Long-run record (`evidence/pool-history.csv`):

| Time | Output descriptors | Action |
|---|---|---|
| 15:00:30 - 15:01:36 | 10 of 10 | ok |
| **15:02:08** | **1 (index 3)** | `restart` |
| **15:02:20** | **10 of 10** | `after-restart` |
| 15:02:53 - 15:06:42 | 10 of 10 | ok |

The ring collapsed from a healthy 10 to a single descriptor within ~32 seconds of continuous
playback (consistent with the operator-reported "fails within minutes"), and was restored in
~12 seconds. The recovered ring transferred real payload again immediately.
