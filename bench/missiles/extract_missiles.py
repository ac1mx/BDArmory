"""Extract missile part definitions (after ModuleManager patching) from GameData/ModuleManager.ConfigCache.
Usage: python extract_missiles.py <ConfigCache> <out.json> [part names...]   (no names: every part with a MissileLauncher module)
Writes {part name: {"mass": t, "title": str, "MissileLauncher": {field: value}, "warhead": {...}}}."""
import json, re, sys

def parse_nodes(lines):
    """Yield (type, dict, children) trees for top-level nodes in a list of cfg lines."""
    root = {'_type': 'root', '_vals': {}, '_nodes': []}
    stack = [root]; pending = None
    for raw in lines:
        line = raw.split('//', 1)[0].strip()
        if not line: continue
        while line:
            if line.startswith('{'):
                node = {'_type': pending or '', '_vals': {}, '_nodes': []}
                stack[-1]['_nodes'].append(node); stack.append(node); pending = None
                line = line[1:].strip(); continue
            if line.startswith('}'):
                if len(stack) > 1: stack.pop()
                line = line[1:].strip(); continue
            m = re.match(r'([^{}=]+?)\s*=\s*([^{}]*)', line)
            if m:
                stack[-1]['_vals'].setdefault(m.group(1).strip(), m.group(2).strip())
                line = line[m.end():].strip(); pending = None; continue
            m = re.match(r'([^{}]+)', line)
            pending = m.group(1).strip(); line = line[m.end():].strip()
    return root['_nodes']

def main():
    cache, out = sys.argv[1], sys.argv[2]
    wanted = set(sys.argv[3:])
    parts = {}
    depth = 0; capture = None; part_depth = -1
    with open(cache, encoding='utf-8', errors='replace') as f:
        prev = ''
        for line in f:
            s = line.strip()
            opens = s.count('{'); closes = s.count('}')
            if capture is None and s == '{' and prev == 'PART':
                capture = ['PART', '{']; part_depth = depth
            elif capture is not None:
                capture.append(line)
            depth += opens - closes
            if capture is not None and depth <= part_depth:
                nodes = parse_nodes(capture)
                capture = None
                if nodes:
                    p = nodes[0]
                    name = p['_vals'].get('name', '').replace('_', '.')
                    ml = [m for m in p['_nodes'] if m['_type'] == 'MODULE' and m['_vals'].get('name') == 'MissileLauncher']
                    if ml and (not wanted or name in wanted):
                        wh = [m for m in p['_nodes'] if m['_type'] == 'MODULE' and m['_vals'].get('name') in ('BDExplosivePart', 'BDWarheadBase', 'BDCustomWarhead', 'BDModuleNuke')]
                        parts[name] = {'mass': float(p['_vals'].get('mass', '0') or 0), 'title': p['_vals'].get('title', ''),
                                       'MissileLauncher': ml[0]['_vals'], 'warhead': wh[0]['_vals'] if wh else {}}
            if s: prev = s
    json.dump(parts, open(out, 'w'), indent=1)
    print(f'{len(parts)} missile parts written to {out}')

if __name__ == '__main__':
    main()
