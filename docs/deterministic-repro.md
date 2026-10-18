# Deterministic reproduction recipe (pinned-core DPC starvation)

Derived from a live fault on 2026-10-18 03:47:11 where the audio ring collapsed while the machine
was globally healthy — the starvation was confined to the audio core.

## Why previous attempts failed

Every earlier load experiment spread work over 16 cores, so it essentially never collided with the
core servicing the audio DPC. Machine-wide metrics (mean DPC load, LatencyMon conclusions) reported
"healthy" while the audio core was starved 12-28 ms. Load alone was never the right variable —
**collision on one core** is.

## CORRECTIONS (2026-10-18, after measuring the whole trace)

Two claims above are too strong, and the recipe below should be read with them in mind.

1. **The audio DPC is NOT pinned to one core.** Across the captured trace `portcls.sys` DPCs land
   on `cpu2: 4016, cpu10: 1023, cpu0: 524, cpu7: 391`. So pinning load to a single "audio core"
   is not a reliable lever — the audio DPC moves between at least four cores. To make collision
   certain you would first have to pin the **xHCI interrupt affinity** so all audio DPCs land on one
   core, and only then pin load there. That step (interrupt affinity) is the missing piece.

2. **Per-core stalls >=10 ms are routine, not rare.** In that same 22-second trace there are
   **231** busy-bracketed stalls >=10 ms on cpu2 — about ten per second. If a stall alone caused an
   orphan, the ring would collapse constantly; it does not (faults arrive every ~15 min). So
   starvation is a **necessary but not sufficient** condition: the completion-order flip must
   coincide with it. That is consistent with the reference trace (53 flips, 1 orphan).

Also note: the r11 trace actually spans only 03:47:09.129 .. 03:47:31.367 (22 s), so it contains
almost no pre-fault data. Earlier "no gaps before the fault" statements drawn from it refer to a
window the trace barely covers, and are therefore weak.

## Evidence

At the fault, on the audio core (cpu2; `portcls.sys` runs there):

```
27.61 ms  ntoskrnl  -> portcls     at 03:47:09.445
27.00 ms  portcls   -> portcls     at 03:47:09.176
16.02 ms  ntoskrnl  -> ntoskrnl    at 03:47:09.535
12.37 ms  storport  -> ntoskrnl    at 03:47:09.148
17.25 ms  tcpip     -> portcls     at 03:47:10.086
15.00 ms  portcls   -> portcls     at 03:47:10.514
26.99 ms  portcls   -> portcls     at 03:47:11.711
```

Ring collapse detected 03:47:11. On cpu0 (graphics) the largest gaps are ~4 ms and occur later.
So the competing DPCs are **storage (`storport`)** and **network (`tcpip`)** on the audio core.

## Recipe

1. **Identify the audio core.** Capture DPC/ISR for ~30 s and take the processor where
   `portcls.sys` DPCs run most (observed: cpu2).
   `wpr -start lean-dpc.wprp!LeanDpc -filemode` ... `wpr -stop x.etl` ... `tracerpt x.etl -o x.xml -of XML -y`
   then `python tools/audio-core.py x.xml` (or the module/cores listing).

2. **Pause the recovery watchdog** so a collapse persists long enough to observe:
   `Stop-ScheduledTask -TaskName ScarlettAudioWatchdog`

3. **Pin network RX DPCs to the audio core** (verified settable and restorable):
   ```powershell
   Set-NetAdapterRss -Name 'vEthernet (FSE HostVnic)' -BaseProcessorNumber 2 -MaxProcessors 1
   ```
   Record the prior values first (`BaseProcessorNumber`, `MaxProcessors`) and restore afterwards.
   This is reversible and does not touch the physical/internet NIC.

4. **Drive bounded traffic** through that adapter (small packets, high rate) — the point is a long
   DPC *queue* on one core, not bandwidth.

5. **Watch for the collapse**: probe the ring every ~5 s
   (`slot-probe.ps1`, needs admin). Success = pool drops below 10.

6. **Restore everything**: `Set-NetAdapterRss` back to the recorded values, restart the device
   (`pnputil /restart-device USB\VID_1235&PID_8218\S1Q8V6Q5A46AF9`), then
   `Start-ScheduledTask -TaskName ScarlettAudioWatchdog`.

## Acceptance criteria

- ring pool drops from 10 (collapse), and
- the queue trace shows `OUT->OUT` completions with **no `Pair` between** (`tools/orphan-signature.py`), and
- on the audio core, gaps of >= 10 ms appear just before the collapse.

## Feasibility audit of the control levers (2026-10-18)

Survey of what this machine actually allows, in order of preference:

| Lever | Status |
|---|---|
| **RSS on a NIC that has an IP** | **Blocked.** The only interface with an IPv4 address is `Ethernet 2` (Intel I226-V, 192.168.0.x) and it exposes **no** RSS control. The two RSS-capable adapters (`vEthernet (FSE HostVnic)*`) have **no IP address**, so traffic cannot be targeted at them. RSS set/restore itself works (verified: Base 0/MaxProc 8 -> 2/1 -> restored). |
| **xHCI interrupt affinity** | **Supported but risky.** `Interrupt Management\Affinity Policy` exists on the AMD xHCI but is empty (floating); `Affinity Policy - Temporal` shows `TargetSet = 65535` (all 16 cores). Pinning would need `DevicePolicy=4` + `AssignmentSetOverride`, then a device restart — which resets **every** USB device on that controller, potentially including keyboard/mouse. |
| **NIC interrupt affinity** | Possible, lower risk than xHCI: same registry mechanism on the Intel I226-V, needs a NIC restart (a few seconds of internet loss), reversible. |
| **Kernel debugger breakpoint** | Deterministic, but needs a second machine (KDNET) or a VM with USB passthrough. |

Also measured: per-core stalls >=10 ms on cpu2 already occur ~10x/second (231 in a 22 s trace), so
simply adding more stalls does not obviously increase the orphan rate. The orphan additionally needs
the completion-order flip to coincide, which is why the fault stays rare.

**Net:** controlled reproduction on this box requires either a NIC-affinity change plus a bounded
flood, or the xHCI-affinity change (with the input-device risk), or a kernel debugger. Statistical
reproduction is already reliable (~1 fault per 15-20 min, captured repeatedly with matching
predictions).

## TESTED 2026-10-18 ~04:00 — both affinity levers are IGNORED by Windows (negative result)

**(a) NIC interrupt affinity — applied, no effect.**
Set on the Intel I226-V (`PCI\VEN_8086&DEV_125C...`):

```
DevicePolicy          = 4          (IrqPolicySpecifiedProcessors)
AssignmentSetOverride = 4,0,0,0,0,0,0,0   -> mask bit 2 = cpu2
adapter restarted, status Up
```

Measured afterwards (DPC capture with a bounded ping, 64,704 DPC/ISR):

```
ndis.sys   418 events   cpu0:418                    <- still cpu0, not cpu2
tcpip.sys  251 events   cpu6:45 cpu2:37 cpu3:34 cpu8:25 ...   <- still spread
```

**(b) xHCI interrupt affinity — applied, no effect.**
Set on the Scarlett's controller (`PCI\VEN_1022&DEV_15B7...`) with `AssignmentSetOverride = 1,0,...`
(bit 0 = cpu0), controller restarted, Scarlett re-enumerated, audio healthy. Measured:

```
portcls.sys  4071 events  cpu3:1390 cpu11:786 cpu10:781 cpu2:413 cpu7:214 cpu1:199
```

Still spread over six processors — the override was not honoured. Over a 10-minute window with the
pin in place there was **1** fault, at 04:00:56, i.e. 21 s after the controller restart (startup
churn); none afterwards, which is inside the ~15-20 min baseline. No rate increase.

Both were reverted (0 affinity values remaining) and audio verified healthy.

### (c) TEMPORAL affinity set — also ignored, and this explains why

`Affinity Policy - Temporal\TargetSet` was set from `65535` (all cores) to `1` (cpu0) and the
controller restarted. Result: `portcls.sys` still spread across
`cpu3:1418, cpu10:819, cpu11:763, cpu2:377, cpu7:243, cpu1:184`.

**The reason all three levers fail is structural:** the audio completion DPC reported as
`portcls.sys` is a **software DPC queued by the audio stack**, not the hardware interrupt of the
xHCI controller. It is therefore *not* bound to the controller's interrupt affinity at all — it runs
on whatever core the queuing context scheduled it to. That is exactly why the "audio core" drifts
between captures (cpu2-dominant at 03:47, cpu3-dominant at 04:10).

**Consequence: no interrupt-affinity setting can pin the audio DPC. The "pin the load to the audio
core" approach is not implementable.** The only deterministic route left is to hold the DPC itself —
i.e. a kernel debugger breakpoint in the driver's completion routine.

### Conclusions from the two failures

1. `DevicePolicy` + `AssignmentSetOverride` are **not applied without a reboot** on this system, and
   may not be honoured at all for these devices (the xHCI reports `Routing Info: StaticVector=42`,
   i.e. a line-based vector, which Windows affinity-manages via the separate
   `Affinity Policy - Temporal\TargetSet`, not `DevicePolicy`).
2. **The audio DPC's core drifts.** It was cpu2-dominant in the 03:47 capture and cpu3-dominant in
   the 04:10 capture. So "pin a load source to the audio core" is inherently unstable — any recipe
   built on a fixed core number is wrong.
3. Therefore the affinity route needs further work (a reboot with the setting pre-applied, and/or
   driving `Affinity Policy - Temporal\TargetSet`), or the kernel-debugger route.

## Risk notes

- Steps 3-4 deliberately starve one core. The machine stays responsive (other 15 cores are free),
  unlike the earlier inode-churn experiment which froze the whole system.
- The watchdog being paused means audio stays broken until step 6 — keep the window short.
- RSS changes are per-adapter and revert on reboot in any case.
