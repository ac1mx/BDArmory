"""Close-in gunfight summary of a run_fight.ps1 run folder (needs -FlightRecorder): per craft, how rounds ended, how the fight was
flown (close, separated, low, slow), stick pushes, gun time near the nose, and the planner's flow and extend counters.
usage: dogfight_report.py <run folder> [more run folders]"""
import csv, glob, os, re, sys
from collections import defaultdict

LOST = re.compile(r'DuelRound lost by (.+?) after (\d+)s, last altitude (-?\d+)m,(?: largest part loss ([\d.]+)t,)? last status (.*)$')
PASS = re.compile(r'Pass (.+?) vs (.+?): (OneCircle|TwoCircle)')
MANOEUVRE = re.compile(r'Manoeuvre (.+?): (.+?) against ')
HITS = re.compile(r'SteeringMetrics (.+?) adaptive=.*? hits=(\d+)/(\d+) ')
GUNDIAG = re.compile(r'GunDiag (.+?) target=.*? dist=(\d+) .*?weapon=(\S+) .*?autoFire=(\w+) .*?targetAspect=(\d+) .*?bore=(-?[\d.]+)')
METRIC = re.compile(r'SteeringMetrics (.+?) adaptive=.*?bfmSecond=reverse (\d+)/pursue (\d+)/extend (\d+)/chop (\d+) crossings=(\d+) scissors=([\d.]+)s bugouts=(\d+)')


def craft_of(path):
    return re.sub(r'^flightdata-\d+-\d+-', '', os.path.basename(path))[:-4]


def summarise(run):
    stats = defaultdict(lambda: defaultdict(float))
    log = os.path.join(run, 'KSP.log')
    lines = open(log, encoding='utf-8', errors='replace').read().splitlines() if os.path.exists(log) else []
    for line in lines:
        m = LOST.search(line)
        if m:
            craft, status, loss = m.group(1), m.group(5), float(m.group(4) or 0)
            stats[craft]['losses'] += 1
            kind = 'shot' if loss >= 0.3 and not status.startswith('Terrain') else 'crash' if status.startswith('Terrain') or int(m.group(3)) < 30 else 'other'
            stats[craft]['loss_' + kind] += 1
        m = PASS.search(line)
        if m: stats[m.group(1)]['pass_' + m.group(3)] += 1
        m = MANOEUVRE.search(line)
        if m: stats[m.group(1)]['m:' + m.group(2)] += 1
        m = HITS.search(line)
        if m:  # Competition scores run for the whole competition: the last line per craft holds the totals.
            stats[m.group(1)]['hits'] = int(m.group(2)); stats[m.group(1)]['shots'] = int(m.group(3))
        m = GUNDIAG.search(line)
        if m and int(m.group(2)) < 600 and int(m.group(5)) > 150:  # In the saddle: within 600 m, astern of it (1 Hz samples).
            s = stats[m.group(1)]
            s['saddle'] += 1
            if m.group(3) == 'gun':
                s['saddle_gun'] += 1
                if m.group(4) == 'True': s['saddle_fire'] += 1
                bore = float(m.group(6))
                if 0 <= bore < 0.5: s['saddle_bore05'] += 1
    best = {}
    for line in lines:  # Per-life counters reset on respawn: take the largest per craft within each life (approximation: max overall).
        m = METRIC.search(line)
        if m:
            c = m.group(1)
            vals = [int(m.group(i)) for i in range(2, 7)] + [float(m.group(7)), int(m.group(8))]
            prev = best.get(c, [0] * 7)
            best[c] = [max(a, b) for a, b in zip(prev, vals)]
    for c, v in best.items():
        for k, x in zip(['2nd_reverse', '2nd_pursue', '2nd_extend', '2nd_chop', 'crossings', 'scissors_s', 'bugouts'], v): stats[c][k] = x
    for path in sorted(glob.glob(os.path.join(run, 'flightdata', 'flightdata-*.csv'))):
        c = craft_of(path)
        s = stats[c]
        prev_d = None
        for r in csv.DictReader(open(path)):
            if r.get('statusText') is None: continue
            try:
                t = float(r['time']); dt = float(r['dt'])
            except ValueError:
                continue
            d = float(r['targetDistance']); v = float(r['speed']); alt = float(r['radarAlt'])
            status = r['statusText']
            if d < 0 or status.startswith('Orbiting'): prev_d = None; continue
            s['time'] += dt
            # The range opening fast inside 3 km: one of the pair running (who runs shows in 'extending' and the status).
            if prev_d is not None and 500 < d < 3000 and (d - prev_d) / max(dt, 1e-3) > 20: s['opening'] += dt
            prev_d = d
            if d < 1500: s['close'] += dt
            if d > 3000: s['separated'] += dt
            if alt < 1000: s['low'] += dt
            if v < 120: s['slow'] += dt
            if status.startswith('Extending'): s['extending'] += dt
            if status.startswith('Evading'): s['evading'] += dt
            if float(r['pitch']) < -0.5: s['push'] += dt
            if r['gunSelected'] == '1' and d < 1500 and abs(float(r['pitchError'])) < 3 and abs(float(r['yawError'])) < 3: s['gun_on_nose'] += dt
            s['speed_sum'] += v * dt
    return stats


def main():
    for run in sys.argv[1:]:
        stats = summarise(run)
        print(f"== {os.path.basename(os.path.normpath(run))}")
        for c in sorted(stats):
            s = stats[c]
            T = max(s['time'], 1e-6)
            pct = lambda k: f"{100 * s[k] / T:4.0f}%"
            print(f"  {c:16s} losses {int(s['losses'])} (shot {int(s['loss_shot'])}, crash {int(s['loss_crash'])}, other {int(s['loss_other'])})"
                  f"  fight {T:5.0f}s close {pct('close')} sep>3km {pct('separated')} extending {pct('extending')} evading {pct('evading')}"
                  f" low<1km {pct('low')} slow<120 {pct('slow')} push {pct('push')} gunOnNose {s['gun_on_nose']:5.1f}s speed {s['speed_sum'] / T:4.0f}"
                  f"  passes one {int(s['pass_OneCircle'])}/two {int(s['pass_TwoCircle'])} scissors {s['scissors_s']:.0f}s crossings {int(s['crossings'])}"
                  f" 2nd extend {int(s['2nd_extend'])} bugouts {int(s['bugouts'])} opening {pct('opening')}")
            print(f"    gunnery: hits {int(s['hits'])}/{int(s['shots'])} shots, saddle {int(s['saddle'])}s (gun {int(s['saddle_gun'])}s,"
                  f" firing {int(s['saddle_fire'])}s, bore<0.5deg {int(s['saddle_bore05'])}s)")
            moves = sorted(((k[2:], int(v)) for k, v in s.items() if k.startswith('m:')), key=lambda x: -x[1])
            if moves: print("    manoeuvres: " + ", ".join(f"{k} {v}" for k, v in moves))


if __name__ == '__main__':
    main()
