"""Read a diagnostic capture and flag missing evidence before locomotion analysis."""
import argparse
import json
import math
from collections import Counter
from pathlib import Path

SCHEMAS = ("manimal.motionmatching.diagnostic.v1", "manimal.motionmatching.diagnostic.v2")

# FootStep curve sits near -1 while the left foot carries weight and +1 for the right (observed in
# the first raid captures, not documented by BSG) — so the sign picks the stance foot.
FOOTSTEP_THRESHOLD = 0.5
MOVING_SPEED = 0.3
MIN_STANCE_SECONDS = 0.06
# frames where the stance foot is still faster than this fraction of root speed are heel-strike/toe-off
CORE_SPEED_RATIO = 0.35
SPEED_BANDS = (("0.3-1.8 m/s", 0.3, 1.8), (">=1.8 m/s", 1.8, math.inf))
FINAL_STAGE_PREFERENCE = ("pre_render", "after_lock", "after_visual")
POP_SPEED = 1.5
# animation-defined contact: near the local lowest foot height and slower than half the body's speed.
# built from after_visual only, so it doesn't reuse the foot lock's FootStep trigger or its output
CONTACT_HEIGHT_TOLERANCE = 0.03
CONTACT_SPEED_RATIO = 0.5
HEIGHT_WINDOW_SECONDS = 0.35
STRAIGHT_LEG_RATIO = 0.995
STEADY_AFTER_SECONDS = 1.0
START_WINDOW_SECONDS = 1.5
STOP_SETTLE_SECONDS = 0.5


def summarize(document):
    if document.get("Schema") not in SCHEMAS:
        raise ValueError("Unsupported capture schema")
    samples = document.get("Samples", [])
    warnings = []
    stages = Counter(sample.get("Stage", "unknown") for sample in samples)
    if len(samples) != document.get("CapturedSamples"):
        warnings.append("Declared sample count differs from recorded rows.")
    if dict(stages) != document.get("StageCounts", {}):
        warnings.append("Declared stage counts differ from recorded rows.")
    if any(sample.get("Index") != index for index, sample in enumerate(samples)):
        warnings.append("Sample indices are missing or out of order.")
    errors = Counter(sample["CaptureError"] for sample in samples if sample.get("CaptureError"))
    if errors:
        warnings.append("Some runtime reads failed; inspect capture_errors.")
    for stage in ("after_body", "before_visual", "after_ik", "after_visual"):
        if not stages[stage]:
            warnings.append(f"No {stage} observations. Check hook installation, visibility, and culling.")
    # measure what renders when that stage exists; after_lock and after_visual are earlier fallbacks
    final_stage = next((stage for stage in FINAL_STAGE_PREFERENCE if stages[stage]), "after_visual")
    final = [s for s in samples if s.get("Stage") == final_stage]
    unique_frames = len({s.get("Frame") for s in final})
    if unique_frames < 2:
        warnings.append("Fewer than two final-pose frames; movement cannot be compared.")
    left = sum(bool(_leg(s, "Left").get("HasFootPosition")) for s in final)
    right = sum(bool(_leg(s, "Right").get("HasFootPosition")) for s in final)
    if final and (left != len(final) or right != len(final)):
        warnings.append("Foot positions are unavailable in some final-pose samples.")
    if document.get("BufferFull"):
        warnings.append("Capture reached its sample cap; it may end before the route does.")

    gait = analyze_gait(final)
    if gait["stances"] == 0:
        warnings.append("No FootStep-curve stances while moving; sliding was not measured (zero is not evidence of no sliding).")
    for band, stats in gait["bands"].items():
        if 0 < stats["stances"] < 6:
            warnings.append(f"Only {stats['stances']} stances in {band}; too few for a baseline.")
    route = document.get("Route")
    if route and route.get("HasMeasuredSpeed") and route.get("RequestedMoveSpeed", 1.0) < 1.0 and route.get("SteadySpeedMetersPerSecond", 0.0) >= 1.8:
        warnings.append("Route requested a reduced move speed but the bot still moved at run speed; something else is driving Player.Speed.")

    puppet = analyze_puppet(document.get("Puppet"), final, samples, final_stage)
    if puppet:
        limited = [s["step"] for s in puppet.get("steps", []) if s.get("speed_limit_of_max") is not None and s["speed_limit_of_max"] < 0.8]
        if limited:
            # EFT caps Player.Speed in water (0.2) and barbed wire (0.15); a flooded lane once read as a 1.23 m/s "run"
            warnings.append(f"Speed-limited legs {limited}: the lane is in water or wire; speed and gait numbers are not comparable.")

    return {
        "reason": document.get("Reason"),
        "schema": document.get("Schema"),
        "runtime": {key: document.get(key) for key in ("UnityVersion", "GameVersion", "AssemblyCSharpMvid")},
        "samples": len(samples),
        "stages": dict(stages),
        "final_pose_frames": unique_frames,
        "final_pose_foot_availability": {"left": left, "right": right, "total": len(final)},
        "animator_types": sorted({s.get("Animator", {}).get("RuntimeType") for s in samples if s.get("Animator", {}).get("RuntimeType")}),
        "capture_errors": dict(errors),
        "route": route,
        "speed": analyze_speed(final),
        "final_stage": final_stage,
        "foot_lock": document.get("FootLock"),
        "foot_placer": document.get("FootPlacer"),
        "pose_playback": document.get("PosePlayback"),
        "ik_stage": analyze_ik_stage(samples),
        "stage_deltas": {
            "lock_correction": _stage_delta(samples, "after_visual", "after_lock"),
            # pose playback: how far it moved the legs, and whether VisualPass/IK then undid it
            "pose_correction": _stage_delta(samples, "before_visual", "after_pose"),
            "pose_to_after_visual": _stage_delta(samples, "after_pose", "after_visual"),
            "after_last_hook": _stage_delta(samples, "after_lock" if stages["after_lock"] else "after_visual", "pre_render"),
        },
        "correction_pops": _correction_pops(samples, final_stage),
        "knee_pops": _correction_pops(samples, final_stage, "KneePosition"),
        "straightened_by_correction": _straightened(samples, final_stage),
        "contact_window_slide": analyze_contact_windows(samples, final_stage),
        "pose_contact_slide": analyze_pose_contacts(samples, final_stage),
        "pose_lag": analyze_pose_lag(samples, final_stage),
        "body_lean": analyze_body_lean(samples, final_stage),
        "hand_shift": analyze_hand_shift(samples),
        "footstep_sliding": gait,
        "puppet": puppet,
        "warnings": warnings,
        "interpretation": "Stance comes from the FootStep curve sign, a heuristic contact signal. Slide numbers are a baseline, not validated plant intervals, and do not prove IK executed.",
    }


def analyze_hand_shift(samples):
    """How far our leg/pelvis/lean writes move the hands, in mm: before_visual (Tarkov) vs after_pose (ours)."""
    before = {s.get("Frame"): s for s in samples if s.get("Stage") == "before_visual"}
    rows = []
    for sample in samples:
        if sample.get("Stage") != "after_pose":
            continue
        other = before.get(sample.get("Frame"))
        if not other:
            continue
        a, b = sample.get("Pose", {}), other.get("Pose", {})
        if not (a.get("HasHands") and b.get("HasHands")):
            continue
        for side in ("HandL", "HandR"):
            p, q = a.get(side), b.get(side)
            if p and q:
                rows.append(1000.0 * math.sqrt(sum((x - y) ** 2 for x, y in zip(p, q))))
    if not rows:
        return None
    return {"frames": len(rows), "shift_mm_p50": _rounded(_percentile(rows, 0.5), 1),
            "shift_mm_p90": _rounded(_percentile(rows, 0.9), 1), "shift_mm_max": _rounded(max(rows), 1)}


def analyze_body_lean(samples, final_stage):
    """Procedural torso lean actually applied, and how far it moved the aim after compensation."""
    pitch, roll, aim = [], [], []
    for sample in samples:
        if sample.get("Stage") != final_stage:
            continue
        pose = sample.get("Pose", {})
        if "LeanPitch" not in pose:
            continue
        pitch.append(abs(pose.get("LeanPitch", 0.0)))
        roll.append(abs(pose.get("LeanRoll", 0.0)))
        aim.append(abs(pose.get("AimShift", 0.0)))
    if not pitch or max(pitch + roll) == 0:
        return None
    return {
        "frames": len(pitch),
        "pitch_deg_p50": _rounded(_percentile(pitch, 0.5), 1),
        "pitch_deg_p90": _rounded(_percentile(pitch, 0.9), 1),
        "pitch_deg_max": _rounded(max(pitch), 1),
        "roll_deg_p50": _rounded(_percentile(roll, 0.5), 1),
        "roll_deg_p90": _rounded(_percentile(roll, 0.9), 1),
        "roll_deg_max": _rounded(max(roll), 1),
        "aim_shift_deg_p90": _rounded(_percentile(aim, 0.9), 1),
        "aim_shift_deg_max": _rounded(max(aim), 1),
    }


def analyze_pose_lag(samples, final_stage):
    """How far the clip runs behind the ground the bot has covered, per phase (metres; negative leads)."""
    by_phase = {}
    for sample in samples:
        if sample.get("Stage") != final_stage:
            continue
        pose = sample.get("Pose", {})
        if pose.get("Weight", 0.0) <= 0.5 or pose.get("Phase") not in ("Start", "Cut"):
            continue
        by_phase.setdefault(pose.get("Phase"), []).append(pose.get("PathBehind", 0.0))
    if not by_phase:
        return None
    return {phase: {"frames": len(values),
                    "behind_m_p50": _rounded(_percentile(values, 0.5), 2),
                    "behind_m_p90": _rounded(_percentile(values, 0.9), 2),
                    "behind_m_max": _rounded(max(values), 2)}
            for phase, values in by_phase.items()}


def analyze_pose_contacts(samples, final_stage):
    """Slide over the windows the Alyx clips themselves call planted, grouped by playback phase.

    EFT's FootStep curve is out of phase with these legs, so it cannot judge them; the clip's own contact
    flags are recorded per sample (PoseProbe). A planted foot that moves is sliding, whatever the cause.
    """
    rows = sorted((s for s in samples if s.get("Stage") == final_stage and s.get("Pose", {}).get("Weight", 0.0) > 0.5),
                  key=lambda s: s.get("Frame", 0))
    windows = []
    for side, flag in (("Left", "ContactL"), ("Right", "ContactR")):
        run = []
        for sample in rows + [None]:
            planted = sample is not None and sample.get("Pose", {}).get(flag)
            # a window that spans a clip change isn't one plant: it used to merge a start, its handoff and the
            # sprint that followed into a single 3 s "contact" and blamed the slide on the start
            if planted and run and sample.get("Pose", {}).get("Clip") != run[-1].get("Pose", {}).get("Clip"):
                planted = False
            if planted:
                run.append(sample)
                continue
            if len(run) > 2:
                travel = sum(_horizontal(_vec(_leg(b, side).get("FootPosition")), _vec(_leg(a, side).get("FootPosition")))
                             for a, b in zip(run, run[1:]))
                phases = [f.get("Pose", {}).get("Phase") for f in run]
                locked = sum(1 for f in run if f.get("Pose", {}).get("LockedL" if side == "Left" else "LockedR"))
                windows.append({
                    "side": side,
                    "phase": max(set(phases), key=phases.count),
                    "clip": run[len(run) // 2].get("Pose", {}).get("Clip"),
                    "seconds": _rounded(run[-1].get("Time", 0) - run[0].get("Time", 0), 2),
                    "slide_mm": _rounded(1000.0 * travel, 0),
                    "net_mm": _rounded(1000.0 * _horizontal(_vec(_leg(run[-1], side).get("FootPosition")), _vec(_leg(run[0], side).get("FootPosition"))), 0),
                    "locked_fraction": _rounded(locked / float(len(run)), 2),
                })
            run = []
    if not windows:
        return None
    by_phase = {}
    for window in windows:
        entry = by_phase.setdefault(window["phase"], [])
        entry.append(window)
    return {
        "windows": len(windows),
        "slide_mm_p50": _percentile([w["slide_mm"] for w in windows], 0.5),
        "slide_mm_p90": _percentile([w["slide_mm"] for w in windows], 0.9),
        "by_phase": {phase: {"windows": len(items),
                             "slide_mm_p50": _percentile([w["slide_mm"] for w in items], 0.5),
                             "net_mm_p50": _percentile([w["net_mm"] for w in items], 0.5),
                             "locked_fraction_p50": _percentile([w["locked_fraction"] for w in items], 0.5)}
                     for phase, items in by_phase.items()},
        "worst": sorted(windows, key=lambda w: -(w["slide_mm"] or 0))[:6],
    }


def _leg(sample, side):
    return sample.get("Grounder", {}).get(side + "Leg", {})


def _vec(value):
    value = value or {}
    return (value.get("X", 0.0), value.get("Y", 0.0), value.get("Z", 0.0))


def _horizontal(a, b):
    return math.hypot(a[0] - b[0], a[2] - b[2])


def _percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(round(fraction * (len(ordered) - 1))))]


def _rounded(value, digits=3):
    return None if value is None else round(value, digits)


def _final_frames(final):
    by_frame = {}
    for sample in final:
        root = sample.get("Root", {})
        animator = sample.get("Animator", {})
        if not root.get("Available") or not animator.get("HasFootStepCurve"):
            continue
        if not (_leg(sample, "Left").get("HasFootPosition") and _leg(sample, "Right").get("HasFootPosition")):
            continue
        by_frame.setdefault(sample.get("Frame"), sample)
    return [by_frame[frame] for frame in sorted(by_frame)]


def _speeds(frames):
    """Per-frame horizontal speeds from displacement; index 0 has no predecessor."""
    root = [None]
    feet = {"Left": [None], "Right": [None]}
    for previous, current in zip(frames, frames[1:]):
        dt = current.get("Time", 0.0) - previous.get("Time", 0.0)
        if dt <= 0:
            root.append(None)
            for side in feet:
                feet[side].append(None)
            continue
        root.append(_horizontal(_vec(current["Root"].get("Value")), _vec(previous["Root"].get("Value"))) / dt)
        for side in feet:
            feet[side].append(_horizontal(_vec(_leg(current, side).get("FootPosition")), _vec(_leg(previous, side).get("FootPosition"))) / dt)
    return root, feet


def analyze_speed(final):
    frames = _final_frames(final)
    root, _ = _speeds(frames)
    moving = [v for v in root if v is not None and v >= MOVING_SPEED]
    player_speed = [s["Movement"]["PlayerSpeed"] for s in frames if s.get("Movement", {}).get("HasPlayerSpeed")]
    return {
        "moving_frames": len(moving),
        "moving_root_speed_mps": {"p10": _rounded(_percentile(moving, 0.1), 2), "p50": _rounded(_percentile(moving, 0.5), 2), "p90": _rounded(_percentile(moving, 0.9), 2)},
        "player_speed_normalized": {"p10": _rounded(_percentile(player_speed, 0.1)), "p50": _rounded(_percentile(player_speed, 0.5)), "p90": _rounded(_percentile(player_speed, 0.9))} if player_speed else None,
    }


def _correction_pops(samples, final_stage, joint="FootPosition"):
    """Horizontal foot speed the rendered pose has beyond the animation's own; spikes read as snapping."""
    if final_stage == "after_visual":
        return None
    has_key = "Has" + joint
    animated = {s.get("Frame"): s for s in samples if s.get("Stage") == "after_visual"}
    rendered = {s.get("Frame"): s for s in samples if s.get("Stage") == final_stage}
    extra = []
    for frame in sorted(rendered):
        if frame - 1 not in rendered or frame not in animated or frame - 1 not in animated:
            continue
        dt = rendered[frame].get("Time", 0) - rendered[frame - 1].get("Time", 0)
        if dt <= 0:
            continue
        for side in ("Left", "Right"):
            if not all(_leg(s, side).get(has_key) for s in (rendered[frame], rendered[frame - 1], animated[frame], animated[frame - 1])):
                continue
            step = lambda a, b: [x - y for x, y in zip(_vec(_leg(a, side).get(joint)), _vec(_leg(b, side).get(joint)))]
            # a held foot moving slower than the clip is the point of the lock; only faster-than-clip motion is a snap
            rendered_speed = math.hypot(*step(rendered[frame], rendered[frame - 1])[0::2]) / dt
            animated_speed = math.hypot(*step(animated[frame], animated[frame - 1])[0::2]) / dt
            extra.append(max(0.0, rendered_speed - animated_speed))
    if not extra:
        return None
    return {
        "foot_frames": len(extra),
        "p99_mps": _rounded(_percentile(extra, 0.99)),
        "max_mps": _rounded(max(extra)),
        "over_1_5_mps": sum(e > POP_SPEED for e in extra),
    }


def _straightened(samples, final_stage):
    """Leg frames the correction pushed to full extension that the animation had bent."""
    if final_stage == "after_visual":
        return None
    animated = {s.get("Frame"): s for s in samples if s.get("Stage") == "after_visual"}
    total = straightened = 0
    for sample in samples:
        if sample.get("Stage") != final_stage or sample.get("Frame") not in animated:
            continue
        for side in ("Left", "Right"):
            ratios = []
            for pose in (animated[sample.get("Frame")], sample):
                leg = _leg(pose, side)
                if not (leg.get("HasHipPosition") and leg.get("HasKneePosition") and leg.get("HasFootPosition")):
                    break
                hip, knee, foot = _vec(leg.get("HipPosition")), _vec(leg.get("KneePosition")), _vec(leg.get("FootPosition"))
                chain = math.dist(hip, knee) + math.dist(knee, foot)
                ratios.append(math.dist(hip, foot) / chain if chain > 1e-4 else 0.0)
            if len(ratios) != 2:
                continue
            total += 1
            straightened += ratios[1] >= STRAIGHT_LEG_RATIO and ratios[0] < STRAIGHT_LEG_RATIO - 0.005
    return None if total == 0 else {"leg_frames": total, "straightened": straightened}


def analyze_contact_windows(samples, final_stage, frame_range=None):
    """Slide over plant windows defined only by the unmodified animation (after_visual): foot near its local
    lowest height and moving slower than the body. Rendered and animated slide share the same windows, so
    the comparison doesn't depend on the correction's own trigger."""
    animated = {s.get("Frame"): s for s in samples if s.get("Stage") == "after_visual"}
    rendered = {s.get("Frame"): s for s in samples if s.get("Stage") == final_stage}
    frames = [f for f in sorted(animated) if f in rendered and (frame_range is None or frame_range[0] <= f <= frame_range[1])]
    frames = [f for f in frames if animated[f].get("Root", {}).get("Available") and _leg(animated[f], "Left").get("HasFootPosition") and _leg(animated[f], "Right").get("HasFootPosition")]
    if len(frames) < 3:
        return None
    times = [animated[f].get("Time", 0.0) for f in frames]
    root = [None] + [
        _horizontal(_vec(animated[frames[i]]["Root"].get("Value")), _vec(animated[frames[i - 1]]["Root"].get("Value"))) / (times[i] - times[i - 1]) if times[i] > times[i - 1] else None
        for i in range(1, len(frames))]
    rows = []
    for side in ("Left", "Right"):
        foot = lambda pose, f: _vec(_leg(pose[f], side).get("FootPosition"))
        heights = [foot(animated, f)[1] - _vec(animated[f]["Root"].get("Value"))[1] for f in frames]
        speed = [None] + [_horizontal(foot(animated, frames[i]), foot(animated, frames[i - 1])) / (times[i] - times[i - 1]) if times[i] > times[i - 1] else None for i in range(1, len(frames))]
        low, start = [], 0
        for i in range(len(frames)):
            while times[i] - times[start] > HEIGHT_WINDOW_SECONDS:
                start += 1
            end = i
            while end + 1 < len(frames) and times[end + 1] - times[i] <= HEIGHT_WINDOW_SECONDS:
                end += 1
            low.append(min(heights[start:end + 1]))
        run = None
        for i in range(1, len(frames) + 1):
            planted = (i < len(frames) and root[i] is not None and root[i] >= MOVING_SPEED and speed[i] is not None
                       and heights[i] <= low[i] + CONTACT_HEIGHT_TOLERANCE and speed[i] < CONTACT_SPEED_RATIO * root[i])
            if planted and run is None:
                run = i
            elif not planted and run is not None:
                last = i - 1
                if times[last] - times[run] >= MIN_STANCE_SECONDS:
                    rows.append({
                        "root_speed": sum(root[run:last + 1]) / (last + 1 - run),
                        "rendered": _horizontal(foot(rendered, frames[last]), foot(rendered, frames[run])),
                        "animated": _horizontal(foot(animated, frames[last]), foot(animated, frames[run])),
                    })
                run = None
    mm = lambda values: None if not values else _rounded(_percentile(values, 0.5) * 1000.0, 1)
    bands = {}
    for name, lo, hi in SPEED_BANDS:
        band = [r for r in rows if lo <= r["root_speed"] < hi]
        bands[name] = {"contacts": len(band), "rendered_slide_mm_p50": mm([r["rendered"] for r in band]), "animated_slide_mm_p50": mm([r["animated"] for r in band])}
    return {
        "contacts": len(rows),
        "rendered_slide_mm_p50": mm([r["rendered"] for r in rows]),
        "animated_slide_mm_p50": mm([r["animated"] for r in rows]),
        "bands": bands,
    }


def _stage_delta(samples, first, second):
    """Per-frame foot movement between two stages; a nonzero after_last_hook means something wrote the legs later."""
    earlier = {s.get("Frame"): s for s in samples if s.get("Stage") == first}
    moves = []
    for sample in samples:
        if sample.get("Stage") != second or sample.get("Frame") not in earlier:
            continue
        prior = earlier[sample.get("Frame")]
        for side in ("Left", "Right"):
            if _leg(sample, side).get("HasFootPosition") and _leg(prior, side).get("HasFootPosition"):
                moves.append(math.dist(_vec(_leg(sample, side).get("FootPosition")), _vec(_leg(prior, side).get("FootPosition"))))
    if not moves:
        return None
    changed = [m for m in moves if m > 1e-3]
    return {
        "foot_samples": len(moves),
        "changed_over_1mm": len(changed),
        "changed_p50_mm": _rounded(None if not changed else _percentile(changed, 0.5) * 1000.0, 1),
        "max_mm": _rounded(max(moves) * 1000.0, 1),
    }


def analyze_ik_stage(samples):
    """Compare feet/pelvis before the visual pass with after the IK call, per frame."""
    before = {s.get("Frame"): s for s in samples if s.get("Stage") == "before_visual"}
    changed = uniform = 0
    largest = 0.0
    frames = 0
    for sample in samples:
        if sample.get("Stage") != "after_ik" or sample.get("Frame") not in before:
            continue
        prior = before[sample.get("Frame")]
        if not (_leg(sample, "Left").get("HasFootPosition") and _leg(prior, "Left").get("HasFootPosition")):
            continue
        frames += 1
        deltas = []
        for side in ("Left", "Right"):
            a, b = _vec(_leg(sample, side).get("FootPosition")), _vec(_leg(prior, side).get("FootPosition"))
            deltas.append(tuple(x - y for x, y in zip(a, b)))
        if sample.get("Pelvis", {}).get("Available") and prior.get("Pelvis", {}).get("Available"):
            deltas.append(tuple(x - y for x, y in zip(_vec(sample["Pelvis"].get("Value")), _vec(prior["Pelvis"].get("Value")))))
        size = max(math.sqrt(sum(c * c for c in d)) for d in deltas)
        if size <= 1e-3:
            continue
        changed += 1
        largest = max(largest, size)
        if all(math.dist(d, deltas[0]) <= 1e-3 for d in deltas):
            uniform += 1
    return {
        "frames_compared": frames,
        "frames_changed_over_1mm": changed,
        "frames_with_identical_offset_on_feet_and_pelvis": uniform,
        "max_change_m": round(largest, 3),
        "note": "Identical offsets on both feet and pelvis mean a whole-body shift, not per-leg IK.",
    }


def analyze_gait(final):
    frames = _final_frames(final)
    empty = {"stances": 0, "signal_agreement": None, "bands": {}}
    if len(frames) < 3:
        return empty
    root, feet = _speeds(frames)

    stances = []
    agree = checked = 0
    index = 1
    while index < len(frames):
        side = _stance_side(frames[index])
        if side is None or root[index] is None or root[index] < MOVING_SPEED:
            index += 1
            continue
        end = index
        while end + 1 < len(frames) and _stance_side(frames[end + 1]) == side and root[end + 1] is not None and root[end + 1] >= MOVING_SPEED:
            end += 1
        start = index - 1
        duration = frames[end]["Time"] - frames[start]["Time"]
        other = "Right" if side == "Left" else "Left"
        for k in range(index, end + 1):
            if feet[side][k] is not None and feet[other][k] is not None:
                checked += 1
                agree += feet[side][k] < feet[other][k]
        if duration >= MIN_STANCE_SECONDS:
            stances.append(_measure_stance(frames, root, feet[side], side, start, end, duration))
        index = end + 1

    bands = {}
    for name, low, high in SPEED_BANDS:
        rows = [s for s in stances if low <= s["root_speed"] < high]
        bands[name] = _band_stats(rows)
    return {
        "stances": len(stances),
        "footstep_threshold": FOOTSTEP_THRESHOLD,
        # share of stance frames where the curve's stance foot moved slower than the other foot
        "signal_agreement": _rounded(agree / checked if checked else None),
        "bands": bands,
        "note": "window_* spans the whole curve half-cycle, including heel-strike and toe-off; core_* is the stretch where the foot moves slower than 35% of root speed, the closest proxy for planted-foot slide.",
    }


def _stance_side(frame):
    value = frame["Animator"].get("FootStepCurve", 0.0)
    if value <= -FOOTSTEP_THRESHOLD:
        return "Left"
    if value >= FOOTSTEP_THRESHOLD:
        return "Right"
    return None


def _measure_stance(frames, root, foot_speed, side, start, end, duration):
    position = lambda k: _vec(_leg(frames[k], side).get("FootPosition"))
    root_position = lambda k: _vec(frames[k]["Root"].get("Value"))
    window = _horizontal(position(end), position(start))
    travel = sum(_horizontal(position(k), position(k - 1)) for k in range(start + 1, end + 1))
    root_travel = _horizontal(root_position(end), root_position(start))

    # longest run of frames where the foot is clearly slower than the body
    best = (0.0, 0.0)
    run_start = None
    for k in range(start + 1, end + 2):
        slow = k <= end and foot_speed[k] is not None and root[k] is not None and foot_speed[k] < CORE_SPEED_RATIO * root[k]
        if slow and run_start is None:
            run_start = k - 1
        elif not slow and run_start is not None:
            seconds = frames[k - 1]["Time"] - frames[run_start]["Time"]
            if seconds > best[0]:
                best = (seconds, _horizontal(position(k - 1), position(run_start)))
            run_start = None
    return {
        "side": side,
        "duration": duration,
        "root_speed": root_travel / duration,
        "window_slide": window,
        "window_travel": travel,
        "core_seconds": best[0],
        "core_slide": best[1],
        "slide_ratio": window / root_travel if root_travel > 1e-3 else None,
    }


def analyze_puppet(puppet, final, samples=(), final_stage="after_visual"):
    """Per scripted step: reached speed, stance slide while moving, foot drift while turning or standing."""
    if not puppet:
        return None
    frames = _final_frames(final)
    rows = []
    for step in puppet.get("Steps") or []:
        window = [f for f in frames if step.get("StartFrame", 0) <= f.get("Frame", -1) <= step.get("EndFrame", -1)]
        row = {"step": step.get("Step"), "end": step.get("EndReason"), "seconds": _rounded(step.get("EndTime", 0) - step.get("StartTime", 0), 2), "meters": _rounded(step.get("Meters"), 2)}
        kind = step.get("Kind")
        if kind in ("Move", "Sprint", "Curve", "Strafe"):
            # skip the acceleration ramp so reached speed reflects the commanded value
            steady = [f for f in window if f.get("Time", 0) >= step.get("StartTime", 0) + STEADY_AFTER_SECONDS]
            root, _ = _speeds(steady)
            speeds = [v for v in root if v is not None]
            player = [f["Movement"]["PlayerSpeed"] for f in steady if f.get("Movement", {}).get("HasPlayerSpeed")]
            limits = [f["Movement"]["StateSpeedLimit"] for f in steady if f.get("Movement", {}).get("HasStateSpeedLimit")]
            # weight and armour limits scale with MaxSpeed (0.606 of 0.625 is normal kit); water and wire are absolute
            maxima = [f["Movement"]["MaxSpeed"] for f in steady if f.get("Movement", {}).get("HasMaxSpeed")]
            gait = analyze_gait(window)
            contact = analyze_contact_windows(samples, final_stage, (step.get("StartFrame", 0), step.get("EndFrame", -1)))
            start_end = next((f.get("Frame") for f in window if f.get("Time", 0) >= step.get("StartTime", 0) + START_WINDOW_SECONDS), step.get("EndFrame", -1))
            start_contact = analyze_contact_windows(samples, final_stage, (step.get("StartFrame", 0), start_end))
            row.update({
                "contact_windows": None if not contact else contact["contacts"],
                # acceleration from standing: where an Alyx start clip plays
                "start_contact_slide_mm_p50": None if not start_contact else start_contact["rendered_slide_mm_p50"],
                "start_contacts": None if not start_contact else start_contact["contacts"],
                "contact_slide_mm_p50": None if not contact else [contact["animated_slide_mm_p50"], contact["rendered_slide_mm_p50"]],
                "steady_mps_p50": _rounded(_percentile(speeds, 0.5), 2),
                "player_speed_p50": _rounded(_percentile(player, 0.5)),
                "speed_limit_min": _rounded(min(limits)) if limits else None,
                "speed_limit_of_max": _rounded(min(1.0, min(limits) / max(maxima))) if limits and maxima and max(maxima) > 0 else None,
                "sprinting_frames": sum(bool(f.get("Movement", {}).get("SprintEnabled")) for f in window),
                "stances": gait["stances"],
                "signal_agreement": gait["signal_agreement"],
                "core_slide_mm": {band: stats["core_slide_mm"] for band, stats in gait["bands"].items() if stats.get("stances")},
            })
        elif kind in ("Turn", "Stop"):
            if kind == "Stop":
                # braking to standing: where an Alyx stop clip plays
                stop_contact = analyze_contact_windows(samples, final_stage, (step.get("StartFrame", 0), step.get("EndFrame", -1)))
                row["stop_contact_slide_mm_p50"] = None if not stop_contact else stop_contact["rendered_slide_mm_p50"]
                # feet still moving once the body has halted reads as stepping in place (the user saw this)
                halted = _halt_index(window)
                if halted is not None:
                    tail = window[halted:]
                    row["seconds_to_halt"] = _rounded(tail[0].get("Time", 0) - step.get("StartTime", 0), 2)
                    row["after_halt_foot_travel_mm"] = _rounded(1000.0 * sum(
                        _horizontal(_vec(_leg(b, side).get("FootPosition")), _vec(_leg(a, side).get("FootPosition")))
                        for side in ("Left", "Right") for a, b in zip(tail, tail[1:])), 0)
                row["stop_contacts"] = None if not stop_contact else stop_contact["contacts"]
            # after a short settle, any horizontal foot travel with no root travel is skating in place
            settled = [f for f in window if f.get("Time", 0) >= step.get("StartTime", 0) + (0.0 if kind == "Turn" else STOP_SETTLE_SECONDS)]
            if len(settled) > 1:
                for side in ("Left", "Right"):
                    row[side.lower() + "_foot_travel_mm"] = _rounded(1000.0 * sum(
                        _horizontal(_vec(_leg(b, side).get("FootPosition")), _vec(_leg(a, side).get("FootPosition"))) for a, b in zip(settled, settled[1:])), 0)
                row["root_travel_mm"] = _rounded(1000.0 * _horizontal(_vec(settled[-1]["Root"].get("Value")), _vec(settled[0]["Root"].get("Value"))), 0)
        rows.append(row)
    return {
        "scenario": puppet.get("Scenario"),
        "freeze_others": puppet.get("FreezeOthers"),
        "brought_to_player": puppet.get("BroughtToPlayer"),
        "lane_clear_m": _rounded(puppet.get("LaneClearMeters"), 1),
        "steps": rows,
    }


def _halt_index(frames):
    """First frame after which the root stays below 0.1 m/s."""
    root, _ = _speeds(frames)
    for i in range(1, len(frames)):
        if all(v is not None and v < 0.1 for v in root[i:]):
            return i
    return None


def _band_stats(rows):
    if not rows:
        return {"stances": 0}
    mm = lambda values, q: _rounded(None if not values else _percentile(values, q) * 1000.0, 0)
    window = [r["window_slide"] for r in rows]
    core = [r["core_slide"] for r in rows if r["core_seconds"] > 0]
    ratios = [r["slide_ratio"] for r in rows if r["slide_ratio"] is not None]
    return {
        "stances": len(rows),
        "left_right": [sum(r["side"] == "Left" for r in rows), sum(r["side"] == "Right" for r in rows)],
        "stance_seconds_p50": _rounded(_percentile([r["duration"] for r in rows], 0.5)),
        "root_speed_mps_p50": _rounded(_percentile([r["root_speed"] for r in rows], 0.5), 2),
        "window_slide_mm": {"p50": mm(window, 0.5), "p90": mm(window, 0.9), "max": _rounded(max(window) * 1000.0, 0)},
        "window_travel_mm_p50": mm([r["window_travel"] for r in rows], 0.5),
        "core_slide_mm": {"p50": mm(core, 0.5), "p90": mm(core, 0.9), "stances_with_core": len(core)},
        "slide_to_root_travel_p50": _rounded(_percentile(ratios, 0.5)),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path, help="Capture JSON, or a folder to read its newest motion-capture-*.json")
    args = parser.parse_args()
    path = args.capture
    if path.is_dir():
        path = max(path.glob("motion-capture-*.json"), key=lambda p: p.stat().st_mtime, default=None)
        if path is None:
            parser.error("No completed capture found in that folder")
    try:
        report = summarize(json.loads(path.read_text(encoding="utf-8")))
    except (OSError, ValueError, TypeError) as exc:
        parser.error(str(exc))
    print(json.dumps({"file": str(path.resolve()), **report}, indent=2))


if __name__ == "__main__":
    main()
