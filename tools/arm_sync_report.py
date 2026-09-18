"""Report playback phase agreement and measured arm/thigh phase changes separately.

Consumes the current MotionSync/Pose.Sync capture schema or fleet syncBefore/syncAfter.
Observed motion compares same-frame before_visual and after_lock signals. A fitted
phase change is a diagnostic heuristic, not a naturalness score or mesh collision.
NumPy is required for observed fits; without it that measurement is unavailable.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import json
import math
from pathlib import Path
import statistics

try:
    import numpy as np
except ImportError:
    np = None

MAX_GAP = 0.25
MIN_SAMPLES = 24
MIN_SECONDS = 2.0
MIN_CYCLES = 2.5
MAX_WINDOW = 6.0
MIN_R2 = 0.65
CHANGE_THRESHOLD = 0.08
STEADY_PHASES = {"Hold", "SprintCycle"}


def number(value):
    return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) else None


def circular_delta(a, b):
    return (a - b + 0.5) % 1.0 - 0.5


def stats(values):
    values = sorted(v for v in values if v is not None and math.isfinite(v))
    if not values:
        return None
    at = (len(values) - 1) * .9
    lower = int(at)
    p90 = values[lower] + (values[min(lower + 1, len(values) - 1)] - values[lower]) * (at - lower)
    return {"n": len(values), "min": values[0], "p50": statistics.median(values),
            "p90": p90, "max": values[-1]}


def point(value):
    if not isinstance(value, dict):
        return None
    if "Available" in value:
        if not value["Available"]:
            return None
        value = value.get("Value") or {}
    result = [number(value.get(axis)) for axis in ("X", "Y", "Z")]
    return result if all(v is not None for v in result) else None


def angles(value, side):
    value = value or {}
    if not value.get("HasRoot"):
        return None
    keys = [side + "ArmAngle", side + "ThighAngle"]
    if not all(value.get("Has" + key) is True for key in keys):
        return None
    result = [number(value.get(key)) for key in keys]
    return result if all(v is not None for v in result) else None


def load_rows(path):
    """Normalize only documented runtime schemas; missing signals stay missing."""
    path = Path(path)
    rows = []
    if path.suffix.lower() == ".jsonl":
        with path.open(encoding="utf-8-sig") as stream:
            for line in stream:
                item = json.loads(line)
                if item.get("k") != "s":
                    continue
                rows.append({"id": str(item.get("b")), "frame": item.get("f"), "time": number(item.get("t")),
                             "position": point(dict(zip(("X", "Y", "Z"), [item.get(k) for k in ("x", "y", "z")]))),
                             "physical_sprint": bool(item["spr"]) if type(item.get("spr")) is int and item["spr"] in (0, 1) else None,
                             "phase": item.get("ph"), "clip": item.get("clip"), "driver": item.get("drv"),
                             "weight": number(item.get("w")), "sync": item.get("sync") or {},
                             "before": item.get("syncBefore"), "after": item.get("syncAfter"),
                             "visible": item.get("vis") == 1, "simplified": item.get("simp") != 0,
                             "suspended": item.get("sus") != 0,
                             "aiming": bool(item.get("aim")) if item.get("aimKnown") == 1 else None,
                             "shooting": bool(item.get("shoot")) if item.get("shootKnown") == 1 else None})
        source = "fleet"
    else:
        document = json.loads(path.read_text(encoding="utf-8-sig"))
        groups = defaultdict(lambda: defaultdict(list))
        for sample in document.get("Samples", []):
            if sample.get("Stage") in {"before_visual", "after_lock"}:
                groups[sample.get("Frame")][sample["Stage"]].append(sample)
        for frame, stages in groups.items():
            finals = stages["after_lock"]
            if len(finals) != 1:
                continue
            item = finals[0]
            before = stages["before_visual"]
            pose = item.get("Pose") or {}
            movement = item.get("Movement") or {}
            sprint = movement.get("SprintEnabled") if movement.get("HasSprintEnabled") is True else None
            rows.append({"id": str(document.get("PlayerId", "puppet")), "frame": frame,
                         "time": number(item.get("UnscaledTime", item.get("Time"))), "position": point(item.get("Root")),
                         "physical_sprint": sprint if type(sprint) is bool else None,
                         "phase": pose.get("Phase"), "clip": pose.get("Clip"), "driver": pose.get("Driver"),
                         "weight": number(pose.get("Weight")), "sync": pose.get("Sync") or {},
                         "before": before[0].get("MotionSync") if len(before) == 1 else None,
                         "after": item.get("MotionSync"),
                         "visible": item.get("HasIsVisible") is True and item.get("IsVisible") is True,
                         "simplified": item.get("HasUsedSimplifiedSkeleton") is not True or item.get("UsedSimplifiedSkeleton") is True,
                         "suspended": pose.get("Active") is not True,
                         "aiming": item.get("IsAiming") if item.get("HasAiming") is True else None,
                         "shooting": item.get("IsShooting") if item.get("HasShooting") is True else None})
        source = "puppet"
    return rows, source


def context_reason(row):
    sync = row["sync"]
    if row["time"] is None or not isinstance(row["frame"], int):
        return "missing_time"
    if not row["visible"] or row["simplified"]:
        return "culled_or_simplified"
    if row["suspended"]:
        return "suspended"
    if row["phase"] not in STEADY_PHASES:
        return "non_cycle_phase"
    if row["weight"] is None or row["weight"] < .9:
        return "low_or_unknown_weight"
    if sync.get("SampleFrame") != row["frame"]:
        return "stale_phase_sample"
    if not sync.get("HasTransition"):
        return "unknown_transition"
    if sync.get("InTransition"):
        return "animator_transition"
    return None


def motion_context(row):
    value = row.get("physical_sprint")
    return "sprint" if value is True else "non_sprint" if value is False else "unknown"


def command_report(rows, scope=None):
    errors, excluded, reasons = [], Counter(), Counter()
    episodes = []
    current = None
    for row in sorted(rows, key=lambda r: (r["id"], r["time"] or 0)):
        sync = row["sync"]
        reasons[str(sync.get("PhaseLockReason", "missing"))] += 1
        reason = "outside_motion_scope" if scope is not None and motion_context(row) != scope else context_reason(row)
        # Only the native Tarkov cycle has the shared phase basis used by the
        # runtime lock. Alyx clips and held stop poses have unrelated clocks.
        if reason is None and (row["phase"] != "SprintCycle" or not str(row["clip"]).startswith("tarkov_")):
            reason = "no_shared_phase_basis"
        a, leg = number(sync.get("AnimatorPhase")), number(sync.get("LegPhase"))
        if reason is None and (not sync.get("HasAnimatorPhase") or not sync.get("HasLegPhase") or a is None or leg is None):
            reason = "missing_phase"
        if reason:
            excluded[reason] += 1
            current = None
            continue
        error = circular_delta(leg, a)
        errors.append(error)
        key = (row["id"], row["clip"], row["driver"], sync.get("AnimatorStateHash"), motion_context(row))
        if abs(error) > .05:
            if current is None or current["key"] != key or not 0 < row["time"] - current["end_time"] <= MAX_GAP:
                current = {"key": key, "id": row["id"], "clip": row["clip"], "start_frame": row["frame"],
                           "end_frame": row["frame"], "start_time": row["time"], "end_time": row["time"], "peak_absolute_cycles": abs(error)}
                episodes.append(current)
            current.update(end_frame=row["frame"], end_time=row["time"], peak_absolute_cycles=max(abs(error), current["peak_absolute_cycles"]))
        else:
            current = None
    for episode in episodes:
        episode.pop("key")
    return {"signed_error_cycles": stats(errors), "absolute_error_cycles": stats([abs(v) for v in errors]),
            "excluded_rows": dict(excluded), "lock_reasons": dict(reasons), "episodes_over_0_05_cycles": episodes,
            "note": "Playback phase agreement only; a successful lock does not prove visible arm synchronization."}


def segments(rows, side):
    # Controller displacement can update less often than render/capture frames.
    # Estimate travel over 0.1 s instead of interpreting intervening identical
    # positions as repeated stops. Never bridge a gap or a teleport.
    history = []
    last_id = None
    for row in sorted(rows, key=lambda r: (r["id"], r["time"] or 0)):
        row["travel_speed"] = None
        if row["id"] != last_id or row["time"] is None or row["position"] is None:
            history = []
        last_id = row["id"]
        if row["time"] is None or row["position"] is None:
            continue
        if history:
            dt = row["time"] - history[-1]["time"]
            distance = math.dist(row["position"], history[-1]["position"])
            if not 0 < dt <= MAX_GAP or distance > 1 or distance > max(.25, 8 * dt):
                history = []
        history.append(row)
        while len(history) > 1 and row["time"] - history[1]["time"] >= .1:
            history.pop(0)
        dt = row["time"] - history[0]["time"]
        if dt >= .08:
            row["travel_speed"] = math.hypot(row["position"][0] - history[0]["position"][0], row["position"][2] - history[0]["position"][2]) / dt
    excluded = Counter()
    result = []
    current = []
    previous = None
    for row in sorted(rows, key=lambda r: (r["id"], r["time"] or 0)):
        reason = context_reason(row)
        if reason is None:
            if row["aiming"] is None or row["shooting"] is None:
                reason = "unknown_weapon_context"
            elif row["aiming"] or row["shooting"]:
                reason = "aiming_or_shooting"
            elif not row["sync"].get("HasAnimatorStateHash"):
                reason = "unknown_animator_state"
            elif angles(row["before"], side) is None or angles(row["after"], side) is None:
                reason = "missing_paired_angles"
            elif row["travel_speed"] is None:
                reason = "missing_travel_window"
            elif row["travel_speed"] < .2:
                reason = "not_moving"
        key = (row["id"], row["clip"], row["driver"], row["phase"], row["sync"].get("AnimatorStateHash"), motion_context(row))
        boundary = previous is None or previous[0] != key
        if previous is not None and not boundary and reason is None:
            old = previous[1]
            dt = row["time"] - old["time"]
            if not 0 < dt <= MAX_GAP:
                boundary = True
            elif old["position"] is None or row["position"] is None:
                reason = "missing_position"
            else:
                distance = math.dist(old["position"], row["position"])
                if distance > 1 or distance > max(.25, 8 * dt):
                    reason = "teleport"
        if reason or boundary:
            if current:
                result.append(current)
            current = []
        if reason:
            excluded[reason] += 1
            previous = None
        else:
            current.append(row)
            previous = (key, row)
    if current:
        result.append(current)
    return result, excluded


def fit(t, y, frequency):
    # Timestamp-based least squares supports irregular capture intervals directly.
    omega = 2 * math.pi * frequency * t
    basis = np.column_stack((np.ones(len(t)), t - t.mean(), np.cos(omega), np.sin(omega)))
    coefficients = np.linalg.lstsq(basis, y, rcond=None)[0]
    residual = y - basis @ coefficients
    variance = float(np.sum((y - y.mean()) ** 2))
    r2 = 1 - float(residual @ residual) / variance if variance > 1e-9 else 0.0
    amplitude = math.hypot(float(coefficients[2]), float(coefficients[3]))
    # Require the harmonic itself, not a fitted linear trend, to explain the motion.
    periodic = basis[:, 2:] @ coefficients[2:]
    periodic_fraction = float(np.sum(periodic ** 2)) / variance if variance > 1e-9 else 0.0
    phase = math.atan2(-float(coefficients[3]), float(coefficients[2])) / (2 * math.pi)
    return {"r2": r2, "periodic_fraction": periodic_fraction, "amplitude_degrees": amplitude, "phase": phase}


def measure_window(rows, side):
    if np is None:
        return None, "numpy_unavailable"
    duration = rows[-1]["time"] - rows[0]["time"]
    if len(rows) < MIN_SAMPLES or duration < MIN_SECONDS:
        return None, "short_window"
    t = np.asarray([r["time"] - rows[0]["time"] for r in rows])
    signals = np.asarray([angles(r["before"], side) + angles(r["after"], side) for r in rows])
    signals = np.rad2deg(np.unwrap(np.deg2rad(signals), axis=0))
    # Search the native thigh's dominant gait frequency; require at least 2.5 cycles.
    low, high = max(.5, MIN_CYCLES / duration), min(4.0, .4 / float(np.median(np.diff(t))))
    if low >= high:
        return None, "insufficient_cycles"
    candidates = np.arange(low, high + 1e-9, 1 / (12 * duration))
    frequency, native_thigh = max(((float(f), fit(t, signals[:, 1], float(f))) for f in candidates), key=lambda item: item[1]["r2"])
    fits = [fit(t, signals[:, column], frequency) for column in range(4)]
    for index, value in enumerate(fits):
        if value["amplitude_degrees"] < (3 if index % 2 == 0 else 2):
            return None, "low_amplitude"
        if value["r2"] < MIN_R2 or not .55 <= value["periodic_fraction"] <= 1.5:
            return None, "nonperiodic_or_unstable_cadence"
    # A harmonic or trend can fit too few true cycles. Count native thigh crossings
    # around its median as an independent minimum-cycle check.
    centered = signals[:, 1] - np.median(signals[:, 1])
    crossings = int(np.count_nonzero((centered[:-1] <= 0) & (centered[1:] > 0)))
    if crossings < 2 or frequency * duration < MIN_CYCLES:
        return None, "insufficient_cycles"
    native_offset = circular_delta(fits[0]["phase"], fits[1]["phase"])
    final_offset = circular_delta(fits[2]["phase"], fits[3]["phase"])
    delta = circular_delta(final_offset, native_offset)
    return {"motion_context": motion_context(rows[0]), "id": rows[0]["id"], "side": side, "clip": rows[0]["clip"], "driver": rows[0]["driver"],
            "start_frame": rows[0]["frame"], "end_frame": rows[-1]["frame"],
            "start_time": rows[0]["time"], "end_time": rows[-1]["time"], "samples": len(rows),
            "frequency_hz": frequency, "cycles": frequency * duration,
            "native_arm_minus_thigh_cycles": native_offset, "final_arm_minus_thigh_cycles": final_offset,
            "change_cycles": delta, "change_seconds": delta / frequency,
            "changed_over_threshold": abs(delta) > CHANGE_THRESHOLD,
            "minimum_fit_r2": min(value["r2"] for value in fits),
            "amplitudes_degrees": dict(zip(("native_arm", "native_thigh", "final_arm", "final_thigh"), [v["amplitude_degrees"] for v in fits]))}, None


def window_slices(segment):
    start = 0
    while start < len(segment):
        end = start + 1
        while end < len(segment) and segment[end]["time"] - segment[start]["time"] <= MAX_WINDOW:
            end += 1
        yield segment[start:end]
        if end == len(segment):
            break
        target = segment[start]["time"] + MAX_WINDOW / 2
        start += 1
        while start < end and segment[start]["time"] < target:
            start += 1


def analyse(path):
    rows, source = load_rows(path)
    windows, excluded_rows, excluded_windows = [], Counter(), Counter()
    for side in ("Left", "Right"):
        runs, excluded = segments(rows, side)
        excluded_rows.update(excluded)
        for run in runs:
            for window in window_slices(run):
                measurement, reason = measure_window(window, side)
                if measurement is not None:
                    windows.append(measurement)
                else:
                    excluded_windows[reason] += 1
    return {"schema": "manimal.motionmatching.arm-sync.v1", "input": str(path), "source": source, "rows": len(rows),
            "commanded_phase_agreement": command_report(rows),
            "motion_contexts": {scope: {
                "priority": "primary" if scope == "sprint" else "informational" if scope == "non_sprint" else "unclassified",
                "rows": sum(motion_context(row) == scope for row in rows),
                "commanded_phase_agreement": command_report(rows, scope),
                "observed_motion": {
                    "assessable_windows": sum(w["motion_context"] == scope for w in windows),
                    "changed_windows": sum(w["motion_context"] == scope and w["changed_over_threshold"] for w in windows),
                    "windows": [w for w in windows if w["motion_context"] == scope]}}
                for scope in ("sprint", "non_sprint", "unknown")},
            "observed_motion": {"assessable_windows": len(windows), "changed_windows": sum(w["changed_over_threshold"] for w in windows),
                                "windows": windows, "excluded_side_rows": dict(excluded_rows), "excluded_windows": dict(excluded_windows),
                                "absolute_change_cycles": stats([abs(w["change_cycles"]) for w in windows])},
            "policy": {"primary_scope": "physical sprinting; non-sprinting phase differences are informational",
                       "sprint_state_source": "explicit Movement.SprintEnabled or fleet spr; internal SprintCycle phase does not identify sprinting",
                       "minimum_cycles": MIN_CYCLES, "minimum_seconds": MIN_SECONDS, "minimum_fit_r2": MIN_R2,
                       "max_gap_seconds": MAX_GAP, "change_threshold_cycles": CHANGE_THRESHOLD,
                       "baseline_stage": "before_visual", "final_stage": "after_lock",
                       "phase_sign": "Positive arm-minus-thigh means the arm leads the thigh in the fitted cosine cycle.",
                       "limits": "Heuristic paired motion comparison, not a naturalness score. Overlapping windows are not independent. The pass includes playback, EFT IK and placement; it does not isolate the foot placer."}}


analyze = analyse


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = json.dumps(analyse(args.input), indent=2, allow_nan=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(output + "\n", encoding="utf-8")
    else:
        print(output)


if __name__ == "__main__":
    main()
