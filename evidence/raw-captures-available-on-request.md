# Raw captures: what exists, and what can be handed over

The aggregates, the extracted raw window and one full per-event CSV are in this repository. The
original binary traces are held locally and **not published**, for two reasons: size, and because
ETW binary files cannot be redacted — they embed the local file paths and process names of the
machine that recorded them.

If you want any of them, say so and they can be provided.

## Driver queue captures (the driver's own tracing)

Provider control GUID `{deadbeef-dead-beef-0005-feedfacebeef}`, keywords `0x100401`, level 5.
These contain per-URB `Submit` / `Complete` / `Pair` / `PrepareOutput` events with descriptor
index, direction, status, `StartFrame` and `CurrentFrame`. No audio content.

| File | Window (local, UTC+03:00) | Size | Contains |
|---|---|---|---|
| `Watch-20261010-1432\queue.etl` | 2026-10-10 14:30:54 – 14:50:58 | 263 MB | **The onset**: ring loses descriptor index 1 at 14:47:53 and stays at 9 for the rest of the capture |
| `Onset-20261010-1440\queue.etl` | 2026-10-10 14:22:16 – 14:30:19 | 103 MB | Healthy ring after a device restart: 10 of 10 descriptors, 1152 bytes per transfer |
| `CurrentState-20261010-1432\FocusriteUsbQueue.etl` | 2026-10-10 14:20:03 – 14:21:03 | 16.7 MB | The collapsed ring: one descriptor (index 8), margin −185, zero bytes transferred. Its analyzer report and full per-event CSV are already in `evidence/` |

## Earlier passes

| File | Date | Size | Contains |
|---|---|---|---|
| `USB-20260928-110513.etl` | 2026-09-28 | 17 MB | Independent capture of the collapsed state (single descriptor, index 2, margin −53) |
| `USB-20260928-105720.etl` | 2026-09-28 | 18 MB | The 9-descriptor recurrence (slot 7 absent, 27 ms period) |
| `UsbQueue-SilentState-20260928-110513\FocusriteUsbQueue.etl` | 2026-09-28 | 17 MB | Same interval from the driver's own provider |
| `UsbQueue-RecurrenceLoopback-20260928-105719\FocusriteUsbQueue.etl` | 2026-09-28 | 10 MB | Driver queue spanning a stream open, mute, unmute and close |
| `USB-20260921-214453.etl`, `USB-20260921-232049.etl` | 2026-09-21 | 17 MB, 17 MB | Six-descriptor fault state and a 30-second read-only disk-trigger test |
| `DiskTrigger-*`, `OverlappedTrigger-*` `\*.etl` | 2026-09-21/22 | 42–85 MB each | Four bounded disk-load tests; no ring loss reproduced |
| `20260921-212342\Audio.etl` | 2026-09-21 | 1.4 GB | Kernel trace of the six-descriptor fault (DPC/ISR, disk, audio scheduling) |
| `scarlett-kernel-20260918-123322.etl` | 2026-09-18 | 335 MB | Elevated kernel trace during audible crackle |
| `live-audio-20260918-120948.etl` | 2026-09-18 | 128 MB | Audio-provider capture, client starvation reports |
| `spotify-audio-20260918-122044.etl` | 2026-09-18 | 34 MB | Audio-provider capture, 222 missed-deadline reports |

## Decoding them

The queue provider is a WPP-style provider with no vendor TMF or PDB available to us, so the event
field layout was reconstructed from the module's own assembly. `tools/analyze-queue.ps1` plus
`tools/AnalyzeFocusriteUsbQueue.cs` do that reconstruction and emit the per-event CSV;
`tools/queue-timeline.py` and `tools/dense-window.py` reduce it to the descriptor-count timeline and
to chronological windows.

Anyone with the driver's own symbols can decode these traces natively and will see more than we
could — in particular the internal state of the request pool at the moment a completion is not
resubmitted, which is the step this investigation could not observe from outside the driver.
