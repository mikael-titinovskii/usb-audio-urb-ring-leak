#!/usr/bin/env python3
"""Detect the exact orphan signature in a FocusriteUsb queue trace.

The fault condition (validated on the 20-minute reference trace, 53 OUT-OUT pairs):
two consecutive OUT *completions* with **no `Pair` event between them**. In that case the
first OUT URB is still sitting in the driver's single-entry pending-OUT mailbox
(`DAT_140027300`) when the second OUT completion overwrites it, and the overwritten URB is
never prepared, resubmitted or paired again -> that ring slot is orphaned.

52 of the 53 OUT-OUT pairs in the reference trace have a `Pair` between them and are harmless.
Exactly one has none -- and that is the orphan at t=1018.855 s.

Usage: orphan-signature.py <queue.csv>
"""
import csv, sys


def main():
    if len(sys.argv) < 2:
        print(__doc__); return
    ev = []
    with open(sys.argv[1], newline='', encoding='utf-8-sig') as f:
        for r in csv.DictReader(f):
            try: t = float(r['time_ms'])
            except (TypeError, ValueError): continue
            ev.append((t, (r.get('kind') or '').strip(), (r.get('direction') or '').strip(),
                       r.get('index'), r.get('start_frame'), r.get('current_frame')))

    comps = [i for i, e in enumerate(ev) if e[1] == 'Complete']
    hits = []
    for a_i, b_i in zip(comps, comps[1:]):
        a, b = ev[a_i], ev[b_i]
        if a[2] != 'OUT' or b[2] != 'OUT':
            continue
        between = ev[a_i + 1:b_i]
        has_pair = any(x[1] == 'Pair' for x in between)
        if not has_pair:
            hits.append((a, b, len(between)))

    print('file                     : %s' % sys.argv[1])
    print('OUT-OUT completion pairs  : %d' % sum(1 for x, y in zip(comps, comps[1:])
                                                if ev[x][2] == 'OUT' and ev[y][2] == 'OUT'))
    print('ORPHAN SIGNATURE (no Pair): %d' % len(hits))
    for a, b, n in hits[:20]:
        print('  t=%.3f s  OUT idx=%s -> OUT idx=%s   events_in_between=%d'
              % (a[0] / 1000.0, a[3], b[3], n))
    if hits:
        print('\n=> orphan(s) present: a ring slot was overwritten and never resubmitted.')
    else:
        print('\n=> no orphan signature; every OUT-OUT pair was consumed by a Pair event.')


if __name__ == '__main__':
    main()
