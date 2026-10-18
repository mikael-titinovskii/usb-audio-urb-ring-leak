# Scarlett recurrence: nine playback requests and a 27 ms stale-audio cycle

28 September 2026, local time UTC+03:00. The user reported the same crackle returning after several days. No reset, output switch, driver change or buffer increase was performed during the following capture.

## Main result

**Later progression:** the user then reported complete silence. Follow-up captures found only one output request left, scheduled 54 USB frames behind current time and completing with zero transferred bytes. Windows still recorded music while Scarlett loopback recorded zeros. This was not recovery. Measurements and the uncaptured transition interval are summarised below; static-analysis detail is available on request.

**A second natural episode reproduces the reduced playback-request pool, with a different missing subset and the corresponding different audio period.** This time only original output slot 7 is absent: nine requests circulate, not ten. Input retains all ten.

Both the Focusrite driver's internal WPP events and Windows' independent USB transfer events confirm this. Simultaneous Scarlett loopback contains a bit-identical 144-frame (3 ms) stale fragment once per 1,296 frames (27 ms). The other 1,152 frames become zero when Spotify is muted, while Windows render-loopback is entirely zero. This closes the earlier timing limitation: the faulty USB queue and stale audio were captured at the same time.

| State | Active playback slots | Request cycle | Recorded stale/fresh pattern |
| --- | --- | --- | --- |
| 21 September fault | 1,2,3,4,6,7 | 6 x 3 ms = 18 ms | 432 stale / 432 live frames |
| 21-22 September controls | All ten | 10 x 3 ms = 30 ms | Cleared-state mute recorded exact silence |
| 28 September recurrence | All except 7 | 9 x 3 ms = 27 ms | 144 stale / 1,152 live frames |

The unchanged conditional refill model from the previous investigation predicted the new 144/1,152 pattern before the new PCM was analyzed. It matches exactly. This strengthens the missing-request/fixed-ring-refill mechanism; it does **not** prove the live refill lead was 40 microframes, identify the initial loss event, or uniquely establish the pending-pointer-overwrite candidate.

## Internal queue captures

1. `UsbQueue-Recurrence-20260928-105532`: 10:55:33.159794-10:56:03.666212. Before opening any new audio stream, 10,167 successful completions/submissions per direction. Input slots 0-9; output slots 0-6,8,9. Output reuse approximately 27 ms. Zero lost events, failed statuses, or warning/error/margin events. Selected event coverage 2.784-30,501.5302 ms of the 30,506.4182 ms session.
2. `UsbQueue-RecurrenceLoopback-20260928-105719`: 10:57:20.476791-10:58:05.622510. Spans ASIO open, playback mute, unmute and ASIO close. **15,048 successful completions/submissions per direction**, input ten/output nine throughout. 15,048 pair events, 13,536 unequal input/output indices, consistent with the differing ring lengths. Zero lost events or failed statuses. Selected events cover essentially the full 45.146 seconds.

Internal decoding matches the expected binary PDB identity. Installed `FocusriteUsb.sys` remains 4.150.0.432, SHA256 `498F4644C737C829FDD8853F9BE79A4FBF65C812B623818DF611587779135B80`, unchanged from the previous investigation.

## Independent Windows USB trace

`USB-20260928-105720.etl`, 10:57:20.638724-10:57:35.826787.

- Positively identifies Scarlett VID:PID `1235:8218` from descriptor rundown.
- 5,056 completed transfers per direction; zero bad statuses, packet errors, lost events or decode failures.
- Input: ten distinct requests and buffers, 30-frame reuse cycle.
- Output: **nine distinct requests and buffers, 27-frame reuse cycle**. Each contains 1,152 bytes / 144 stereo frames over 24 microframes, or 3 ms.
- Allocation base remains `0xffffa87d67c00000` with stride `0x12c0`; original slot 7 is absent. The analyzer's printed sorted *active-buffer* indices are not original ten-slot indices.
- No PCM transfer payload was captured in this ETL.

## Simultaneous digital audio

Prefix: `Device-loopback-20260928-105720`.

- Input-only ASIO Loopback 1/2, native inputs 3/4, zero ASIO outputs. No microphone channels. Current and final sample rate 48,000 Hz, preferred buffer 128. No setting change, reset/resync request, sample-rate callback or callback error.
- ASIO recording 10:57:21.024334-10:57:44.078309; 1,106,432 stereo frames / 8,644 callbacks. Maximum callback gap 3.4263 ms; stop/dispose returned ASE_OK.
- Spotify PID 39464 muted at 10:57:25.061406 and restored at 10:57:40.062908, original volume 1.0000 and mute=false verified. Its playback session remained active.
- Windows muted render-loopback: **576,000 stereo frames, all 4,608,000 PCM data bytes zero**, with no discontinuity or timestamp-error flags.
- Scarlett recording seconds 6-18: **smallest exact period 1,296 frames**, zero mismatches in **574,704** lag comparisons. Exactly **144 nonzero frames per period** followed circularly by 1,152 zeros. RMS -36.932887 dBFS, peak -21.883259 dBFS.
- The nonzero fragment remains identical outside mute: 10,656/10,656 tested stale-template frames match both before and after mute. The remaining positions follow changing music rather than matching silence.

## Windows-side ring during the fault

`WdmCounters-RecurrenceLoopback-20260928-105719`, 10:57:20.594586-10:58:05.672716.

- 32,289 decoded buffer events; matching SwRoot PDB identity; zero lost events.
- Queued frames 4,416-5,527, with **no negative or zero values**, in a 9,600-frame ring.
- Zero cumulative/interleaved accounting mismatches. Pointer-minus-queued offset stays constant at 336 frames; this alone is not an error.
- Consumer chunks 144 frames; maximum consumer-event gap 3.6872 ms.

Thus the persistent stale pattern exists while the inspected Windows-side queue remains supplied and internally consistent. A temporary problem before capture is not excluded. The older hypothetical negative-queued-count mechanism was not present during this faulty interval.

## What this establishes, and what remains unresolved

The captured corruption is digital and already present before speakers/headphones. A persistent reduced USB output pool and fixed-ring refill behavior are a much stronger explanation than ordinary continuing application underruns or analogue noise. The recurrence has a different missing subset, which the same model predicts without retuning.

The original transition into the nine-request state was not recorded. Successful surviving transfers do not explain why slot 7 was lost. Pending-output overwrite, a prior failed completion/re-submission, or another initiating condition still need onset evidence or vendor instrumentation. No specific permanent fix has been verified.

The useful technical action is to investigate request retention/re-submission and refill selection when the active set shrinks, including the single pending-output pointer previously identified in `FocusriteUsb.sys`. A vendor diagnostic/fixed driver should demonstrate that all ten requests remain/recover correctly and stale segments no longer recur at 48 kHz / 128 samples. Merely restarting the stream would not establish that fix.

On 28 September the [official release-notes page](https://support.focusrite.com/hc/en-gb/articles/13070702714130-Focusrite-Windows-Driver-Release-Notes) still lists 4.150.0 as its newest entry and does not identify a fix for this captured mechanism. This is not proof that no private diagnostic build exists. No installer was downloaded or executed, no signed driver was patched, and no report/data was sent externally.

All evidence is under `%LOCALAPPDATA%\FocusriteDiagnostics`. Playback recordings and ETL files may contain private audio, paths and process metadata; review before sharing. The temporary recording streams and trace helpers finished normally, Spotify was restored, and both Focusrite provider enable counts returned to zero. The later silence report was confirmed as continued failure, not recovery, in the linked follow-up report.
