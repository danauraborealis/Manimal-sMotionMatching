"""Report torso rotation steps around reaction exits from full-bone captures."""
import argparse
import json
import math
from pathlib import Path


def angle(a, b):
    norm = math.sqrt(sum(v*v for v in a) * sum(v*v for v in b))
    return math.degrees(2 * math.acos(min(1, abs(sum(x*y for x, y in zip(a, b))) / norm))) if norm else 0


def analyze(path):
    data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    frames = [s for s in data["Samples"] if s["Stage"] == "after_lock" and s.get("Bones")]
    exits = []
    for i in range(1, len(frames)):
        before, after = frames[i-1], frames[i]
        if before.get("Pose", {}).get("Phase") != "Reaction" or after.get("Pose", {}).get("Phase") == "Reaction":
            continue
        steps = []
        for j in range(i, len(frames)):
            old, new = frames[j-1], frames[j]
            if new["Time"] - after["Time"] > .6:
                break
            a = {b["N"]: b for b in old["Bones"] if b}
            b = {b["N"]: b for b in new["Bones"] if b}
            name = "Base HumanSpine1"
            if name in a and name in b:
                degrees = angle(a[name]["R"], b[name]["R"])
                dt = new["Time"] - old["Time"]
                steps.append({"frame": new["Frame"], "degrees": degrees, "dt": dt,
                              "deg_per_second": degrees/dt if dt > 0 else 0})
        exits.append({"frame": after["Frame"], "time": after["Time"],
                      "last_clip_frame": before.get("Pose", {}).get("Frame"),
                      "boundary": steps[0] if steps else None,
                      "peak": max(steps, key=lambda s: s["deg_per_second"]) if steps else None})
    return {"capture": str(path), "exits": exits}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", nargs="+")
    args = parser.parse_args()
    print(json.dumps([analyze(p) for p in args.captures], indent=2))
