#!/usr/bin/env python3
"""Find the CPU(s) that service the USB-audio DPC/ISR, then look for stalls there.

Per-core gap analysis is unusable naively, because an idle core services no DPCs and therefore
shows multi-hundred-ms "gaps". This restricts the analysis to cores that demonstrably run the
USB / xHCI / Focusrite DPCs, and only reports gaps that are bracketed by activity (so the core
was busy, not parked).

Usage: audio-core.py <trace.xml> [--from ISO] [--to ISO]
"""
import sys, bisect
from datetime import datetime
from xml.etree.ElementTree import iterparse

EV = '{http://schemas.microsoft.com/win/2004/08/events/event}'
TR = '{http://schemas.microsoft.com/2004/08/events/trace}'
TR2 = '{http://schemas.microsoft.com/win/2004/08/events/trace}'


def naivedt(s):
    s = s.strip().replace('Z', '')
    try:
        d = datetime.fromisoformat(s)
    except ValueError:
        return None
    return d.replace(tzinfo=None)


def main():
    if len(sys.argv) < 2:
        print(__doc__); return
    path = sys.argv[1]
    t_from = t_to = None
    for i, a in enumerate(sys.argv):
        if a == '--from' and i + 1 < len(sys.argv): t_from = naivedt(sys.argv[i + 1])
        if a == '--to' and i + 1 < len(sys.argv): t_to = naivedt(sys.argv[i + 1])

    modules = []
    evs = []          # (wall, proc, raddr, op, dur_ms)
    perf_freq = None; boot_wall = None

    for _, el in iterparse(path, events=('end',)):
        if not el.tag.endswith('Event'):
            continue
        syse = el.find(EV + 'System')
        if syse is None:
            el.clear(); continue
        tc = syse.find(EV + 'TimeCreated')
        wall = naivedt(tc.get('SystemTime', '')) if tc is not None else None
        ex = syse.find(EV + 'Execution')
        proc = ex.get('ProcessorID') if ex is not None else None
        ri = el.find(EV + 'RenderingInfo')
        op = ri.findtext(EV + 'Opcode', '') if ri is not None else ''
        evname = ''
        if ri is not None:
            evname = ri.findtext(TR, '') or ri.findtext(TR2, '') or ''

        data = {}
        ed = el.find(EV + 'EventData')
        if ed is not None:
            for d in ed.findall(EV + 'Data'):
                data[d.get('Name', '')] = (d.text or '').strip()

        if op == 'Header' or evname == 'EventTrace':
            try: perf_freq = int(data.get('PerfFreq', '')) or perf_freq
            except ValueError: pass
            try:
                sq = int(data.get('StartTime', ''))
                if wall is not None and perf_freq:
                    boot_wall = wall - __import__('datetime').timedelta(seconds=sq / perf_freq)
            except ValueError: pass
        elif op in ('DPC', 'ISR', 'ISR-MSI') and wall is not None:
            if (t_from and wall < t_from) or (t_to and wall > t_to):
                el.clear(); continue
            try: raddr = int(data.get('Routine', ''), 0)
            except ValueError: raddr = None
            dur = None
            try:
                if perf_freq and boot_wall:
                    iw = boot_wall + __import__('datetime').timedelta(seconds=int(data.get('InitialTime', '')) / perf_freq)
                    dur = (wall - iw).total_seconds() * 1000.0
                    if dur < 0 or dur > 100000: dur = None
            except ValueError: pass
            evs.append((wall, proc, raddr, op, dur))
        elif data.get('ImageBase') and data.get('ImageSize') and data.get('FileName'):
            try:
                b = int(data['ImageBase'], 0); s = int(data['ImageSize'], 0)
                modules.append((b, b + s, data['FileName']))
            except ValueError: pass
        el.clear()

    modules.sort(); bases = [m[0] for m in modules]
    def lookup(a):
        if a is None: return None
        i = bisect.bisect_right(bases, a) - 1
        if 0 <= i < len(modules) and modules[i][0] <= a < modules[i][1]:
            return modules[i][2]
        return None

    print('trace     : %s' % path)
    print('DPC/ISR   : %d' % len(evs))
    print('modules   : %d' % len(modules))

    usb = {}
    for w, p, a, op, d in evs:
        m = (lookup(a) or '').lower()
        if 'xhci' in m or 'focusrite' in m or 'usbhub' in m or 'usbport' in m:
            usb.setdefault(p, [0, (lookup(a) or '?')])
            usb[p][0] += 1
    print('\ncores servicing USB/xHCI DPCs:')
    for p, (n, name) in sorted(usb.items(), key=lambda kv: -kv[1][0]):
        print('  cpu=%-3s %6d events   e.g. %s' % (p, n, name))

    if not usb:
        print('  (none identified -- module names may differ)')
        return

    percore = {}
    for e in evs: percore.setdefault(e[1], []).append(e)

    print('\nstalls on those cores (gap >= 2 ms, core busy either side):')
    rows = []
    for p in usb:
        seq = sorted(percore.get(p, []), key=lambda x: x[0])
        if len(seq) < 50: continue
        for a, b in zip(seq, seq[1:]):
            g = (b[0] - a[0]).total_seconds() * 1000.0
            if g < 2.0: continue
            # require the core to be busy nearby: >=10 events in the 50 ms before the gap
            lo = bisect.bisect_left([x[0] for x in seq], a[0] - __import__('datetime').timedelta(milliseconds=50))
            hi = bisect.bisect_right([x[0] for x in seq], a[0])
            if hi - lo < 10: continue
            rows.append((g, p, a, b))
    rows.sort(reverse=True)
    if not rows:
        print('  none')
    for g, p, a, b in rows[:15]:
        print('  cpu=%-3s %8.2f ms  %-6s %-32s -> %-6s %-32s at %s'
              % (p, g, a[3], (lookup(a[2]) or '?').split('\\')[-1], b[3],
                 (lookup(b[2]) or '?').split('\\')[-1], a[0].isoformat()))


if __name__ == '__main__':
    main()
