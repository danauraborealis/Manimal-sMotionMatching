"""Knee pops: frames where a knee moves faster than the clip's own knee does, compared stage to stage.

    python tools/knee_pops.py <run dir or capture.json> [--threshold 4.0]

Knee speed alone is not a pop (a running knee legitimately passes 4-5 m/s in swing), so the count is taken at the
pose stage (after_visual, the clip as written) and at the final stage (after_lock, after the placer) on the same
sample times; the excess is what the placer added. Also reports the knee's lateral offset from the hip-ankle line
(outward positive) and the fraction of frames the knee points behind that line.
"""
import json
import math
import sys
from pathlib import Path


def _knee(sample, side):
    bones = {b["N"]: b for b in sample["Bones"] if b}
    return bones["Base Human%sCalf" % side]["P"], bones["Base Human%sThigh1" % side]["P"], bones["Base Human%sFoot" % side]["P"]


def analyse(path, threshold=4.0):
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    stages = {}
    for s in document["Samples"]:
        if s.get("Bones") and s.get("Stage") in ("after_visual", "after_lock"):
            stages.setdefault(s["Stage"], []).append(s)
    out = {"file": str(path), "threshold_mps": threshold}
    for stage, rows in stages.items():
        rows.sort(key=lambda s: s["Time"])
        fast = 0
        frames = 0
        lateral = []
        behind = 0
        for i in range(1, len(rows)):
            dt = rows[i]["Time"] - rows[i - 1]["Time"]
            if dt <= 0 or dt > 0.05:
                continue
            frames += 1
            for side, sign in (("L", -1), ("R", 1)):
                k1, h1, a1 = _knee(rows[i], side)
                k0, _, _ = _knee(rows[i - 1], side)
                if math.dist(k1, k0) / dt > threshold:
                    fast += 1
                axis = [a - h for a, h in zip(a1, h1)]
                la = math.sqrt(sum(c * c for c in axis)) or 1e-9
                axis = [c / la for c in axis]
                k = [x - h for x, h in zip(k1, h1)]
                along = sum(a * b for a, b in zip(k, axis))
                perp = [a - along * b for a, b in zip(k, axis)]
                lateral.append(perp[0] * sign * 1000)
                if perp[2] < -0.02:
                    behind += 1
        lateral.sort()
        n = len(lateral)
        out[stage] = {"frames": frames, "fast_knee_frames": fast, "knee_lateral_mm_p10_p50_p90": [round(lateral[int(n * .1)]), round(lateral[n // 2]), round(lateral[int(n * .9)])] if n else None,
                      "knee_lateral_mm_min_max": [round(lateral[0]), round(lateral[-1])] if n else None, "knee_behind_line_frames": behind}
    if "after_visual" in out and "after_lock" in out:
        out["fast_knee_frames_added_by_placer"] = out["after_lock"]["fast_knee_frames"] - out["after_visual"]["fast_knee_frames"]
    return out


def main():
    path = Path(sys.argv[1])
    if path.is_dir():
        status = path / "off/status.json" if (path / "off/status.json").exists() else path / "status.json"
        path = Path(json.loads(status.read_text(encoding="utf-8"))["LastCapturePath"])
    thr = float(sys.argv[sys.argv.index("--threshold") + 1]) if "--threshold" in sys.argv else 4.0
    print(json.dumps(analyse(path, thr), indent=1))


if __name__ == "__main__":
    main()
