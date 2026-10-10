# Progression to silence: one output request and a persistently past USB start frame

28 September 2026, UTC+03:00. This supplements `earlier-20260928-recurrence.md` and a technical support report (not retained in this repository). **The user clarified that all sound was gone, not that normal music had recovered.** No output switch or audio-service/device reset was performed during these captures.

## Observed progression

| Interval | Output / input request identities | Output scheduling margin | Audio evidence |
| --- | --- | --- | --- |
| 10:55:33-10:56:03 | 9 / 10 | +25 | User reports crackle; slot 7 absent |
| 10:57:20-10:58:05 | 9 / 10 | +25 | Simultaneous 3 ms stale fragment per 27 ms; Windows silent during mute |
| 11:02:10-11:02:30 | 1 / 10 | -53, once -54 | User subsequently confirms all sound is gone; only original slot 2 remains |
| 11:05:13-11:06:13 | 1 / 10 | -53 throughout | Windows USB trace: outgoing requests return zero transferred bytes |
| 11:06:12-11:06:20 | Audio capture; overlaps preceding queue trace for about 1.5 seconds | Not independently traced for the remainder | Windows records music; Scarlett loopback records all zeros |

Margin is the driver's logged 32-bit expression **StartFrame - CurrentFrame + 1**, not buffer size and not the microframe count. Thus -53 means the requested USB StartFrame is 54 frame numbers behind the sampled current frame. Requests explicitly use TransferFlags=0 (ASAP not enabled). The scheduling problem is observed directly; it is not inferred solely from silence.

**Important gap:** the transition from nine to one happened between captures, after 10:58:05 and before 11:02:10. Its callback order, exact time, and initiating condition were not recorded. The diagnostic input stream had already closed at 10:57:44, and the subsequent trace still showed nine through 10:58:05. This excludes an immediate queue reset/loss at that stream closure, not every possible indirect timing effect of diagnostics or other activity.

## Current silent-state measurements

Internal trace `UsbQueue-SilentState-20260928-110513\FocusriteUsbQueue.etl`:

- 60.136-second declared session; selected events 1.2235-60,131.6953 ms. Matching binary PDB identity; zero lost events.
- 20,044 completions and submissions per direction, all reporting successful status.
- Input: all ten indices, margin +28 on every submission.
- Output: **only index 2**, margin **-53 on all 20,044 submissions**.
- Same-output-index completion interval 2.6176-3.3924 ms, but logged submission/completion elapsed field only 6-284 microseconds. These are different quantities: the descriptor waits for an input pair before being resubmitted, then completes almost immediately.
- All 20,044 render-prepare events read zero from the preceding URB's TransferBufferLength.

Independent `USB-20260928-110513.etl`, 11:05:13.612427-11:05:28.785388:

- Positively identified Scarlett `1235:8218`; zero lost events/decode failures.
- Input: 5,052 completed transfers, ten identities, 2,304 bytes each, normal 30-frame identity cycle.
- Output: 5,052 submitted/completed transfers, **one identity**, request buffer `0xffffa87d67c02580` (original slot 2).
- Every output submission requests **1,152 bytes**, TransferFlags **0**, and every completion reports **0 bytes**. Completion URB status and reported packet-error count remain zero. This is the host-reported result, not a hardware USB-analyzer measurement.
- The same output request's StartFrame advances by three per reuse. The internal margin trace shows that advancing it does not catch up with current USB time; it stays behind.

Microsoft documents TransferBufferLength as the requested buffer size on submission and the number of transferred bytes on return, and StartFrame as the requested start unless ASAP is selected. See [URB_ISOCH_TRANSFER](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/usb/ns-usb-_urb_isoch_transfer). The observation here is therefore more informative than a success-status count alone.

## Music present in Windows, zero in Scarlett

Prefix `Device-loopback-20260928-110612`, eight-second input-only ASIO Loopback 1/2 plus simultaneous Windows render-loopback. **No mute operation was performed for this test.**

- Windows endpoint unmuted, scalar 0.5400 / -9.3428 dB. Spotify PID 39464 active, unmuted, session volume 100%. Scarlett remains the default console/multimedia output. These queries did not change volume.
- Windows float32 recording: 383,520 stereo frames, all 767,040 samples nonzero; RMS -26.592363 dBFS, peak -11.691089 dBFS.
- Scarlett PCM32 recording: **384,768 stereo frames, every sample exactly zero**. The interior 288,000-frame analysis is also entirely zero.
- ASIO initial/final rate 48 kHz, preferred buffer 128; zero reset/resync/rate-change callbacks; no callback error; stop/dispose ASE_OK. Only digital loopback channels were opened, no microphone or ASIO outputs.

This is a failed playback path, not a Spotify mute left behind by the earlier test. That earlier temporary session mute was restored and verified at 10:57:40.062908.

## Code-level recovery blind spot

The installed `FocusriteUsb.sys` remains 4.150.0.432 with the previously verified SHA256. Assembly for submission function RVA `0x9e20` computes:

```text
RVA 0x9ea5: EDI = URB.StartFrame
RVA 0x9eab: EDI -= sampled current USB frame
RVA 0x9eae: EDI += 1
RVA 0x9eb0: CMP EDI, 10
RVA 0x9eb3: JNC skip_margin_warning   // unsigned comparison
```

The live signed margin -53 is unsigned 4,294,967,243, so it **bypasses** this margin-below-ten warning. That explains why no margin warning accompanied thousands of requests scheduled in the past. The branch only controls a diagnostic warning: changing the comparison alone is **not** a playback repair and does not explain initial request loss. No driver binary was edited.

The inspected submission path advances an output frame cursor without correcting the observed late schedule in these intervals. Completion pairing still has the previously identified single pending-output slot. Driver analysis needs to examine both **why requests cease circulating** and **why the surviving stream is allowed to continue behind USB time with zero-byte completions**. Pending-pointer overwrite remains a candidate, not a captured onset fact.

## Analyzer corrections and reproducibility

- `AnalyzeFocusriteUsbQueue.cs` now preserves both submitted/current frame numbers and asserts their wrapped arithmetic equals the logged margin. It reports negative margins explicitly, independent of the driver's warning.
- Its former `URB_reuse_us` label was too broad: the binary writes that timer at submission as well as completion. Corrected reports call it `logged_completion_elapsed_us` and separately compute actual same-index completion intervals from event timestamps. Counts, missing indices and the independently measured USB frame periods were not affected by the old label.
- New reports use `.usb-queue-timing.txt/.csv`; original reports and raw ETLs were retained.
- Windows USB analysis now retains full transfer flags in `.usb-cycles-flags.csv`; original reports were retained.
- `AnalyzeLoopbackPcm.cs` (helper not retained in this repository) now checks ordinary/extensible PCM32 and float32 RIFF data for signal statistics. The exact Windows/Scarlett comparison is in `Device-loopback-20260928-110612-signal-comparison.txt`.
- `Test-UsbPoolModel.ps1` (helper not retained in this repository) passes the original six-slot case, the independently predicted nine-slot case, and the signed/unsigned warning example. These remain offline models, not a live driver fix.

## Status and solution boundary

Evidence now establishes a persistent **host playback queue/scheduling failure** much more specifically than “audio crackling.” The source of the first lost descriptor and a verified permanent repair are still unresolved. A fixed driver must maintain/recover its request pool and valid future scheduling, and demonstrate clean output at 48 kHz / 128 samples. Restarting the stream would only mask the defective state.

All temporary capture helpers completed and their owned traces stopped; both Focusrite provider enable counts returned to zero. No background recorder is intentionally left running. No audio configuration was changed, no microphone was recorded, and nothing was uploaded. The user-reported silent state was preserved, not reset. Raw evidence is local under `%LOCALAPPDATA%\FocusriteDiagnostics`.
