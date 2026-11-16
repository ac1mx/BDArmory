# Close-in gunnery summary from a run's KSP.log (GunDiag lines, one per craft per second within 2.5 km of its target, and the
# SteeringMetrics gun counters): where the time goes (weapon, steering, planner manoeuvre), speeds, aim error, and how often the
# gun fired.
#   gun_report.py <KSP.log> [<KSP.log> ...]
import re
import sys
from collections import Counter, defaultdict

field = re.compile(r"(\w+)=(\S+)")
status = re.compile(r"status=(.*?) speed=")
by_craft = defaultdict(list)
for path in sys.argv[1:]:
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            i = line.find("GunDiag ")
            if i < 0: continue
            rest = line[i + 8:]
            craft = rest[:rest.index(" target=")]
            d = dict(field.findall(rest))
            m = status.search(rest)
            d["status"] = m.group(1) if m else "?"
            by_craft[craft].append(d)


def pct(n, total): return f"{100.0 * n / max(total, 1):.0f}%"


def quantiles(values):
    if not values: return "-"
    v = sorted(values)
    return "/".join(f"{v[int(q * (len(v) - 1))]:.0f}" for q in (0.1, 0.5, 0.9))


for craft, rows in sorted(by_craft.items()):
    n = len(rows)
    print(f"== {craft}: {n} s within 2.5 km")
    weapons = Counter(r["weapon"] for r in rows)
    print("  weapon:", ", ".join(f"{k} {pct(v, n)}" for k, v in weapons.most_common()))
    steer = Counter(r["steer"] for r in rows)
    print("  steer:", ", ".join(f"{k} {pct(v, n)}" for k, v in steer.most_common()))
    plans = Counter(r["status"] for r in rows)
    print("  status:", ", ".join(f"{k} {pct(v, n)}" for k, v in plans.most_common(8)))
    print("  speed p10/50/90:", quantiles([float(r["speed"]) for r in rows]), " dist:", quantiles([float(r["dist"]) for r in rows]),
          " alt:", quantiles([float(r["alt"]) for r in rows]))
    guns = [r for r in rows if r["weapon"] == "gun"]
    if guns:
        aims = [float(r["aim"]) for r in guns]
        print("  gun aim p10/50/90:", quantiles(aims), f" aim<2: {pct(sum(a < 2 for a in aims), len(aims))}  <5: {pct(sum(a < 5 for a in aims), len(aims))}",
              f" fired: {pct(sum(r['autoFire'] == 'True' for r in guns), len(guns))}")
        close = [r for r in guns if float(r["dist"]) < 1000]
        if close:
            ca = [float(r["aim"]) for r in close]
            print(f"  inside 1 km: {len(close)} s, aim p10/50/90 {quantiles(ca)}, fired {pct(sum(r['autoFire'] == 'True' for r in close), len(close))}")
        fails = Counter(r["fail"] for r in guns if r["autoFire"] != "True")
        print("  not fired:", ", ".join(f"{k} {v}" for k, v in fails.most_common(6)))
