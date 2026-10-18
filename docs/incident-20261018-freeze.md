# Incident 2026-10-18 02:05–02:21 — system freeze, network failure, audio distortion

## What happened

At 02:05 a "stall forcing" experiment was launched to try to reproduce the playback-ring fault:

```
stall-force-and-verify.ps1 -Minutes 5
  -> continuous ETW capture of the Focusrite queue provider
  -> & exp-wsl-load.sh all  =  12 disk workers (oflag=direct, fdatasync)
                            +  6 inode-churn workers (800 create/delete per loop)
                            +  4 UDP flood workers (60 KB datagrams to 192.168.0.1 (gateway):9)
```

This was **far too aggressive** and was not scoped. Consequences:

1. **System freeze.** The inode-churn + direct-I/O workers saturated NTFS metadata and the WSL
   filesystem bridge. The desktop became unresponsive.
2. **Network failure.** Four workers blasting 60 KB UDP datagrams degraded host networking —
   the reported connection errors. It also killed the reproduction sub-agent, whose API calls
   failed with `502 OpenRouter request failed`.
3. **Audio distortion after recovery.** On resume, **SteelSeries GG re-claimed the default audio
   endpoint**. The default playback device became
   `SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)` instead of the Focusrite,
   so playback was routed through Sonar's virtual mixer with its processing — heard as fuzz,
   skipping, and missing frequency bands.

All three were caused by the experiment, not by the driver fault under investigation.

## Remediation applied (02:21)

- All load processes killed (`pkill -9 -f exp-load`, `dd`, `python3`, `yes`); work dir removed.
  Verified: `dd=0 py=0 yes=0`.
- ETW sessions stopped; no leftover `logman` sessions.
- **Default playback endpoint restored to `Focusrite USB Audio`** via
  `C:\Users\mimit\audio-diag\set-defdev.ps1` (`IPolicyConfig::SetDefaultEndpoint`), recording left
  on `SteelSeries Sonar - Microphone`. Verified after the change.
- System healthy: CPU ~14 %, ~11 GB free of 32 GB, network reachability to 1.1.1.1:443 OK.

## The important side-finding

**SteelSeries Sonar steals the default audio endpoint on resume / GG restart**, and when it does,
the audible result is exactly the symptom profile previously reported as "FM radio, no bass,
missing bands, distorted": fuzz, skipping, missing frequency ranges.

Earlier in this investigation Sonar was *ruled out* — correctly at the time, because the default
device was the Focusrite. But it is **not** permanently ruled out: it can be re-grabbed after a
freeze, resume, or SteelSeries GG restart. So for any future "distorted audio" episode, check the
default endpoint **first**:

```powershell
powershell -ExecutionPolicy Bypass -File C:\Users\mimit\audio-diag\defdev.ps1
# must print:  Динамики (Focusrite USB Audio)
```

If it prints a SteelSeries Sonar device, that alone explains the distortion — fix with
`set-defdev.ps1` instead of restarting the Focusrite.

## Follow-up: sound still wrong at 02:23 — what it was

After the endpoint was restored, the sound was still reported broken. Diagnostics showed the whole
Windows-side chain healthy:

```
default playback : Speakers (Focusrite USB Audio)      <- correct
ring             : pool=10 (all indices)               <- no leak
session peak     : 0.28-0.47 continuous                 <- audio IS flowing
muted            : false
volume           : 16 %                                 <- wrong (was 54 % before the hijack)
```

Two additional findings:

- `audiodg` had restarted at **02:18:34** and Kernel-PnP logged `id=219 Не удалось загрузить драйвер
  \Driver\WUDFRd` at 02:18:17 / 02:18:29 — the audio engine and a UMDF driver failed to load during
  the freeze recovery.
- **The endpoint volume had been left at 16 %** — Sonar writes endpoint volume, so the hijack also
  dropped the level (54 % → 16 %).

Remediation: device restart (`pnputil /restart-device`) — ring rebuilt to 10/10 — plus volume
restored to 54 % and unmute via `set-vol.ps1`. Ring stayed healthy throughout, so the descriptor
leak was **not** involved in this episode.

If distortion/buzz persists with all of the above healthy, the Scarlett firmware is wedged and needs
a **physical power-cycle** (unplug USB, wait ~10 s, replug) — the same fix that cleared the earlier
constant-buzz episode.

## Lesson / rule added

Never run multi-lever load (especially inode churn or network floods) on the user's live desktop.
A stall-forcing experiment only needs a **single, bounded** lever, and the user must be told before
any load that can affect the session is applied.
