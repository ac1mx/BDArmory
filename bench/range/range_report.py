"""Summarise missile range runs: MissileRangeTrial and MissileShot lines from one or more KSP.log files.
Usage: python range_report.py <KSP.log> [<KSP.log> ...] [--csv shots.csv]"""
import collections, csv, math, re, sys

FIELD = re.compile(r'(\w+)=(\S+)')


def parse(paths):
    trials, shots = [], []
    for path in paths:
        for line in open(path, encoding='utf-8', errors='replace'):
            if 'MissileRangeTrial ' in line:
                trials.append(dict(FIELD.findall(line.split('MissileRangeTrial ', 1)[1])))
            elif 'MissileShot ' in line and 'harness=range' in line:
                d = dict(FIELD.findall(line.split('MissileShot ', 1)[1]))
                d['_log'] = path
                shots.append(d)
    return trials, shots


def f(d, k, default=float('nan')):
    try:
        return float(d[k])
    except (KeyError, ValueError):
        return default


def table(title, rows, key, bins):
    print(title)
    for lo, hi in bins:
        sel = [d for d in rows if lo <= key(d) < hi]
        if not sel: continue
        hits = sum(d['hit'] == 'True' for d in sel)
        print(f"  {lo:>7g}-{hi:<7g} n={len(sel):4d} hit={hits:4d} ({100 * hits / len(sel):3.0f}%)")


def main():
    args = sys.argv[1:]
    out_csv = None
    if '--csv' in args:
        i = args.index('--csv'); out_csv = args[i + 1]; del args[i:i + 2]
    trials, shots = parse(args)
    print(f"{len(trials)} trials, {len(shots)} shots logged")
    print('trial results:', dict(collections.Counter((t.get('result'), t.get('reason')) for t in trials)))
    no_lock = collections.Counter(t['missile'] for t in trials if t.get('result') == 'NoLock')
    zero_vel = sum(1 for s in shots if s.get('vel') == '0,0,0')
    print(f"launched at zero velocity: {zero_vel}")
    by = collections.defaultdict(list)
    for s in shots: by[s['part']].append(s)
    print(f"\n{'missile':14s} {'shots':>5s} {'hit%':>5s} {'evade%':>6s} {'straight%':>9s} {'noLock':>6s} {'medFlight':>9s}  miss reasons")
    for part in sorted(by):
        rows = by[part]
        hits = sum(r['hit'] == 'True' for r in rows)
        ev = [r for r in rows if r.get('targetMode') == 'evade']; st = [r for r in rows if r.get('targetMode') == 'straight']
        pct = lambda sel: 100 * sum(r['hit'] == 'True' for r in sel) / len(sel) if sel else float('nan')
        ft = sorted(f(r, 'flightTime') for r in rows)
        reasons = collections.Counter(r.get('missReason', '?') for r in rows if r['hit'] != 'True')
        print(f"{part:14s} {len(rows):5d} {100 * hits / len(rows):5.0f} {pct(ev):6.0f} {pct(st):9.0f} {no_lock[part]:6d} {ft[len(ft) // 2]:9.1f}  {dict(reasons)}")
    table('\nslant range (m), all missiles', shots, lambda d: f(d, 'range'), [(0, 2000), (2000, 5000), (5000, 10000), (10000, 20000), (20000, 40000), (40000, 1e9)])
    table('target aspect (0 = coming at the shooter)', shots, lambda d: f(d, 'aspect'), [(0, 45), (45, 90), (90, 135), (135, 181)])
    table('off boresight at launch', shots, lambda d: f(d, 'offBoresight'), [(0, 10), (10, 30), (30, 60), (60, 181)])
    decoyed = [s for s in shots if s['hit'] != 'True' and f(s, 'decoyFraction', 0) > 0.2]
    print(f"\nmisses guided on something else for >20% of the flight (decoys/stale locks): {len(decoyed)} of {sum(s['hit'] != 'True' for s in shots)}")
    if out_csv:
        keys = sorted({k for s in shots for k in s})
        with open(out_csv, 'w', newline='') as fh:
            w = csv.DictWriter(fh, fieldnames=keys); w.writeheader(); w.writerows(shots)
        print('wrote', out_csv)


if __name__ == '__main__':
    main()
