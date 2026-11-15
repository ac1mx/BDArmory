# Seeker comparison from a fight flown with run_fight.ps1 -SmartCraft (one side's missiles smart, the other's stock):
# python seeker_report.py <KSP.log> [smart|advanced]
# With "advanced" the two arms are the craft whose missiles fly their homing law, motor mode, midcourse and salvo behaviour
# (run_fight.ps1 -AdvancedCraft) and those whose missiles don't; a second table then counts what those behaviours did.
# Per missile part and seeker: hit rate, hit rate with the target still alive at the end, how many shots a decoy took before closest
# approach and how those ended, how far the point steered at was from the craft in the last second, and the team losses.
import collections
import re
import statistics
import sys

kv = re.compile(r"(\w+)=(\S+)")
stamp = re.compile(r"^\[\w+ (\d\d):(\d\d):(\d\d)\.(\d+)\]")
decoy = re.compile(r"\[BDAStealth\] (Chaff|Flare): (.+?) (?:seduced off (.+?) for|decoyed)")


def seconds(line):
    m = stamp.match(line)
    return int(m[1]) * 3600 + int(m[2]) * 60 + int(m[3]) + int(m[4]) / 1000 if m else None


def number(shot, key):
    try:
        return float(shot.get(key, "nan"))
    except ValueError:
        return float("nan")


def share(group, test):
    return 100 * sum(1 for s in group if test(s)) / len(group) if group else float("nan")


def main(path, arm="smart"):
    shots, events = [], []
    with open(path, encoding="utf-8", errors="replace") as log:
        for line in log:
            m = decoy.search(line)
            if m:
                events.append((seconds(line), m[2].replace(" ", "").replace("-", ""), (m[3] or "").replace(" ", "_")))
                continue
            start = line.find("MissileShot missile=")
            if start >= 0 and "minDistance=" in line and "guidance=None" not in line:  # Not the stores a craft drops (tanks).
                shot = dict(kv.findall(line[start:]))
                shot["end"] = seconds(line)
                shots.append(shot)
    for s in shots:
        if "decoys" in s:
            # Counted on the missile itself; only a decoy before closest approach can have caused the miss.
            s["decoys"] = int(s["decoys"]) if 0 <= number(s, "lastDecoy") <= number(s, "timeToClosest") + 0.5 else 0
        else:
            # Older logs: decoy events joined by missile name, target and time (several missiles in the air at one target share them).
            launch = s["end"] - number(s, "flightTime")
            closest = launch + number(s, "timeToClosest")
            name = s["missile"].replace("-", "")
            s["decoys"] = sum(1 for t, missile, target in events if launch <= t <= closest + 0.5 and missile == name and target in ("", s["at"]))
        s["alive"] = s.get("targetDestroyed") != "True" or s["hit"] == "True"

    hit = lambda s: s["hit"] == "True"
    print(f"{len(shots)} shots")
    print(f"{'part':<14}{'seeker':<7}{'n':>4}{'hit':>6}{'alive n':>9}{'hit':>6}{'decoyed':>9}{'hit':>6}{'clean':>7}{'hit':>6}{'aim m':>7}{'miss m':>8}{'>60 off':>9}")
    for part in sorted({s["part"] for s in shots}):
        for smart in ("False", "True"):
            group = [s for s in shots if s["part"] == part and s.get(arm) == smart]
            if not group:
                continue
            alive = [s for s in group if s["alive"]]
            decoyed = [s for s in alive if s["decoys"] > 0]
            clean = [s for s in alive if s["decoys"] == 0]
            aim = [number(s, "aimErrorRecent") for s in alive if not hit(s) and number(s, "minDistance") < 400 and number(s, "aimErrorRecent") >= 0]
            miss = [number(s, "minDistance") for s in alive if not hit(s)]
            print(f"{part:<14}{(arm[:5] if smart == 'True' else 'stock' if arm == 'smart' else 'plain'):<7}{len(group):>4}{share(group, hit):>5.0f}%{len(alive):>9}{share(alive, hit):>5.0f}%"
                  f"{share(alive, lambda s: s['decoys'] > 0):>8.0f}%{share(decoyed, hit):>5.0f}%{len(clean):>7}{share(clean, hit):>5.0f}%"
                  f"{statistics.median(aim) if aim else float('nan'):>7.0f}{statistics.median(miss) if miss else float('nan'):>8.0f}"
                  f"{share(group, lambda s: abs(number(s, 'offBoresight')) > 60):>8.0f}%")
            reasons = collections.Counter(s.get("missReason", "?") for s in alive if not hit(s))
            print(f"{'':<21}misses: {dict(reasons)}")

    flown = [s for s in shots if "law" in s]
    if flown:
        mean = lambda values: statistics.mean(values) if values else float("nan")
        print()
        print(f"{'part':<14}{'arm':<7}{'n':>4}{'range km':>9}{'v@closest':>10}{'law s':>7}{'mid s':>7}{'lit s':>7}{'burn end':>9}{'hoj':>5}{'inertial':>9}{'salvo':>7}  roles")
        for part in sorted({s["part"] for s in flown}):
            for value in ("False", "True"):
                group = [s for s in flown if s["part"] == part and s.get("advanced") == value]
                if not group:
                    continue
                alive = [s for s in group if s["alive"]]
                roles = collections.Counter(s.get("salvoRole", "single") for s in group)
                print(f"{part:<14}{('adv' if value == 'True' else 'plain'):<7}{len(group):>4}{mean([number(s, 'range') for s in group]) / 1000:>9.1f}"
                      f"{mean([number(s, 'speedAtClosest') for s in alive]):>10.0f}{mean([number(s, 'lawTime') for s in group]):>7.1f}{mean([number(s, 'midcourseTime') for s in group]):>7.1f}"
                      f"{mean([number(s, 'sustainLit') for s in group if number(s, 'sustainLit') >= 0]):>7.1f}{mean([number(s, 'sustainEnd') for s in group if number(s, 'sustainEnd') >= 0]):>9.1f}"
                      f"{sum(1 for s in group if number(s, 'hojTime') > 0):>5}{sum(1 for s in group if number(s, 'inertialTime') > 0):>9}"
                      f"{sum(1 for s in group if s.get('salvoRole', 'single') not in ('single', 'lead')):>7}  {dict(roles)}")
                for role in ("left", "right", "high"):
                    members = [s for s in alive if s.get("salvoRole") == role]
                    if members:
                        print(f"{'':<25}{role}: {len(members)} shots, {share(members, hit):.0f}% hit")

    losses = collections.Counter()
    with open(path, encoding="utf-8", errors="replace") as log:
        for line in log:
            m = re.search(r"VesselSpawnerBase\]: Vessel (.+?) spawned!", line)
            if m:
                losses[m[1]] += 1
    if losses:
        # Every spawn after a craft's first replaces one that was lost.
        print("\nlosses:", {craft: n - 1 for craft, n in sorted(losses.items())})


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "smart")
