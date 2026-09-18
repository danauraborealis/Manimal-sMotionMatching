"""Offline foot, leg, and body placement diagnostics.

The runtime writes two related formats:

* puppet captures are JSON documents with ``Samples``.  A logical frame is
  paired from the exact same ``Frame``: ``after_visual`` is the pose before
  the placer and ``after_lock`` is the pose after it.  ``pre_render`` is kept
  as a separate observation because it can contain writes made later in the
  frame.
* fleet captures are JSONL streams.  ``geometrySchema=1`` rows contain
  ``h/k/a`` and ``h0/k0/a0`` for each side, plus ``hl/to`` and ``hl0/to0``
  sole points.  Older streams deliberately remain unsupported for geometry;
  absent points are never replaced with zeroes.

``load_frames(path)`` returns ``(frames, meta)``.  Each normalized frame has
  this stable shape (keys with unavailable data are ``None``):

    {
      "source": "puppet" | "fleet", "id": ..., "time": float,
      "frame": int, "stage": str, "before_stage": str,
      "after_stage": str, "before": {"root", "pelvis", "legs"},
      "after": {"root", "pelvis", "legs"},
      "render": optional ``pre_render`` geometry,
      "legs": alias for ``after.legs``, "L"/"R": aliases for final legs,
      "context": {"clip", "phase", "speed", "driver", "sprint",
                  "command_speed", "measured_speed", "body_yaw", "turn_rate"},
      "paired": bool, "pair_status": "paired_after_lock" | "unpaired_after_visual",
      "coverage": str, "coverage_reasons": [str, ...]
    }

``analyse``/``analyze`` accepts either an input path/document or normalized
frames and returns JSON-serializable diagnostics.  It reports measurements,
heuristic flags, physical geometry invalidity, and sustained episodes.  A
proxy inter-leg distance is explicitly labelled as proximity evidence; it is
not a mesh-collision result and no naturalness score is produced.
"""

from __future__ import annotations

import argparse
import json
import math
import statistics
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Dict, Iterable, List, Mapping, MutableMapping, Optional, Sequence, Tuple


SIDES = ("L", "R")
MIN_EPISODE_SECONDS = 0.15
MAX_CONTINUITY_DT = 0.10
# Fleet geometry is sampled at roughly 5 Hz while movement derivatives are
# sampled near 30 Hz.  A slower geometry stream can still support a static
# clearance/pose episode; sole drift and travel derivatives keep the stricter
# interval above.
MAX_GEOMETRY_DT = 0.30
TELEPORT_DISTANCE_M = 1.0
TELEPORT_SPEED_MPS = 8.0
BASELINE_MIN_SAMPLES = 20
BASELINE_MIN_SECONDS = 0.50


def _get(value: Any, *names: str, default: Any = None) -> Any:
    """Case-insensitive mapping lookup with a small amount of schema tolerance."""

    if not isinstance(value, Mapping):
        return default
    for name in names:
        if name in value:
            return value[name]
    lowered = {str(k).lower(): v for k, v in value.items()}
    for name in names:
        if name.lower() in lowered:
            return lowered[name.lower()]
    return default


def _number(value: Any, default: Optional[float] = None) -> Optional[float]:
    if value is None or isinstance(value, bool):
        return default
    try:
        result = float(value)
    except (TypeError, ValueError):
        return default
    return result if math.isfinite(result) else default


def _integer(value: Any, default: Optional[int] = None) -> Optional[int]:
    number = _number(value)
    if number is None:
        return default
    return int(number)


def _truth(value: Any, *names: str, default: Optional[bool] = None) -> Optional[bool]:
    if not isinstance(value, Mapping):
        return default
    found = _get(value, *names, default=None)
    if found is None:
        return default
    if isinstance(found, bool):
        return found
    if isinstance(found, (int, float)):
        return bool(found)
    if isinstance(found, str):
        if found.strip().lower() in {"true", "yes", "on", "1"}:
            return True
        if found.strip().lower() in {"false", "no", "off", "0"}:
            return False
    return default


def _point(value: Any) -> Optional[List[float]]:
    """Return a finite xyz list without treating missing vectors as origin."""

    if isinstance(value, Mapping):
        # Unity position snapshots wrap vectors in {Available, Value}.
        available = _truth(value, "Available", default=None)
        if available is False:
            return None
        nested = _get(value, "Value", "value", default=None)
        if nested is not None and nested is not value:
            point = _point(nested)
            if point is not None:
                return point
        value = (
            _get(value, "X", "x", default=None),
            _get(value, "Y", "y", default=None),
            _get(value, "Z", "z", default=None),
        )
    if isinstance(value, (list, tuple)) and len(value) >= 3:
        numbers = [_number(value[0]), _number(value[1]), _number(value[2])]
        if all(number is not None for number in numbers):
            return [float(number) for number in numbers]  # type: ignore[arg-type]
    return None


def _position_field(container: Any, value_name: str, flag_name: Optional[str] = None) -> Optional[List[float]]:
    if not isinstance(container, Mapping):
        return None
    if flag_name is not None:
        available = _truth(container, flag_name, default=None)
        if available is False:
            return None
    return _point(_get(container, value_name, default=None))


def _copy_point(point: Optional[Sequence[float]]) -> Optional[List[float]]:
    return None if point is None else [float(point[0]), float(point[1]), float(point[2])]


def _vsub(a: Sequence[float], b: Sequence[float]) -> List[float]:
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]


def _vadd(a: Sequence[float], b: Sequence[float]) -> List[float]:
    return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]


def _vmul(a: Sequence[float], scalar: float) -> List[float]:
    return [a[0] * scalar, a[1] * scalar, a[2] * scalar]


def _dot(a: Sequence[float], b: Sequence[float]) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _norm(a: Sequence[float]) -> float:
    return math.sqrt(max(0.0, _dot(a, a)))


def _distance(a: Optional[Sequence[float]], b: Optional[Sequence[float]]) -> Optional[float]:
    if a is None or b is None:
        return None
    return _norm(_vsub(a, b))


def _horizontal_distance(a: Optional[Sequence[float]], b: Optional[Sequence[float]]) -> Optional[float]:
    if a is None or b is None:
        return None
    return math.hypot(a[0] - b[0], a[2] - b[2])


def _horizontal_speed(a: Optional[Sequence[float]], b: Optional[Sequence[float]], dt: float) -> Optional[float]:
    distance = _horizontal_distance(a, b)
    return None if distance is None or dt <= 0.0 else distance / dt


def _safe_unit_horizontal(vector: Optional[Sequence[float]]) -> Optional[List[float]]:
    if vector is None:
        return None
    length = math.hypot(vector[0], vector[2])
    if length < 1e-7:
        return None
    return [vector[0] / length, 0.0, vector[2] / length]


def _yaw_forward(yaw_degrees: Optional[float]) -> Optional[List[float]]:
    if yaw_degrees is None:
        return None
    radians = math.radians(yaw_degrees)
    return [math.sin(radians), 0.0, math.cos(radians)]


def _yaw_right(yaw_degrees: Optional[float]) -> Optional[List[float]]:
    if yaw_degrees is None:
        return None
    radians = math.radians(yaw_degrees)
    return [math.cos(radians), 0.0, -math.sin(radians)]


def _angle_delta_degrees(current: Optional[float], previous: Optional[float]) -> Optional[float]:
    if current is None or previous is None:
        return None
    return (current - previous + 180.0) % 360.0 - 180.0


def segment_segment_distance(
    a0: Sequence[float],
    a1: Sequence[float],
    b0: Sequence[float],
    b1: Sequence[float],
) -> float:
    """Shortest 3D distance between two finite line segments.

    This is the geometry primitive used by the inter-leg proxy.  It handles
    parallel, touching, and degenerate segments without projecting to XZ.
    """

    u = _vsub(a1, a0)
    v = _vsub(b1, b0)
    w = _vsub(a0, b0)
    aa = _dot(u, u)
    bb = _dot(u, v)
    cc = _dot(v, v)
    dd = _dot(u, w)
    ee = _dot(v, w)
    denominator = aa * cc - bb * bb
    epsilon = 1e-12

    if aa <= epsilon and cc <= epsilon:
        return _norm(_vsub(a0, b0))
    if aa <= epsilon:
        t = max(0.0, min(1.0, ee / cc if cc > epsilon else 0.0))
        return _norm(_vsub(a0, _vadd(b0, _vmul(v, t))))
    if cc <= epsilon:
        s = max(0.0, min(1.0, -dd / aa))
        return _norm(_vsub(_vadd(a0, _vmul(u, s)), b0))

    if denominator > epsilon:
        s = (bb * ee - cc * dd) / denominator
        t = (aa * ee - bb * dd) / denominator
    else:
        # Parallel segments: choose the projection of one endpoint, then
        # clamp and let the endpoint correction below resolve the other.
        s = 0.0
        t = ee / cc if cc > epsilon else 0.0

    if s < 0.0:
        s = 0.0
        t = ee / cc if cc > epsilon else 0.0
    elif s > 1.0:
        s = 1.0
        t = (ee + bb) / cc if cc > epsilon else 0.0
    if t < 0.0:
        t = 0.0
        s = max(0.0, min(1.0, -dd / aa))
    elif t > 1.0:
        t = 1.0
        s = max(0.0, min(1.0, (bb - dd) / aa))
    closest_a = _vadd(a0, _vmul(u, max(0.0, min(1.0, s))))
    closest_b = _vadd(b0, _vmul(v, max(0.0, min(1.0, t))))
    return _norm(_vsub(closest_a, closest_b))


# Friendly aliases used by small downstream viewers/tests.
segment_distance_3d = segment_segment_distance
segment_distance = segment_segment_distance


def _stats(values: Iterable[Optional[float]]) -> Optional[Dict[str, Any]]:
    clean = sorted(float(value) for value in values if value is not None and math.isfinite(float(value)))
    if not clean:
        return None

    def quantile(fraction: float) -> float:
        index = min(len(clean) - 1, max(0, int(round((len(clean) - 1) * fraction))))
        return clean[index]

    return {
        "n": len(clean),
        "min": clean[0],
        "p10": quantile(0.10),
        "p50": statistics.median(clean),
        "p90": quantile(0.90),
        "max": clean[-1],
        "mean": statistics.fmean(clean),
    }


def _stage_geometry(sample: Mapping[str, Any], source: str, final: bool = True) -> Dict[str, Any]:
    """Extract root/pelvis/legs from one puppet sample."""

    # Root/Pelvis are availability-wrapped vectors.  Read the wrapper through
    # _point so ``Available: false`` cannot turn its serialized zero Value
    # into a real origin observation.
    root = _point(_get(sample, "Root", default=None))
    pelvis = _point(_get(sample, "Pelvis", default=None))
    legs: Dict[str, Dict[str, Any]] = {}
    grounder = _get(sample, "Grounder", default={}) or {}
    pose = _get(sample, "Pose", default={}) or {}

    for side, long_side in (("L", "Left"), ("R", "Right")):
        source_leg = _get(grounder, long_side + "Leg", default={}) or {}
        probe = _get(pose, "Placer" + side, default={}) or {}
        leg: Dict[str, Any] = {}
        for key, field in (("hip", "HipPosition"), ("knee", "KneePosition"), ("ankle", "FootPosition")):
            flag = "Has" + field
            point = _position_field(source_leg, field, flag)
            if point is not None:
                leg[key] = point
        toe = _position_field(source_leg, "ToePosition", "HasToePosition")
        if toe is not None:
            # Grounder ToePosition is a toe-bone joint. Keep it available for
            # replay, but do not call it a sole endpoint: only explicit
            # PlacedToe/ToeSolePosition data participates in contact drift.
            leg["toe_bone"] = toe
        # FootPlacerProbe only exposes PlacedHeel/PlacedToe.  Those fields are
        # meaningful only for an active placer probe; an inactive probe can
        # contain reset zeroes or stale values.  Pre-placement and pre-render
        # samples deliberately omit these fields because they are not an
        # observed sole endpoint in those stages.
        if final and _truth(probe, "Active", default=None) is True:
            heel = _point(_get(probe, "PlacedHeel", default=None))
            placed_toe = _point(_get(probe, "PlacedToe", default=None))
            if heel is not None:
                leg["heel"] = heel
            if placed_toe is not None:
                leg["toe"] = placed_toe
        for key, field in (("active", "Active"), ("locked", "Locked"), ("frozen", "Frozen")):
            flag = _truth(probe, field, default=None)
            if flag is not None:
                leg[key] = flag
        for key, field in (("authored_grounded", "AuthoredGrounded"),
                           ("in_authored_swing", "InAuthoredSwing"), ("fading", "Fading"),
                           ("refinement", "Refinement"), ("ground_normal_available", "GroundNormalAvailable")):
            flag = _truth(probe, field, default=None)
            if flag is not None:
                leg[key] = flag
        contact = _truth(pose, "Contact" + side, default=None)
        if contact is not None:
            leg["contact"] = contact
        for key, field in (("cycle", "Cycle"), ("progression", "Progression"),
                           ("authority", "Authority"), ("correction", "Correction"),
                           ("failure", "Failure"), ("heading", "PlacedHeading"),
                           ("sole_target_error", "SoleTargetError"), ("ankle_limit_degrees", "AnkleLimitDegrees"),
                           ("midpoint_correction", "MidpointCorrection"), ("stride_blend", "StrideBlend")):
            number = _number(_get(probe, field, default=None))
            if number is not None:
                leg[key] = number
        for key, field in (("target", "Target"), ("shown", "Shown"), ("placed", "Placed"),
                           ("previous", "Prev"), ("next", "Next")):
            point = _point(_get(probe, field, default=None))
            if point is not None:
                leg[key] = point
        if leg:
            legs[side] = leg
    return {"root": root, "pelvis": pelvis, "legs": legs}


def _empty_geometry() -> Dict[str, Any]:
    """Represent an unavailable stage without manufacturing zero vectors."""

    return {"root": None, "pelvis": None, "legs": {}}


def _complete_leg_count(geometry: Mapping[str, Any]) -> int:
    """Count legs with a complete hip/knee/ankle chain, ignoring probe metadata."""

    legs = geometry.get("legs", {}) if isinstance(geometry, Mapping) else {}
    if not isinstance(legs, Mapping):
        return 0
    return sum(
        1
        for side in SIDES
        if isinstance(legs.get(side), Mapping)
        and all(legs[side].get(key) is not None for key in ("hip", "knee", "ankle"))
    )


def _fleet_geometry(row: Mapping[str, Any], supported: bool) -> Tuple[Dict[str, Any], Dict[str, Any]]:
    """Extract the explicit fleet geometry pair without fabricating values."""

    final_root = _point(_get(row, "rj", default=None))
    final_pelvis = _point(_get(row, "pe", default=None))
    before_pelvis = _point(_get(row, "pe0", default=None))
    if not supported:
        # Body position is valid context, but it is not leg geometry. Keep
        # roots for travel while making the unsupported state explicit.
        empty = {"root": final_root, "pelvis": final_pelvis, "legs": {}}
        return empty, {"root": None, "pelvis": before_pelvis, "legs": {}}

    final: Dict[str, Any] = {"root": final_root, "pelvis": final_pelvis, "legs": {}}
    before: Dict[str, Any] = {"root": _point(_get(row, "rj0", default=None)),
                              "pelvis": before_pelvis, "legs": {}}
    for side in SIDES:
        value = _get(row, side, default={}) or {}
        after_leg: Dict[str, Any] = {}
        before_leg: Dict[str, Any] = {}
        for key, field in (("hip", "h"), ("knee", "k"), ("ankle", "a"),
                           ("heel", "hl"), ("toe", "to")):
            point = _point(_get(value, field, default=None))
            if point is not None:
                after_leg[key] = point
            before_point = _point(_get(value, field + "0", default=None))
            if before_point is not None:
                before_leg[key] = before_point
        # Fleet metadata is compact, but retaining these fields makes rows
        # useful to the replay viewer and allows attribution to lock state.
        for key, field in (("active", "on"), ("locked", "lk"),
                           ("authored_grounded", "gr"), ("in_authored_swing", "sw"),
                           ("fading", "fade"), ("ground_normal_available", "normal")):
            flag = _truth(value, field, default=None)
            if flag is not None:
                after_leg[key] = flag
                before_leg[key] = flag
        for key, field in (("cycle", "cy"), ("progression", "pg"), ("authority", "auth"),
                           ("correction", "corr"), ("failure", "fail"), ("heading", "hd"),
                           ("sole_target_error", "soleError"), ("ankle_limit_degrees", "ankleLimit"),
                           ("midpoint_correction", "midCorrection"), ("stride_blend", "strideBlend")):
            number = _number(_get(value, field, default=None))
            if number is not None:
                after_leg[key] = number
                before_leg[key] = number
        source = _get(value, "src", default=None)
        if source is not None:
            after_leg["source"] = source
            before_leg["source"] = source
        if after_leg:
            final["legs"][side] = after_leg
        if before_leg:
            before["legs"][side] = before_leg
    return before, final


def _sample_context(sample: Mapping[str, Any], source: str) -> Dict[str, Any]:
    if source == "fleet":
        command_speed = _number(_get(sample, "spd", default=None))
        context = {
            "clip": _get(sample, "clip", default=None),
            "phase": _get(sample, "ph", default=None),
            "speed": None,
            "command_speed": command_speed,
            "driver": _get(sample, "drv", default=None),
            "sprint": bool(_truth(sample, "spr", default=False)),
            "body_yaw": _number(_get(sample, "yaw", default=None)),
            "turn": _number(_get(sample, "turn", "turnDeg", default=None)),
            "control": bool(_truth(sample, "control", default=False)),
        }
        vx = _number(_get(sample, "vx", default=None))
        vz = _number(_get(sample, "vz", default=None))
        context["velocity"] = [vx or 0.0, 0.0, vz or 0.0] if vx is not None or vz is not None else None
        if vx is not None or vz is not None:
            # vx/vz are the body's measured velocity.  Keep spd separately
            # because it is a command/driver value in the fleet stream.
            context["speed"] = math.hypot(vx or 0.0, vz or 0.0)
        return context

    pose = _get(sample, "Pose", default={}) or {}
    movement = _get(sample, "Movement", default={}) or {}
    velocity = _point(_get(sample, "Velocity", default=None)) if _truth(sample, "VelocityAvailable", default=False) else None
    command_speed = _number(_get(movement, "PlayerSpeed", default=None)) if _truth(movement, "HasPlayerSpeed", default=False) else None
    speed = command_speed
    if velocity is not None:
        speed = math.hypot(velocity[0], velocity[2])
    body_yaw = _number(_get(sample, "BodyYaw", default=None))
    if body_yaw is None:
        body_yaw = _number(_get(sample, "RootJointYaw", default=None))
    return {
        "clip": _get(pose, "Clip", default=None),
        "phase": _get(pose, "Phase", default=None),
        "speed": speed,
        "command_speed": command_speed,
        "driver": _get(pose, "Driver", default=None),
        "sprint": bool(_truth(movement, "SprintEnabled", "HasSprintEnabled", default=False)),
        "body_yaw": body_yaw,
        "velocity": velocity,
        "turn": None,
        "rate": _number(_get(pose, "Rate", default=None)),
        "weight": _number(_get(pose, "Weight", default=None)),
    }


def _coverage_for_sample(sample: Mapping[str, Any], source: str, geometry_count: int) -> Tuple[str, List[str]]:
    reasons: List[str] = []
    if source == "fleet":
        if bool(_truth(sample, "sus", default=False)):
            reasons.append("suspended")
        if _truth(sample, "vis", default=None) is False:
            reasons.append("culled")
        if bool(_truth(sample, "simp", default=False)):
            reasons.append("simplified_skeleton")
        if geometry_count == 0:
            reasons.append("missing_geometry")
    else:
        if _truth(sample, "HasIsVisible", default=None) is True and _truth(sample, "IsVisible", default=None) is False:
            reasons.append("culled")
        if _truth(sample, "HasUsedSimplifiedSkeleton", default=None) is True and _truth(sample, "UsedSimplifiedSkeleton", default=None):
            reasons.append("simplified_skeleton")
        culling = _get(sample, "Culling", default={}) or {}
        if _truth(culling, "HasIsActiveAndEnabled", default=None) is True and _truth(culling, "IsActiveAndEnabled", default=None) is False:
            reasons.append("culled")
        if geometry_count == 0:
            reasons.append("missing_geometry")
    if reasons:
        if "culled" in reasons:
            return "culled", reasons
        if "suspended" in reasons:
            return "suspended", reasons
        if "missing_geometry" in reasons:
            return "missing_geometry", reasons
        return "partial", reasons
    return "valid" if geometry_count > 0 else "missing_geometry", reasons


def _make_row(
    source: str,
    identifier: Any,
    time_value: Optional[float],
    frame_value: Optional[int],
    before_stage: str,
    after_stage: str,
    before: Dict[str, Any],
    after: Dict[str, Any],
    context: Dict[str, Any],
    coverage: str,
    reasons: List[str],
    render: Optional[Dict[str, Any]] = None,
    body_position: Optional[Sequence[float]] = None,
) -> Dict[str, Any]:
    row = {
        "source": source,
        "id": identifier,
        "time": float(time_value if time_value is not None else 0.0),
        "frame": int(frame_value if frame_value is not None else 0),
        "stage": after_stage,
        "before_stage": before_stage,
        "after_stage": after_stage,
        "before": before,
        "after": after,
        "render": render,
        "legs": after.get("legs", {}),
        "L": after.get("legs", {}).get("L", {}),
        "R": after.get("legs", {}).get("R", {}),
        "context": context,
        "coverage": coverage,
        "coverage_reasons": list(dict.fromkeys(reasons)),
        "body_position": _copy_point(body_position),
        "continuity_break": coverage in {"culled", "suspended", "missing_geometry", "unsupported", "partial", "unpaired"},
    }
    row["geometry"] = {"before": before, "after": after}
    return row


def _load_puppet_document(document: Mapping[str, Any]) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
    samples = _get(document, "Samples", default=[])
    if not isinstance(samples, list):
        samples = []
    groups: Dict[Any, Dict[str, List[Mapping[str, Any]]]] = defaultdict(lambda: defaultdict(list))
    order: Dict[Any, int] = {}
    for index, sample in enumerate(samples):
        if not isinstance(sample, Mapping):
            continue
        frame = _get(sample, "Frame", default=None)
        key: Any = ("frame", frame) if frame is not None else ("time", round(_number(_get(sample, "Time", default=0.0), 0.0) or 0.0, 6))
        stage = str(_get(sample, "Stage", default="(unspecified)"))
        groups[key][stage].append(sample)
        order.setdefault(key, index)

    rows: List[Dict[str, Any]] = []
    warnings: List[str] = []
    paired = 0
    pre_render = 0
    omitted_before = 0
    omitted_since_last = False
    for key in sorted(groups, key=lambda item: order[item]):
        stage_map = groups[key]

        def pick(stage: str) -> Optional[Mapping[str, Any]]:
            values = stage_map.get(stage) or []
            if not values:
                return None
            return max(values, key=lambda value: _integer(_get(value, "Index", default=0), 0) or 0)

        before_sample = pick("after_visual")
        if before_sample is None:
            # before_visual precedes clip playback and IK, so its delta would
            # incorrectly attribute those writes to the placement pass.
            omitted_before += 1
            omitted_since_last = True
            warnings.append("frames without after_visual omitted from paired placement analysis")
            continue
        after_sample = pick("after_lock")
        paired_frame = after_sample is not None
        if not paired_frame:
            after_stage = "after_visual"
        else:
            after_stage = "after_lock"
            paired += 1
        render_sample = pick("pre_render")
        if render_sample is not None:
            pre_render += 1
        before = _stage_geometry(before_sample, "puppet", final=False)
        # A missing after_lock is an unpaired pre-placement observation.  Do
        # not reuse it as a final stage: that would turn stale probe fields
        # into fake sole endpoints and create zero placement deltas.
        after = _stage_geometry(after_sample, "puppet", final=True) if paired_frame else _empty_geometry()
        # Probe sole endpoints are not observed in pre_render, so this stage
        # is intentionally extracted without final-only placer points.
        render = _stage_geometry(render_sample, "puppet", final=False) if render_sample is not None and paired_frame else None
        geometry_count = _complete_leg_count(after if paired_frame else before)
        coverage, reasons = _coverage_for_sample(after_sample or before_sample, "puppet", geometry_count)
        if not paired_frame:
            reasons.append("no_after_lock_pair")
            if coverage == "valid":
                coverage = "unpaired"
        observation_sample = after_sample or before_sample
        sample_time = _number(_get(observation_sample, "Time", default=None))
        sample_frame = _integer(_get(observation_sample, "Frame", default=None))
        context = _sample_context(observation_sample, "puppet")
        body_position = after.get("root") or before.get("root")
        row = _make_row("puppet", _get(document, "LocalBotId", default="puppet"), sample_time, sample_frame,
                        str(_get(before_sample, "Stage", default="after_visual")), after_stage,
                        before, after, context, coverage, reasons, render, body_position)
        row["paired"] = paired_frame
        if omitted_since_last:
            row["missing_before_pair_gap"] = True
            omitted_since_last = False
        row["pair_status"] = "paired_after_lock" if paired_frame else "unpaired_after_visual"
        row["raw"] = {"before": before_sample, "after": after_sample, "render": render_sample}
        rows.append(row)

    meta = {
        "source": "puppet",
        "schema": _get(document, "Schema", default=None),
        "geometry_schema": None,
        "coverage": {
            "input_samples": len(samples),
            "logical_frames": len(rows),
            "paired_after_lock": paired,
            "unpaired_frames": sum(not row.get("paired", False) for row in rows),
            "omitted_missing_after_visual_frames": omitted_before,
            "pre_render_frames": pre_render,
            "culled_frames": sum(row["coverage"] == "culled" for row in rows),
            "suspended_frames": sum(row["coverage"] == "suspended" for row in rows),
            "missing_geometry_frames": sum(row["coverage"] == "missing_geometry" for row in rows),
        },
        "warnings": list(dict.fromkeys(warnings)),
    }
    return rows, meta


def _read_jsonl(path: Path) -> Tuple[List[Mapping[str, Any]], Dict[str, Any]]:
    records: List[Mapping[str, Any]] = []
    meta: Dict[str, Any] = {"source": "fleet", "warnings": [], "malformed_lines": 0}
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except UnicodeDecodeError:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    for line_number, line in enumerate(lines, 1):
        if not line.strip():
            continue
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            meta["malformed_lines"] += 1
            continue
        if not isinstance(record, Mapping):
            continue
        kind = _get(record, "k", default=None)
        if kind == "meta":
            meta.update(dict(record))
        elif kind == "s":
            records.append(record)
    if meta["malformed_lines"]:
        meta["warnings"].append("ignored %d malformed fleet JSONL line(s)" % meta["malformed_lines"])
    return records, meta


def _load_fleet_records(records: Sequence[Mapping[str, Any]], meta: Mapping[str, Any]) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
    geometry_schema = _integer(_get(meta, "geometrySchema", "geometry_schema", default=None))
    supported_schema = geometry_schema == 1
    rows: List[Dict[str, Any]] = []
    warnings = list(_get(meta, "warnings", default=[])) if isinstance(_get(meta, "warnings", default=[]), list) else []
    if not supported_schema:
        warnings.append("fleet geometry is unsupported: geometrySchema=1 is required; no leg points were inferred")
    for sample in records:
        # A probe/metadata object alone is not geometry.  Require one
        # complete final hip/knee/ankle chain before calling a row supported.
        has_geometry = any(
            isinstance(_get(sample, side, default=None), Mapping)
            and all(_point(_get(_get(sample, side, default={}), field, default=None)) is not None
                    for field in ("h", "k", "a"))
            for side in SIDES
        )
        supported = supported_schema and has_geometry
        before, after = _fleet_geometry(sample, supported)
        geometry_count = _complete_leg_count(after)
        coverage, reasons = _coverage_for_sample(sample, "fleet", geometry_count)
        if not supported:
            coverage = "unsupported"
            reasons = list(dict.fromkeys(reasons + ["unsupported_geometry_schema" if not supported_schema else "missing_geometry"]))
        t = _number(_get(sample, "t", "Time", default=None))
        frame = _integer(_get(sample, "f", "Frame", default=None))
        body_position = _point([_get(sample, "x", default=None), _get(sample, "y", default=None), _get(sample, "z", default=None)])
        context = _sample_context(sample, "fleet")
        row = _make_row("fleet", _get(sample, "b", "bot", default="fleet"), t, frame,
                        "before_geometry", "final_geometry", before, after, context,
                        coverage, reasons, None, body_position)
        row["raw"] = sample
        row["geometry_supported"] = bool(supported)
        rows.append(row)
    rows.sort(key=lambda row: (str(row["id"]), row["time"], row["frame"]))
    bot_ids = sorted({str(row["id"]) for row in rows})
    result_meta = dict(meta)
    result_meta.update({
        "source": "fleet",
        "geometry_schema": geometry_schema,
        "schema": "fleet.jsonl",
        "coverage": {
            "input_samples": len(records),
            "logical_frames": len(rows),
            "geometry_supported": bool(supported_schema),
            "geometry_frames": sum(row.get("geometry_supported", False) for row in rows),
            "unsupported_geometry_frames": sum(row["coverage"] == "unsupported" for row in rows),
            "culled_frames": sum(row["coverage"] == "culled" for row in rows),
            "suspended_frames": sum(row["coverage"] == "suspended" for row in rows),
            "missing_geometry_frames": sum(row["coverage"] == "missing_geometry" for row in rows),
            "bots": bot_ids,
        },
        "warnings": list(dict.fromkeys(warnings)),
    })
    return rows, result_meta


def _resolve_input(path: Path) -> Path:
    if path.is_dir():
        status_candidates = [path / "status.json", path / "off" / "status.json", path / "on" / "status.json"]
        for status_path in status_candidates:
            if status_path.exists():
                try:
                    status = json.loads(status_path.read_text(encoding="utf-8"))
                    target = _get(status, "LastCapturePath", default=None)
                    if target and Path(str(target)).exists():
                        return Path(str(target))
                except (OSError, ValueError):
                    pass
        candidates = list(path.glob("*.jsonl")) + list(path.glob("motion-capture-*.json"))
        if not candidates:
            candidates = [candidate for candidate in path.glob("*.json") if candidate.name not in {"status.json", "summary.json", "comparison.json"}]
        if candidates:
            return max(candidates, key=lambda candidate: candidate.stat().st_mtime_ns)
    if path.suffix.lower() == ".json":
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
            target = _get(value, "LastCapturePath", default=None)
            if target and Path(str(target)).exists():
                return Path(str(target))
        except (OSError, ValueError):
            pass
    return path


def load_frames_from_document(document: Mapping[str, Any]) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
    """Normalize an already-loaded puppet capture document."""

    if isinstance(document, Mapping) and isinstance(_get(document, "Samples", default=None), list):
        return _load_puppet_document(document)
    raise ValueError("document is not a puppet capture with a Samples array")


def load_frames(path: Any) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
    """Load and normalize a puppet JSON capture or fleet JSONL stream."""

    if isinstance(path, Mapping):
        return load_frames_from_document(path)

    resolved = _resolve_input(Path(path))
    if resolved.suffix.lower() == ".jsonl":
        records, meta = _read_jsonl(resolved)
        frames, meta = _load_fleet_records(records, meta)
        meta["path"] = str(resolved)
        return frames, meta
    document = json.loads(resolved.read_text(encoding="utf-8"))
    if isinstance(document, Mapping) and isinstance(_get(document, "Samples", default=None), list):
        frames, meta = load_frames_from_document(document)
        meta["path"] = str(resolved)
        return frames, meta
    if isinstance(document, list):
        records = [record for record in document if isinstance(record, Mapping)]
        frames, meta = _load_fleet_records(records, {})
        meta["path"] = str(resolved)
        return frames, meta
    raise ValueError("input is neither a puppet capture JSON document nor a fleet JSONL stream")


def _chain(leg: Mapping[str, Any]) -> Optional[Dict[str, float]]:
    hip, knee, ankle = leg.get("hip"), leg.get("knee"), leg.get("ankle")
    if hip is None or knee is None or ankle is None:
        return None
    upper = _distance(hip, knee)
    lower = _distance(knee, ankle)
    direct = _distance(hip, ankle)
    if upper is None or lower is None or direct is None:
        return None
    total = upper + lower
    if total <= 1e-8:
        return {"upper_m": upper, "lower_m": lower, "chain_m": total, "reach_m": direct, "straightness": None}
    return {"upper_m": upper, "lower_m": lower, "chain_m": total, "reach_m": direct, "straightness": direct / total}


def _inter_leg(geometry: Mapping[str, Any]) -> Optional[Dict[str, Any]]:
    legs = geometry.get("legs", {}) if isinstance(geometry, Mapping) else {}
    left, right = legs.get("L"), legs.get("R")
    if not isinstance(left, Mapping) or not isinstance(right, Mapping):
        return None
    segments: Dict[str, Tuple[Sequence[float], Sequence[float]]] = {}
    for side, leg in (("L", left), ("R", right)):
        if all(leg.get(key) is not None for key in ("hip", "knee", "ankle")):
            segments[side + "_thigh"] = (leg["hip"], leg["knee"])
            segments[side + "_shin"] = (leg["knee"], leg["ankle"])
    pairs: List[Tuple[str, str, float]] = []
    for left_name in ("L_thigh", "L_shin"):
        for right_name in ("R_thigh", "R_shin"):
            if left_name in segments and right_name in segments:
                pairs.append((left_name, right_name, segment_segment_distance(*segments[left_name], *segments[right_name])))
    if not pairs:
        return None
    pair, pair_right, distance = min(pairs, key=lambda value: value[2])
    left_chain, right_chain = _chain(left), _chain(right)
    chain = [c["chain_m"] for c in (left_chain, right_chain) if c is not None and c["chain_m"] > 1e-8]
    scale = statistics.fmean(chain) if chain else None
    return {"distance_m": distance, "pair": [pair, pair_right], "ratio": distance / scale if scale else None}


def _trailing(geometry: Mapping[str, Any], travel: Optional[Sequence[float]]) -> Dict[str, Dict[str, Optional[float]]]:
    result: Dict[str, Dict[str, Optional[float]]] = {}
    for side in SIDES:
        leg = (geometry.get("legs", {}) or {}).get(side, {})
        chain = _chain(leg) if isinstance(leg, Mapping) else None
        ankle, hip = leg.get("ankle"), leg.get("hip") if isinstance(leg, Mapping) else (None, None)
        if travel is None or ankle is None or hip is None or chain is None or chain["chain_m"] <= 1e-8:
            result[side] = {"raw_ratio": None, "trailing_ratio": None, "signed_m": None, "trailing_m": None}
            continue
        signed_m = _dot(_vsub(ankle, hip), travel)
        trailing_m = max(0.0, -signed_m)
        result[side] = {
            "raw_ratio": signed_m / chain["chain_m"],
            "trailing_ratio": trailing_m / chain["chain_m"],
            "signed_m": signed_m,
            "trailing_m": trailing_m,
        }
    return result


def _width(geometry: Mapping[str, Any], yaw: Optional[float]) -> Optional[float]:
    right_axis = _yaw_right(yaw)
    legs = geometry.get("legs", {}) if isinstance(geometry, Mapping) else {}
    if right_axis is None or not isinstance(legs, Mapping):
        return None
    left, right = legs.get("L", {}), legs.get("R", {})
    if not isinstance(left, Mapping) or not isinstance(right, Mapping):
        return None
    if left.get("ankle") is None or right.get("ankle") is None:
        return None
    return _dot(_vsub(right["ankle"], left["ankle"]), right_axis)


def _stable_sole_delta(current: Mapping[str, Any], previous: Mapping[str, Any]) -> Optional[Dict[str, Any]]:
    distances: List[Tuple[str, float, float]] = []
    for name in ("heel", "toe"):
        current_point, previous_point = current.get(name), previous.get(name)
        if current_point is None or previous_point is None:
            continue
        horizontal = _horizontal_distance(current_point, previous_point)
        three_d = _distance(current_point, previous_point)
        if horizontal is not None and three_d is not None:
            distances.append((name, horizontal, three_d))
    if not distances:
        return None
    stable = min(distances, key=lambda value: value[1])
    return {"endpoint": stable[0], "horizontal_m": stable[1], "three_d_m": stable[2], "available_endpoints": [d[0] for d in distances]}


def _contact_known(leg: Mapping[str, Any]) -> bool:
    # Frozen is the next-target state in FootPlacer, and can be true before a
    # landing is authored or observed.  Only a lock, explicit contact, or the
    # grounded flag emitted by the probe/fleet row is evidence of support.
    return bool(leg.get("locked") or leg.get("contact") or leg.get("authored_grounded"))


def _stage_values(geometry: Mapping[str, Any], travel: Optional[Sequence[float]], yaw: Optional[float]) -> Dict[str, Any]:
    legs = geometry.get("legs", {}) if isinstance(geometry, Mapping) else {}
    trailing = _trailing(geometry, travel)
    by_side: Dict[str, Dict[str, Any]] = {}
    for side in SIDES:
        leg = legs.get(side, {}) if isinstance(legs, Mapping) else {}
        chain = _chain(leg) if isinstance(leg, Mapping) else None
        by_side[side] = {
            "trailing_raw_ratio": trailing[side]["raw_ratio"],
            "trailing_ratio": trailing[side]["trailing_ratio"],
            "trailing_signed_m": trailing[side]["signed_m"],
            "trailing_m": trailing[side]["trailing_m"],
            "chain_length_m": chain["chain_m"] if chain else None,
            "reach_m": chain["reach_m"] if chain else None,
            "straightness": chain["straightness"] if chain else None,
            "reach_excess_m": (chain["reach_m"] - chain["chain_m"]) if chain else None,
        }
    interleg = _inter_leg(geometry)
    return {
        "by_side": by_side,
        "inter_leg": interleg,
        "ankle_width_m": _width(geometry, yaw),
        "root": _copy_point(geometry.get("root")),
        "pelvis": _copy_point(geometry.get("pelvis")),
    }


def _travel_for_row(row: Mapping[str, Any], previous: Optional[Mapping[str, Any]]) -> Optional[List[float]]:
    current_root = row.get("body_position") or ((row.get("after") or {}).get("root"))
    previous_root = (previous or {}).get("body_position") or ((previous or {}).get("after") or {}).get("root")
    if current_root is not None and previous_root is not None:
        unit = _safe_unit_horizontal(_vsub(current_root, previous_root))
        if unit is not None:
            return unit
    context = row.get("context", {}) or {}
    raw_velocity = context.get("velocity")
    unit = _safe_unit_horizontal(raw_velocity)
    if unit is not None:
        return unit
    return None


def _continuity(previous: Optional[Mapping[str, Any]], current: MutableMapping[str, Any]) -> Tuple[bool, Optional[float], List[str]]:
    reasons: List[str] = []
    if previous is None:
        return False, None, reasons
    if current.get("missing_before_pair_gap"):
        reasons.append("missing_after_visual")
    dt = current.get("time", 0.0) - previous.get("time", 0.0)
    if dt <= 0.0:
        reasons.append("non_positive_dt")
    elif dt > MAX_GEOMETRY_DT:
        reasons.append("time_gap")
    if current.get("coverage") in {"culled", "suspended", "missing_geometry", "unsupported", "partial", "unpaired"}:
        reasons.extend(current.get("coverage_reasons", []))
    if previous.get("coverage") in {"culled", "suspended", "missing_geometry", "unsupported", "partial", "unpaired"}:
        reasons.extend(previous.get("coverage_reasons", []))
    if current.get("paired") is False:
        reasons.append("unpaired_after_lock")
    if previous.get("paired") is False:
        reasons.append("unpaired_after_lock")
    current_root = current.get("body_position") or ((current.get("after") or {}).get("root"))
    previous_root = previous.get("body_position") or ((previous.get("after") or {}).get("root"))
    displacement = _distance(current_root, previous_root)
    if displacement is not None and dt > 0.0 and (displacement > TELEPORT_DISTANCE_M or displacement / dt > TELEPORT_SPEED_MPS):
        reasons.append("teleport")
    geometry_reasons = list(reasons)
    derivative_reasons = list(reasons)
    if dt > MAX_CONTINUITY_DT and dt <= MAX_GEOMETRY_DT:
        derivative_reasons.append("derivative_gap")
    current["geometry_continuity"] = not geometry_reasons
    current["derivative_continuity"] = not derivative_reasons
    if derivative_reasons:
        current["continuity_break"] = True
        current.setdefault("coverage_reasons", []).extend(reason for reason in derivative_reasons if reason not in current.get("coverage_reasons", []))
    return not derivative_reasons, dt if dt > 0.0 else None, list(dict.fromkeys(derivative_reasons))


def _context_with_turn(row: MutableMapping[str, Any], previous: Optional[Mapping[str, Any]], dt: Optional[float]) -> None:
    context = row.get("context", {})
    current_yaw = _number(context.get("body_yaw"))
    previous_yaw = _number(((previous or {}).get("context") or {}).get("body_yaw"))
    delta = _angle_delta_degrees(current_yaw, previous_yaw)
    turn_rate = None if delta is None or dt is None or dt <= 0.0 else delta / dt
    if context.get("turn") is None:
        context["turn"] = turn_rate
    context["turn_rate"] = turn_rate
    context["transition"] = str(context.get("phase") or "").lower() in {
        "start", "stop", "cut", "transition", "handoff", "hand_off"
    }


def _trigger(metric: str, value: Optional[float], extra: Optional[Mapping[str, Any]] = None) -> bool:
    if value is None or not math.isfinite(value):
        return False
    if metric == "trailing_ankle":
        return value > 0.35
    if metric == "inter_leg_separation":
        ratio = _number((extra or {}).get("ratio"))
        if ratio is None:
            ratio = _number((extra or {}).get("ratio_after"))
        return value < 0.08 and (ratio is None or ratio < 0.08)
    if metric == "straightness":
        return value > 0.985
    if metric == "reach":
        return False
    if metric == "pelvis_shift":
        return value > 0.04
    if metric == "knee_displacement":
        return value > 0.08
    if metric == "sole_contact_drift":
        return value > 0.25
    if metric == "pre_render_drift":
        return value > 0.01
    return False


METRIC_DEFINITIONS = {
    "trailing_ankle": {
        "units": "chain_ratio",
        "description": "Positive amount of each ankle behind its own hip along measured body travel, divided by that leg chain; raw signed ratio is retained per frame.",
        "kind": "heuristic",
    },
    "inter_leg_separation": {
        "units": "m",
        "description": "Minimum 3D distance among opposite thigh/shin segment pairs; this is a proximity proxy and does not prove mesh collision.",
        "kind": "heuristic",
    },
    "ankle_width": {
        "units": "m",
        "description": "Signed right-minus-left ankle width in the body frame. Crossover is valid and this descriptive value is not flagged.",
        "kind": "descriptive",
    },
    "straightness": {
        "units": "ratio",
        "description": "Direct hip-to-ankle distance divided by measured thigh-plus-shin chain; one is a straight reach.",
        "kind": "heuristic",
    },
    "reach": {
        "units": "m",
        "description": "Direct hip-to-ankle reach. The paired straightness ratio is normalized reach; triangle-inequality residuals are retained for input sanity only.",
        "kind": "descriptive",
    },
    "pelvis_shift": {
        "units": "m",
        "description": "3D pelvis displacement from the pre-placement geometry to the final geometry at the same logical frame.",
        "kind": "heuristic",
    },
    "knee_displacement": {
        "units": "m",
        "description": "3D knee displacement from the pre-placement geometry to the final geometry at the same logical frame.",
        "kind": "heuristic",
    },
    "sole_contact_drift": {
        "units": "m_per_s",
        "description": "Horizontal movement rate of the more stable heel/toe endpoint between final samples while contact is known; the slower endpoint avoids counting a heel-to-toe roll as a base hop.",
        "kind": "heuristic",
    },
    "pre_render_drift": {
        "units": "m",
        "description": "Displacement written after after_lock and before the first camera cull; this is a separate later-write observation, not placer correction.",
        "kind": "heuristic",
    },
}


def _value_sample(metric: str, side: str, before: Optional[float], after: Optional[float], delta: Optional[float], row: Mapping[str, Any], attribution: str, **extra: Any) -> Dict[str, Any]:
    value = {
        "metric": metric,
        "side": side,
        "id": row.get("id"),
        "time": row.get("time"),
        "frame": row.get("frame"),
        "before": before,
        "after": after,
        "delta": delta,
        "attribution": attribution,
        "context": dict(row.get("context", {}) or {}),
    }
    value.update(extra)
    return value


def _attribution(metric: str, before_value: Optional[float], after_value: Optional[float], before_extra: Optional[Mapping[str, Any]], after_extra: Optional[Mapping[str, Any]]) -> str:
    before_extra = dict(before_extra or {})
    after_extra = dict(after_extra or {})
    # A missing stage value is not a healthy baseline.  Displacement metrics
    # intentionally have no ``before`` scalar because their value is the
    # paired pre-to-post movement itself; pre-render drift is likewise a
    # separately attributed later write.
    if metric == "pre_render_drift":
        return "later_write" if after_value is not None else "none"
    if metric in {"knee_displacement", "pelvis_shift"}:
        return "post_placement" if after_value is not None and _trigger(metric, after_value, after_extra) else "none"
    if after_value is None:
        return "unknown_after" if before_value is not None else "none"
    if before_value is None:
        return "unknown_before"
    if "ratio_before" in before_extra:
        before_extra["ratio"] = before_extra["ratio_before"]
    if "ratio_after" in after_extra:
        after_extra["ratio"] = after_extra["ratio_after"]
    before_bad = _trigger(metric, before_value, before_extra)
    after_bad = _trigger(metric, after_value, after_extra)
    if after_bad and not before_bad:
        return "post_placement"
    if before_bad and after_bad:
        return "preexisting_and_post"
    if before_bad and not after_bad:
        return "improved_by_placement"
    return "none"


def _metric_frame_samples(rows: Sequence[Mapping[str, Any]]) -> Tuple[List[Dict[str, Any]], Dict[str, Any], Dict[str, Any], Dict[str, Any]]:
    """Compute frame values, distributions, and trigger records."""

    frame_metrics: List[Dict[str, Any]] = []
    samples: Dict[str, Dict[str, Dict[str, List[float]]]] = defaultdict(lambda: defaultdict(lambda: {"before": [], "after": [], "delta": [], "post_placement": []}))
    raw_samples: Dict[str, Dict[str, List[float]]] = defaultdict(lambda: defaultdict(list))
    trigger_records: Dict[Tuple[str, str, str], List[Dict[str, Any]]] = defaultdict(list)
    physical_records: Dict[Tuple[str, str, str], List[Dict[str, Any]]] = defaultdict(list)

    previous_by_id: Dict[str, Mapping[str, Any]] = {}
    for row in rows:
        identifier = str(row.get("id"))
        previous = previous_by_id.get(identifier)
        continuous, dt, break_reasons = _continuity(previous, row) if previous is not None else (False, None, [])
        _context_with_turn(row, previous, dt)
        travel = None if "teleport" in break_reasons else _travel_for_row(row, previous)
        context = row.get("context", {}) or {}
        current_motion_root = row.get("body_position") or ((row.get("after") or {}).get("root"))
        previous_motion_root = (previous or {}).get("body_position") or ((previous or {}).get("after") or {}).get("root")
        measured_speed = _horizontal_speed(current_motion_root, previous_motion_root, dt or 0.0) if previous is not None and "teleport" not in break_reasons else None
        context["measured_speed"] = measured_speed
        if measured_speed is not None:
            context["speed"] = measured_speed
        before_geometry, after_geometry = row.get("before", {}), row.get("after", {})
        before_values = _stage_values(before_geometry, travel, _number((row.get("context") or {}).get("body_yaw")))
        after_values = _stage_values(after_geometry, travel, _number((row.get("context") or {}).get("body_yaw")))
        before_by_side = before_values["by_side"]
        after_by_side = after_values["by_side"]
        post: Dict[str, Any] = {"by_side": {}}
        for side in SIDES:
            before_leg = (before_geometry.get("legs", {}) or {}).get(side, {})
            after_leg = (after_geometry.get("legs", {}) or {}).get(side, {})
            knee_delta = _distance(after_leg.get("knee"), before_leg.get("knee"))
            ankle_delta = _distance(after_leg.get("ankle"), before_leg.get("ankle"))
            sole_after = None
            sole_before = None
            previous_row = previous
            if previous_row is not None and continuous:
                previous_after_leg = ((previous_row.get("after") or {}).get("legs", {}) or {}).get(side, {})
                previous_before_leg = ((previous_row.get("before") or {}).get("legs", {}) or {}).get(side, {})
                sole_after = _stable_sole_delta(after_leg, previous_after_leg)
                sole_before = _stable_sole_delta(before_leg, previous_before_leg)
            sole_rate = None
            sole_before_rate = None
            if sole_after is not None and dt and dt > 0.0:
                sole_rate = sole_after["horizontal_m"] / dt
            if sole_before is not None and dt and dt > 0.0:
                sole_before_rate = sole_before["horizontal_m"] / dt
            post["by_side"][side] = {
                "knee_displacement_m": knee_delta,
                "ankle_displacement_m": ankle_delta,
                "sole_contact_drift_m": None if sole_after is None else sole_after["horizontal_m"],
                "sole_contact_drift_m_per_s": sole_rate,
                "sole_raw_velocity_m_per_s": sole_rate,
                "sole_before_drift_m_per_s": sole_before_rate,
                "sole_added_drift_m_per_s": None if sole_rate is None or sole_before_rate is None else sole_rate - sole_before_rate,
                "sole_endpoint": None if sole_after is None else sole_after["endpoint"],
                "sole_endpoints": None if sole_after is None else sole_after["available_endpoints"],
                "contact_known": _contact_known(after_leg),
            }
        pelvis_shift = _distance(after_geometry.get("pelvis"), before_geometry.get("pelvis"))
        post["pelvis_shift_m"] = pelvis_shift
        render = row.get("render") or {}
        pre_render: Dict[str, Any] = {"by_side": {}, "pelvis_drift_m": None}
        if render:
            render_legs = (render.get("legs", {}) or {})
            after_legs = (after_geometry.get("legs", {}) or {})
            for side in SIDES:
                render_leg = render_legs.get(side, {}) or {}
                after_leg = after_legs.get(side, {}) or {}
                pre_render["by_side"][side] = {
                    "ankle_drift_m": _distance(render_leg.get("ankle"), after_leg.get("ankle")),
                    "knee_drift_m": _distance(render_leg.get("knee"), after_leg.get("knee")),
                    "heel_drift_m": _distance(render_leg.get("heel"), after_leg.get("heel")),
                    "toe_drift_m": _distance(render_leg.get("toe"), after_leg.get("toe")),
                }
            pre_render["pelvis_drift_m"] = _distance(render.get("pelvis"), after_geometry.get("pelvis"))
        post["pre_render_drift"] = pre_render
        row_metric = {
            "source": row.get("source"), "id": row.get("id"), "time": row.get("time"), "frame": row.get("frame"),
            "stage": row.get("stage"), "coverage": row.get("coverage"), "coverage_reasons": list(row.get("coverage_reasons", [])),
            "paired": row.get("paired", True), "pair_status": row.get("pair_status", "paired"),
            "continuity": continuous, "geometry_continuity": bool(row.get("geometry_continuity", continuous)),
            "derivative_continuity": bool(row.get("derivative_continuity", continuous)),
            "continuity_break_reasons": break_reasons,
            "context": dict(row.get("context", {}) or {}),
            "before": before_values, "after": after_values, "post_placement": post,
            "pre_render_drift": pre_render,
        }
        frame_metrics.append(row_metric)

        row_usable = row.get("coverage") not in {"culled", "suspended", "missing_geometry", "unsupported", "partial", "unpaired"}

        def add_sample(metric: str, side: str, before_value: Optional[float], after_value: Optional[float], extra: Optional[Mapping[str, Any]] = None, post_value: Optional[float] = None, trigger_value: Optional[float] = None) -> None:
            if not row_usable:
                before_value = after_value = post_value = trigger_value = None
            if metric == "sole_contact_drift" and not bool((extra or {}).get("support_confirmed")):
                # Reported contact drift is an interval measurement with
                # confirmed support at both endpoints. Raw sole velocity is
                # retained separately below for swing/coverage inspection.
                before_value = after_value = post_value = trigger_value = None
            delta = None if before_value is None or after_value is None else after_value - before_value
            effective_after = after_value if trigger_value is None else trigger_value
            attribution = _attribution(metric, before_value, effective_after, extra, extra)
            sample = _value_sample(metric, side, before_value, after_value, delta, row, attribution, **(dict(extra or {})))
            samples[metric][side]["before"].extend([] if before_value is None else [before_value])
            samples[metric][side]["after"].extend([] if after_value is None else [after_value])
            samples[metric][side]["delta"].extend([] if delta is None else [delta])
            samples[metric][side]["post_placement"].extend([] if post_value is None else [post_value])
            if metric == "trailing_ankle":
                raw_samples[metric][side].extend([] if not row_usable or extra is None or extra.get("raw_after") is None else [extra["raw_after"]])
                if row_usable and extra is not None:
                    if extra.get("raw_before") is not None:
                        samples[metric][side].setdefault("raw_before", []).append(extra["raw_before"])
                    if extra.get("raw_after") is not None:
                        samples[metric][side].setdefault("raw_after", []).append(extra["raw_after"])
            trigger = _trigger(metric, after_value if trigger_value is None else trigger_value, extra)
            if metric == "sole_contact_drift" and not bool((extra or {}).get("support_confirmed")):
                # A sole can move while swinging, but that is not contact
                # drift. Keep the row as a false marker so it breaks an
                # episode rather than silently joining two flagged samples.
                trigger = False
            # Keep every row, including false/missing rows, in the continuity
            # stream. Episodes therefore break on an ordinary below-threshold
            # sample, cull, suspension, or missing geometry instead of being
            # reconstructed from sparse trigger samples.
            metric_continuity = bool(row.get("derivative_continuity", continuous)) if metric == "sole_contact_drift" else bool(row.get("geometry_continuity", continuous))
            trigger_records[(metric, side, identifier)].append(dict(sample, trigger=trigger, dt=dt,
                                                                     continuous=metric_continuity,
                                                                     max_dt=MAX_CONTINUITY_DT if metric == "sole_contact_drift" else MAX_GEOMETRY_DT))
            if metric == "reach" and trigger:
                physical_records[(metric, side, identifier)].append(dict(sample, trigger=True, dt=dt, continuous=continuous))

        for side in SIDES:
            before_side, after_side = before_by_side[side], after_by_side[side]
            add_sample("trailing_ankle", side, before_side["trailing_ratio"], after_side["trailing_ratio"],
                       {"raw_before": before_side["trailing_raw_ratio"], "raw_after": after_side["trailing_raw_ratio"]})
            add_sample("straightness", side, before_side["straightness"], after_side["straightness"],
                       {"chain_before_m": before_side["chain_length_m"], "chain_after_m": after_side["chain_length_m"]})
            add_sample("reach", side, before_side["reach_m"], after_side["reach_m"],
                       {"reach_before_m": before_side["reach_m"], "reach_after_m": after_side["reach_m"],
                        "chain_before_m": before_side["chain_length_m"], "chain_after_m": after_side["chain_length_m"]},
                       post_value=None)
            add_sample("knee_displacement", side, None, None, {}, post_value=post["by_side"][side]["knee_displacement_m"],
                       trigger_value=post["by_side"][side]["knee_displacement_m"])
            sole_after = post["by_side"][side]["sole_contact_drift_m_per_s"]
            sole_before = post["by_side"][side]["sole_before_drift_m_per_s"]
            sole_delta = post["by_side"][side]["sole_added_drift_m_per_s"]
            previous_after_leg = ((previous or {}).get("after", {}).get("legs", {}) or {}).get(side, {}) if previous is not None else {}
            support_confirmed = bool(
                row_usable and continuous and sole_after is not None and
                _contact_known(after_geometry.get("legs", {}).get(side, {})) and
                _contact_known(previous_after_leg)
            )
            add_sample("sole_contact_drift", side, sole_before, sole_after,
                       {"contact_known": post["by_side"][side]["contact_known"],
                        "support_confirmed": support_confirmed,
                        "endpoint": post["by_side"][side]["sole_endpoint"]},
                       post_value=sole_delta)
            if row_usable and sole_after is not None:
                raw_samples.setdefault("sole_contact_drift", defaultdict(list))[side].append(sole_after)

        inter_before, inter_after = before_values["inter_leg"], after_values["inter_leg"]
        add_sample("inter_leg_separation", "both",
                   None if inter_before is None else inter_before["distance_m"],
                   None if inter_after is None else inter_after["distance_m"],
                   {"ratio_before": None if inter_before is None else inter_before["ratio"],
                    "ratio_after": None if inter_after is None else inter_after["ratio"],
                    "pair_before": None if inter_before is None else inter_before["pair"],
                    "pair_after": None if inter_after is None else inter_after["pair"]})
        add_sample("ankle_width", "both", before_values["ankle_width_m"], after_values["ankle_width_m"], {})
        add_sample("pelvis_shift", "both", None, None, {}, post_value=pelvis_shift, trigger_value=pelvis_shift)
        for side in SIDES:
            add_sample("pre_render_drift", side, None, None, {},
                       post_value=(pre_render["by_side"].get(side, {}) or {}).get("ankle_drift_m"),
                       trigger_value=(pre_render["by_side"].get(side, {}) or {}).get("ankle_drift_m"))
        add_sample("pre_render_drift", "both", None, None, {},
                   post_value=pre_render.get("pelvis_drift_m"), trigger_value=pre_render.get("pelvis_drift_m"))

        previous_by_id[identifier] = row

    return frame_metrics, samples, raw_samples, {"heuristic": trigger_records, "physical": physical_records}


def _metric_report(metric: str, side_samples: Mapping[str, Mapping[str, List[float]]], raw: Mapping[str, List[float]]) -> Dict[str, Any]:
    definition = dict(METRIC_DEFINITIONS[metric])
    result: Dict[str, Any] = {
        **definition,
        "before": _stats(v for side in side_samples.values() for v in side["before"]),
        "after": _stats(v for side in side_samples.values() for v in side["after"]),
        "delta": _stats(v for side in side_samples.values() for v in side["delta"]),
        "post_placement": _stats(v for side in side_samples.values() for v in side["post_placement"]),
        "by_side": {},
    }
    for side, values in side_samples.items():
        result["by_side"][side] = {
            "before": _stats(values["before"]),
            "after": _stats(values["after"]),
            "delta": _stats(values["delta"]),
            "post_placement": _stats(values["post_placement"]),
        }
        # Convenient direct access for the replay viewer and small scripts.
        result[side] = result["by_side"][side]
    if raw:
        if metric == "sole_contact_drift":
            result["raw_interval_velocity"] = {side: _stats(values) for side, values in raw.items()}
        else:
            result["raw_signed_ratio"] = {side: _stats(values) for side, values in raw.items()}
    if metric == "trailing_ankle":
        result["added_vs_before"] = result["delta"]
        result["raw_signed_ratio"] = {
            "before": _stats(v for values in side_samples.values() for v in values.get("raw_before", [])),
            "after": _stats(v for values in side_samples.values() for v in values.get("raw_after", [])),
            "by_side": {
                side: {"before": _stats(values.get("raw_before", [])), "after": _stats(values.get("raw_after", []))}
                for side, values in side_samples.items()
            },
        }
        for side, values in side_samples.items():
            result["by_side"][side]["added_vs_before"] = result["by_side"][side]["delta"]
    return result


def _episode_list(records: Mapping[Tuple[str, str], Sequence[Mapping[str, Any]]], category: str) -> Tuple[List[Dict[str, Any]], Counter, Counter, float]:
    episodes: List[Dict[str, Any]] = []
    flag_counts: Counter = Counter()
    attribution_counts: Counter = Counter()
    exposure_seconds = 0.0
    for key, values in records.items():
        if len(key) == 3:
            metric, side, identifier = key
        else:
            metric, side = key  # type: ignore[misc]
            identifier = None
        for record in values:
            if record.get("trigger"):
                flag_counts[metric + ":" + side] += 1
                attribution_counts[metric + ":" + str(record.get("attribution", "none"))] += 1
        active: Optional[Dict[str, Any]] = None
        last: Optional[Mapping[str, Any]] = None
        for record in values:
            dt = _number(record.get("dt"), 0.0) or 0.0
            if record.get("continuous") and dt > 0.0:
                exposure_seconds += dt
            max_dt = _number(record.get("max_dt"), MAX_CONTINUITY_DT) or MAX_CONTINUITY_DT
            connected = bool(record.get("continuous")) and (last is None or
                       ((_number(record.get("time"), 0.0) or 0.0) - (_number(last.get("time"), 0.0) or 0.0)) <= max_dt)
            if active is not None and (not connected or not record.get("trigger")):
                duration = active["end_time"] - active["start_time"]
                if duration >= MIN_EPISODE_SECONDS:
                    episodes.append(active)
                active = None
            if record.get("trigger"):
                if active is None:
                    active = {
                        "category": category,
                        "metric": metric,
                        "side": side,
                        "id": record.get("id", identifier),
                        "start_time": record.get("time"),
                        "end_time": record.get("time"),
                        "start_frame": record.get("frame"),
                        "end_frame": record.get("frame"),
                        "frames": 1,
                        "attributions": Counter([record.get("attribution", "none")]),
                        "context_start": dict(record.get("context", {}) or {}),
                        "contexts": [],
                    }
                else:
                    active["end_time"] = record.get("time")
                    active["end_frame"] = record.get("frame")
                    active["frames"] += 1
                    active["attributions"][record.get("attribution", "none")] += 1
                context = dict(record.get("context", {}) or {})
                if context:
                    active["contexts"].append({k: context.get(k) for k in ("clip", "phase", "speed", "driver", "turn", "turn_rate", "transition")})
            last = record
        if active is not None:
            duration = active["end_time"] - active["start_time"]
            if duration >= MIN_EPISODE_SECONDS:
                episodes.append(active)
    for episode in episodes:
        if isinstance(episode.get("attributions"), Counter):
            episode["attributions"] = dict(episode["attributions"])
        episode["duration_s"] = episode["end_time"] - episode["start_time"]
        episode["rate_per_minute"] = None
    return episodes, flag_counts, attribution_counts, exposure_seconds


def _physical_invalidity(frame_metrics: Sequence[Mapping[str, Any]]) -> Tuple[Counter, List[Dict[str, Any]]]:
    counts: Counter = Counter()
    episodes: List[Dict[str, Any]] = []
    current: Dict[Tuple[str, str, str], Dict[str, Any]] = {}
    for row in frame_metrics:
        if row.get("coverage") != "valid":
            for side in SIDES:
                current.pop(("degenerate_chain", side, str(row.get("id"))), None)
            continue
        for side in SIDES:
            values = ((row.get("after") or {}).get("by_side", {}) or {}).get(side, {})
            chain = values.get("chain_length_m")
            if chain is not None and chain <= 1e-8:
                invalid = True
                kind = "degenerate_chain"
            else:
                # A positive direct-minus-chain residual cannot be produced by
                # finite Euclidean points (triangle inequality). Keep reach
                # as a descriptive metric and reserve physical invalidity for
                # an actually degenerate chain.
                invalid = False
                kind = "degenerate_chain"
            key = (kind, side, str(row.get("id")))
            if invalid:
                counts["%s:%s" % (kind, side)] += 1
                if not row.get("geometry_continuity", row.get("continuity", False)):
                    current.pop(key, None)
                    continue
                active = current.get(key)
                if active is None:
                    active = {"category": "physical_invalidity", "metric": kind, "side": side,
                              "start_time": row.get("time"), "end_time": row.get("time"),
                              "start_frame": row.get("frame"), "end_frame": row.get("frame"), "frames": 1,
                              "id": row.get("id"),
                              "context": dict(row.get("context", {}) or {})}
                    current[key] = active
                else:
                    active["end_time"] = row.get("time"); active["end_frame"] = row.get("frame"); active["frames"] += 1
            else:
                active = current.pop(key, None)
                if active is not None and active["end_time"] - active["start_time"] >= MIN_EPISODE_SECONDS:
                    active["duration_s"] = active["end_time"] - active["start_time"]
                    episodes.append(active)
    for active in current.values():
        if active["end_time"] - active["start_time"] >= MIN_EPISODE_SECONDS:
            active["duration_s"] = active["end_time"] - active["start_time"]
            episodes.append(active)
    return counts, episodes


def _context_bucket(context: Mapping[str, Any]) -> str:
    speed = _number(context.get("speed"), 0.0) or 0.0
    if speed < 0.5:
        speed_band = "0-0.5"
    elif speed < 1.5:
        speed_band = "0.5-1.5"
    elif speed < 3.0:
        speed_band = "1.5-3"
    else:
        speed_band = "3+"
    turn = abs(_number(context.get("turn_rate"), 0.0) or 0.0)
    turn_band = "turning" if turn >= 45.0 else "straight"
    return "%s|%s|%s" % (speed_band, "sprint" if context.get("sprint") else "steady", turn_band)


def _baseline_comparison(baseline: Any, frame_metrics: Sequence[Mapping[str, Any]]) -> Dict[str, Any]:
    """Compare distributions by context without changing diagnostic thresholds."""

    result: Dict[str, Any] = {
        "calibrated": False,
        "minimum_samples": BASELINE_MIN_SAMPLES,
        "minimum_seconds": BASELINE_MIN_SECONDS,
        "matched_buckets": {},
        "unmatched_buckets": [],
        "note": "Empirical baseline distributions only; thresholds and flags are unchanged.",
    }
    if baseline is None:
        result["status"] = "not_requested"
        return result
    try:
        if isinstance(baseline, (str, Path)):
            baseline_path = _resolve_input(Path(baseline))
            if baseline_path.suffix.lower() == ".jsonl":
                baseline_frames, _ = load_frames(baseline_path)
                baseline_report = _analyse_frames(baseline_frames, None, include_baseline=False)
            else:
                baseline_document = json.loads(baseline_path.read_text(encoding="utf-8"))
                if isinstance(baseline_document, Mapping) and "frame_metrics" in baseline_document:
                    baseline_report = baseline_document
                else:
                    baseline_frames, _ = load_frames(baseline_path)
                    baseline_report = _analyse_frames(baseline_frames, None, include_baseline=False)
        elif isinstance(baseline, Mapping):
            baseline_report = baseline
        else:
            result["status"] = "unavailable"
            return result
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as error:
        result["status"] = "unavailable"
        result["error"] = str(error)
        return result
    def collect(row: Mapping[str, Any], target: MutableMapping[str, List[float]]) -> None:
        after = row.get("after", {}) or {}
        for side in SIDES:
            side_values = (after.get("by_side", {}) or {}).get(side, {})
            for metric, field in (("trailing_ankle", "trailing_ratio"), ("straightness", "straightness"), ("reach", "reach_m")):
                value = side_values.get(field)
                if value is not None:
                    target[metric + ":" + side].append(value)
        inter_leg = after.get("inter_leg") or {}
        if inter_leg.get("distance_m") is not None:
            target["inter_leg_separation:both"].append(inter_leg["distance_m"])
        if after.get("ankle_width_m") is not None:
            target["ankle_width:both"].append(after["ankle_width_m"])

    baseline_buckets: Dict[str, Dict[str, List[float]]] = defaultdict(lambda: defaultdict(list))
    baseline_intervals: Dict[str, float] = Counter()
    baseline_counts: Counter = Counter()
    baseline_previous: Dict[str, Mapping[str, Any]] = {}
    baseline_rows = baseline_report.get("frame_metrics", []) if isinstance(baseline_report, Mapping) else []
    for row in baseline_rows:
        if row.get("coverage") != "valid":
            continue
        bucket = _context_bucket(row.get("context", {}) or {})
        collect(row, baseline_buckets[bucket])
        baseline_counts[bucket] += 1
        identifier = str(row.get("id"))
        previous = baseline_previous.get(identifier)
        if previous is not None and row.get("geometry_continuity", row.get("continuity", False)):
            dt = (_number(row.get("time"), 0.0) or 0.0) - (_number(previous.get("time"), 0.0) or 0.0)
            if 0.0 < dt <= MAX_GEOMETRY_DT and _context_bucket(previous.get("context", {}) or {}) == bucket:
                baseline_intervals[bucket] += dt
        baseline_previous[identifier] = row

    current_buckets: Dict[str, Dict[str, List[float]]] = defaultdict(lambda: defaultdict(list))
    current_counts: Counter = Counter()
    current_previous: Dict[str, Mapping[str, Any]] = {}
    current_intervals: Counter = Counter()
    for row in frame_metrics:
        if row.get("coverage") != "valid":
            continue
        bucket = _context_bucket(row.get("context", {}) or {})
        collect(row, current_buckets[bucket])
        current_counts[bucket] += 1
        identifier = str(row.get("id"))
        previous = current_previous.get(identifier)
        if previous is not None and row.get("geometry_continuity", row.get("continuity", False)):
            dt = (_number(row.get("time"), 0.0) or 0.0) - (_number(previous.get("time"), 0.0) or 0.0)
            if 0.0 < dt <= MAX_GEOMETRY_DT and _context_bucket(previous.get("context", {}) or {}) == bucket:
                current_intervals[bucket] += dt
        current_previous[identifier] = row

    for bucket, current_values in current_buckets.items():
        reference = baseline_buckets.get(bucket, {})
        enough_reference = (baseline_counts[bucket] >= BASELINE_MIN_SAMPLES and baseline_intervals[bucket] >= BASELINE_MIN_SECONDS)
        enough_current = (current_counts[bucket] >= 1 and current_intervals[bucket] >= 0.0)
        if not enough_reference or not enough_current:
            result["unmatched_buckets"].append(bucket)
            continue
        bucket_result = result["matched_buckets"].setdefault(bucket, {})
        for key, current in current_values.items():
            reference_values = reference.get(key, [])
            if not reference_values or not current:
                continue
            bucket_result[key] = {"current": _stats(current), "baseline": _stats(reference_values),
                                  "delta_p50": statistics.median(current) - statistics.median(reference_values)}
    result["status"] = "matched" if result["matched_buckets"] else "insufficient_coverage"
    result["unmatched_buckets"] = sorted(result["unmatched_buckets"])
    return result


def _analyse_frames(frames: Sequence[MutableMapping[str, Any]], meta: Optional[Mapping[str, Any]], baseline: Any = None, include_baseline: bool = True) -> Dict[str, Any]:
    # Fleet rows are sorted by bot so an adjacent bot cannot create a fake
    # travel vector or episode. Puppet rows already have one identifier.
    rows = sorted(frames, key=lambda row: (str(row.get("id")), row.get("time", 0.0), row.get("frame", 0)))
    frame_metrics, sample_values, raw_values, triggers = _metric_frame_samples(rows)
    metric_reports = {
        metric: _metric_report(metric, values, raw_values.get(metric, {}))
        for metric, values in sample_values.items()
    }
    for metric in METRIC_DEFINITIONS:
        metric_reports.setdefault(metric, _metric_report(metric, sample_values.get(metric, {}), raw_values.get(metric, {})))
    heuristic_episodes, heuristic_counts, attribution_counts, exposure = _episode_list(triggers["heuristic"], "heuristic")
    later_write_episodes = [episode for episode in heuristic_episodes if episode.get("metric") == "pre_render_drift"]
    physical_counts, physical_episodes = _physical_invalidity(frame_metrics)
    valid_seconds = 0.0
    derivative_valid_seconds = 0.0
    coverage_counts: Counter = Counter()
    for row in rows:
        coverage_counts[row.get("coverage", "unknown")] += 1
    previous_by_id: Dict[str, Mapping[str, Any]] = {}
    for row in rows:
        identifier = str(row.get("id"))
        previous = previous_by_id.get(identifier)
        if previous is not None and row.get("coverage") == "valid":
            dt = row.get("time", 0.0) - previous.get("time", 0.0)
            if 0.0 < dt <= MAX_GEOMETRY_DT and row.get("geometry_continuity", False):
                valid_seconds += dt
            if 0.0 < dt <= MAX_CONTINUITY_DT and row.get("derivative_continuity", False):
                derivative_valid_seconds += dt
        previous_by_id[identifier] = row
    heuristic_rate = {key: round(value / max(valid_seconds, 1e-9) * 60.0, 6) for key, value in heuristic_counts.items()}
    sustained_rate = Counter()
    for episode in heuristic_episodes:
        sustained_rate[episode["metric"] + ":" + episode["side"]] += 1
    report: Dict[str, Any] = {
        "schema": "manimal.motionmatching.placement-diagnostics.v1",
        "source": (meta or {}).get("source") if meta else (rows[0].get("source") if rows else None),
        "input": (meta or {}).get("path") if meta else None,
        "frames": len(rows),
        "metric_frames": len(frame_metrics),
        "coverage": {"frame_counts": dict(coverage_counts), "valid_seconds": valid_seconds,
                      "geometry_valid_seconds": valid_seconds, "derivative_valid_seconds": derivative_valid_seconds,
                      "unsupported_geometry": sum(row.get("coverage") == "unsupported" for row in rows),
                      "missing_geometry": sum(row.get("coverage") in {"missing_geometry", "unsupported"} for row in rows)},
        "warnings": list((meta or {}).get("warnings", [])) if meta else [],
        "metrics": metric_reports,
        "frame_metrics": frame_metrics,
        "heuristic_flags": {
            "counts": dict(heuristic_counts), "rates_per_minute": heuristic_rate,
            "attribution": dict(attribution_counts), "episodes": heuristic_episodes,
            "sustained_episodes_per_minute": {key: round(value / max(valid_seconds, 1e-9) * 60.0, 6) for key, value in sustained_rate.items()},
        },
        "later_write_drift": {
            "metric": metric_reports.get("pre_render_drift"),
            "episodes": later_write_episodes,
            "note": "Only pre_render samples are used here; after_visual/after_lock metrics remain the placement pair.",
        },
        "physical_invalidity": {
            "counts": dict(physical_counts), "episodes": physical_episodes,
            "note": "Only geometric impossibilities and degenerate chains are classified here; inter-leg proximity remains a heuristic proxy.",
        },
        "episode_policy": {
            "minimum_seconds": MIN_EPISODE_SECONDS,
            "max_continuity_dt": MAX_CONTINUITY_DT,
            "max_geometry_dt": MAX_GEOMETRY_DT,
            "teleport_distance_m": TELEPORT_DISTANCE_M,
            "teleport_speed_mps": TELEPORT_SPEED_MPS,
            "clip_transitions": "retained and labelled in context",
        },
    }
    if meta:
        report["input_meta"] = dict(meta)
    if include_baseline:
        report["baseline"] = _baseline_comparison(baseline, frame_metrics)
    return report


def analyse(value: Any, baseline: Any = None) -> Dict[str, Any]:
    """Analyze a path, raw document, or normalized rows."""

    if isinstance(value, (str, Path)):
        frames, meta = load_frames(value)
        return _analyse_frames(frames, meta, baseline)
    if isinstance(value, Mapping) and isinstance(_get(value, "Samples", default=None), list):
        frames, meta = load_frames_from_document(value)
        return _analyse_frames(frames, meta, baseline)
    if isinstance(value, Sequence):
        return _analyse_frames(list(value), None, baseline)
    raise TypeError("analyse expects an input path, capture document, or normalized frame sequence")


analyze = analyse
report = analyse


def _json_default(value: Any) -> Any:
    if isinstance(value, Counter):
        return dict(value)
    raise TypeError("not JSON serializable: %r" % (type(value),))


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Offline foot/leg/body placement diagnostics")
    parser.add_argument("input", type=Path, help="puppet capture JSON or fleet JSONL")
    parser.add_argument("--output", type=Path, help="write JSON report to this path")
    parser.add_argument("--baseline", type=Path, help="optional empirical baseline input/report")
    args = parser.parse_args(argv)
    try:
        result = analyse(args.input, args.baseline)
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as error:
        parser.error(str(error))
        return 2
    payload = json.dumps(result, indent=2, ensure_ascii=False, default=_json_default)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(payload + "\n", encoding="utf-8")
    else:
        print(payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
