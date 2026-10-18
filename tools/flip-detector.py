#!/usr/bin/env python3
"""Measure the fault indicators in a FocusriteUsb queue trace.

Three related signals, from cheapest to most conclusive:

1. `consecutive same-direction completions` — the TRIGGER signature. Healthy cycles strictly
   alternate IN/OUT completions, so two completions of the same direction in a row marks a cycle
   where the OUT completion overtook the IN one. Far more frequent than an actual leak
   (106 in the 20-minute onset trace vs 1 leak) -> use as a high-sensitivity proxy.

2. `advance == +2 (mod 10)` on OUT submissions — the ORPHAN fingerprint. The driver's ring
   rotation normally advances by exactly one slot per submit; an advance of two means it stepped
   over an index that was never re-armed. Every one of these names the same missing index.

3. leaks — counted elsewhere (`,restart` rows in watchdog/pool-history.csv).

Usage:
    flip-detector.py <queue.csv> [--per-minute] [--list]
"""
import csv, sys, collections


def main():
    if len(sys.argv) < 2:
        print(__doc__); return
    path = sys.argv[1]
    per_minute = '--per-minute' in sys.argv
    do_list = '--list' in sys.argv

    comps = []          # (time_ms, direction, index)
    out_submits = []    # (time_ms, index)
    with open(path, newline='', encoding='utf-8-sig') as f:
        for r in csv.DictReader(f):
            k = (r.get('kind') or '').strip()
            d = (r.get('direction') or '').strip()
            try: t = float(r.get('time_ms'))
            except (TypeError, ValueError): continue
            if k == 'Complete':
                try: i = int(r.get('index'))
                except (TypeError, ValueError): i = None
                comps.append((t, d, i))
            elif k == 'Submit' and d == 'OUT':
                try: i = int(r.get('index'))
                except (TypeError, ValueError): i = None
                out_submits.append((t, i))

    same = [(a, b) for a, b in zip(comps, comps[1:]) if a[1] == b[1]]

    # THE TRIGGER (see docs/decompiled-findings.md): a completion-processing stall. The driver
    # re-primes after a gap > ~5 ms, and that re-prime leaves the pairing offset at +1 and the
    # margin one slot short — after which a flip orphans a slot ~30 s later.
    # A real stall is bounded: the reference events are 14 ms and 22 ms. Gaps of hundreds of ms or
    # seconds are capture/flush artifacts or stream-idle periods, NOT stalls — reporting them as
    # stalls produces huge false-positive counts. Keep them separate.
    _gaps = [((b[0] - a[0]) * 1000.0, b[0]) for a, b in zip(comps, comps[1:])]
    stalls = [(t, g) for g, t in _gaps if 5000.0 < g <= 200000.0]
    capture_gaps = [(t, g) for g, t in _gaps if g > 200000.0]

    adv = collections.Counter()
    missing = collections.Counter()
    orphan_events = []
    for a, b in zip(out_submits, out_submits[1:]):
        if a[1] is None or b[1] is None: continue
        d = (b[1] - a[1]) % 10
        adv[d] += 1
        if d == 2:
            m = (a[1] + 1) % 10
            missing[m] += 1
            orphan_events.append((b[0], m))

    span_min = (comps[-1][0] - comps[0][0]) / 60000.0 if comps else 0.0

    print('file                       : %s' % path)
    print('completions                : %d' % len(comps))
    print('OUT submits                : %d' % len(out_submits))
    print('stall  : gaps 5ms..200ms      : %d  %s' % (len(stalls),
          ['%.0fus@%.1fs' % (g, t / 1000.0) for t, g in stalls[:10]]))
    print('         gaps >200ms (artifact or stream-idle, NOT a stall): %d' % len(capture_gaps))
    print('trigger: same-dir completions : %d' % len(same))
    print('orphan : advance==+2 events   : %d' % adv.get(2, 0))
    print('         missing index(es)     : %s' % (dict(sorted(missing.items())) or '{}'))
    print('         other advances        : %s' % {k: v for k, v in sorted(adv.items()) if k != 2})
    if span_min > 0:
        print('span                       : %.1f min' % span_min)
        print('same-dir per minute        : %.2f' % (len(same) / span_min))

    if per_minute and comps:
        buckets = collections.Counter(int(a[0] // 60000) for a, _ in same)
        t0 = int(comps[0][0] // 60000)
        print('same-dir completions per minute:')
        for m in sorted(buckets):
            print('  min +%-4d %d' % (m - t0, buckets[m]))
    if do_list:
        print('\nsame-direction completion pairs (first 40):')
        for a, b in same[:40]:
            print('  t=%.3f  %s idx=%s  ->  %s idx=%s' % (a[0], a[1], a[2], b[1], b[2]))
        print('\nfirst orphan (advance==+2) events:')
        for t, m in orphan_events[:10]:
            print('  t=%.3f  ring stepped over index %d' % (t, m))


if __name__ == '__main__':
    main()
