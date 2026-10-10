# Strongest lead: reduced playback USB request pool

**28 September update:** a second episode had nine outputs and ten inputs, with simultaneous exact 3 ms stale / 24 ms live audio per 27 ms cycle, matching the unchanged model. It subsequently progressed between captures to only output slot 2, scheduled 54 USB frames in the past and returning zero transfer bytes; Windows music continued but Scarlett loopback was silent. See `earlier-20260928-recurrence.md` and `earlier-20260928-silent-state-timing.md`. The unsigned margin-warning guard at RVA `0x9eb3` bypasses the observed negative margin. This is a verified diagnostic/recovery lead, not proof of the initiating pending-pointer overwrite or a completed fix. No audio reset/settings change was performed.

Updated 21 September 2026, approximately 23:35, UTC+03:00. No audio settings were changed during this investigation. This is evidence and a testable driver mechanism, not a claim that a permanent fix has been implemented.

## Measured difference

Both USB traces positively identify the Scarlett Solo 4th Gen by descriptor VID:PID `1235:8218`. Analysis uses Microsoft USB event schemas, request pointers, transfer-buffer addresses and USB frame numbers, not audio impressions alone.

| Measurement | Persistent-fault interval, 21:44:53-21:45:08 | Post-recovery control, 23:20:49-23:21:04 |
| --- | ---: | ---: |
| Playback requests completed | 5,046 | 5,044 |
| Distinct circulating playback requests / data buffers | **6 / 6** | **10 / 10** |
| Playback-buffer identity cycle | **6 transfers / 18 USB frames** | **10 transfers / 30 USB frames** |
| Distinct circulating capture requests / buffers | 10 / 10 | 10 / 10 |
| Capture-buffer identity cycle | 10 transfers / 30 USB frames | 10 transfers / 30 USB frames |
| Lost trace events / decode failures | 0 / 0 | 0 / 0 |
| Reported transfer or packet errors | 0 | 0 |

Each outgoing request contains 1,152 bytes, stereo 32-bit samples, 144 audio frames and 24 USB microframe packets: **3 ms at 48 kHz**. Incoming requests carry 2,304 bytes for four channels over the same duration.

The faulty six-request cycle is exactly **864 audio frames / 18 ms**, matching the smallest exact period of the stale audio previously recorded from Scarlett Loopback 1/2. The output buffer allocation base and slot stride match between the two USB captures: base `0xffffa87d67c00000`, stride `0x12c0`. Original slots **1,2,3,4,6,7** circulate in the fault interval; **0,5,8,9** are absent. The control uses all slots 0-9. The first report's `buffer-index sequence` numbers are sorted *active-buffer* indices, not the original ten-slot indices.

Timing limitation: the fault USB trace is earlier than the 22:36 fault audio capture, not simultaneous. The user repeatedly reported the fault continuing and kept it available for diagnosis until the approved output switch at 22:41:50. The user confirmed recovery after that switch and again reported clear audio around 23:10. Audibility was not re-asked during the later metadata-only controls. This limits exact onset attribution, but the request-count difference is directly measured.

## Specific driver mechanism to investigate

Read-only Ghidra inspection of `FocusriteUsb.sys` 4.150.0.432, SHA256 `498F4644C737C829FDD8853F9BE79A4FBF65C812B623818DF611587779135B80`. Addresses below are RVAs from the image base, not live kernel addresses. Selected control flow was checked against assembly.

- RVA `0x8fd0` initializes ten linked descriptors, with next/previous pointers forming a fixed ten-element ring. RVA `0x9800` initially submits all ten.
- Completion routine RVA `0x8a50` pairs input and output using two single global pointer slots at RVAs `0x272f8` and `0x27300`. Input completions resubmit their own request immediately. Output requests are resubmitted only when an input/output pair is available.
- At RVA **`0x8ca0`**, a successful output completion stores its descriptor into the single pending-output slot **without checking whether it already holds a different output completion**. The pair check follows at `0x8ca7`; output re-submission is at `0x8d0c`; the two pending slots are cleared at `0x8d90` and `0x8d97`.
- Thus, in the inspected routine, a sequential `OUT(A), OUT(B), IN(C)` completion order starting with no pending input replaces A with B before either output has been paired. Only B is subsequently resubmitted by this path. A lock around pairing does not by itself prevent this sequential-order case. **That ordering was not captured at fault onset**, and system-wide invariants could affect reachability. This is a concrete candidate defect, not a verified live reproduction.
- A separate negative-completion-status branch bypasses normal re-submission and calls RVA `0xdc20`, which logs request details rather than restoring the request. A failed re-submission can also reduce pending work. The available fault trace starts too late to distinguish these mechanisms; its surviving requests all complete successfully.
- Output refill selection at RVA `0x87f0` traverses the original linked ring using `floor(stream[+0x630] / stream[+0x62c]) + 1` steps plus a packet offset. Traversal at RVA `0xdac0` follows links without testing whether those descriptors remain in circulation. RVA `0xf130` clears/mixes the selected output segments. A reduced active subset can therefore leave some transmitted buffers stale while refilling inactive slots.

This combination explains why ordinary successful USB status checks can miss a persistent driver queue fault, why input can continue normally, and why restarting the playback stream can rebuild the pool.

### Conditional arithmetic model, not observed PCM transfers

Read-only registry query returned `Safeties = 01-00-08-00-10-00-20-00-00-00-00-00-00-00` under `HKLM\SYSTEM\CurrentControlSet\Services\FocusriteUSB\Parameters`: selected index 1, output table entry 16 microframes. Static setup adds `device[+0x370] * 8` if that field exceeds 2. **That live kernel field and the resulting refill lead were not read.**

If that field is 3, matching the observed request duration, the modeled lead is 40 microframes. Applying the actual refill formula to the measured active slots produces exactly one circular **432-frame stale block and 432-frame fresh block per 864 frames**. The standalone `Test-UsbPoolModel.ps1` (helper not retained in this repository) asserts this arithmetic and illustrates the pending-slot overwrite case. It accesses no hardware. Other hypotheses are not excluded by a matching model.

## Internal-driver control trace, 23:30:30-23:30:51

A bounded 20-second WPP trace used USB control GUID `deadbeef-dead-beef-0005-feedfacebeef`, flags `0x100401`, level 5. No pre-existing session was enabled; this session was stopped and provider enables returned to zero. Expected USB PDB identity `84fa3647-78d6-49c4-b63e-44df0202fe42`, age 1, matched ETL metadata.

Assembly-derived decoding found 6,786 input and 6,786 output completions, corresponding successful submissions, all ten indices, 6,786 matching input/output pairs, and zero warning/error/margin events. Completion-entry directions alternate throughout; no events were lost. This validates a healthy internal-queue baseline and a decoder for a future onset trace. Completion-entry logging precedes the pairing lock, so it is not itself a direct observation of the pending pointer writes.

The WDM-only control at 23:14:46-23:15:07 decoded 16,832 events with queued frames 4,720-5,117 and a 9,600-frame ring. No negative counts or accounting changes were observed. A constant 45-frame pointer/count offset was present throughout; the corrected `.counters-v2.txt` reports it without mislabeling every event a new failure. The earlier negative-count hypothesis is not supported by this healthy interval and is secondary to the directly measured USB pool difference.

## Evidence and next technical test

**Further follow-up, 22 September 00:09-00:15:** four 30-second overlapped-read stages, queue limits four/eight, 35.9375 GiB total, again retained all ten indices. The complete second attempt has 31,944 successful completions/submissions per direction and matching pairs, with zero lost events/failures. An offline completion-to-pair check found at most one logged unpaired output, no duplicate pending completion and no leftover output. This is event-log accounting, not direct pending-pointer instrumentation. Despite actual overlapping 2 MiB requests and 80 MiB completing within 50 ms, storage DPCs stayed below 0.483 ms. The first raw file hit its cap; live monitoring continued, and the full repeat removed the raw-coverage limitation. See `earlier-20260922-overlapped-trigger-tests.md`. The proposed overwrite condition remains unproven; no audio settings changed and no monitor remains running.

**Follow-up at 23:50-23:51:** the approved bounded disk-load test did not reproduce pool loss; all ten playback indices remained active before, during and after the 30-second read workload. Storage DPC delays stayed below 0.242 ms, not the earlier 6.84 ms. The trace includes harmless adjacent OUT entry events where the preceding output was already paired; those are not the hypothesized pending-output overwrite condition. See `earlier-20260921-disk-trigger-test.md` for the negative reproduction result and limitations.

All raw data remains under `C:\Users\<user>\AppData\Local\FocusriteDiagnostics`:

- `USB-20260921-214453.etl`, `.usb-cycles.csv`, `.usb-cycles.txt`: persistent fault.
- `USB-20260921-232049.etl`, `.usb-cycles.csv`, `.usb-cycles.txt`: post-recovery control.
- `UsbQueue-Baseline-20260921-233800\FocusriteUsbQueue.etl`, `.usb-queue.csv`, `.usb-queue.txt`, `.event-inventory.txt`, `capture-status.txt`. The folder's RunId is just a label; actual time is **23:30**, as recorded in ETL/logs.
- `Counters-Baseline-20260921-231500\FocusriteCounters.etl`, `.counters-v2.txt`, `.counters-v2.csv`, `capture-status.txt`.
- Fault and control audio prefixes: `Device-loopback-20260921-223641` and `Device-loopback-20260921-224200`.
- `ReverseEngineering\Logs\Usb-queue-targets-pass1.txt`: selected assembly; `FocusriteUsb.sys-static-pass1-decompiled.c`: offline pseudocode.

The most useful next capture is the **transition from ten circulating playback requests to fewer**, including completion order, statuses and re-submissions. The single-pending-output pairing behaviour, and the recovery path when a descriptor stops circulating, should be checked rather than merely raising the ASIO buffer. A vendor diagnostic build with explicit pending-pointer/drop logging would distinguish overwrite from USB completion/send errors.

`Capture-FocusriteCounters.ps1 -Mode UsbQueue -Seconds 20 -State Fault` (helper not retained in this repository) can take a bounded metadata-only snapshot from an elevated PowerShell when needed; the default mode remains WDM counters. `Analyze-FocusriteUsbQueue.ps1 -TracePath <saved-etl>` (helper not retained in this repository) decodes its selected queue events. Do not start another session for the same classic provider while one is already active. No automatic background capture has been left running.

No signed binary was edited, no microphone was recorded by the new traces, no full memory dump was taken, and 128 samples / 48 kHz were not changed. The permanent fix and the first triggering event remain unresolved. Nothing has been submitted to Focusrite.
