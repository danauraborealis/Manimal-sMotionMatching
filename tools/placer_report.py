"""Foot placer report from a capture: how far the placer moved each foot, how still anchored feet stayed, and
how much predictions wandered before they froze. Reads the PlacerL/PlacerR probe written per sample.

    python tools/placer_report.py <capture.json | capture directory>
"""
import json
import math
import statistics
import sys
from collections import defaultdict
from pathlib import Path


def _dist(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def _horizontal(a, b):
    return math.hypot(a[0] - b[0], a[2] - b[2])


def _stats(values):
    if not values:
        return None
    values = sorted(values)
    return {"n": len(values), "p50_mm": round(statistics.median(values) * 1000), "p90_mm": round(values[int(len(values) * 0.9)] * 1000), "max_mm": round(values[-1] * 1000)}


def report(document):
    samples = [s for s in document["Samples"] if s.get("Stage") == "after_lock"]
    by_phase = defaultdict(lambda: {"correction": [], "vertical": [], "residual": [], "anchored_drift": [], "prediction_shift": [], "prediction_error": [], "start_error": [], "frames": 0})
    previous = {}
    stills = []
    for s in samples:
        pose = s.get("Pose", {})
        phase = pose.get("Phase") or "none"
        for side in ("L", "R"):
            probe = pose.get("Placer" + side)
            if not probe or not probe.get("Active"):
                continue
            row = by_phase[phase]
            row["frames"] += 1
            row["correction"].append(_horizontal(probe["Target"], probe["Shown"]))
            if probe.get("ClipFoot") is not None:
                # anchored: how far the frozen step sits from where the clip's own foot ended up (prediction error)
                (row["prediction_error"] if probe["Frozen"] else row["start_error"]).append(_horizontal(probe["Next"] if probe["Frozen"] else probe["Prev"], probe["ClipFoot"]) if probe["Frozen"] or probe["Progression"] < 0.05 else 0.0)
            row["vertical"].append(probe["Target"][1] - probe["Shown"][1])
            row["residual"].append(probe["Residual"])
            key = (side, probe["Cycle"], pose.get("Clip"))
            last = previous.get(side)
            if last and last[0] == key:
                if probe["Frozen"] and last[1]["Frozen"]:
                    # anchored: the foot on screen should sit still in the world
                    row["anchored_drift"].append(_horizontal(probe["Shown"], last[1]["Shown"]))
                elif not probe["Frozen"] and not last[1]["Frozen"]:
                    row["prediction_shift"].append(_horizontal(probe["Next"], last[1]["Next"]))
            previous[side] = (key, probe)
    out = {}
    for phase, row in by_phase.items():
        out[phase] = {
            "frames": row["frames"],
            "correction": _stats(row["correction"]),
            "vertical_mm_p50": round(statistics.median(row["vertical"]) * 1000) if row["vertical"] else None,
            "residual": _stats(row["residual"]),
            "anchored_drift_per_frame": _stats(row["anchored_drift"]),
            "prediction_shift_per_frame": _stats(row["prediction_shift"]),
            "prediction_error": _stats(row["prediction_error"]),
            "start_error": _stats([v for v in row["start_error"] if v > 0]),
        }
    return {"file": document.get("Reason"), "foot_placer": document.get("FootPlacer"), "by_phase": out}


def main():
    path = Path(sys.argv[1])
    if path.is_dir():
        path = max(path.glob("*.json"), key=lambda p: p.stat().st_mtime)
    document = json.loads(path.read_text(encoding="utf-8"))
    print(json.dumps(report(document), indent=1))


if __name__ == "__main__":
    main()
