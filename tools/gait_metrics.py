"""Gait metrics shared by the movement analysis: one definition, three sources (Tarkov captures, our captures,
Alyx source clips), so the report compares like with like.

A "track" is a dict of per-frame arrays in the CHARACTER frame (yaw removed, origin under the character on the
ground, x right, y up, z forward, metres), plus the character's world root path:
    fps, root: [(x, z, yaw_rad)], pelvis: [(x,y,z)], hipL/hipR, kneeL/kneeR, ankleL/ankleR, toeL/toeR: [(x,y,z)],
    optional pelvisFwd/torsoFwd: [(x,y,z)] forward vectors, footFwdL/footFwdR: [(x,y,z)].
Feet are considered in world space for contacts so slide and stride are real.
"""
import math
import statistics

CONTACT_HEIGHT = 0.10
CONTACT_SPEED = 1.0
PLANT_SPEED = 0.3
# time-based so 30 fps clips and 100 fps captures are judged alike: sole speed over a short window, plants at
# least this long, and gaps shorter than this merged (per-frame placer nudges otherwise split a plant into many)
SPEED_WINDOW_S = 0.05
MIN_PLANT_S = 0.08
MERGE_GAP_S = 0.08


def _hyp(a, b):
    return math.hypot(a[0] - b[0], a[2] - b[2])


def _to_world(root, p):
    s, c = math.sin(root[2]), math.cos(root[2])
    return (root[0] + p[0] * c + p[2] * s, p[1], root[1] - p[0] * s + p[2] * c)


def _angle(a, b):
    d = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a)) or 1e-9
    nb = math.sqrt(sum(x * x for x in b)) or 1e-9
    return math.degrees(math.acos(max(-1.0, min(1.0, d / (na * nb)))))


def _stats(values, nd=3):
    if not values:
        return None
    v = sorted(values)
    return {"p50": round(statistics.median(v), nd), "p10": round(v[int(len(v) * 0.1)], nd), "p90": round(v[int(len(v) * 0.9)], nd), "min": round(v[0], nd), "max": round(v[-1], nd)}


def contacts(track, side):
    """Per-frame planted flag for one foot from the lower sole point's world height and speed."""
    fps = track["fps"]
    ankle, toe, root = track["ankle" + side], track["toe" + side], track["root"]
    n = len(ankle)
    low = [_to_world(root[f], ankle[f] if ankle[f][1] < toe[f][1] else toe[f]) for f in range(n)]
    floor = min(p[1] for p in low)
    w = max(1, int(round(SPEED_WINDOW_S * fps)))
    speed = [_hyp(low[f], low[max(0, f - w)]) * fps / max(1, f - max(0, f - w)) for f in range(n)]
    if n > 1:
        speed[0] = speed[1]
    flags = [low[f][1] <= floor + CONTACT_HEIGHT and speed[f] < CONTACT_SPEED for f in range(n)]
    min_plant = max(2, int(round(MIN_PLANT_S * fps)))
    merge_gap = max(1, int(round(MERGE_GAP_S * fps)))
    runs, start = [], None
    for f, flag in enumerate(flags + [False]):
        if flag and start is None:
            start = f
        elif not flag and start is not None:
            runs.append((start, f - 1))
            start = None
    merged = []
    for run in runs:
        if merged and run[0] - merged[-1][1] - 1 < merge_gap:
            merged[-1] = (merged[-1][0], run[1])
        else:
            merged.append(run)
    runs = [r for r in merged if r[1] - r[0] + 1 >= min_plant]
    # slide is judged over the still core of a plant (landing and push-off frames legitimately move)
    cores = []
    for a, b in runs:
        still = [f for f in range(a, b + 1) if speed[f] < PLANT_SPEED]
        cores.append((still[0], still[-1]) if still else (a, b))
    return flags, runs, low, cores


def analyse(track, name=""):
    fps = track["fps"]
    root = track["root"]
    n = len(root)
    seconds = n / fps
    travelled = sum(_hyp((root[f][0], 0, root[f][1]), (root[f - 1][0], 0, root[f - 1][1])) for f in range(1, n))
    out = {"name": name, "frames": n, "seconds": round(seconds, 2), "speed_mps": round(travelled / max(seconds, 1e-6), 2)}
    per_side = {}
    all_strides, all_widths, all_slides, all_travel, all_excursion, contact_frames, swing_heights, foot_pitch_contact = [], [], [], [], [], 0, [], []
    for side in ("L", "R"):
        flags, runs, low, cores = contacts(track, side)
        contact_frames += sum(flags)
        plants = [(a + b) // 2 for a, b in runs]
        for a, b in cores:
            all_slides.append(_hyp(low[b], low[a]))
            # endpoint drift hides jitter inside the plant; travel is the summed per-frame path over the same core
            all_travel.append(sum(_hyp(low[f], low[f - 1]) for f in range(a + 1, b + 1)))
            # farthest the sole got from where the plant started: travel >> excursion means oscillation, not drift
            all_excursion.append(max(_hyp(low[f], low[a]) for f in range(a, b + 1)))
        for i in range(1, len(plants)):
            all_strides.append(_hyp(low[plants[i]], low[plants[i - 1]]))
        # width: lateral distance between ankles when this foot is planted
        for a, b in runs:
            mid = (a + b) // 2
            all_widths.append(abs(track["ankleL"][mid][0] - track["ankleR"][mid][0]))
        # swing height: peak sole height between plants
        for i in range(1, len(runs)):
            seg = low[runs[i - 1][1]:runs[i][0] + 1]
            floor = min(p[1] for p in low)
            swing_heights.append(max(p[1] for p in seg) - floor)
        per_side[side] = {"plants": len(runs), "steps_per_s": round(len(runs) / max(seconds, 1e-6), 2)}
    steps = per_side["L"]["plants"] + per_side["R"]["plants"]
    out.update({
        "cadence_steps_per_s": round(steps / max(seconds, 1e-6), 2),
        "stride_m": _stats(all_strides),
        "stance_width_m": _stats(all_widths),
        "contact_slide_mm": _stats([s * 1000 for s in all_slides], 0),
        "contact_travel_mm": _stats([s * 1000 for s in all_travel], 0),
        "contact_excursion_mm": _stats([s * 1000 for s in all_excursion], 0),
        "plants": len(all_slides),
        "swing_height_m": _stats(swing_heights),
        "double_support_fraction": round(sum(1 for f in range(n) if contacts(track, "L")[0][f] and contacts(track, "R")[0][f]) / n, 2) if n else None,
        "contact_fraction": round(contact_frames / (2 * n), 2) if n else None,
    })
    # knees: flexion (180 = straight) and lateral splay (deg off the hip-ankle line, outward positive)
    flex, splay = [], []
    for side, sign in (("L", -1), ("R", 1)):
        for f in range(n):
            hip, knee, ank = track["hip" + side][f], track["knee" + side][f], track["ankle" + side][f]
            flex.append(_angle([h - k for h, k in zip(hip, knee)], [a - k for a, k in zip(ank, knee)]))
            axis = [a - h for a, h in zip(ank, hip)]
            la = math.sqrt(sum(c * c for c in axis)) or 1e-9
            axis = [c / la for c in axis]
            k = [x - h for x, h in zip(knee, hip)]
            along = sum(a * b for a, b in zip(k, axis))
            perp = [a - along * b for a, b in zip(k, axis)]
            splay.append(math.degrees(math.atan2(perp[0] * sign, max(along, 1e-3))))
    out["knee_flexion_deg"] = _stats(flex, 1)
    out["knee_splay_deg"] = _stats(splay, 1)
    pel = track["pelvis"]
    out["pelvis_height_m"] = _stats([p[1] for p in pel])
    out["pelvis_bob_m"] = round(max(p[1] for p in pel) - min(p[1] for p in pel), 3)
    out["pelvis_lateral_sway_m"] = round(max(p[0] for p in pel) - min(p[0] for p in pel), 3)
    if track.get("pelvisFwd"):
        yaws = [math.degrees(math.atan2(v[0], v[2])) for v in track["pelvisFwd"]]
        pitches = [math.degrees(math.asin(max(-1.0, min(1.0, v[1])))) for v in track["pelvisFwd"]]
        out["pelvis_yaw_deg"] = _stats(yaws, 1)
        out["pelvis_pitch_deg"] = _stats(pitches, 1)
    if track.get("torsoFwd"):
        tyaw = [math.degrees(math.atan2(v[0], v[2])) for v in track["torsoFwd"]]
        tpitch = [math.degrees(math.asin(max(-1.0, min(1.0, v[1])))) for v in track["torsoFwd"]]
        out["torso_yaw_deg"] = _stats(tyaw, 1)
        out["torso_pitch_deg"] = _stats(tpitch, 1)
        if track.get("pelvisFwd"):
            out["torso_vs_pelvis_yaw_deg"] = _stats([((t - p + 180) % 360) - 180 for t, p in zip(tyaw, [math.degrees(math.atan2(v[0], v[2])) for v in track["pelvisFwd"]])], 1)
    if track.get("footFwdL"):
        fy = []
        for side in ("L", "R"):
            for v in track["footFwd" + side]:
                fy.append(math.degrees(math.atan2(v[0], v[2])))
        out["foot_yaw_deg"] = _stats(fy, 1)
    return out
