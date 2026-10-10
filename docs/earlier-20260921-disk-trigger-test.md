# Bounded disk-trigger test: did not reproduce the reduced USB pool

21 September 2026, UTC+03:00. The proposed 30-second read-only disk workload with driver tracing was approved. One test was run; it was not repeated or escalated.

## Outcome

All ten playback and ten input request indices remained active in every one-second trace window, including after the workload stopped. There were no failed completion/send statuses or warning/margin events, and no lost events in either trace. **The previously observed reduced playback pool was not reproduced.** This is an objective queue-state result, not a claim that the speakers were monitored or established the absence of every audible click.

| Actual phase | Input completions | Output completions | Playback indices |
| --- | ---: | ---: | --- |
| Before disk reads | 3,443 | 3,443 | All 0-9 |
| During disk reads | 10,001 | 10,001 | All 0-9 |
| After disk reads | 5,033 | 5,033 | All 0-9 |

Totals: 18,477 completions and corresponding successful submissions per direction, 18,477 matching input/output pairs. Output request reuse stayed approximately 29.696-30.303 ms; the previous faulty state reused its six output requests every 18 ms.

## Procedure and bounds

- Internal USB WPP trace: **23:50:43.6587-23:51:39.0924**. Control GUID `deadbeef-dead-beef-0005-feedfacebeef`, flags `0x100401`, level 5; expected binary PDB identity matched.
- Baseline began 23:50:43.6627; reader launched after ten seconds.
- Actual disk-read interval: **23:50:53.9863-23:51:23.9901**, 30.0033 seconds.
- Quiet recovery interval: 23:51:24.0326-23:51:39.0416.
- A separate memory-buffered Windows kernel trace captured disk I/O, DPC/interrupt timing and process/image attribution. No audio samples, microphone inputs, call stacks or memory dump were recorded.
- Workload: one normal-priority synchronous reader, 128 KiB requests, 16 MiB bursts on a 125 ms schedule, at most 30 seconds and a 4 GiB outer cap. No CPU affinity or process-priority changes. The parent imposed a 36-second worker-process timeout, which was not reached.
- Source: the existing local diagnostic file `20260921-212342\Audio.etl`, opened with **GENERIC_READ / OPEN_EXISTING / FILE_FLAG_NO_BUFFERING**, never with write access. Physical sector size was verified as 4096 bytes; the buffer and requests were aligned accordingly. No personal-folder scan, new stress-data file, external download or network workload was used.
- Reader PID 17276 reported **4,026,531,840 bytes (3.75 GiB)** across 30,720 reads. Source length and last-write timestamp were unchanged. Maximum individual read-call duration was 1.8144 ms; this is not the same measurement as DPC duration.
- Disk ETW attributed 4,026,630,144 read bytes to the reader process, including small PowerShell/startup reads. Its 4096-byte write was diagnostic output; the source data workload was read-only. Other applications and trace logging continued their usual I/O.
- Spotify was active and unmuted through Focusrite before and after. Console/multimedia defaults stayed Focusrite and communications stayed Sonar Chat. The existing audio-host PID stayed 15968. No output switch, service/device reset, sample-rate change, buffer change or driver modification was performed; 128 samples / 48 kHz were left unchanged.

## Why this does not settle the trigger

The largest measured storage-driver DPC was **0.2416 ms**, much shorter than the earlier **6.8389 ms** observed while the original background search was running. The largest DPC from any measured routine was 0.8774 ms in `Wdf01000.sys`; the underlying WDF client is not identified by that routine name alone. No measured DPC/ISR reached 1 ms in this interval.

This test produced real disk I/O but did **not** recreate the earlier long scheduling delays. It used bounded sequential reads of one file, not the original multi-file search. Therefore, it neither establishes disk activity as a trigger nor rules out storage-related stalls as one possible trigger. The earlier search trace itself began after the fault had started, so even its longer delays were never proven to initiate the persistent fault.

The USB trace also recorded six adjacent same-direction completion-entry pairs, from three harmless reversals of IN/OUT arrival order. Each involved a completed pair followed by the next OUT arriving a few microseconds before its own IN; all pairs and submissions remained intact. **Seeing OUT, OUT in the event stream alone is not proof of pending-pointer overwrite.** The proposed overwrite requires the first output still to be unpaired when the next output arrives, and entry logging occurs before the pairing lock. That condition was not demonstrated here.

## Files and cleanup

Evidence folder: `C:\Users\<user>\AppData\Local\FocusriteDiagnostics\DiskTrigger-20260921-235042`.

- `FocusriteUsbQueue.etl`, `.usb-queue.txt`, `.usb-queue.csv`: internal queue events, including per-second index checks.
- `DiskDPC.etl`, `.analysis.txt`: disk/DPC timing. The generic analyzer retains historical fixed-time inspection sections intended for the earlier trace; those particular windows are not phase markers for this test. Use `phases.csv` and the reader's actual timestamps instead.
- `read-workload.json`, `phases.csv`, `test-status.txt`, `audio-before.txt`, `audio-after.txt`.

The reader exited normally. Both diagnostic sessions stopped, USB-provider enables returned to zero, and WPR reported that it was not recording. Saving the kernel trace finished at 23:51:50. No background test or monitor remains running. No audio-setting rollback is needed. Nothing was submitted to Focusrite.

Implementation: `Test-FocusriteDiskTrigger.ps1` (helper not retained in this repository), `Run-ReadOnlyDiskBurst.ps1`, `ReadOnlyDiskBurst.cs`, `Disk-DPC.wprp`. Sources and removal notes are in the desktop diagnostics folder. The scripts do not install services or scheduled tasks. Microsoft documents the [unbuffered file-I/O alignment requirements](https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering); local WPR schema validation required the keyword `DiskIOInit` before the test was started.
