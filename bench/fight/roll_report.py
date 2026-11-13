"""Roll behaviour from flight recorder CSVs (GameData/BDArmory/Logs/flightdata-*.csv): how often the roll stick and roll rate swing from one
side to the other, and whether the swinging follows the roll target moving or the craft overshooting a target that holds still. The roll
target's own rate is d(roll error)/dt plus or minus the roll rate, whichever sign convention makes it the smaller over the file.
Grouped by steer mode and AI status code, or with --by-angle by how far the target direction is off the nose.
Usage: python roll_report.py <flightdata.csv> [...] [--by-angle]"""
import collections, csv, math, sys

STICK_LEVEL, RATE_LEVEL = 0.3, 0.3  # Swings count when the stick / roll rate (rad/s) passes these on the other side.


def off_nose_band(r):
    angle = math.hypot(float(r['pitchError']), float(r['yawError']))
    return next(f"<{b}°" for b in (3, 8, 15, 30, 60, 999) if angle < b)


def load(path):
    out = []
    by_angle = '--by-angle' in sys.argv
    for r in csv.DictReader(open(path, encoding='utf-8', errors='replace')):
        try:
            out.append((float(r['time']), float(r['dt']), float(r['roll']), float(r['rollRate']), math.radians(float(r['rollError'])),
                        r.get('steerMode', '?'), off_nose_band(r) if by_angle else r.get('status', '?')))
        except (KeyError, ValueError, TypeError):  # A line still being written.
            pass
    return out


def main():
    groups = collections.defaultdict(lambda: collections.defaultdict(float))
    for path in [a for a in sys.argv[1:] if not a.startswith('--')]:
        rows = load(path)
        # Sign convention between the error and the rate.
        plus = minus = 0.0
        for a, b in zip(rows, rows[1:]):
            if b[0] <= a[0]: continue
            d = b[4] - a[4]; d -= 2 * math.pi * round(d / (2 * math.pi)); d /= b[0] - a[0]
            plus += (d + b[3]) ** 2; minus += (d - b[3]) ** 2
        sign = 1.0 if plus < minus else -1.0
        stick_side = rate_side = 0
        for a, b in zip(rows, rows[1:]):
            g = groups[(b[5], b[6])]
            g['time'] += b[1]; g['n'] += 1
            g['rate2'] += b[3] ** 2; g['err2'] += b[4] ** 2; g['sat'] += abs(b[2]) > 0.95
            if abs(b[2]) > STICK_LEVEL:
                side = 1 if b[2] > 0 else -1
                if stick_side and side != stick_side: g['stickSwings'] += 1
                stick_side = side
            if abs(b[3]) > RATE_LEVEL:
                side = 1 if b[3] > 0 else -1
                if rate_side and side != rate_side: g['rateSwings'] += 1
                rate_side = side
            if b[0] > a[0]:
                d = b[4] - a[4]; d -= 2 * math.pi * round(d / (2 * math.pi)); d /= b[0] - a[0]
                target = d + sign * b[3]
                g['target2'] += target ** 2
                if abs(target) > 3: g['jumps'] += 1  # Roll target moving faster than 3 rad/s: it jumped.
        print(f"{path.split(chr(92))[-1]}: error/rate sign {'+' if sign > 0 else '-'}")
    print(f"{'mode':5s} {'status':7s} {'time s':>7s} {'stick swings/s':>14s} {'rate swings/s':>13s} {'rate rms':>8s} {'target rms':>10s} {'jumps/s':>7s} {'err rms°':>8s} {'sat%':>5s}")
    for key in sorted(groups, key=lambda k: -groups[k]['time']):
        g = groups[key]
        if g['time'] < 5: continue
        print(f"{key[0]:5s} {key[1]:7s} {g['time']:7.0f} {g['stickSwings'] / g['time']:14.2f} {g['rateSwings'] / g['time']:13.2f} {math.sqrt(g['rate2'] / g['n']):8.2f}"
              f" {math.sqrt(g['target2'] / g['n']):10.2f} {g['jumps'] / g['time']:7.2f} {math.degrees(math.sqrt(g['err2'] / g['n'])):8.1f} {100 * g['sat'] / g['n']:5.0f}")


if __name__ == '__main__':
    main()
