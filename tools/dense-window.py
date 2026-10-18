import csv, sys

path = sys.argv[1]
lo = float(sys.argv[2]); hi = float(sys.argv[3])
rows = []
with open(path, newline='', encoding='utf-8-sig') as f:
    for r in csv.DictReader(f):
        t = r.get('time_ms')
        try: t = float(t)
        except: continue
        if lo <= t <= hi:
            rows.append((t, r.get('direction'), r.get('kind'), r.get('index'),
                         r.get('input_pair_index'), r.get('value'), r.get('status'),
                         r.get('start_frame'), r.get('current_frame')))
rows.sort(key=lambda x: x[0])
print('window %.3f .. %.3f  events=%d' % (lo, hi, len(rows)))
print('%-14s %-4s %-14s %-4s %-10s %-8s %-10s %s' % ('time_ms','dir','kind','idx','pair','value','status','start/cur'))
for t, d, k, i, p, v, s, sf, cf in rows:
    extra = ''
    if sf and cf:
        # margin is the driver's StartFrame - CurrentFrame + 1, treated as signed 32-bit
        try:
            margin = int(sf) - int(cf) + 1
            if margin > 2**31 - 1: margin -= 2**32
            extra = 'start=%s cur=%s margin=%d' % (sf, cf, margin)
        except: extra = 'start=%s cur=%s' % (sf, cf)
    print('%-14.4f %-4s %-14s %-4s %-10s %-8s %-10s %s' % (t, d, k, i, p, v, s, extra))
