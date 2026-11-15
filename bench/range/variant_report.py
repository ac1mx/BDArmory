# Missile variant comparison from a range run flown with run_range.ps1 -RepeatGeometry (each launch geometry flown once per variant,
# or several times by one missile): python variant_report.py <KSP.log>
# Per part: hit rate, what the misses were, how far the point steered at was from the craft at closest approach. Per geometry: which
# variants hit the same shot. For repeats of one part: the share of geometries whose outcome was the same every time.
import collections
import re
import statistics
import sys

kv = re.compile(r"(\w+)=(\S+)")


def number(shot, key, default=0.0):
    try:
        return float(shot.get(key, default))
    except ValueError:
        return default


def outcome(shot):
    if shot.get("hit") == "True":
        return "hit"
    if number(shot, "speedAtClosest") < number(shot, "targetSpeed") + 150:
        return "energy"
    if number(shot, "minDistance") < 400:
        return "decoyed" if number(shot, "aimError") > 150 else "flew past"
    return "never close"


def main(path):
    shots = []
    with open(path, encoding="utf-8", errors="replace") as log:
        for line in log:
            start = line.find("MissileShot missile=")
            if start >= 0:
                shot = dict(kv.findall(line[start:]))
                if shot.get("harness") == "range":
                    shots.append(shot)
    parts = sorted({s["part"] for s in shots})
    kinds = ["hit", "flew past", "decoyed", "energy", "never close"]
    print(f"{len(shots)} range shots, {len({s.get('geometry') for s in shots})} geometries")
    for mode in ("straight", "evade"):
        print(f"\ntarget {mode}")
        print(f"  {'part':<14}{'n':>4}" + "".join(f"{k:>13}" for k in kinds) + f"{'aim error m':>13}{'last second':>12}{'miss m':>8}{'turn g':>8}  guidance")
        for part in parts:
            group = [s for s in shots if s["part"] == part and s.get("targetMode") == mode]
            if not group:
                continue
            counts = collections.Counter(outcome(s) for s in group)
            misses = [number(s, "minDistance") for s in group if outcome(s) in ("flew past", "decoyed")]
            aim = [number(s, "aimError") for s in group if number(s, "aimError", -1) >= 0]
            # What a missile that flew past without a decoy was doing at closest approach: steering at a point off the craft, or out of turn.
            past = [s for s in group if outcome(s) == "flew past"]
            recent = [number(s, "aimErrorRecent") for s in past if "aimErrorRecent" in s]
            turn = [number(s, "turnG") for s in past if "turnG" in s]
            guidance = ",".join(sorted({s.get("guidance", "?") for s in group}))
            print(f"  {part:<14}{len(group):>4}" + "".join(f"{100 * counts[k] / len(group):>12.0f}%" for k in kinds)
                  + f"{statistics.median(aim) if aim else float('nan'):>13.0f}{statistics.median(recent) if recent else float('nan'):>12.0f}"
                  + f"{statistics.median(misses) if misses else float('nan'):>8.0f}{statistics.median(turn) if turn else float('nan'):>8.1f}  {guidance}")

    by_geometry = collections.defaultdict(list)
    for s in shots:
        by_geometry[s.get("geometry")].append(s)
    if len(parts) > 1:
        print("\nsame shot, variant against variant (row hit and column missed)")
        print(f"  {'':<14}" + "".join(f"{p:>14}" for p in parts))
        for a in parts:
            row = []
            for b in parts:
                wins = sum(1 for g in by_geometry.values()
                           if any(s["part"] == a and outcome(s) == "hit" for s in g) and any(s["part"] == b and outcome(s) != "hit" for s in g))
                row.append(wins if a != b else "-")
            print(f"  {a:<14}" + "".join(f"{str(w):>14}" for w in row))

    print("\nrepeat consistency (geometries a part flew more than once)")
    for part in parts:
        repeated = [[outcome(s) == "hit" for s in g if s["part"] == part] for g in by_geometry.values()]
        repeated = [r for r in repeated if len(r) > 1]
        if not repeated:
            continue
        same = sum(1 for r in repeated if all(r) or not any(r))
        print(f"  {part:<14}{len(repeated):>4} geometries, same outcome every time in {100 * same / len(repeated):.0f}%")


if __name__ == "__main__":
    main(sys.argv[1])
