import csv, sys, collections

path = sys.argv[1]
rows = []
with open(path, newline='', encoding='utf-8-sig') as f:
    for r in csv.DictReader(f):
        rows.append(r)

def num(x):
    try: return int(x)
    except: return None

def fnum(x):
    try: return float(x)
    except: return None

# per-second buckets for OUT submits
buckets = collections.defaultdict(lambda: {'idx': set(), 'n': 0, 'margins': []})
many = collections.defaultdict(lambda: {'idx': set(), 'n': 0, 'margins': []})

for r in rows:
    d = r.get('direction')
    if r.get('kind') != 'Submit':
        continue
    t = fnum(r.get('time_ms'))
    if t is None:
        continue
    sec = int(t // 1000)
    idx = num(r.get('index'))
    sf = num(r.get('start_frame'))
    cf = num(r.get('current_frame'))
    margin = None
    if sf is not None and cf is not None:
        # margin is the driver's StartFrame - CurrentFrame + 1, treated as signed 32-bit
        margin = sf - cf + 1
        if margin > 2**31 - 1: margin -= 2**32
    tgt = buckets if d == 'OUT' else many
    b = tgt[sec]
    b['idx'].add(idx)
    b['n'] += 1
    if margin is not None:
        b['margins'].append(margin)

print('rows=%d' % len(rows))
print()
print('=== OUT (playback) per second ===')
prev = None
for sec in sorted(buckets):
    b = buckets[sec]
    slots = len(b['idx'])
    mg = b['margins']
    m = (min(mg), max(mg)) if mg else (None, None)
    state = (slots,)
    if prev is not None and state != prev:
        print('  >> CHANGE at %ds: slots %s -> %s  indices=%s' % (sec, prev[0], slots, sorted(x for x in b['idx'] if x is not None)))
    if prev is None or state != prev or sec % 30 == 0:
        print('  t=%4ds slots=%2d submits=%3d margin=%s..%s indices=%s' % (sec, slots, b['n'], m[0], m[1], sorted(x for x in b['idx'] if x is not None)))
    prev = state

print()
print('=== IN (record) per second (first/last/change only) ===')
prev = None
for sec in sorted(many):
    b = many[sec]
    slots = len(b['idx'])
    mg = b['margins']
    m = (min(mg), max(mg)) if mg else (None, None)
    state = (slots,)
    if prev is None or state != prev or sec % 60 == 0:
        print('  t=%4ds slots=%2d submits=%3d margin=%s..%s' % (sec, slots, b['n'], m[0], m[1]))
    prev = state
