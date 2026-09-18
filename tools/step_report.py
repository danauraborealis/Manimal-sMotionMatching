"""Step report: every swing and landing of each foot, measured against the placer's own prediction, the previous
anchor and the clip's stride, with the unnatural ones flagged.

    python tools/step_report.py <capture.json> [--stage after_lock] [--all]

Per step (lift to landing of one foot): lift and landing sole positions, duration, length, landing position in the
body frame (ahead/lateral of the pelvis), foot spread at landing, the placer's predicted landing (Next at lift) and
its error, the previous anchor (Prev) against the lift position, the clip and phase at landing, body speed and the
clip's stride length. While planted: the largest sole move in one frame (a snap) and the total drift.

Flags: crossed (left sole right of the right sole in the body frame), behind (landed behind the pelvis while the
body moved), stalled (the body travelled more than a stride with both feet planted), snap (planted sole moved over
5 cm in a frame), short/long swing, wide (spread over 0.9 m at landing), mispredicted (prediction error over 25 cm),
dragged (planted sole drifted over 15 cm). --all lists every step; otherwise only flagged ones plus the summary.
"""
import json
import math
import sys
from collections import Counter
from pathlib import Path

SWING_SPEED = 0.45     # m/s: sole faster than this for 3 frames = swinging
PLANT_SPEED = 0.15     # m/s: slower than this for 3 frames = planted
MIN_FRAMES = 3


def vec(v):
    return (v.get("X", 0.0), v.get("Y", 0.0), v.get("Z", 0.0)) if isinstance(v, dict) else tuple(v)


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def horiz(a, b=None):
    if b is not None:
        a = sub(a, b)
    return math.hypot(a[0], a[2])


def body_frame(p, origin, yaw_deg):
    """x right, z forward of a body facing yaw_deg (Unity yaw)."""
    d = sub(p, origin)
    yaw = math.radians(yaw_deg)
    fwd = (math.sin(yaw), 0.0, math.cos(yaw))
    right = (math.cos(yaw), 0.0, -math.sin(yaw))
    return d[0] * right[0] + d[2] * right[2], d[0] * fwd[0] + d[2] * fwd[2]


def load(path, stage):
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    rows = [s for s in doc["Samples"] if s.get("Stage") == stage and s.get("Bones")]
    rows.sort(key=lambda s: s["Frame"])
    return doc, rows


def sole(sample, side):
    """World-space sole: the placer's placed footbase when it ran this frame, else the ankle bone brought from the
    root joint's frame (bones are recorded relative to Root_Joint) with the body yaw."""
    pl = probe(sample, side)
    placed = pl.get("Placed")
    if pl.get("Active") and placed and any(abs(v) > 1e-6 for v in placed):
        return tuple(placed)
    bones = {b["N"]: b for b in sample["Bones"] if b}
    foot = bones["Base Human%sFoot" % side]["P"]
    root = vec((sample.get("Root") or {}).get("Value") or {})
    yaw = math.radians(sample.get("RootJointYaw") if sample.get("RootJointYaw") is not None else (sample.get("BodyYaw") or 0.0))
    return (root[0] + foot[0] * math.cos(yaw) + foot[2] * math.sin(yaw), root[1] + foot[1], root[2] - foot[0] * math.sin(yaw) + foot[2] * math.cos(yaw))


def probe(sample, side):
    return (sample.get("Pose") or {}).get("Placer" + side) or {}


def steps_for(rows, side, clip_strides):
    """Segment one foot into planted intervals and swings from its sole speed."""
    soles = [sole(r, side) for r in rows]
    times = [r["Time"] for r in rows]
    speed = [0.0]
    for i in range(1, len(rows)):
        dt = times[i] - times[i - 1]
        speed.append(horiz(soles[i], soles[i - 1]) / dt if dt > 0 else 0.0)
    state = "planted"
    run = 0
    lift = None
    plant_start = 0
    steps = []
    plants = []
    for i in range(len(rows)):
        if state == "planted":
            if speed[i] > SWING_SPEED:
                run += 1
                if run >= MIN_FRAMES:
                    lift = i - MIN_FRAMES + 1
                    plants.append((plant_start, lift))
                    state = "swing"
                    run = 0
            else:
                run = 0
        else:
            if speed[i] < PLANT_SPEED:
                run += 1
                if run >= MIN_FRAMES:
                    land = i - MIN_FRAMES + 1
                    steps.append((lift, land))
                    plant_start = land
                    state = "planted"
                    run = 0
            else:
                run = 0
    if state == "planted":
        plants.append((plant_start, len(rows) - 1))
    out = []
    for lift, land in steps:
        r_lift, r_land = rows[lift], rows[land]
        pl_lift, pl_land = probe(r_lift, side), probe(r_land, side)
        pose = r_land.get("Pose") or {}
        origin = vec((r_land.get("Root") or {}).get("Value") or {})
        yaw = r_land.get("BodyYaw") or 0.0
        v = r_land.get("Velocity") or {}
        body_speed = math.hypot(v.get("X", 0.0), v.get("Z", 0.0))
        lx, lz = body_frame(soles[land], origin, yaw)
        other = "R" if side == "L" else "L"
        ox, oz = body_frame(sole(r_land, other), origin, yaw)
        # travel frame: along the body's velocity when it moves, else the body's forward
        travel_yaw = math.degrees(math.atan2(v.get("X", 0.0), v.get("Z", 0.0))) if body_speed > 0.3 else yaw
        tx, tz = body_frame(soles[land], origin, travel_yaw)
        lift_origin = vec((r_lift.get("Root") or {}).get("Value") or {})
        _, lift_behind = body_frame(soles[lift], lift_origin, travel_yaw)
        otx, otz = body_frame(sole(r_land, other), origin, travel_yaw)
        # the probe's anchors are only current while the placer runs (stale values sit in Idle)
        predicted = pl_lift.get("Next") if pl_lift.get("Active") else None
        prev = pl_lift.get("Prev") if pl_lift.get("Active") else None
        pred_err = horiz(vec(predicted), soles[land]) if predicted else None
        prev_err = horiz(vec(prev), soles[lift]) if prev else None
        clip = pose.get("Clip")
        stride = clip_strides.get((clip, side))
        length = horiz(soles[land], soles[lift])
        if length > 2.5:
            continue  # a puppet re-seat or teleport, not a step
        # the planted interval that follows this landing
        drift = jump = 0.0
        nxt = next(((a, b) for a, b in plants if a == land), None)
        if nxt:
            a, b = nxt
            a = min(a + MIN_FRAMES, b)
            for i in range(a + 1, b + 1):
                d = horiz(soles[i], soles[i - 1])
                if d > 2.0:
                    break  # teleport ends the plant
                jump = max(jump, d)
                drift = horiz(soles[i], soles[a])
        flags = []
        if (side == "L" and lx > ox + 0.02) or (side == "R" and lx < ox - 0.02):
            flags.append("crossed")
        if body_speed > 0.5 and tz < -0.10:
            flags.append("behind")
        # wide against the clip's own stride when known (a 1.7 m walk stride lands the feet 1 m apart)
        spread = math.hypot(lx - ox, lz - oz)
        # wide: along-travel separation beyond 0.7 of the clip's stride, or a stance (across travel) over 0.5 m
        if abs(tz - otz) > (0.7 * stride if stride else 0.9) or abs(tx - otx) > 0.5:
            flags.append("wide")
        dur = times[land] - times[lift]
        if dur < 0.15:
            flags.append("short")
        if dur > 1.0:
            flags.append("long")
        if pred_err is not None and pred_err > 0.25:
            flags.append("mispredicted")
        if jump > 0.05:
            flags.append("snap")
        if drift > 0.15:
            flags.append("dragged")
        out.append({
            "side": side, "lift_frame": r_lift["Frame"], "land_frame": r_land["Frame"], "t": round(times[land], 2),
            "phase": pose.get("Phase"), "clip": clip, "duration_s": round(dur, 2), "length_m": round(length, 2),
            "clip_stride_m": stride, "land_ahead_m": round(lz, 2), "land_lateral_m": round(lx, 2),
            "other_ahead_m": round(oz, 2), "spread_m": round(spread, 2),
            "travel_ahead_m": round(tz, 2), "lift_behind_m": round(lift_behind, 2), "stance_width_m": round(abs(tx - otx), 2),
            "body_mps": round(body_speed, 2), "prediction_error_m": None if pred_err is None else round(pred_err, 2),
            "prev_anchor_error_m": None if prev_err is None else round(prev_err, 2),
            "planted_snap_m": round(jump, 3), "planted_drift_m": round(drift, 3),
            "locked_at_land": pl_land.get("Locked"), "failure_at_land": pl_land.get("Failure"), "flags": flags,
        })
    return out, plants, soles


def stalled_intervals(rows, plants_l, plants_r, soles_l, soles_r, stride=0.9):
    """Body travel while both feet are planted, beyond a stride."""
    out = []
    origin = [vec((r.get("Root") or {}).get("Value") or {}) for r in rows]
    for a, b in plants_l:
        for c, d in plants_r:
            lo, hi = max(a, c), min(b, d)
            if hi - lo < MIN_FRAMES:
                continue
            travel = horiz(origin[hi], origin[lo])
            if stride < travel < 3.0:
                out.append({"from_frame": rows[lo]["Frame"], "to_frame": rows[hi]["Frame"], "t": round(rows[lo]["Time"], 2),
                            "seconds": round(rows[hi]["Time"] - rows[lo]["Time"], 2), "body_travel_m": round(travel, 2),
                            "phase": (rows[hi].get("Pose") or {}).get("Phase")})
    return out


def clip_stride_table():
    strides = {}
    db = Path("tmp/alyx/alyx_posedb_stride.json")
    if not db.exists():
        return strides
    for clip in json.loads(db.read_text(encoding="utf-8"))["clips"]:
        for side in ("L", "R"):
            cycles = (clip.get("stride") or {}).get(side, {}).get("cycles") or []
            lengths = [c.get("strideLength") for c in cycles if c.get("strideLength")]
            if lengths:
                strides[(clip["name"], side)] = round(sorted(lengths)[len(lengths) // 2], 2)
    return strides


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    stage = sys.argv[sys.argv.index("--stage") + 1] if "--stage" in sys.argv else "after_lock"
    show_all = "--all" in sys.argv
    doc, rows = load(args[0], stage)
    if not rows:
        print(json.dumps({"error": "no rows for stage " + stage}))
        return
    strides = clip_stride_table()
    steps_l, plants_l, soles_l = steps_for(rows, "L", strides)
    steps_r, plants_r, soles_r = steps_for(rows, "R", strides)
    steps = sorted(steps_l + steps_r, key=lambda s: s["land_frame"])
    stalls = stalled_intervals(rows, plants_l, plants_r, soles_l, soles_r)
    flags = Counter(f for s in steps for f in s["flags"])
    by_phase = Counter((s["phase"], f) for s in steps for f in s["flags"])
    swing_l = [s["duration_s"] for s in steps if s["side"] == "L" and s["phase"] == "SprintCycle"]
    swing_r = [s["duration_s"] for s in steps if s["side"] == "R" and s["phase"] == "SprintCycle"]
    summary = {
        "file": str(args[0]), "stage": stage, "steps": len(steps), "flagged_steps": sum(1 for s in steps if s["flags"]),
        "cycle_swing_s_L_R_p50": [_pct(swing_l)[0] if swing_l else None, _pct(swing_r)[0] if swing_r else None],
        "cycle_step_length_L_R_p50": [_p50([s["length_m"] for s in steps if s["side"] == "L" and s["phase"] == "SprintCycle"]),
                                      _p50([s["length_m"] for s in steps if s["side"] == "R" and s["phase"] == "SprintCycle"])],
        "flags": dict(flags), "flags_by_phase": {"%s:%s" % k: v for k, v in sorted(by_phase.items(), key=lambda kv: -kv[1])},
        "stalls_over_a_stride": len(stalls),
        "prediction_error_m_p50_p90": _pct([s["prediction_error_m"] for s in steps if s["prediction_error_m"] is not None]),
        "step_length_over_clip_stride_p50": _ratio(steps),
        "planted_drift_m_p50_p90": _pct([s["planted_drift_m"] for s in steps]),
    }
    listed = steps if show_all else [s for s in steps if s["flags"]]
    print(json.dumps({"summary": summary, "stalls": stalls, "steps": listed}, indent=1))


def _p50(values):
    p = _pct(values)
    return None if p is None else p[0]


def _pct(values):
    if not values:
        return None
    values = sorted(values)
    return [values[len(values) // 2], values[int(len(values) * 0.9)]]


def _ratio(steps):
    r = sorted(s["length_m"] / s["clip_stride_m"] for s in steps if s["clip_stride_m"] and s["phase"] == "SprintCycle")
    return None if not r else round(r[len(r) // 2], 2)


if __name__ == "__main__":
    main()
