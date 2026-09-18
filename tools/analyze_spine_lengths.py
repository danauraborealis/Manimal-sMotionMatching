"""Measure captured torso segment lengths; compare runs to expose positional pinning."""
import argparse
import json
import math
from collections import defaultdict
from pathlib import Path


def analyze(path):
    capture = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    lengths = defaultdict(list)
    chain = ["Pelvis", "Spine1", "Spine2", "Spine3", "Ribcage"]
    for sample in capture.get("Samples", []):
        bones = {b["N"]: b["P"] for b in sample.get("Bones") or [] if b}
        for parent, child in zip(chain, chain[1:]):
            a, b = bones.get("Base Human" + parent), bones.get("Base Human" + child)
            if a is None or b is None:
                continue
            distance = math.dist(a, b)
            if math.isfinite(distance):
                lengths[(sample["Stage"], parent + "->" + child)].append(distance)
    return {
        "capture": str(path),
        "segments": [
            {"stage": stage, "segment": segment, "samples": len(values),
             "min_m": min(values), "max_m": max(values),
             "range_mm": 1000 * (max(values) - min(values))}
            for (stage, segment), values in sorted(lengths.items())
        ],
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", nargs="+")
    args = parser.parse_args()
    print(json.dumps([analyze(path) for path in args.captures], indent=2))
