# Spotify crackling: confirmed live timing reports

**Later elevated-trace result:** No large DPC/ISR execution time or audio ready-to-run delay was found while the user heard frequent crackles. The deadline events did not match the first reported crackles. See [earlier-20260918-kernel-trace-findings.md](earlier-20260918-kernel-trace-findings.md) for the following findings and limits; the initial deadline reports below do not establish the root cause.

During Spotify playback on 18 September 2026, Windows Audio recorded **222 event 118 reports, "Pump: missed deadline!"**. The user confirmed hearing crackling during this capture. This supports investigating intermittent audio timing/scheduling trouble; it does not identify an offending driver or establish a one-to-one match between each logged event and an audible crackle.

There is no evidence here of a full Scarlett restart. The Windows audio host (`audiodg.exe`, PID 16668) remained running from 16 September at 12:00:02. The capture contained no audio-device state-change event (65).

## Capture

- Window: 18 September, 12:20:44-12:25:46, local time (UTC+03:00).
- Spotify desktop (`Spotify.exe`, PID 28148) was the only active audio client in all ten session snapshots, sampled about 30 seconds apart.
- Spotify's playback session used the Focusrite/Scarlett endpoint directly. Sonar was not in that playback route.
- 332,550 total ETW events; zero collection-buffer events lost. The 35,454,976-byte file remained below the 128 MiB circular limit, so this capture retained the whole window.
- All diagnostic recording sessions were stopped. No playback settings, services, drivers, or devices were changed/reset.

## Missed-deadline clusters

Events are grouped when consecutive reports are at most one second apart. A cluster's span is the time between its first and last reports, **not a measurement of audible crackle duration**.

| Local time | Reports | First-to-last span |
|---|---:|---:|
| 12:22:53.693-12:22:53.903 | 20 | 0.211 s |
| 12:23:07.023-12:23:07.203 | 19 | 0.180 s |
| 12:23:23.283-12:23:23.432 | 16 | 0.150 s |
| 12:24:10.223-12:24:12.202 | 140 | 1.980 s |
| 12:24:18.343-12:24:18.433 | 2 | 0.090 s |
| 12:24:20.103-12:24:20.303 | 2 | 0.200 s |
| 12:24:25.273 | 1 | Single event |
| 12:24:29.532-12:24:30.152 | 22 | 0.620 s |

The reports were emitted by the Windows audio engine, not by a Spotify crash handler. No event 33 input-starvation report occurred in this Spotify trace. The Firefox starvation events in the earlier capture are separate and should not be blamed for Spotify's symptoms.

The user confirmed crackling somewhere during this window, without an exact timestamp. Individual clusters therefore remain uncorrelated with a user-marked audible burst. A missed-deadline report is evidence of a timing condition, not proof that each event produced audible corruption.

## Root cause still to establish

The power-management workarounds were intact, and both previously disabled AMD HDMI-audio devices remained disabled. The available trace cannot distinguish a Focusrite/USB-driver problem from interference by another driver or Windows/app scheduling.

A kernel DPC/ISR and thread-scheduling trace is the next discriminating diagnostic. Windows denied the earlier attempt with 0x80070005 because this session is not elevated. A three-minute diagnostic script is ready:

`C:\Users\<user>\audio-diag\capture-scarlett-live.ps1`

Its PowerShell syntax and `scarlett-live.wprp` profile were validated. It has not been run elevated. It records diagnostic events only; it does not record sound, change audio settings, or restart audio.

## Evidence

- `spotify-audio-20260918-122044.etl` — original trace.
- `spotify-audio-20260918-122044-summary.txt` — native event counts and loss report.
- `spotify-analysis-20260918.json` — event counts, all 222 deadline records, cluster boundaries.
- `spotify-deadline-verified-20260918.json` — first event verified directly through Get-WinEvent.
- `spotify-sessions-20260918-122044.jsonl` — active audio-session snapshots.
- `spotify-capture-20260918-122044.txt` — capture lifecycle.

tracerpt writes an incorrect `+02:59` suffix on this host. Local clock times were checked with Get-WinEvent, which correctly reports `+03:00`. JSON `time_exported` fields retain the original export strings; use the verified timestamps/report for interpretation. The exporter warned of schema mismatches for some event types; the missed-deadline payload was verified independently. The large intermediate XML was removed after extraction; the original ETL is retained.
