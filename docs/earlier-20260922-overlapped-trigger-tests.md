# Overlapped disk-burst trigger attempts, 22 September 2026

## Result

**The missing-playback-buffer fault was not reproduced.** Four bounded 30-second stages read a total of 35.9375 GiB from an existing diagnostic file. Each attempt used up to four outstanding 2 MiB reads, a quiet interval, then up to eight outstanding reads. The real-time queue monitor retained all ten input and output buffer indices throughout both attempts. The complete raw trace from the second attempt independently confirms this result.

This is an objective queue-state result, not a claim that listening or PCM recording confirmed perfect sound throughout. No fresh listening confirmation or audio recording was requested during these tests. The original fault remains unresolved.

All times below are local, UTC+03:00.

## Workload and safeguards

- Existing source: `%LOCALAPPDATA%\FocusriteDiagnostics\20260921-212342\Audio.etl`, 1,463,812,096 bytes. It was opened read-only, using unbuffered overlapped I/O. No personal-folder content search was run.
- Each stage issued 4,600 reads of 2 MiB: 115 bursts of 80 MiB, with a nominal 250 ms burst period. All requests returned pending/asynchronous; none completed synchronously at submission. Each stage read 8.984375 GiB.
- Four/eight is the application request limit. Kernel requests can be split; kernel overlap counts are not interchangeable with this limit.
- Each stage was capped at 30 seconds and 10 GiB, with a parent-process deadline. A metadata-only live USB monitor was armed to stop the workload if it detected sustained output-pool reduction, failed statuses, or a monitoring problem. No alarm occurred.
- Each attempt included a ten-second baseline, ten seconds between stages, and fifteen seconds of final quiet. Workloads used normal priority and no CPU affinity changes.
- No audio-service restart, output switch, mute, driver update, buffer/sample-rate change, microphone recording, full memory dump, or permanent system change was performed. The 128-sample / 48 kHz settings were left unchanged.

| Attempt / queue limit | Actual read interval | Bytes read | Stop reason |
| --- | --- | ---: | --- |
| First / 4 | 00:09:36.197-00:10:06.204 | 9,646,899,200 | Duration complete |
| First / 8 | 00:10:16.587-00:10:46.600 | 9,646,899,200 | Duration complete |
| Repeat / 4 | 00:13:24.188-00:13:54.190 | 9,646,899,200 | Duration complete |
| Repeat / 8 | 00:14:04.545-00:14:34.559 | 9,646,899,200 | Duration complete |

Source length and last-write UTC ticks were unchanged before/after every stage: `639256119384329859`. Diagnostic reports/traces themselves create separate disk writes; the workload source was not modified.

## Complete second attempt: USB evidence

Folder: `%LOCALAPPDATA%\FocusriteDiagnostics\OverlappedTrigger-20260922-001313`.

- Session interval: 00:13:13.823552-00:14:49.713454, approximately 95.89 seconds.
- Raw USB file: 89,325,568 bytes, below the revised 256 MiB cap. Selected events span 3.4935-95,834.8384 ms relative to session start, covering both workloads and the final quiet interval.
- Expected matching-binary PDB identity confirmed; 191,664 selected decoded rows; zero lost events.
- **31,944 completions and 31,944 submissions in each direction**, all successful.
- **All ten buffer indices in every one-second window**, including the final partial window.
- **31,944 input/output pair events**, zero unequal paired indices; no warning/error/margin events.
- Output request reuse: 29.716-30.255 ms, consistent with the ten-request cycle rather than the faulty six-request / 18 ms cycle.
- A chronological offline check of OUT completion and pair events found at most one logged unpaired output, zero duplicate completions while pending, zero pairs without a preceding output, and none left unpaired at the end.

There were 86 adjacent same-direction completion-entry events. These do not establish pending-pointer overwrite: the preceding output can already have paired. Entry-event ordering and the offline pending set are not direct measurements of the live pointer writes inside the pairing lock. The hypothesized loss condition was not observed.

## Storage timing: similar bursts, not the original long stall

Both kernel timing traces reported zero lost events. Maximum `storport.sys` DPC duration was **0.4829 ms** in the first attempt and **0.2155 ms** in the repeat, compared with **6.8389 ms** in the earlier faulty-state trace. Neither new attempt had a DPC/ISR at or above 1 ms.

The complete repeat's kernel disk events independently confirmed substantial overlapping reads:

| Worker | Kernel read operations | Exactly 2 MiB operations | Peak reported interval overlap | Most read data completed within 50 ms |
| --- | ---: | ---: | ---: | ---: |
| Q4, PID 29260 | 5,213 | 3,988 | 7 | 80 MiB |
| Q8, PID 30480 | 5,212 | 3,988 | 11 | 80 MiB |

The extra operations and overlap above four/eight reflect kernel request splitting and small process-startup reads, not a failure of the application's queue limit. Reported disk-response duration is not DPC execution time. The earlier search completed approximately 77.8 MiB in a 50 ms window near its largest storage DPC. The new tests therefore approached that burst volume/read size without recreating its long scheduling delay.

**Interpretation:** heavy reads of this shape alone are a weaker reproduction lead now. This does not rule out a rare storage/interrupt scheduling stall, a different completion ordering, or a separate trigger. The earlier trace began after the fault already existed, so the original search has never been established as its cause.

## First attempt's recording limitation

Folder: `%LOCALAPPDATA%\FocusriteDiagnostics\OverlappedTrigger-20260922-000925`.

The first raw WPP file reached its initial 64 MiB limit. Selected events stop at 67,681.3955 ms although the declared session continued for 95,914.4291 ms. **Zero reported lost events does not make this a complete raw recording.** A final unmatched boundary event in that truncated file must not be treated as request-pool loss.

The independently consumed real-time stream and `live-queue.csv` continued through the entire first attempt, with all ten indices and zero reported lost events. The kernel timing trace also completed. To remove the raw-file coverage limitation, the cap was raised to 256 MiB and both stages were repeated; the complete repeat is the primary raw evidence above. Original files were preserved. The USB analyzer now explicitly reports actual event coverage and warns about a long gap before declared session end.

## Evidence, cleanup, and next useful step

Each attempt folder contains `read-Q4.json`, `read-Q8.json`, `phases.csv`, `test-status.txt`, `live-queue.csv`, monitor status files, audio-state snapshots, `FocusriteUsbQueue.etl` plus queue analyses, and `DiskDPC.etl` plus timing analysis. The first attempt's corrected coverage report is `FocusriteUsbQueue.usb-queue-coverage.txt`.

Both controllers, all four disk workers, and both live monitors exited. Both owned USB sessions stopped successfully; provider enable counts returned to zero. Both helpers verified WPR was no longer recording. Default playback/session snapshots were preserved without alteration. No background test, installed service, scheduled task, or startup entry was left behind. Nothing was uploaded or deleted.

The next useful evidence is **the first transition from ten circulating output requests to fewer during a real recurrence**, with completion order, statuses, and pair/submission events immediately around it. That calls for a bounded onset capture or a vendor diagnostic build with explicit pending-pointer/drop logging, rather than assuming more disk bandwidth will trigger it. The new monitor is saved as source; it is not running automatically, and its bounded test runner is not an unattended long-term recorder.
