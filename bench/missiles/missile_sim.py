"""Offline replica of BDArmory's missile flight model, for comparing missile configs (point mass, BDA's own aero curves).
Physics as in MissileLauncher/MissileGuidance.DoAeroForces: thrust along the body during boost then cruise, lift = q*liftArea*GLOBAL_LIFT*CL(AoA)
across the velocity, drag = q*dragArea*GLOBAL_DRAG*CD(AoA) (dynamic pressure in Pa applied as kN on a mass in t), gravity, KSP's own drag off.
Steering: proportional navigation, the angle of attack it needs capped by maxAoA and by the torque the missile can hold (getTorqueAoALimit).
Usage: python missile_sim.py <missiles.json> [part names...]"""
import json, math, sys

GLOBAL_LIFT, GLOBAL_DRAG, G0 = 0.25, 6.0, 9.81

def hermite(points):
    """KSP FloatCurve (Hermite with in/out tangents); points (t, v, in, out), tangents default to 0."""
    pts = [(p[0], p[1], p[2] if len(p) > 2 else 0.0, p[3] if len(p) > 3 else 0.0) for p in points]
    def f(x):
        if x <= pts[0][0]: return pts[0][1]
        if x >= pts[-1][0]: return pts[-1][1]
        for (t0, v0, _, o0), (t1, v1, i1, _) in zip(pts, pts[1:]):
            if x <= t1:
                dt = t1 - t0; s = (x - t0) / dt
                h00 = 2*s**3 - 3*s**2 + 1; h10 = s**3 - 2*s**2 + s; h01 = -2*s**3 + 3*s**2; h11 = s**3 - s**2
                return h00*v0 + h10*dt*o0 + h01*v1 + h11*dt*i1
    return f

CL = hermite([(0, 0, 0.04375, 0.04375), (8, 0.35, 0.04801136, 0.04801136), (30, 1.5), (65, 0.6), (90, 0.7)])
CD = hermite([(0, 0.00215, 0, 0), (5, 0.00285, 0.0002775, 0.0002775), (30, 0.01, 0.0002142857, 0.01115385), (55, 0.3, 0.008434067, 0.008434067), (90, 0.5, 0.005714285, 0.005714285)])

def density(alt):
    """Approximate Kerbin air density (kg/m^3); calibrate against logged flights before trusting absolute ranges."""
    return 1.225 * math.exp(-max(alt, 0.0) / 5600.0)

def first(value, default):
    try: return float(str(value).split(',')[0])
    except (TypeError, ValueError): return default

class Missile:
    def __init__(self, name, spec):
        m = spec['MissileLauncher']
        self.name = name
        self.mass = spec['mass']
        self.thrust, self.boost = first(m.get('thrust'), 30), first(m.get('boostTime'), 2.2)
        self.cruiseThrust, self.cruise = first(m.get('cruiseThrust'), 3), first(m.get('cruiseTime'), 30)
        self.drop = first(m.get('dropTime'), 0.4)
        self.lift = first(m.get('liftArea'), 0.015)
        drag = first(m.get('dragArea'), -1)
        self.drag = drag if drag > 0 else self.lift
        self.maxAoA = first(m.get('maxAoA'), 35)
        self.maxTorque = first(m.get('maxTorque'), 90)
        self.optimumAirspeed = first(m.get('optimumAirspeed'), 220)

    def thrust_at(self, t):
        if t < self.drop: return 0.0
        t -= self.drop
        if t < self.boost: return self.thrust
        if t < self.boost + self.cruise: return self.cruiseThrust
        return 0.0

    def kinematic_speed(self, rho, speed_of_sound=320.0):
        """MissileLauncher.GetKinematicSpeed: the speed at which maxAoA only gives 4 g, capped at half the speed of sound."""
        return min(math.sqrt(4 * self.mass * G0 / (0.5 * rho * self.lift * GLOBAL_LIFT * CL(self.maxAoA))), 0.5 * speed_of_sound)

    def aoa_limit(self, q):
        """Largest AoA whose aerodynamic torque (centre of lift 1 m behind the CoM) stays inside 90% of maxTorque."""
        limit = 0.9 * self.maxTorque
        aoa = 0.0
        while aoa < self.maxAoA:
            a = aoa + 0.5
            r = math.radians(a)
            torque = q * (self.lift * GLOBAL_LIFT * max(CL(a), 0) * math.cos(r) + self.drag * GLOBAL_DRAG * max(CD(a), 0) * math.sin(r))
            if torque > limit: break
            aoa = a
        return max(aoa, 0.5)

def fly(missile, launch_pos, launch_vel, target_pos, target_vel, target_accel=(0.0, 0.0, 0.0), dt=0.05, t_max=150.0, nav=3.0, blast=15.0):
    """Fly one shot in a flat-earth frame (x, y horizontal, z up). Returns dict: hit, time, miss distance, speed and closing at closest."""
    px, py, pz = launch_pos; vx, vy, vz = launch_vel
    tx, ty, tz = target_pos; tvx, tvy, tvz = target_vel
    best = (1e18, 0.0, 0.0, 0.0)
    t = 0.0; prev_los = None
    while t < t_max:
        rx, ry, rz = tx - px, ty - py, tz - pz
        r = math.sqrt(rx*rx + ry*ry + rz*rz)
        speed = math.sqrt(vx*vx + vy*vy + vz*vz)
        rvx, rvy, rvz = tvx - vx, tvy - vy, tvz - vz
        closing = -(rx*rvx + ry*rvy + rz*rvz) / max(r, 1e-6)
        # Closest approach within the coming step (relative motion taken as straight over one step).
        rv2 = rvx*rvx + rvy*rvy + rvz*rvz
        s = min(max(-(rx*rvx + ry*rvy + rz*rvz) / rv2, 0.0), dt) if rv2 > 0 else 0.0
        cx, cy, cz = rx + rvx*s, ry + rvy*s, rz + rvz*s
        cpa = math.sqrt(cx*cx + cy*cy + cz*cz)
        if cpa < best[0]: best = (cpa, t + s, speed, closing)
        if best[0] < blast * 0.5 or (t > 3.0 and closing < 0 and r > best[0] + 50): break
        if pz < 0: break
        rho = density(pz); q = 0.5 * rho * speed * speed
        thrust = missile.thrust_at(t)
        if thrust == 0 and t > missile.drop + missile.boost and speed < missile.kinematic_speed(rho): break  # MissileLauncher.CheckMiss gives up here.
        ux, uy, uz = vx / speed, vy / speed, vz / speed
        # Proportional navigation: lateral acceleration nav * closing * LOS rate, perpendicular to the velocity.
        ax_c = ay_c = az_c = 0.0
        if t >= missile.drop:
            wx = (ry*rvz - rz*rvy) / (r*r); wy = (rz*rvx - rx*rvz) / (r*r); wz = (rx*rvy - ry*rvx) / (r*r)  # LOS rate (r x v)/r^2
            lx, ly, lz = rx / r, ry / r, rz / r
            # a = N * Vc * (omega x los), plus gravity compensation
            ax_c = nav * closing * (wy*lz - wz*ly); ay_c = nav * closing * (wz*lx - wx*lz); az_c = nav * closing * (wx*ly - wy*lx) + G0
            d = ax_c*ux + ay_c*uy + az_c*uz; ax_c -= d*ux; ay_c -= d*uy; az_c -= d*uz
        a_cmd = math.sqrt(ax_c*ax_c + ay_c*ay_c + az_c*az_c)
        # AoA needed for that acceleration, capped.
        aoa_cap = min(missile.maxAoA, missile.aoa_limit(q)) if q > 1 else 0.0
        aoa = 0.0
        if a_cmd > 1e-3 and q > 1:
            lo, hi = 0.0, aoa_cap
            def lat(a): return (q * missile.lift * GLOBAL_LIFT * max(CL(a), 0) + thrust * math.sin(math.radians(a))) / missile.mass
            if lat(hi) <= a_cmd: aoa = hi
            else:
                for _ in range(20):
                    mid = 0.5 * (lo + hi)
                    if lat(mid) < a_cmd: lo = mid
                    else: hi = mid
                aoa = hi
        lateral = (q * missile.lift * GLOBAL_LIFT * max(CL(aoa), 0) + thrust * math.sin(math.radians(aoa))) / missile.mass
        along = (thrust * math.cos(math.radians(aoa)) - q * missile.drag * GLOBAL_DRAG * max(CD(aoa), 0)) / missile.mass
        if a_cmd > 1e-6: nx, ny, nz = ax_c / a_cmd, ay_c / a_cmd, az_c / a_cmd
        else: nx = ny = nz = 0.0
        ax = along*ux + lateral*nx; ay = along*uy + lateral*ny; az = along*uz + lateral*nz - G0
        vx += ax*dt; vy += ay*dt; vz += az*dt
        px += vx*dt; py += vy*dt; pz += vz*dt
        tvx += target_accel[0]*dt; tvy += target_accel[1]*dt; tvz += target_accel[2]*dt
        tx += tvx*dt; ty += tvy*dt; tz += tvz*dt
        t += dt
    miss, when, spd, clo = best
    return {'hit': miss <= blast, 'miss': miss, 'time': when, 'speed': spd, 'closing': clo}

def max_range(missile, alt, launch_speed, target_speed, aspect_deg, target_alt=None, blast=15.0, lo=500.0, hi=200000.0):
    """Largest launch distance at which the missile still reaches a target flying straight (aspect 0 = coming at the shooter)."""
    target_alt = alt if target_alt is None else target_alt
    def ok(rng):
        a = math.radians(aspect_deg)
        res = fly(missile, (0, 0, alt), (launch_speed, 0, 0), (rng, 0, target_alt), (-target_speed*math.cos(a), target_speed*math.sin(a), 0), blast=blast)
        return res['hit'], res
    if not ok(lo)[0]: return 0.0, None
    good = lo; res_good = None
    while hi - good > 250:
        mid = 0.5 * (good + hi)
        h, res = ok(mid)
        if h: good, res_good = mid, res
        else: hi = mid
    return good, res_good

def report(missiles):
    """Per missile: motor delta-v in vacuum, speed at motor cut-out (level from 300 m/s at 6 km), longest reach against a straight-flying
    280 m/s target coming at the shooter (hot) and running away (cold) at 9 km and 3 km, and turning acceleration at 600 m/s, 6 km."""
    print(f"{'missile':13s} {'dv vac':>7s} {'v@cut':>7s} {'hot9k':>7s} {'cold9k':>7s} {'hot3k':>7s} {'cold3k':>7s} {'g@600,6k':>9s}")
    for m in missiles:
        dv = (m.thrust * m.boost + m.cruiseThrust * m.cruise) / m.mass
        res = fly(m, (0, 0, 6000), (300, 0, 0), (1e7, 0, 6000), (0, 0, 0), t_max=m.drop + m.boost + m.cruise)
        rh9, _ = max_range(m, 9000, 300, 280, 0)
        rc9, _ = max_range(m, 9000, 300, 280, 180)
        rh3, _ = max_range(m, 3000, 300, 280, 0)
        rc3, _ = max_range(m, 3000, 300, 280, 180)
        q = 0.5 * density(6000) * 600 ** 2
        aoa = min(m.maxAoA, m.aoa_limit(q))
        g = q * m.lift * GLOBAL_LIFT * CL(aoa) / m.mass / G0
        print(f"{m.name:13s} {dv:7.0f} {res['speed']:7.0f} {rh9 / 1000:7.1f} {rc9 / 1000:7.1f} {rh3 / 1000:7.1f} {rc3 / 1000:7.1f} {g:9.0f}")


if __name__ == '__main__':
    data = json.load(open(sys.argv[1]))
    names = sys.argv[2:] or list(data)
    report([Missile(n, data[n]) for n in names if n in data])
