"""Find pose discontinuities (snaps) in a capture and say what each one coincides with.

Sliding metrics miss snaps: a foot can jump 10 cm in two frames and still average out. A snap shows up as a
spike in the SECOND difference of a joint's world position (smooth body motion and rotation don't produce those),
so this flags frames where that spike is far above the run's own noise floor, then attributes each one to the
event at that moment: a clip change, a phase change, a foot-lock state flip, or none of those.
"""
import argparse
import json
import math
import statistics
from collections import Counter
from pathlib import Path

JOINTS = {"foot": "FootPosition", "knee": "KneePosition"}


def vec(value):
    value = value or {}
    return (value.get("X", 0.0), value.get("Y", 0.0), value.get("Z", 0.0))


def length(a):
    return math.sqrt(sum(x * x for x in a))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--stage", default="pre_render")
    parser.add_argument("--sigma", type=float, default=8.0, help="multiples of the run's own noise floor")
    parser.add_argument("--top", type=int, default=15)
    parser.add_argument("--floor-mps", type=float, default=2.0, help="ignore velocity changes smaller than this")
    args = parser.parse_args()

    document = json.loads(args.capture.read_text(encoding="utf-8"))
    rows = sorted((s for s in document["Samples"] if s.get("Stage") == args.stage), key=lambda s: s.get("Frame", 0))
    if len(rows) < 8:
        raise SystemExit("not enough samples")

    events = []
    for name, field in JOINTS.items():
        for side in ("Left", "Right"):
            series = []
            for sample in rows:
                leg = sample.get("Grounder", {}).get(side + "Leg", {})
                series.append((sample, vec(leg.get(field))))
            # velocity change per frame, so a long frame (bridge stall, hitch) isn't mistaken for a snap
            jumps = []
            for i in range(2, len(series)):
                (s0, a), (s1, b), (sample, c) = series[i - 2], series[i - 1], series[i]
                dt0 = s1.get("Time", 0) - s0.get("Time", 0)
                dt1 = sample.get("Time", 0) - s1.get("Time", 0)
                if dt0 <= 1e-4 or dt1 <= 1e-4 or dt1 > 0.05:
                    continue
                v0 = tuple((y - x) / dt0 for x, y in zip(a, b))
                v1 = tuple((y - x) / dt1 for x, y in zip(b, c))
                jumps.append((length(tuple(y - x for x, y in zip(v0, v1))), sample))
            if not jumps:
                continue
            floor = statistics.median(j for j, _ in jumps) or 0.1
            for magnitude, sample in jumps:
                if magnitude > floor * args.sigma and magnitude > args.floor_mps:
                    events.append({"joint": name, "side": side, "mps": round(magnitude, 2),
                                   "time": round(sample.get("Time", 0), 2), "frame": sample.get("Frame"),
                                   "pose": sample.get("Pose", {})})

    # what changed around each flagged frame
    index = {s.get("Frame"): i for i, s in enumerate(rows)}
    causes = Counter()
    for event in events:
        i = index.get(event["frame"])
        window = rows[max(0, i - 4):min(len(rows), i + 5)] if i is not None else []
        clips = {s.get("Pose", {}).get("Clip") for s in window}
        phases = {s.get("Pose", {}).get("Phase") for s in window}
        locks = {(s.get("Pose", {}).get("LockedL"), s.get("Pose", {}).get("LockedR")) for s in window}
        weights = [s.get("Pose", {}).get("Weight", 0.0) for s in window]
        cause = ("clip change" if len(clips) > 1 else
                 "phase change" if len(phases) > 1 else
                 "lock flip" if len(locks) > 1 else
                 "blending" if weights and 0.01 < min(weights) < 0.99 else
                 "steady")
        event["cause"] = cause
        event["clips"] = sorted(c for c in clips if c)
        causes[cause] += 1

    events.sort(key=lambda e: -e["mps"])
    print(json.dumps({
        "stage": args.stage,
        "frames": len(rows),
        "snaps": len(events),
        "by_cause": dict(causes),
        "by_phase": dict(Counter(e["pose"].get("Phase") for e in events)),
        "worst": [{k: e[k] for k in ("mps", "time", "joint", "side", "cause", "clips")} | {"phase": e["pose"].get("Phase")}
                  for e in events[:args.top]],
    }, indent=1))


if __name__ == "__main__":
    main()
