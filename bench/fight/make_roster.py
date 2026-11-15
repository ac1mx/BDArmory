# Builds an AutoSpawn test folder from the save's craft files without touching them: each entry is a copy under a new craft name
# (team words "Red"/"Blue" in the name put it on that team in a run_fight.ps1 -Teams fight), optionally with some missiles kept
# from being fired at aircraft or swapped for a test copy of the part.
#   make_roster.py <dest folder> "<source.craft>|<craft name>[|noair=PART,PART][|swap=PART:NEWPART,...]" ...
import os
import sys

dest = sys.argv[1]
os.makedirs(dest, exist_ok=True)
for entry in sys.argv[2:]:
    fields = entry.split("|")
    source, name, noair, swaps = fields[0], fields[1], [], []
    for option in fields[2:]:
        key, _, value = option.partition("=")
        if key == "noair": noair = value.split(",")
        elif key == "swap": swaps = [pair.split(":") for pair in value.split(",")]
        else: sys.exit(f"unknown option {option}")
    with open(source, encoding="utf-8", newline="") as f: lines = f.read().split("\n")
    part, disabled, swapped = "", 0, 0
    for i, line in enumerate(lines):
        s = line.strip()
        if i == 0 and s.startswith("ship = "): lines[i] = line[:line.index("ship = ")] + "ship = " + name + ("\r" if line.endswith("\r") else "")
        if s.startswith("part = "): part = s[7:].rsplit("_", 1)[0]
        elif s.startswith("engageAir = True") and part in noair:
            lines[i] = line.replace("engageAir = True", "engageAir = False"); disabled += 1
        for old, new in swaps:  # Part ids are <part name>_<number>, wherever they are referenced.
            if old + "_" in lines[i]:
                if s.startswith("part = "): swapped += 1
                lines[i] = lines[i].replace(old + "_", new + "_")
    with open(os.path.join(dest, name + ".craft"), "w", encoding="utf-8", newline="") as f: f.write("\n".join(lines))
    print(f"{name}: {disabled} missiles not for aircraft, {swapped} parts swapped")
