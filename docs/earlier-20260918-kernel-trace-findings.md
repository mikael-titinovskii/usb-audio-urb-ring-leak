# Scarlett crackling: elevated kernel-trace findings

**The recording does not identify an offending driver.** It contains audio-pump missed-deadline reports, but no large individual DPC/ISR execution time, no long observed ready-to-run delay for the audio pump, and no audio processing pass exceeding its stated budget. This makes a classic large DPC/ISR or audio-CPU scheduling bottleneck less likely during this capture. It does not exclude a Focusrite/USB buffering or device-timing issue, application-specific behavior, or a problem farther along the playback path.

The user's audible crackles started about 1-2 seconds into recording, recurred at 3-4 and 6-7 seconds, and then roughly every 5-10 seconds. The logged missed-deadline reports do **not** reproduce that timing: their first cluster begins about 46 seconds into this trace. They cannot be treated as a complete explanation or count of the audible crackles.

## Recording and decoding

- Input: `scarlett-kernel-20260918-123322.etl`.
- Start: 18 September 2026, 12:33:23.016, local time (UTC+03:00).
- End: 12:36:23.801; approximately 180.8 seconds.
- Decoded using Microsoft's TraceEvent 3.2.6 library, downloaded into the local diagnostic folder. No system software was installed.
- Zero ETW events lost; zero unhandled Microsoft-Windows-Audio events reported by the reader.
- 614,854 DPC events, 384,757 ISR events, 4,244,248 context switches, and 198,287 audio-provider events decoded.
- All decoded interrupt durations were nonnegative. Six initial interrupt durations were independently cross-checked against native tracerpt raw timestamps/header clock data and matched exactly.
- The older custom parser treated tracerpt's converted InitialTime values as QPC ticks, producing invalid all-zero durations. Its report was not used for the present conclusions.

## Driver interrupt measurements

All 999,611 decoded DPC/ISR executions were shorter than 0.5 ms. These maxima are observed durations, not universal pass/fail thresholds. ISR nesting can be included in DPC elapsed time, so totals must not be interpreted as non-overlapping CPU busy time.

| Routine's module | Type | Maximum observed |
|---|---|---:|
| Wdf01000.sys (Windows Driver Framework) | DPC | 0.4606 ms |
| dxgkrnl.sys (graphics kernel) | ISR | 0.4370 ms |
| amdkmdag.sys (AMD graphics) | DPC | 0.3002 ms |
| ntoskrnl.exe | DPC | 0.2878 ms |
| winhvr.sys | DPC | 0.2661 ms |
| portcls.sys (audio class driver) | DPC | 0.0503 ms |
| ndis.sys (network driver interface) | DPC | 0.0369 ms |

Wdf01000.sys is a framework wrapper; its module name alone cannot identify a particular device's underlying driver. The profile did not capture driver callback stacks, so it cannot attribute that wrapper's work specifically to Focusrite.

## Audio thread and processing measurements

- Audio pump: `audiodg.exe`, PID 16668, TID 11700.
- 18,045 observed ready/preempted-to-running intervals for that thread; maximum 0.2650 ms, 99th percentile 0.0226 ms. None exceeded 1 ms.
- A second busy audiodg thread, TID 21856, had maximum observed ready-to-run delay 0.1799 ms.
- Maximum reported audio processing pass: 0.3142 ms; reported budget: 4 ms. No processing pass exceeded that budget.
- Largest observed ready-to-ready gap for the pump was about 11.61 ms. This metric includes intentional waits and is not by itself a stall measurement.
- Among the observed Spotify process threads, the largest ready-to-run delay was 1.3322 ms at about +26.8 s. It was not coincident with an audio missed-deadline cluster. The trace alone does not identify that thread's application role.

Ready-to-run measurements describe how long an already-ready thread waited for CPU time. They do not measure USB transfer completion, driver/device buffering, time a thread remained blocked, or analog output quality.

## Audio deadline reports

There were 98 event 118 reports, grouped below when successive events are at most one second apart. Cluster spans are not measurements of audible crackle duration.

| Local time | Relative time | Reports | First-to-last span |
|---|---:|---:|---:|
| 12:34:09.368-12:34:09.568 | +46.35 s | 21 | 0.200 s |
| 12:34:42.263-12:34:42.883 | +79.25 s | 60 | 0.620 s |
| 12:36:09.952-12:36:10.112 | +166.94 s | 17 | 0.160 s |

No interrupt longer than 1 ms was observed within these clusters or the 20 ms preceding them. The largest nearby individual interrupt durations were approximately 0.176, 0.226, and 0.174 ms respectively, across all CPUs.

The first report followed an audio position/timing correction (event 135, StartCorrection -193460 in the trace) while the audio processing work itself remained short. This is consistent with the deadline bookkeeping involving playback-position timing, but is insufficient to diagnose a Focusrite clock or driver bug.

No audio event 33 (application-input starvation), event 48 (kernel render starvation), or event 65 (device-state change) occurred in this capture. No audio-host restart is indicated. Absence of these events does not rule out a driver/device issue that the enabled providers do not report.

## Next controlled test (updated after user feedback)

The user's screenshots confirm 128 samples, 48 kHz, Safe Mode enabled, USB Internal clock synced. The 128-sample buffer was retained by choice; no larger-buffer comparison was performed. Preserve that constraint for further diagnostics. No device settings were changed during this investigation.

Use `capture-playback-probe.ps1` with Spotify playing. This records 90 seconds of the Focusrite Windows playback stream locally, its packet flags/positions/timestamps, and SPACE-key markers for audible crackles. It does not open a microphone, change the ASIO buffer, change endpoint formats, or reset the audio driver. No elevation is required. Press Q to stop early.

The capture's own 100 ms read queue is separate from the Focusrite ASIO buffer. Capture discontinuities can originate in the observer and are not automatically audible playback failures. First-packet discontinuity flags are listed separately. The WAV concatenates received packets; `packets.tsv` maps file positions to source timestamps so missing data and silent playback gaps are not mistaken for continuous time.

The recorder compiled and passed an integration check with a silent two-second PCM stream: 117120 frames, 244 packets, zero capture discontinuities, no error. This validates data capture, not crackle detection. An earlier five-second check collected no frames because Spotify was no longer active.

The Focusrite statistics log was refreshed at 12:55:53 and reports zero playback and record errors. Focusrite Control 2 logs show version 1.1108 starting at 12:55:57 and version 1.1139 starting at 12:56:48; this is contextual information, not evidence of an audio driver restart or a cause.

Further evidence: `playback-probe-validation-20260918-130536/validation.json`. API reference: https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer

## Evidence and reproducibility

- `kernel-analysis-20260918-123322/summary.json`: capture metadata and event counts.
- `kernel-analysis-20260918-123322/findings.json`: driver timing aggregates, deadline clusters, and scheduling statistics.
- `kernel-analysis-20260918-123322/decoder-validation.json`: independent duration checks.
- `kernel-analysis-20260918-123322/interrupts.tsv`, `images.tsv`, `threads.tsv`, `switches.tsv`, `ready.tsv`: extracted kernel data.
- `kernel-analysis-20260918-123322/audio.jsonl`: selected audio events and timing/processing payloads.
- `trace-reader/read-trace.ps1`, `ReadTrace.cs`, `analyze-kernel.py`: local analysis helpers. The reader's audio/Spotify scheduling filter targets the PIDs in this capture and needs updating for a different process lifetime.

The original ETL is retained. The approximately 7.9 GB intermediate raw XML export was removed after independent validation; it can be regenerated from the ETL.
