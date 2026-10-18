#!/usr/bin/env python3
"""Attribute a fault to a driver: find the longest DPC/ISR around a wall-clock instant.

Usage:
    dpc-window.py <trace.xml> <ISO-time> [--window SECONDS] [--top N]

`trace.xml` is a `tracerpt <etl> -o <xml> -of XML` export of the WPR DPC/Interrupt trace.
`ISO-time` is the fault timestamp, e.g. 2026-10-18T03:04:06  (local, as the watchdog logs it,
or with an explicit offset).

Duration of a DPC/ISR event is taken as (event wall time - InitialTime converted to wall time).
InitialTime is a QPC value; the trace header carries PerfFreq, and the header's BootTime is the
QPC origin, so wall = boot_wall + (qpc / freq). The header's own TimeCreated gives boot_wall.

Reports, for the window, the longest events with the owning module resolved from the trace's
Image/FileName records (requires the Loader keyword).
"""
import sys
from datetime import datetime, timezone, timedelta
from xml.etree.ElementTree import iterparse

EV = '{http://schemas.microsoft.com/win/2004/08/events/event}'
TR = '{http://schemas.microsoft.com/win/2004/08/events/trace}'


def parse_iso(s):
    s = s.strip().replace('Z', '+00:00')
    try:
        return datetime.fromisoformat(s)
    except ValueError:
        return None


def main():
    if len(sys.argv) < 3:
        print(__doc__); return
    path = sys.argv[1]
    target = parse_iso(sys.argv[2])
    if target is None:
        print('bad ISO time'); return
    # Compare on LOCAL WALL-CLOCK, not absolute time: tracerpt renders trace timestamps with a
    # slightly wrong offset (e.g. +02:59 for a +03:00 zone), so aware comparison misaligns by a
    # minute. Dropping tzinfo aligns the printed local times, which is what the watchdog logs.
    def naive(dt):
        return dt.replace(tzinfo=None) if dt is not None else None
    target = naive(target)
    window = 2.0
    top = 15
    for i, a in enumerate(sys.argv):
        if a == '--window' and i + 1 < len(sys.argv): window = float(sys.argv[i + 1])
        if a == '--top' and i + 1 < len(sys.argv): top = int(sys.argv[i + 1])

    modules = []          # (base, end, name)
    hits = []             # (dur_ms, wall, routine, opcode) in window
    seq = []              # every DPC/ISR in the wider gap window, chronological
    perf_freq = None
    boot_wall = None      # wall time of QPC 0
    n_total = 0

    lo = target - timedelta(seconds=window)
    # a wider band around the fault, used to find holes in DPC/ISR activity
    glo = target - timedelta(seconds=max(window * 4, 20))
    ghi = target + timedelta(seconds=5)
    hi = target + timedelta(seconds=window)

    for _, el in iterparse(path, events=('end',)):
        if not el.tag.endswith('Event'):
            # NEVER clear non-Event elements: clearing a child (RenderingInfo/EventData)
            # destroys it before its parent <Event> is yielded.
            continue
        n_total += 1
        syse = el.find(EV + 'System')
        if syse is None:
            el.clear(); continue
        tc = syse.find(EV + 'TimeCreated')
        wall = naive(parse_iso(tc.get('SystemTime', ''))) if tc is not None else None
        ex = syse.find(EV + 'Execution')
        proc = ex.get('ProcessorID') if (ex is not None and ex.get('ProcessorID') is not None) else None
        ri = el.find(EV + 'RenderingInfo')
        opcode = ri.findtext(EV + 'Opcode', '') if ri is not None else ''
        evname = ri.findtext(TR + 'EventName', '') if ri is not None else ''

        data = {}
        ed = el.find(EV + 'EventData')
        if ed is not None:
            for d in ed.findall(EV + 'Data'):
                data[d.get('Name', '')] = (d.text or '').strip()

        # trace header: gives us PerfFreq and the QPC<->wall origin
        if evname == 'EventTrace' or opcode == 'Header':
            try:
                perf_freq = int(data.get('PerfFreq', '')) or perf_freq
            except ValueError:
                pass
            # StartTime is QPC at session start; its TimeCreated is that wall moment
            try:
                start_qpc = int(data.get('StartTime', ''))
                if wall is not None and perf_freq:
                    boot_wall = wall - timedelta(seconds=start_qpc / perf_freq)
            except ValueError:
                pass
        elif opcode in ('DPC', 'ISR'):
            if wall is None:
                el.clear(); continue
            wide = not (glo <= wall <= ghi)
            if wide and not (lo <= wall <= hi):
                el.clear(); continue
            routine = data.get('Routine', '')
            init = data.get('InitialTime', '')
            dur = None
            try:
                if perf_freq and boot_wall is not None:
                    init_wall = boot_wall + timedelta(seconds=int(init) / perf_freq)
                    dur = (wall - init_wall).total_seconds() * 1000.0
                    if dur < 0 or dur > 100000: dur = None
            except ValueError:
                pass
            try:
                raddr = int(routine, 0)
            except ValueError:
                raddr = None
            hits.append((dur, wall, raddr, opcode, data.get('Routine', '')))
            seq.append((wall, dur, raddr, opcode, proc))
        elif 'Image' in evname or 'FileName' in data:
            try:
                base = int(data.get('ImageBase', ''), 0)
                size = int(data.get('ImageSize', ''), 0)
            except ValueError:
                base = size = 0
            name = data.get('FileName') or data.get('ImageFileName') or ''
            if base and size and name:
                modules.append((base, base + size, name))
        el.clear()

    modules.sort()
    bases = [m[0] for m in modules]
    import bisect

    def lookup(addr):
        if addr is None: return None
        i = bisect.bisect_right(bases, addr) - 1
        if 0 <= i < len(modules) and modules[i][0] <= addr < modules[i][1]:
            return modules[i][2]
        return None

    print('trace            : %s' % path)
    print('events scanned   : %d' % n_total)
    print('modules seen     : %d' % len(modules))
    print('perf_freq        : %s' % perf_freq)
    print('fault time       : %s (+/- %ss)' % (target.isoformat(), window))
    print('DPC/ISR in window: %d' % len(hits))
    # ---- biggest holes in DPC/ISR activity: a stall is a 20-50 ms gap, not necessarily one
    # long DPC. Report the hole and which driver's DPCs bracket it.
    seq.sort(key=lambda x: x[0])
    gaps = []
    for a, b in zip(seq, seq[1:]):
        d = (b[0] - a[0]).total_seconds() * 1000.0
        if d >= 3.0:
            gaps.append((d, a, b))
    gaps.sort(reverse=True)
    print('\n--- largest gaps between DPC/ISR events (>=3 ms) in [%s .. %s] ---'
          % (glo.isoformat(), ghi.isoformat()))
    if not gaps:
        print('  none (max gap < 3 ms)')
    for d, a, b in gaps[:10]:
        print('  %8.2f ms  after %s %s  ->  next %s %s   gap_start=%s'
              % (d, a[3], lookup(a[2]) or '?', b[3], lookup(b[2]) or '?',
                 a[0].isoformat()))

    # ---- PER-PROCESSOR gaps. The global gap list only catches a total blackout; a stall on the
    # one core that services the audio DPC would not show there. This is the analysis that can
    # actually implicate a single core.
    percore = {}
    for e in seq:
        percore.setdefault(e[4], []).append(e)
    print('')
    print('--- per-processor largest gap (>=2 ms), with bracketing drivers ---')
    rows = []
    for cpu, evs in percore.items():
        evs.sort(key=lambda x: x[0])
        g = 0.0; ga = gb = None
        for a, b in zip(evs, evs[1:]):
            d = (b[0] - a[0]).total_seconds() * 1000.0
            if d > g: g, ga, gb = d, a, b
        if g >= 2.0:
            rows.append((g, cpu, ga, gb))
    rows.sort(reverse=True)
    if not rows:
        print('  no processor had a gap >= 2 ms')
    for g, cpu, a, b in rows[:12]:
        print('  cpu=%-3s %8.2f ms  %s %s -> %s %s  at %s'
              % (cpu, g, a[3], lookup(a[2]) or '?', b[3], lookup(b[2]) or '?', a[0].isoformat()))

    # which cores run the USB-audio stack?
    usb = {}
    for e in seq:
        m = (lookup(e[2]) or '').lower()
        if 'usbxhci' in m or 'focusrite' in m or 'usb' in m:
            usb.setdefault(e[4], 0)
            usb[e[4]] += 1
    if usb:
        print('')
        print('  cores hosting USB/Focusrite DPCs: %s'
              % ', '.join('cpu=%s(%d events)' % (k, v) for k, v in sorted(usb.items(), key=lambda x: -x[1])[:6]))

    hits.sort(key=lambda h: (h[0] if h[0] is not None else -1), reverse=True)
    print('\n%-10s %-6s %-12s %-28s %s' % ('dur_ms', 'type', 'routine', 'module', 'wall'))
    for dur, wall, raddr, op, raw in hits[:top]:
        print('%-10s %-6s %-12s %-28s %s' % (
            ('%.3f' % dur) if dur is not None else '?', op,
            ('0x%X' % raddr) if raddr else raw or '?',
            lookup(raddr) or '(unresolved)', wall.isoformat()))


if __name__ == '__main__':
    main()
