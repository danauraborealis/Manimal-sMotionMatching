"""Planted-sole hold from the placer probe: how far each pinned sole moved while the placer held it.

    python tools/plant_hold.py <run dir or capture.json> [--phase SprintCycle]

Complements gait_metrics (which judges contacts from bone heights and cannot tell a heel-to-toe roll from a
slide): a plant here is a run of frames where the probe reports the foot frozen/locked with full authority and
the stride at its stance (progression <= 0.02 or >= 0.99). Reports endpoint displacement, maximum excursion and
travel of the placed sole per plant.
"""
import json
import math
import statistics
import sys
from pathlib import Path


def analyse(path, phase="SprintCycle", min_frames=8):
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    rows = sorted([s for s in document["Samples"] if s.get("Stage") == "after_lock" and s.get("Bones")], key=lambda s: s["Time"])
    plants = []
    for side in ("L", "R"):
        window = []
        for s in rows + [None]:
            pose = None if s is None else (s.get("Pose") or {})
            pr = None if pose is None else (pose.get("Placer" + side) or {})
            stance = (pr and pr.get("Active") and pr.get("Authority", 0) >= 0.99 and (pr.get("Frozen") or pr.get("Locked"))
                      and (pr.get("Progression", 0) >= 0.99 or pr.get("Progression", 0) <= 0.02) and (phase is None or pose.get("Phase") == phase))
            if stance:
                window.append(pr)
                continue
            if len(window) >= min_frames:
                pts = [w["Placed"] for w in window]
                p0 = pts[0]
                h = lambda a, b: math.hypot(a[0] - b[0], a[2] - b[2])
                plants.append({"side": side, "frames": len(window), "endpoint_mm": round(h(pts[-1], p0) * 1000), "excursion_mm": round(max(h(p, p0) for p in pts) * 1000),
                               "travel_mm": round(sum(h(pts[i], pts[i - 1]) for i in range(1, len(pts))) * 1000), "locked_frames": sum(1 for w in window if w.get("Locked"))})
            window = []
    stats = lambda key: None if not plants else {"p50": round(statistics.median(p[key] for p in plants)), "p90": round(sorted(p[key] for p in plants)[int(len(plants) * 0.9)]), "max": max(p[key] for p in plants)}
    return {"file": str(path), "phase": phase, "plants": len(plants), "frames_p50": None if not plants else statistics.median(p["frames"] for p in plants),
            "endpoint_mm": stats("endpoint_mm"), "excursion_mm": stats("excursion_mm"), "travel_mm": stats("travel_mm"), "events": plants}


def main():
    path = Path(sys.argv[1])
    if path.is_dir():
        status = path / "off/status.json" if (path / "off/status.json").exists() else path / "status.json"
        path = Path(json.loads(status.read_text(encoding="utf-8"))["LastCapturePath"])
    phase = sys.argv[sys.argv.index("--phase") + 1] if "--phase" in sys.argv else "SprintCycle"
    print(json.dumps(analyse(path, None if phase == "all" else phase), indent=1))


if __name__ == "__main__":
    main()
