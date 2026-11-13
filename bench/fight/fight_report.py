"""Score fight runs from KSP.log: fair-round wins/losses per craft, how losers died, round lengths, and the pilots' last metrics.
Usage: python fight_report.py <KSP.log> [<KSP.log> ...]"""
import collections, re, sys

WON = re.compile(r'DuelRound won by (.+?) after (\d+)s')
LOST = re.compile(r'DuelRound lost by (.+?) after (\d+)s, last altitude (-?\d+)m,(?: largest part loss ([\d.]+)t,)? last status (.*)$')
METRICS = re.compile(r'SteeringMetrics (.+?) adaptive=')
FIELD = re.compile(r'(\w+)=(\S+)')
DEBRIS = re.compile(r' Debris$')  # A craft that has lost parts is renamed; it's still the same pilot.
DRAW = re.compile(r'DuelRound draw: (.+?) after (\d+)s')
PLAN_METRICS = re.compile(r'(plan=.*? health=\S+)')  # The planner's counters (multi-word fields) from the last metrics line.
PLANNER = re.compile(r'Planner (.+?): (abort|no merge|disengaging|missile at|forcing shot|followUp shot|reshot shot|normal shot)')


def main():
    wins, losses, draws = collections.Counter(), collections.Counter(), collections.Counter()
    lengths, causes = collections.defaultdict(list), collections.defaultdict(collections.Counter)
    metrics = {}
    shots = collections.defaultdict(lambda: [0, 0])
    shot_rows = collections.defaultdict(list)
    planner = collections.defaultdict(collections.Counter)
    for path in sys.argv[1:]:
        for line in open(path, encoding='utf-8', errors='replace'):
            m = PLANNER.search(line)
            if m:
                planner[DEBRIS.sub('', m.group(1))][m.group(2).replace('missile at', 'written off')] += 1; continue
            m = DRAW.search(line)
            if m:
                draws[DEBRIS.sub('', m.group(1))] += 1; continue
            m = WON.search(line)
            if m:
                craft = DEBRIS.sub('', m.group(1)); wins[craft] += 1; lengths[craft].append(int(m.group(2))); continue
            m = LOST.search(line)
            if m:
                craft = DEBRIS.sub('', m.group(1)); losses[craft] += 1
                status = m.group(5).strip()
                # A part over 1 t shot off (a wing, the tail) usually ends in the sea: that's a hit, not a flying error.
                shot_apart = m.group(4) is not None and float(m.group(4)) > 1.0
                causes[craft]['shot apart' if shot_apart else 'low (<150 m)' if int(m.group(3)) < 150 else status.split('(')[0].strip()] += 1
                continue
            m = METRICS.search(line)
            if m:
                metrics[m.group(1)] = dict(FIELD.findall(line.split('SteeringMetrics ', 1)[1]))
                p = PLAN_METRICS.search(line)
                if p: metrics[m.group(1)]['planSummary'] = p.group(1)
            if 'MissileShot ' in line:
                d = dict(FIELD.findall(line.split('MissileShot ', 1)[1]))
                shooter = DEBRIS.sub('', d.get('from', '?').replace('_', ' '))
                shots[shooter][0] += 1; shots[shooter][1] += d.get('hit') == 'True'
                shot_rows[shooter].append(d)
    rounds = sum(losses.values())
    print(f"{rounds} decided rounds, {max(draws.values(), default=0)} drawn")
    for craft in sorted(set(wins) | set(losses) | set(draws)):
        w, l = wins[craft], losses[craft]
        ln = sorted(lengths[craft])
        print(f"  {craft:20s} won {w:3d} lost {l:3d} drew {draws[craft]:3d} ({100 * w / max(1, w + l):3.0f}% of decided)  median winning round {ln[len(ln) // 2] if ln else 0}s  losses: {dict(causes[craft])}")
    for craft, (n, h) in sorted(shots.items()):
        rows = shot_rows[craft]
        num = lambda k: [float(r[k]) for r in rows if k in r and r[k] not in ('', 'NaN')]
        med = lambda v: sorted(v)[len(v) // 2] if v else float('nan')
        est = num('estimate')
        dec = [x for x in num('decision') if x >= 0]
        print(f"  shots by {craft}: {n}, hits {h}; median range {med(num('range')):.0f} m, mean estimate {sum(est) / max(1, len(est)):.2f}"
              f"{f', mean decision {sum(dec) / len(dec):.2f}' if dec else ''} (hit rate {h / max(1, n):.2f}); by missile {dict(collections.Counter(r.get('part') for r in rows))};"
              f" misses {dict(collections.Counter(r.get('missReason') for r in rows if r.get('hit') != 'True'))}")
        kinds = collections.defaultdict(lambda: [0, 0])
        for r in rows:
            kinds[r.get('shotKind', '?')][0] += 1; kinds[r.get('shotKind', '?')][1] += r.get('hit') == 'True'
        if '?' not in kinds:
            print(f"    by kind (shots/hits): " + ', '.join(f"{k} {s}/{t}" for k, (s, t) in sorted(kinds.items())))
    for craft, c in sorted(planner.items()):
        print(f"  planner {craft}: {dict(c)}")
    keys = ['bfm', 'bfmReplans', 'bfmAimHandovers', 'aimTime', 'onTarget', 'energyModel', 'rollRate', 'liftCapability', 'planCost']
    for craft, d in sorted(metrics.items()):
        print(f"  {craft:20s} " + ' '.join(f"{k}={d.get(k, '-')}" for k in keys))
        if 'planSummary' in d: print(f"  {'':20s} planner={d.get('planner', '-')} {d['planSummary']}")


if __name__ == '__main__':
    main()
