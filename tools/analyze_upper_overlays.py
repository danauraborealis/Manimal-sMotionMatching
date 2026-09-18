"""Report overlay timing, native ownership, and upper-body recovery discontinuities."""
import argparse
import json
from pathlib import Path
from analyze_reaction_exit import angle

NATIVE = {"Jump", "JumpLanding", "FallDown", "ClimbOver", "ClimbUp", "VaultingFallDown", "VaultingLanding"}

def analyze(path):
    data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    final = {}
    for s in data["Samples"]:
        if s["Stage"] in ("after_visual", "after_lock") and s.get("Bones"):
            final[s["Frame"]] = s
    frames = sorted(final.values(), key=lambda s: s["Time"])
    episodes = []
    current = None
    for i, s in enumerate(frames):
        key = s.get("Pose", {}).get("UpperOverlay")
        if key and (current is None or current["clip"] != key):
            current = dict(clip=key, start=s["Time"], end=s["Time"], samples=0, native_owned_samples=0)
            episodes.append(current)
        if key:
            current["end"] = s["Time"]
            current["samples"] += 1
            entry = s["Pose"].get("SprintEntry") or {}
            current["native_owned_samples"] += int(entry.get("State") in NATIVE or entry.get("ManagedState") in NATIVE)
        else:
            current = None
    for event in episodes:
        peaks = {}
        for a, b in zip(frames, frames[1:]):
            if not event["end"] <= b["Time"] <= event["end"] + .6:
                continue
            before = {x["N"]: x["R"] for x in a["Bones"] if x}
            for bone in b["Bones"]:
                if bone and bone["N"] in before:
                    name = bone["N"]
                    if any(n in name for n in ("Spine", "Ribcage", "Upperarm", "Forearm")):
                        peaks[name] = max(peaks.get(name, 0), angle(before[name], bone["R"]))
        event["recovery_peak_degrees_per_sample"] = peaks
    return dict(capture=str(path), episodes=episodes,
                note="Per-sample rotations depend on frame rate; skeleton timing is not final mesh acceptance.")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture")
    parser.add_argument("--output")
    args = parser.parse_args()
    result = json.dumps(analyze(args.capture), indent=2)
    if args.output:
        Path(args.output).write_text(result, encoding="utf-8")
    else:
        print(result)
