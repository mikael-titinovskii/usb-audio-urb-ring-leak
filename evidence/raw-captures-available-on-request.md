# Raw captures: what is published, and what can be handed over on request

## Published: the 10 October captures

Release page: https://github.com/mikael-titinovskii/usb-audio-urb-ring-leak/releases/tag/traces-2026-10-10

| Asset | Window (local time, UTC+03:00) | Size | Contents |
|---|---|---|---|
| [onset-capture-2026-10-10-queue.etl](https://github.com/mikael-titinovskii/usb-audio-urb-ring-leak/releases/download/traces-2026-10-10/onset-capture-2026-10-10-queue.etl) | 14:30:54 – 14:50:58 | 263 MB | **The onset**: the output ring loses descriptor index 1 at 14:47:53 and stays at 9 descriptors for the rest of the capture |
| [post-restart-healthy-2026-10-10-queue.etl](https://github.com/mikael-titinovskii/usb-audio-urb-ring-leak/releases/download/traces-2026-10-10/post-restart-healthy-2026-10-10-queue.etl) | 14:22:16 – 14:30:19 | 103 MB | Healthy ring after a device restart: 10 of 10 descriptors, 1152 bytes per transfer |
| [collapsed-state-2026-10-10-queue.etl](https://github.com/mikael-titinovskii/usb-audio-urb-ring-leak/releases/download/traces-2026-10-10/collapsed-state-2026-10-10-queue.etl) | 14:20:03 – 14:21:03 | 16.7 MB | The collapsed ring: one descriptor only, index 8, margin −185, zero bytes transferred. Its analyzer report and full per-event CSV are also in `evidence/` |

These are unredacted binary ETW files. They contain local file paths and process names from the
machine that recorded them; that is why the earlier captures below are not published.

## Available on request

| File | Date | Size | Contains |
|---|---|---|---|
| `USB-20260928-110513.etl` | 2026-09-28 | 17 MB | Independent capture of the collapsed state (single descriptor, index 2, margin −53) |
| `USB-20260928-105720.etl` | 2026-09-28 | 18 MB | The 9-descriptor recurrence (slot 7 absent, 27 ms period) |
| `UsbQueue-SilentState-20260928-110513\FocusriteUsbQueue.etl` | 2026-09-28 | 17 MB | Same interval from the driver's own provider |
| `UsbQueue-RecurrenceLoopback-20260928-105719\FocusriteUsbQueue.etl` | 2026-09-28 | 10 MB | Driver queue spanning a stream open, mute, unmute and close |
| `USB-20260921-214453.etl`, `USB-20260921-232049.etl` | 2026-09-21 | 17 MB each | Six-descriptor fault state, and a 30-second read-only disk-trigger test |
| `DiskTrigger-*`, `OverlappedTrigger-*` `\*.etl` | 2026-09-21/22 | 42–85 MB each | Four bounded disk-load tests; no ring loss reproduced |
| `20260921-212342\Audio.etl` | 2026-09-21 | 1.4 GB | Kernel trace of the six-descriptor fault (DPC/ISR, disk, audio scheduling) |
| `scarlett-kernel-20260918-123322.etl` | 2026-09-18 | 335 MB | Elevated kernel trace during audible crackle |
| `live-audio-20260918-120948.etl` | 2026-09-18 | 128 MB | Audio-provider capture, client starvation reports |
| `spotify-audio-20260918-122044.etl` | 2026-09-18 | 34 MB | Audio-provider capture, 222 missed-deadline reports |

## Decoding them

The queue provider is a WPP-style provider with no vendor TMF or PDB available to this
investigation, so the event field layout was reconstructed from the module's own assembly.
`tools/analyze-queue.ps1` with `tools/AnalyzeFocusriteUsbQueue.cs` perform that reconstruction and
emit a per-event CSV; `tools/queue-timeline.py` and `tools/dense-window.py` reduce it to the
descriptor-count timeline and to chronological windows.

Anyone with the driver's own symbols can decode these traces natively and will see more than an
external observer can — in particular the internal state of the request pool at the moment a
completed output is not resubmitted, which is the step this investigation could not observe from
outside the driver.
