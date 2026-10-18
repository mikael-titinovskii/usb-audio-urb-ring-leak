#!/usr/bin/env python3
"""Show, per module, which processors its DPC/ISR run on — and the largest per-core stall.

Useful for verifying interrupt-affinity pinning, and for deciding whether a load source can be
made to collide with the audio core.

Usage: module-cores.py <trace.xml> [--filter tcpip|ndis|portcls|...]
"""
import sys, bisect, collections, os
from datetime import datetime, timedelta
from xml.etree.ElementTree import iterparse

EV = '{http://schemas.microsoft.com/win/2004/08/events/event}'


def nd(s):
    try: return datetime.fromisoformat(s.replace('Z', '')).replace(tzinfo=None)
    except Exception: return None


def main():
    if len(sys.argv) < 2:
        print(__doc__); return
    path = sys.argv[1]
    filt = None
    for i, a in enumerate(sys.argv):
        if a == '--filter' and i + 1 < len(sys.argv):
            filt = sys.argv[i + 1].lower()

    mods = []; evs = []
    for _, el in iterparse(path, events=('end',)):
        if not el.tag.endswith('Event'):
            continue
        s = el.find(EV + 'System')
        if s is None:
            el.clear(); continue
        tc = s.find(EV + 'TimeCreated')
        w = nd(tc.get('SystemTime', '')) if tc is not None else None
        ex = s.find(EV + 'Execution')
        p = ex.get('ProcessorID') if ex is not None else None
        ri = el.find(EV + 'RenderingInfo')
        op = ri.findtext(EV + 'Opcode', '') if ri is not None else ''
        d = {}
        ed = el.find(EV + 'EventData')
        if ed is not None:
            for x in ed.findall(EV + 'Data'):
                d[x.get('Name', '')] = (x.text or '').strip()
        if op in ('DPC', 'ISR', 'ISR-MSI') and w is not None:
            try: a = int(d.get('Routine', ''), 0)
            except ValueError: a = None
            evs.append((w, p, a, op))
        elif d.get('ImageBase') and d.get('ImageSize') and d.get('FileName'):
            try:
                b = int(d['ImageBase'], 0); s2 = int(d['ImageSize'], 0)
                mods.append((b, b + s2, d['FileName']))
            except ValueError: pass
        el.clear()

    mods.sort(); bases = [m[0] for m in mods]
    def lk(a):
        if a is None: return None
        i = bisect.bisect_right(bases, a) - 1
        return mods[i][2] if 0 <= i < len(mods) and mods[i][0] <= a < mods[i][1] else None

    print('trace: %s   DPC/ISR: %d   modules: %d' % (path, len(evs), len(mods)))
    by = collections.Counter(); cores = collections.defaultdict(collections.Counter)
    for w, p, a, op in evs:
        n = os.path.basename(lk(a) or '?')
        by[n] += 1; cores[n][p] += 1
    print('\n%-30s %8s   processors (top 6)' % ('module', 'count'))
    for n, c in by.most_common(30):
        if filt and filt not in n.lower():
            continue
        top = ', '.join('cpu%s:%d' % (k, v) for k, v in cores[n].most_common(6))
        print('  %-28s %8d   %s' % (n, c, top))


if __name__ == '__main__':
    main()
