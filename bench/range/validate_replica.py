"""Compare the flight replica (MissileBench features) with what logged shots did.
Usage: python validate_replica.py <shots.csv from range_report.py> <features.csv from MissileBench features>"""
import csv, statistics, sys


def main():
    shots = [r for r in csv.DictReader(open(sys.argv[1])) if r.get('vel') != '0,0,0']
    feats = list(csv.DictReader(open(sys.argv[2])))
    rows = [(s, f) for s, f in zip(shots, feats) if s['part'] == f['part']]
    print(f"{len(rows)} shots with a usable launch state")
    straight = [(s, f) for s, f in rows if s.get('targetMode') == 'straight']
    print(f"\nshots at straight-flying targets: {len(straight)}  (actual / replica)")
    for s, f in straight:
        print(f"  {s['part']:13s} hit {s['hit']:5s}/{f['flyReaches']:5s} time {float(s['timeToClosest']):5.1f}/{float(f['flyTime']):5.1f}"
              f" speed {float(s['speedAtClosest']):5.0f}/{float(f['flySpeed']):5.0f} miss {float(s['minDistance']):6.0f}/{float(f['flyMiss']):6.0f}"
              f" range {float(s['range']):6.0f} {s.get('missReason')}")
    for label, sel in (('straight', straight), ('all', rows)):
        hits = [(s, f) for s, f in sel if s['hit'] == 'True' and float(s['timeToClosest']) > 0.5]
        if not hits: continue
        rt = sorted(float(f['flyTime']) / float(s['timeToClosest']) for s, f in hits)
        rv = sorted(float(f['flySpeed']) / max(1.0, float(s['speedAtClosest'])) for s, f in hits)
        q = lambda v, p: v[min(len(v) - 1, int(p * len(v)))]
        print(f"{label} hits {len(hits)}: replica/actual time median {q(rt, 0.5):.2f} (IQR {q(rt, 0.25):.2f}-{q(rt, 0.75):.2f}),"
              f" speed at closest median {q(rv, 0.5):.2f} (IQR {q(rv, 0.25):.2f}-{q(rv, 0.75):.2f})")
        agree = sum((s['hit'] == 'True') == (f['flyReaches'] == 'True') for s, f in sel)
        print(f"{label}: replica reach agrees with the outcome on {agree}/{len(sel)}")


if __name__ == '__main__':
    main()
