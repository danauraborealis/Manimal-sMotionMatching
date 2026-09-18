"""Audit a local pose database's event clocks and stride-array integrity.

Lift/off/strike/land are normalized TIME, while frames.progression is spatial
progress along the anchor segment. Disagreements demonstrate why those clocks
cannot be substituted; they are not, by themselves, corrupt data or visual bugs.
Usage: python tools/audit_stride_events.py database.json [--output report.json]
"""
import argparse
import hashlib
import json
import math
from pathlib import Path


def cycle_time(frame, start, end, period, loop):
    if loop and frame < start:
        frame += period
    return (frame - start) / (end - start)


def audit(database):
    result = {"clips": [], "errors": [], "clock_disagreement_frames": 0,
              "note": "Clock disagreements are diagnostic evidence, not data errors. Thresholds are authored event times, not spatial progression."}
    for clip in database.get("clips", []):
        name = clip.get("name", "<unnamed>")
        n = clip.get("frames", 0)
        entry = {"clip": name, "feet": {}}
        result["clips"].append(entry)
        if not isinstance(n, int) or n < 2:
            result["errors"].append(f"{name}: expected at least two frames")
            continue
        for side in ("L", "R"):
            errors = []
            foot = clip.get("stride", {}).get(side, {})
            cycles, frames = foot.get("cycles", []), foot.get("frames", {})
            for key in ("cycle", "progression", "translationOffset", "rotationOffset", "footbase", "grounded"):
                if not isinstance(frames.get(key), list) or len(frames[key]) != n:
                    errors.append(f"{key}: expected {n} entries")
            if not cycles:
                errors.append("missing cycles")
            for k, cy in enumerate(cycles):
                start, end = cy.get("startFrame"), cy.get("endFrame")
                if not isinstance(start, int) or not isinstance(end, int) or not (0 <= start < n and start < end <= (start + n if clip.get("loop") else n - 1)):
                    errors.append(f"cycle {k}: invalid frame interval")
                    continue
                times = [cy.get(key) for key in ("footLiftCycle", "footOffCycle", "footStrikeCycle", "footLandCycle")]
                if any(not isinstance(v, (int, float)) or not math.isfinite(v) for v in times) or not (0 <= times[0] <= times[1] <= times[2] <= times[3] <= 1):
                    errors.append(f"cycle {k}: invalid event ordering")
                strike = cy.get("strikeFrame")
                if not isinstance(strike, int) or not start <= strike <= end:
                    errors.append(f"cycle {k}: invalid strike frame")
                elif isinstance(times[2], (int, float)) and abs(times[2] - (strike - start) / (end - start)) > 0.00011:
                    errors.append(f"cycle {k}: strike time disagrees with strikeFrame")
            detail = {"airborne_frames": 0, "swing_gate_disagreement_frames": 0, "examples": []}
            entry["feet"][side] = detail
            if not errors:
                for f, k in enumerate(frames["cycle"]):
                    if not isinstance(k, int) or not 0 <= k < len(cycles):
                        errors.append(f"frame {f}: invalid cycle index")
                        continue
                    p = frames["progression"][f]
                    if not isinstance(p, (int, float)) or not math.isfinite(p):
                        errors.append(f"frame {f}: nonfinite progression")
                        continue
                    cy = cycles[k]
                    clock = cycle_time(f, cy["startFrame"], cy["endFrame"], n, clip.get("loop", False))
                    if not -0.0001 <= clock <= 1.0001:
                        errors.append(f"frame {f}: outside assigned cycle")
                        continue
                    if frames["grounded"][f]:
                        continue
                    detail["airborne_frames"] += 1
                    expected = cy["footLiftCycle"] <= clock < cy["footStrikeCycle"]
                    old = cy["footLiftCycle"] <= p < cy["footStrikeCycle"]
                    if expected != old:
                        detail["swing_gate_disagreement_frames"] += 1
                        result["clock_disagreement_frames"] += 1
                        if len(detail["examples"]) < 3:
                            detail["examples"].append({"frame": f, "cycle": k, "time": round(clock, 5), "progression": p,
                                                       "in_authored_swing": expected, "old_gate": old})
            result["errors"].extend(f"{name}/{side}: {e}" for e in errors)
    result["clip_count"] = len(result["clips"])
    result["clips_with_clock_disagreement"] = sum(any(f["swing_gate_disagreement_frames"] for f in c["feet"].values()) for c in result["clips"])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    raw = args.database.read_bytes()
    report = audit(json.loads(raw))
    report.update(database=str(args.database.resolve()), sha256=hashlib.sha256(raw).hexdigest())
    text = json.dumps(report, indent=2, allow_nan=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8")
        print(json.dumps({k: report[k] for k in ("clip_count", "clips_with_clock_disagreement", "clock_disagreement_frames", "errors")}))
    else:
        print(text)
    return int(bool(report["errors"]))


if __name__ == "__main__":
    raise SystemExit(main())
