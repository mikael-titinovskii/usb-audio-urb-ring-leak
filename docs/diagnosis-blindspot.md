# The diagnosis blind spot: the watchdog was starving every capture

## Finding (2026-10-18 02:35, A/B verified)

The recovery watchdog (`ScarlettAudioWatchdog`) probes the driver's ETW queue provider with its own
short `logman` session every ~8 s. **Two concurrent sessions on that provider do not coexist** — the
long capture is starved to almost nothing.

Controlled A/B, identical capture parameters (`-max 512 -bs 64 -nb 16 64`), 45 s each:

| Condition | coverage_ms first .. last | declared_session_ms | captured | ETL |
|---|---|---|---|---|
| watchdog **ON**  | 2.9 .. **146.9** | 45,032 | **0.3 %** | 524 KB |
| watchdog **OFF** | 1.5 .. **45,029** | 45,033 | **100 %** | 10.8 MB |

A ~300× difference in recorded events.

## What this invalidates

Every long capture taken while the watchdog was running recorded only milliseconds to a few seconds:

| run | declared | actually captured |
|---|---|---|
| `OnsetCatch-012325` (12 min) | 720 s | ~1.6 MB, unknown but small |
| `StallForce-020511` (11 min) | 686 s | **0.79 s** |
| `UsbRead-022644` (5.5 min) | 330 s | **1.5 s** |
| agent-10 `quiet-1` / `baseline-A` / `wslio-1` / `cover-1` | 2-6 min | >500 ms holes throughout |

**All "0 stalls / 0 orphans" conclusions drawn from those runs are void.** They were not negative
results — they were *no data*. This is why nothing ever reproduced and why the 5–50 ms stall was
never observed outside the single 10 Oct reference trace (which was captured before the watchdog
existed, and has full 1204 s coverage).

## Consequence for method

- **Pause the watchdog before any capture.** `Stop-ScheduledTask -TaskName ScarlettAudioWatchdog`,
  capture, then `Start-ScheduledTask` to restore mitigation.
- **Always verify coverage** before interpreting a trace: read `Selected event coverage_ms` vs
  `declared_session_ms` in the `.usb-queue-*.txt`. A valid run has coverage ≈ declared. The analyzer
  prints an explicit WARNING when it is short — it was being ignored.
- Any future harness must fail loudly when coverage < ~95 %.

## Was further code analysis worth it?

**No — not for reproduction.** The reproduction gap was never missing code knowledge: the chain is
fully established (see the note at the end of this file). The gap was that the instrument was
recording 0.3 % of the window. With capture fixed, the existing hypothesis is directly testable:

> force a 5–50 ms completion stall → margin 28→25 → `advance==+2` orphan

Further code analysis would only help for *mitigation* work (a lighter health probe than the ETW
scan, or a cheaper ring-rebuild than a PnP device restart) — not for reproduction.

---

## Note on driver-internals material

Analysis of the driver binary (call chain, pseudocode, instruction-level detail) and the
consolidated report prepared for the vendor are **not published in this repository**. They are
available on request to Focusrite engineering, or to others with a legitimate interest, subject to
the licence terms shipped with the driver. The material here is limited to behaviour observed on
the wire and from the host side.
