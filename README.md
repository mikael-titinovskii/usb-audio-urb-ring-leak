# USB audio playback-ring descriptor leak

**Measured onset, mechanism, and an automated recovery for a total-loss playback failure in a
USB Audio Class 2.0 interface.**

A ten-descriptor isochronous playback ring progressively loses descriptors. Each loss removes one
3 ms slot from the ring and leaves that slot's audio stale — audible as a repeating fragment. When
the ring is reduced to a single descriptor the schedule falls behind USB time and every transfer
returns **zero bytes**: total silence, while Windows, the audio engine and the device driver all
report success and emit no failure event.

The onset is captured here to the second, together with the driver's own event ordering around
the descriptor loss.

---

## 1. Environment

Full machine-readable record: `evidence/environment.txt` (read-only queries at capture time).

| Item | Value |
|---|---|
| Interface | VID:PID `1235:8218`; interface class reported as `USB\Class_01&SubClass_00&Prot_20` (audio control, protocol 2.0); 4 record / 2 playback channels |
| Driver module under test | `FocusriteUsb.sys` 4.150.0.432 — SHA256 `498F4644C737C829FDD8853F9BE79A4FBF65C812B623818DF611587779135B80` |
| Companion modules | `FocusriteUsbAudio.sys`, `FocusriteUsbSwRoot.sys`, `FocusriteUsbAsio.dll` (versions and hashes in `evidence/environment.txt`) |
| OS | Windows 10.0.26200, 64-bit |
| Stream settings | 48 kHz, 128-sample buffer, Safe Mode on, clock source USB Internal (synced). Reported by the interface control application; its settings screenshot is not retained here |
| Transport | Interface on a CPU-attached xHCI root port, not a hub (parent chain in `evidence/environment.txt`) |

Power management was verified disabled at capture time, and the values are recorded in
`evidence/environment.txt`: USB selective suspend (power scheme, AC value `0x0`), PCIe ASPM (AC
value `0x0`), and `SelectiveSuspendEnabled`, `AllowIdleIrpInD3`, `EnhancedPowerManagementEnabled`,
`DeviceSelectiveSuspended` all `0` on both the interface and its root hub. Unused GPU HDMI-audio
endpoints were kept disabled.

## 2. Symptom

1. Audio plays normally.
2. Intermittent crackle begins.
3. Within minutes the output goes completely silent.
4. Restarting the USB device restores audio — and the cycle repeats.

The fault leaves no trace where one would normally look. In the 20-minute capture that contains
the descriptor loss, the Windows audio provider log and the PnP log recorded **zero** events, the
interface's PnP status stayed `OK` at every 5-second sample, and the audio host process identity
did not change. See `evidence/negative-evidence-no-windows-events.txt`.

## 3. Measurement method

Two ETW sources are used, and one of them is the driver's own tracing:

- The driver registers control GUIDs that look like the classic magic values `deadbeef` /
  `feedfacebeef` — they are the driver's own, not invented here:
  - `{deadbeef-dead-beef-0005-feedfacebeef}` — per-URB queue events (`Submit`, `Complete`, `Pair`,
    `PrepareOutput`) with descriptor index, direction, status, `StartFrame`, `CurrentFrame`;
    keywords `0x100401`, level 5. Used for every queue measurement in this repository.
  - `{deadbeef-dead-beef-000b-feedfacebeef}` — audio-client buffer counters; keywords `0x11`.
- Windows' own USB trace independently confirms transfer sizes and buffer identities
  (`evidence/earlier-20260928-usb-cycles.txt`).

`StartFrame − CurrentFrame + 1` is the logged scheduling margin. The margin **fluctuates during
healthy operation** — both `+25` and `+28` dominate the healthy capture, and `+25` appears before
the loss as well as after it — so the margin alone does not identify the fault. The reliable
indicator is the **number of distinct output descriptors in circulation**.

A useful side effect of how the driver was built: the margin-warning branch in `FocusriteUsb.sys`
compares the value as **unsigned**, so a negative margin wraps to a large positive number and is
not flagged (established from the disassembly in the earlier pass, not from the trace). The
analyzer's own margin counters are reported in each aggregate file rather than judged here.

## 4. Mechanism captured at onset

What the capture establishes directly:

- Output descriptors circulate in a ten-slot ring with a ~30 ms period (10 × 3 ms).
- At a single completion, one output descriptor is **never resubmitted**, and it is lost
  permanently for the life of the stream.

The window `evidence/onset-window-events.csv` contains the loss and its immediate aftermath:

```
HEALTHY cycle (index 2)                FAILING cycle (index 1)
OUT Complete  idx=2                     IN  Complete  idx=1
IN  Complete  idx=2                     IN  Submit    idx=1
IN  Submit    idx=2                     OUT PrepareOutput idx=0
OUT PrepareOutput idx=2                 OUT Submit    idx=0
OUT Submit    idx=2                     OUT Pair      idx=0  pair=1
OUT Pair      idx=2  pair=2             OUT Complete  idx=1   <- and then nothing
```

In the healthy cycle the output is paired with the input of the **same index** (`pair=2` with
`idx=2`). In the failing cycle the pair record shows **input index 1 paired with output index 0**,
output index 1 then completes, and no further `PrepareOutput`/`Submit`/`Pair` for index 1 ever
appears. Every status in the window is `0x00000000`.

**Interpretation and its limits.** The missing resubmission of index 1 is a captured fact. The
unequal pairing at that instant is the leading explanation and matches the single-pending-output
lead identified in the earlier static analysis — but it is **not proven to be the cause**: unequal
input/output pair indices are common in the very same capture (65,521 of 401,365 engine pairs,
about 16%) while only one descriptor was lost. The earlier documents of this investigation called
this a candidate rather than a captured onset fact; that status is now partly superseded, in that
the onset itself is captured, but the causal step from "unequal pair" to "orphaned descriptor"
remains unproven.

### Timeline of the loss

Capture window `14:30:54.530`–`14:50:58.653` (about 20 minutes), continuous playback, no setting
changed:

| Time | Output descriptors | Ring period | Margin |
|---|---|---|---|
| 14:30:54 → 14:47:53 | 10 of 10 | 30 ms | +25/+28 (fluctuating) |
| **14:47:53** | **9 of 10 — index 1 orphaned** | 27 ms | +25 |
| 14:47:53 → end of capture | 9 of 10 | 27 ms | +25 |

The loss is at trace-relative 1019 s (`evidence/pool-timeline-onset-capture.txt`, `>> CHANGE at
1019s: slots 10 -> 9 indices=[0, 2, 3, 4, 5, 6, 7, 8, 9]`). In the same capture the count for
output index 1 is 33,962 against about 40,823 for every other index — exactly one descriptor's
worth of work missing.

The operator reported crackle at approximately 14:47:30–14:48, which brackets both the last
healthy-looking second and the loss at 14:47:53. That report is a session observation, not an
artifact in this repository.

### Progression to total loss

| State | Output descriptors | Ring period | Margin | Result |
|---|---|---|---|---|
| Healthy | 10 | 30 ms | +25 / +28 | clean |
| Degraded | 9 (one absent) | 27 ms | +25 | crackle |
| Degraded | 6 (four absent) | 18 ms | — | crackle |
| Collapsed | **1** | 3 ms | **−185** | **silence; every transfer returns 0 bytes** |

- 9-descriptor row: `evidence/onset-capture-aggregate.txt`, `evidence/pool-timeline-onset-capture.txt`
- 6-descriptor row: `docs/earlier-20260921-queue-finding.md` (restated in `docs/earlier-20260928-recurrence.md`)
- 1-descriptor row: `evidence/collapsed-state-aggregate.txt` (the original analyzer report),
  `evidence/collapsed-state-events.csv` (every one of its 120,258 event rows) and
  `evidence/collapsed-state-probe-results.txt`; an independent earlier capture of the same state is
  in `evidence/earlier-20260928-silent-state-queue.txt` (single descriptor index 2, margin −53,
  `Bytes=0`)

In the collapsed state recorded on 10 October (14:20), 20,043 consecutive submissions of a single
descriptor (**index 8**) all report success while transferring zero bytes, with the record ring
still fully intact at ten descriptors. A second collapsed observation (15:02:08) had a single
descriptor too but a different one (**index 3**) — the surviving index varies, so it identifies the
session, not the mechanism.

### Windows and device divergence

With the endpoint unmuted and active playback at −11.7 dBFS peak, a simultaneous digital loopback
of the device's own playback channels recorded **every sample exactly zero** (384,768 frames →
`evidence/earlier-20260928-windows-vs-device-signal.txt`). This failure is a
broken host-side request ring: not a mute, not an analogue fault, and not starvation of the audio
thread. From the elevated kernel trace (18 September,
`docs/earlier-20260918-kernel-trace-findings.md`):

- Audio thread ready-to-run delay: worst case 0.265 ms
- Audio processing pass: worst case 0.314 ms against a 4 ms budget
- All 999,611 decoded DPC/ISR executions under 0.5 ms
- Two bounded heavy read-only disk workloads did not reproduce any ring loss
  (`docs/earlier-20260921-disk-trigger-test.md`, `docs/earlier-20260922-overlapped-trigger-tests.md`)

## 5. Real-world recurrence, and automatic recovery

After the recovery mechanism was deployed, the ring collapsed on its own during normal playback and
was repaired without intervention. Record: `evidence/pool-history.csv`, with the same rows
extracted into `evidence/real-world-recovery-20261010.csv` (a subset, valid CSV, same header).

```
15:01:36   pool=10   ok
15:02:08   pool=1    indices=3   restart
15:02:20   pool=10   after-restart
```

The ring collapsed from a healthy 10 to a single descriptor within ~32 seconds of the previous
reading, and was back to 10 within ~12 seconds, transferring real payload again immediately. This
is the automatic recovery working on a **genuine** collapse, and it is the end-to-end validation of
the mechanism.

A separate, earlier test exercised the restart path deliberately by raising the watchdog threshold
so that a healthy pool (10 of 10) triggered it — see the `threshold=10` run in
`evidence/watchdog.log` and the disclosure in `docs/root-cause.md`. That run validated the path,
not detection.

## 6. Recovery mechanism

`tools/pool-watchdog.ps1` polls the driver's own provider using a bounded 2-second capture
(`tools/slot-probe.ps1`) and counts circulating output descriptors:

- Probe unavailable (not elevated, `logman` missing) → logged as such, no action.
- Pool not measurable because no stream is open → no action.
- Pool measurable and **≤ 2 descriptors** (silent or imminent) → restart the interface device, then
  re-probe and record the result.
- Healthy pools are never interrupted.

Two limits worth stating plainly:

- An audio-service restart was tested once and left the ring **unmeasurable** (no stream open), so
  it is not established whether that action rebuilds the ring. A device restart demonstrably does.
- Partially degraded rings (3–9 descriptors) are deliberately left alone, so **audible crackle in
  those states is not corrected** by this tool.

Installed as a logon task running elevated and hidden (`tools/install-pool-watchdog.ps1`). Every
decision is appended to `watchdog.log` plus a CSV history, so long-run degradation is measurable.
The shipped log spans two revisions of the script (the first line predates the `maxcycles` field).

## 7. What this is not

This is a mitigation, not a correction of the defect. The defect is in the driver's pairing and
resubmission logic. A correction requires, in that logic:

1. Guarantee a resubmission after every output completion, on every path.
2. Pair strictly by descriptor identity rather than by arrival order, or recover deterministically
   when the input/output index alignment slips.
3. Make the scheduling-margin check signed so a behind-time request is actually reported.

## 8. Repository layout

```
README.md
LICENSE
docs/
  root-cause.md                                  mechanism, evidence and mitigation
  earlier-20260918-kernel-trace-findings.md
  earlier-20260918-deadline-reports.md
  earlier-20260921-disk-trigger-test.md
  earlier-20260921-queue-finding.md
  earlier-20260922-overlapped-trigger-tests.md
  earlier-20260928-silent-state-timing.md
  earlier-20260928-recurrence.md
evidence/
  environment.txt                            machine, driver, transport and power-management record
  onset-capture-aggregate.txt                aggregate report of the onset capture
  onset-capture-run-log.txt                  capture lifecycle log (UTF-8)
  onset-capture-samples.csv                  host state sampled every 5 s during that capture
  onset-window-events.csv                    raw events across an 18 ms window containing the loss
  pool-timeline-onset-capture.txt            per-second descriptor count for that capture
  post-reset-healthy-aggregate.txt           post-restart healthy ring (10/10, 1152 bytes/transfer)
  post-reset-run-log.txt                     capture lifecycle log (UTF-8)
  collapsed-state-aggregate.txt              collapsed ring: original analyzer report
  collapsed-state-events.csv                 collapsed ring: all 120,258 raw event rows
  collapsed-state-probe-results.txt          measured 1-descriptor and 9-of-10-descriptor states
  negative-evidence-no-windows-events.txt    the fault produced no Windows audio/PnP event
  raw-captures-available-on-request.md       what binary traces exist and can be handed over
  real-world-recovery-20261010.csv           subset of pool-history.csv (valid CSV)
  pool-history.csv, watchdog.log             long-run monitoring record
  earlier-20260928-silent-state-queue.txt    earlier independent capture of the collapsed state
  earlier-20260928-usb-cycles.txt            Windows USB-side transfer/cadence analysis
  earlier-20260928-wdm-counters.txt          audio-client buffer counters
  earlier-20260928-windows-vs-device-signal.txt  Windows audio vs device loopback, same interval
tools/
  slot-probe.ps1                         bounded descriptor-count probe
  pool-watchdog.ps1                      recovery watchdog
  install-pool-watchdog.ps1              logon-task installer
  analyze-queue.ps1                      queue trace analyzer wrapper
  AnalyzeFocusriteUsbQueue.cs            analyzer source compiled by the wrapper
  query-trace-provider.ps1               read-only provider enumeration
  capture-playback-probe.ps1             render-endpoint playback stream recorder
  PlaybackProbe.cs                       recorder implementation used above
  get-audio-sessions.ps1                 session snapshot helper used above
  queue-timeline.py                      per-second descriptor-count timeline from a trace CSV
  dense-window.py                        chronological event dump for a time window
```

### Running the tools

Environment-specific values are parameters, not edits:

- `pool-watchdog.ps1` and `slot-probe.ps1` take `-Root` and `-DeviceId`; when `-DeviceId` is empty
  the composite device is resolved at runtime by matching `USB\VID_1235&PID_8218\*`.
- `slot-probe.ps1` takes `-Analyzer` (default: `analyze-queue.ps1` beside it).
- `pool-watchdog.ps1` takes `-ProbeScript` (default: `slot-probe.ps1` beside it).
- `analyze-queue.ps1` takes `-AnalysisRoot` (default
  `%LOCALAPPDATA%\FocusriteDiagnostics\AnalysisTools`) and requires four assemblies staged there:
  `System.Runtime.CompilerServices.Unsafe 6.1.2`, `System.Memory 4.5.5`, `System.Buffers 4.5.1`,
  `TraceEvent 3.2.6`. It targets Windows PowerShell 5.1.
- Capturing from the driver's provider and restarting a device both require an elevated session.
- Helper tools named in the older documents but not retained here (for example
  `Capture-FocusriteCounters.ps1`, `Test-UsbPoolModel.ps1`, `AnalyzeLoopbackPcm.cs`) are marked as
  such where they are mentioned.

## 9. Provenance, limits and notes on the data

- **One record is not a raw artifact.** `evidence/negative-evidence-no-windows-events.txt` is a
  written statement that the two capture windows produced no Windows audio or PnP event, since an
  empty file carries no information. Everything else is an artifact of a capture or of a scripted
  reduction: `onset-window-events.csv` and `collapsed-state-events.csv` are extracted raw event
  rows; the `*-aggregate.txt` files are analyzer reports; `pool-timeline-onset-capture.txt` is a
  per-second reduction produced by `tools/queue-timeline.py`.
- **The binary traces are held locally and can be provided.** They are not published because of
  size and because ETW binaries cannot be redacted (they embed local paths and process names).
  `evidence/raw-captures-available-on-request.md` lists every capture with its window, size and
  contents.
- **One column is unpopulated.** In `evidence/onset-capture-samples.csv` the `default_peak` column is
  empty in every row (that sampler step was not implemented); the file's useful content is the
  timestamp, audio-host process identity, CPU time and device status.
- **The transfer-flags export was not retained.** The earlier pass produced a `-flags` CSV claim,
  but the file retained here is identical to its non-flags sibling and contains no flag field, so no
  transfer-flag claim is made. `TransferFlags=0` (ASAP not set) is quoted from the driver's own
  queue events instead.
- **Detection requires an open stream.** With no application holding the playback endpoint the ring
  is not measurable and no assessment is made. Worst case for a fault that begins with silence is
  one polling interval (30 s while a stream is open, 60 s otherwise) plus the ~8 s device restart.
- **Not everything can be recomputed here.** The aggregates were produced from traces that are not
  in the repository, so the figures can be followed in detail but not re-derived from scratch. A
  ~7.9 GB intermediate XML export from an earlier pass was deleted after analysis and can be
  regenerated from the retained trace if it is ever wanted.
- Recorded audio was deliberately excluded.
- Personal identifiers were redacted in file contents: user paths appear as `C:\Users\<user>` and
  the device serial as `<serial>`. Driver module names, provider control GUIDs, hashes and the
  VID:PID are retained because they are the identifiers needed to locate the defect. The two
  `deadbeef`/`feedfacebeef` control GUIDs read as magic values but are the driver's own. Commit
  metadata carries the publishing account, which is inherent to how the repository is hosted.
- Timestamps are local time (UTC+03:00).

## 10. Limitations

- **Liability** — none accepted. This material is published for information only. No responsibility
  is taken for any loss, damage or consequence arising from its use, from any measurement or script
  in it, or from acting on its conclusions.
- **Warranty** — none given. Everything here is provided "as is", without warranty of any kind,
  express or implied, including but not limited to the implied warranties of merchantability,
  fitness for a particular purpose and non-infringement. The measurements describe one machine at
  one point in time and are not a specification; see section 9 for what can and cannot be
  re-derived from this repository.
