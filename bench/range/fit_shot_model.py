"""Fit the shot model (BDArmory/Control/ShotModel.cs) to logged missile shots and compare it with the model that was flying.

Usage: python fit_shot_model.py <KSP.log | features.csv> [...] [--out shot_model.cfg] [--folds 5] [--range-only]

Inputs are KSP.log files (MissileShot lines carrying the in-game flight prediction fields) or CSV files with the same columns
(bench/MissileBench features). The feature vector is built exactly as ShotModel.Features.Vector builds it. Per-missile offsets are
fitted with a stronger pull towards zero than the shared weights, so missiles with few shots stay close to the shared model.
Prints held-out log loss and Brier score (k-fold) for the base rate, the logged estimate and the refit, a calibration table, and
the weights; --out writes them in the format ShotModel reads."""
import csv, math, random, re, sys

FIELD = re.compile(r'(\w+)=(\S+)')
NAMES = ["bias", "reaches", "escapeReaches", "energyMargin", "flightTime", "hot", "cold", "offBoresight", "heat", "launcherGuided", "lateralG", "low"]
HOT_ASPECT, COLD_ASPECT, LOW_ALTITUDE = 60.0, 135.0, 500.0
L2_SHARED, L2_MISSILE = 0.5, 4.0


def load(paths, range_only):
    rows = []
    for path in paths:
        if path.lower().endswith('.csv'):
            rows += [dict(r) for r in csv.DictReader(open(path))]
            continue
        for line in open(path, encoding='utf-8', errors='replace'):
            if 'MissileShot ' not in line or 'flyReaches=' not in line: continue
            if range_only and 'harness=range' not in line: continue
            d = dict(FIELD.findall(line.split('MissileShot ', 1)[1]))
            d['heat'] = str(d.get('mode') == 'Heat')
            rows.append(d)
    return [r for r in rows if r.get('vel', '1') != '0,0,0' and r.get('flyReaches') in ('True', 'False')]


def num(r, k, default=0.0):
    try: return float(r[k])
    except (KeyError, ValueError): return default


def vector(r):
    aspect = num(r, 'aspect')
    return [1.0, float(r['flyReaches'] == 'True'), float(r.get('escReaches') == 'True'), num(r, 'energyMargin'), num(r, 'flyTime') / 30.0,
            float(aspect < HOT_ASPECT), float(aspect > COLD_ASPECT), num(r, 'offBoresightFraction'), float(r.get('heat') == 'True'),
            num(r, 'launcherGuided'), min(num(r, 'flyG'), 40.0) / 10.0, float(num(r, 'launchAlt', 1e4) < LOW_ALTITUDE)]


def solve(a, b):
    n = len(b); m = [row[:] + [b[i]] for i, row in enumerate(a)]
    for c in range(n):
        p = max(range(c, n), key=lambda i: abs(m[i][c])); m[c], m[p] = m[p], m[c]
        if abs(m[c][c]) < 1e-12: continue
        for i in range(n):
            if i != c:
                f = m[i][c] / m[c][c]
                for j in range(c, n + 1): m[i][j] -= f * m[c][j]
    return [m[i][n] / m[i][i] if abs(m[i][i]) > 1e-12 else 0.0 for i in range(n)]


def fit(xs, ys, parts, missiles):
    """Newton's method on the penalised log likelihood: shared weights then one offset per missile."""
    k = len(NAMES); n = k + len(missiles)
    index = {m: k + i for i, m in enumerate(missiles)}
    penalty = [1e-4] + [L2_SHARED] * (k - 1) + [L2_MISSILE] * len(missiles)
    w = [0.0] * n
    rows = [(x + [0.0] * len(missiles), y) for x, y in zip(xs, ys)]
    for (x, _), p in zip(rows, parts):
        if p in index: x[index[p]] = 1.0
    for _ in range(25):
        grad = [-penalty[i] * w[i] for i in range(n)]
        hess = [[0.0] * n for _ in range(n)]
        for i in range(n): hess[i][i] = penalty[i]
        for x, y in rows:
            z = sum(wi * xi for wi, xi in zip(w, x)); p = 1 / (1 + math.exp(-max(-30, min(30, z))))
            g = y - p; h = p * (1 - p)
            nz = [i for i in range(n) if x[i] != 0]
            for i in nz:
                grad[i] += g * x[i]
                for j in nz: hess[i][j] += h * x[i] * x[j]
        step = solve(hess, grad)
        w = [wi + si for wi, si in zip(w, step)]
        if max(abs(s) for s in step) < 1e-6: break
    return w[:k], {m: w[index[m]] for m in missiles}


def predict(shared, offsets, x, part):
    z = sum(a * b for a, b in zip(shared, x)) + offsets.get(part, 0.0)
    return 1 / (1 + math.exp(-max(-30, min(30, z))))


def scores(ps, ys):
    eps = 1e-4
    ll = -sum(y * math.log(max(p, eps)) + (1 - y) * math.log(max(1 - p, eps)) for p, y in zip(ps, ys)) / len(ys)
    return ll, sum((p - y) ** 2 for p, y in zip(ps, ys)) / len(ys)


def main():
    args = sys.argv[1:]
    out = None; folds = 5; range_only = '--range-only' in args
    if range_only: args.remove('--range-only')
    if '--out' in args: i = args.index('--out'); out = args[i + 1]; del args[i:i + 2]
    if '--folds' in args: i = args.index('--folds'); folds = int(args[i + 1]); del args[i:i + 2]
    rows = load(args, range_only)
    if len(rows) < 20: print(f"only {len(rows)} usable shots"); return
    xs = [vector(r) for r in rows]; ys = [float(r['hit'] == 'True') for r in rows]; parts = [r['part'] for r in rows]
    missiles = sorted(set(parts))
    print(f"{len(rows)} shots, {int(sum(ys))} hits ({100 * sum(ys) / len(ys):.0f}%), {len(missiles)} missiles")

    order = list(range(len(rows))); random.Random(1).shuffle(order)
    held = [None] * len(rows)
    for f in range(folds):
        test = set(order[f::folds]); train = [i for i in range(len(rows)) if i not in test]
        shared, offsets = fit([xs[i] for i in train], [ys[i] for i in train], [parts[i] for i in train], missiles)
        for i in test: held[i] = predict(shared, offsets, xs[i], parts[i])
    base = sum(ys) / len(ys)
    print(f"\nheld-out ({folds}-fold)     log loss  Brier")
    print(f"  base rate              {scores([base] * len(ys), ys)[0]:.3f}  {scores([base] * len(ys), ys)[1]:.3f}")
    logged = [(num(r, 'estimate', -1), y) for r, y in zip(rows, ys) if num(r, 'estimate', -1) >= 0]
    if logged:
        ll, br = scores([p for p, _ in logged], [y for _, y in logged])
        print(f"  logged estimate        {ll:.3f}  {br:.3f}   ({len(logged)} shots)")
    ll, br = scores(held, ys)
    print(f"  refit                  {ll:.3f}  {br:.3f}")

    print("\ncalibration of the held-out refit: predicted band, shots, predicted mean, hit rate")
    for lo in (0, 0.2, 0.4, 0.6, 0.8):
        sel = [(p, y) for p, y in zip(held, ys) if lo <= p < lo + 0.2 + (1e-9 if lo == 0.8 else 0)]
        if sel: print(f"  {lo:.1f}-{lo + 0.2:.1f}  {len(sel):4d}  {sum(p for p, _ in sel) / len(sel):.2f}  {sum(y for _, y in sel) / len(sel):.2f}")

    shared, offsets = fit(xs, ys, parts, missiles)
    print("\nweights (all shots):")
    for name, w in zip(NAMES, shared): print(f"  {name:16s} {w:+.3f}")
    for m in missiles: print(f"  bias.{m:18s} {offsets[m]:+.3f}  ({parts.count(m)} shots, {sum(y for y, p in zip(ys, parts) if p == m):.0f} hits)")
    if out:
        with open(out, 'w', newline='\n') as fh:
            fh.write(f"// Shot model weights fitted by bench/range/fit_shot_model.py on {len(rows)} shots\n")
            for name, w in zip(NAMES, shared): fh.write(f"{name} = {w:.4f}\n")
            for m in missiles: fh.write(f"bias.{m} = {offsets[m]:.4f}\n")
        print('wrote', out)


if __name__ == '__main__':
    main()
