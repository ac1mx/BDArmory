"""Stall attribution for BDAPerf captures (run_stalls.ps1 / run_fight.ps1 -Sample): stall time per minute and per spawn/removal
cycle, split by owner (inclusive: a stall sample counts once for the first owner whose frame is on its stack, in the order below).
usage: stall_report.py <run folder> [more run folders]"""
import glob, os, re, sys
from collections import Counter

# First match on the stack wins.
OWNERS = [
    ('MAS cockpit props', ('AvionicsSystems.',)),
    ('RPM cockpit props', ('JSI.',)),
    ('vessel removal', ('RemoveVesselCoroutine', 'RemoveAllVessels', 'KerbalSafetyManager:RecoverVesselNow', 'RecoverVesselFromFlight')),
    ('part death/decouple', ('Part.Die', 'Part:Die', 'Part.decouple', 'Part:decouple', 'Part:Decoupling')),
    ('vessel spawn', ('VesselSpawner', 'ContinuousSpawning', 'SpawnUtils', 'ShipConstruction:LoadShip', 'ShipConstruct:', 'ProtoVessel:Load', 'ProtoPartSnapshot:Load')),
    ('vessel create', ('Vessel:Initialize', 'Vessel:Awake', 'VesselModuleManager')),
    ('focus switch', ('setActiveVessel', 'SetActiveVessel', 'CameraManager:SetCameraFlight', 'Vessel:MakeActive')),
    ('IVA spawn', ('SpawnIVA', 'InternalModel', 'InternalProp')),
    ('drag cubes', ('DragCube',)),
    ('reflection probes', ('TickRealtimeProbes',)),
    ('buoyancy', ('PartBuoyancy',)),
    ('radar render', ('RenderVesselRadarSnapshot', 'RadarUtils:Render')),
    ('GC', ('GC_', 'gc_', 'GarbageCollect', 'mono-2.0-bdwgc.dll+0x30')),
    ('render wait', ('FinishFrameRendering',)),
]


def owner(stack):
    for name, patterns in OWNERS:
        if any(p in stack for p in patterns): return name
    return 'other'


def read_ms_per_sample(report):
    text = open(report, encoding='utf-8', errors='replace').read()
    m = re.search(r'stall frames: (\d+) frames over ([\d.]+) ms, (\d+) ms total, (\d+) samples', text)
    if not m: return None
    frames, threshold, total, samples = int(m.group(1)), float(m.group(2)), float(m.group(3)), int(m.group(4))
    d = re.search(r'duration_s=([\d.]+)', text)
    mx = re.search(r'max_ms=([\d.]+)', text)
    return dict(frames=frames, threshold=threshold, total=total, samples=samples, ms=total / max(samples, 1),
                duration=float(d.group(1)) if d else 0, max=float(mx.group(1)) if mx else 0)


def main():
    for run in sys.argv[1:]:
        reports = [r for r in glob.glob(os.path.join(run, 'bench_run_2*.txt'))]
        if not reports:
            print(f"{run}: no main capture"); continue
        report = sorted(reports)[-1]
        stamp = os.path.basename(report)[len('bench_run_'):-4]
        info = read_ms_per_sample(report)
        stacks = os.path.join(run, f'stacks_run_stalls_{stamp}.folded')
        counts = Counter(); total = 0; other = Counter()
        for line in open(stacks, encoding='utf-8', errors='replace'):
            if line.startswith('#') or not line.strip(): continue
            s, _, n = line.rstrip().rpartition(' ')
            try: n = int(n)
            except ValueError: continue
            o = owner(s)
            counts[o] += n; total += n
            if o == 'other':
                frames = [f for f in s.split(';') if not any(x in f for x in ('wrapper', 'InvokeMoveNext', 'PlayerLoop', 'ScriptRun', 'Update.', 'FixedUpdate.', 'PostLateUpdate.', 'PreLateUpdate.', '(between'))]
                other[frames[0][:90] if frames else s.split(';')[0][:90]] += n
        log = os.path.join(run, 'KSP.log')
        cycles = 0; focusDies = 0
        if os.path.exists(log):
            text = open(log, encoding='utf-8', errors='replace').read()
            cycles = len(re.findall(r'DuelRound (?:draw|won by)', text)) // 2 + len(re.findall(r'DuelRound lost by', text))
            focusDies = len(re.findall(r'Removing the focussed vessel .* part by part', text))
        minutes = info['duration'] / 60 if info['duration'] else 1
        print(f"== {os.path.basename(os.path.normpath(run))}: {info['duration']:.0f}s, {info['frames']} stall frames > {info['threshold']:.1f} ms, "
              f"{info['total'] / 1000:.1f} s stalled ({info['total'] / minutes:.0f} ms/min), worst {info['max']:.0f} ms, ~{cycles} rounds"
              f"{f', {info['total'] / cycles:.0f} ms/round' if cycles else ''}, focussed vessel killed part by part {focusDies}x")
        for name, n in counts.most_common():
            print(f"  {n * info['ms']:8.0f} ms  {100 * n / max(total, 1):5.1f}%  {name}")
        print("  other, by outermost managed frame:")
        for name, n in other.most_common(15):
            print(f"    {n * info['ms']:8.0f} ms  {name}")


if __name__ == '__main__':
    main()
