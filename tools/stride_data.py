"""Foot motion data in the form Valve described for Half-Life: Alyx (SIGGRAPH 2021, slides 12-19).

Per foot, a clip is cut into foot cycles between stance frames (middle of each ground contact). Each frame's
FootBase (the lowest point of the sole) is stored as a stride-relative offset from the straight line between
the two stances plus a progression along that line, so a runtime can move the stances anywhere (predicted
step targets) and rebuild the foot's path between them: foot = start + progression * stride + R(stride) * offset.

Positions in are character-local (yaw-removed, relative to the root), metres; root is [x, z, yaw radians].
"""
import math

# a sole point counts as touching when it is within this of the clip's lowest frame AND slower than
# MAX_STANCE_SPEED. height alone let a swing foot skimming 2-3 cm up at walking pace bridge two plants into one;
# speed alone is no use because the retarget leaves fast-run contacts and some end stances 4-10 cm up, and
# sprint contacts (3-5 frames at 30 fps) still drift 0.3-0.8 m/s through the sole
HOVER_HEIGHT = 0.10
MAX_STANCE_SPEED = 1.0
# a touching sole point moving slower than this is planted (reported per frame; the lock wants it)
PLANT_SPEED = 0.3
MIN_PLANT_FRAMES = 2
MIN_SWING_FRAMES = 3
# a stride shorter than this has no direction of its own; the character's facing frames the offsets instead
MIN_STRIDE = 0.02
# blend width (m) over which the FootBase moves from toe to heel as the heel becomes the lower point
FOOTBASE_BLEND = 0.02


def _lerp(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def _wrap(degrees):
    return (degrees + 180.0) % 360.0 - 180.0


def _hypot(dx, dz):
    return math.sqrt(dx * dx + dz * dz)


def footbase(heel, toe, prev_w=None):
    """FootBase for one frame: position of the lower sole point (blended near the crossover) and the foot's
    heading in degrees (heel -> toe, atan2(x, z)). Both inputs are (x, y, z) in the same space.
    A flat plant (heel and toe within a quarter of the band, Tarkov's clips land exactly flat) keeps the end
    the base came from instead of dropping to the midpoint: that drop read as a 3 m/s spike mid-stance.
    Returns (position, heading, w) so a caller can carry w to the next frame."""
    diff = toe[1] - heel[1]
    w = min(max(diff / FOOTBASE_BLEND * 0.5 + 0.5, 0.0), 1.0)
    if prev_w is not None and abs(diff) < FOOTBASE_BLEND * 0.5 and (prev_w <= 0.0 or prev_w >= 1.0):
        w = prev_w
    position = _lerp(toe, heel, w)
    heading = math.degrees(math.atan2(toe[0] - heel[0], toe[2] - heel[2]))
    return position, heading, w


def to_world(root, local):
    """Character-local point -> world, given root [x, z, yaw]."""
    s, c = math.sin(root[2]), math.cos(root[2])
    return (root[0] + local[0] * c + local[2] * s, local[1], root[1] - local[0] * s + local[2] * c)


def to_local(root, world):
    s, c = math.sin(root[2]), math.cos(root[2])
    dx, dz = world[0] - root[0], world[2] - root[1]
    return (dx * c - dz * s, world[1], dx * s + dz * c)


def plant_runs(grounded):
    """(first, last) frame of each plant, merging flickers and dropping one-frame touches."""
    runs, start = [], None
    for f, flag in enumerate(list(grounded) + [False]):
        if flag and start is None:
            start = f
        elif not flag and start is not None:
            runs.append((start, f - 1))
            start = None
    merged = []
    for run in runs:
        if merged and run[0] - merged[-1][1] - 1 < MIN_SWING_FRAMES:
            merged[-1] = (merged[-1][0], run[1])
        else:
            merged.append(run)
    return [r for r in merged if r[1] - r[0] + 1 >= MIN_PLANT_FRAMES]


def loop_extend(root, locals_, copies):
    """Repeat a seamless loop so cycles that wrap can be measured. The last root frame is the loop's return
    point (export slices loops with one extra root frame), so copy k is transformed by k loop deltas."""
    n = len(locals_)
    a, b = root[0], root[n]
    dyaw = b[2] - a[2]
    s, c = math.sin(dyaw), math.cos(dyaw)
    # the loop delta as a rigid transform D(p) = b + R(dyaw) (p - a); copy k is D applied k times
    out_root = [tuple(root[f][:3]) for f in range(n)]
    for k in range(1, copies):
        for f in range(n):
            px, pz, yaw = out_root[(k - 1) * n + f]
            dx, dz = px - a[0], pz - a[1]
            out_root.append((b[0] + dx * c + dz * s, b[1] - dx * s + dz * c, yaw + dyaw))
    return out_root, list(locals_) * copies


def analyse_foot(root, heel, toe, fps, loop=False):
    """Foot cycles and per-frame trajectory for one foot.

    root: per-frame [x, z, yaw]; for loops one extra frame (the loop's return point) follows the clip frames.
    heel, toe: per-frame character-local sole points. Returns {"cycles": [...], "frames": {...}} with clip-frame
    indices; a loop cycle's end frame may exceed the clip length (wraps).
    """
    n = len(heel)
    copies = 3 if loop else 1
    if loop:
        root_x, heel_x = loop_extend(root, heel, copies)
        _, toe_x = loop_extend(root, toe, copies)
    else:
        root_x, heel_x, toe_x = list(root[:n]), list(heel), list(toe)
    total = n * copies

    base_local, heading_local, base_world, heading_world = [], [], [], []
    heel_world, toe_world = [], []
    prev_w = None
    for f in range(total):
        position, heading, prev_w = footbase(heel_x[f], toe_x[f], prev_w)
        base_local.append(position)
        heading_local.append(heading)
        base_world.append(to_world(root_x[f], position))
        heading_world.append(heading + math.degrees(root_x[f][2]))
        heel_world.append(to_world(root_x[f], heel_x[f]))
        toe_world.append(to_world(root_x[f], toe_x[f]))

    floor = min(p[1] for p in base_world)
    speed = [0.0] + [_hypot(base_world[f][0] - base_world[f - 1][0], base_world[f][2] - base_world[f - 1][2]) * fps for f in range(1, total)]
    if total > 1:
        speed[0] = speed[1]
    def down(points, moving):
        return [points[f][1] <= floor + HOVER_HEIGHT and moving[f] < MAX_STANCE_SPEED for f in range(total)]

    def speeds(points):
        out = [0.0] + [_hypot(points[f][0] - points[f - 1][0], points[f][2] - points[f - 1][2]) * fps for f in range(1, total)]
        if total > 1:
            out[0] = out[1]
        return out

    heel_speed, toe_speed = speeds(heel_world), speeds(toe_world)
    heel_down = down(heel_world, heel_speed)
    toe_down = down(toe_world, toe_speed)
    # contact from the sole points themselves: the blended base hands over heel -> toe in one frame on a foot
    # that lifts its heel fast (Tarkov's clips), which read as a 6 m/s frame inside a plant
    touching = [down(base_world, speed)[f] or heel_down[f] or toe_down[f] for f in range(total)]
    sole_speed = [min(heel_speed[f], toe_speed[f]) for f in range(total)]
    grounded = [touching[f] and sole_speed[f] < PLANT_SPEED for f in range(total)]

    runs = [r for r in plant_runs(touching) if min(speed[r[0]:r[1] + 1]) < MAX_STANCE_SPEED]
    # slowest frame of each contact; ties go to the middle of the window
    stances = [min(range(a, b + 1), key=lambda f: (round(speed[f], 3), abs(f - (a + b) / 2))) for a, b in runs]
    run_of = {}
    for index, (a, b) in enumerate(runs):
        for f in range(a, b + 1):
            run_of[f] = index
    virtual = set()
    if not loop:
        # a foot planted at the clip's edge is anchored there (its plant continues past the clip), and a foot in
        # the air at an edge gets a virtual stance where it is, so every frame has two anchors
        if runs and runs[0][0] == 0:
            stances[0] = 0
        if runs and runs[-1][1] == n - 1:
            if len(runs) == 1 and stances[0] == 0:
                stances.append(n - 1)
            else:
                stances[-1] = n - 1
        if not stances or stances[0] > 0:
            stances.insert(0, 0)
            virtual.add(0)
        if stances[-1] < n - 1:
            stances.append(n - 1)
            virtual.add(n - 1)
    if len(stances) < 2:
        stances = [0, total - 1]
        virtual.update(stances)

    # unwrapped world heading so a stride that turns the foot past 180 degrees lerps the long way, as authored
    unwrapped = [heading_world[0]]
    for f in range(1, total):
        unwrapped.append(unwrapped[-1] + _wrap(heading_world[f] - heading_world[f - 1]))

    cycles = []
    frame_cycle = [None] * total
    progression = [0.0] * total
    offsets = [(0.0, 0.0, 0.0)] * total
    rotation_offset = [0.0] * total
    for k in range(len(stances) - 1):
        s0, s1 = stances[k], stances[k + 1]
        start, end = base_world[s0], base_world[s1]
        stride = (end[0] - start[0], end[2] - start[2])
        length = _hypot(*stride)
        stationary = length < MIN_STRIDE
        # stride frame: z along the stride (or the character's facing when there is no stride), x to its right
        if stationary:
            axis_yaw = root_x[s0][2]
        else:
            axis_yaw = math.atan2(stride[0], stride[1])
        sa, ca = math.sin(axis_yaw), math.cos(axis_yaw)
        yaw0, yaw1 = unwrapped[s0], unwrapped[s1]
        span = max(s1 - s0, 1)
        for f in range(s0, s1 + 1):
            if frame_cycle[f] is not None and f == s0:
                continue  # the stance frame belongs to the cycle it starts, except for the very first one
            frame_cycle[f] = k
            p = base_world[f]
            if stationary:
                t = (f - s0) / span
                ref = start
            else:
                t = ((p[0] - start[0]) * stride[0] + (p[2] - start[2]) * stride[1]) / (length * length)
                ref = (start[0] + stride[0] * t, 0.0, start[2] + stride[1] * t)
            dx, dy, dz = p[0] - ref[0], p[1] - floor, p[2] - ref[2]
            progression[f] = t
            offsets[f] = (dx * ca - dz * sa, dy, dx * sa + dz * ca)
            rotation_offset[f] = unwrapped[f] - (yaw0 + (yaw1 - yaw0) * t)
        mid = (s0 + s1) // 2
        # events come from the contact windows the stances sit in: off is the frame after the start plant ends,
        # strike is where the end plant begins; lift (heel up) and land (sole flat) refine them. a stride whose
        # two stances share one plant never left the ground, so every event sits at its end
        def first(pred, lo, hi, default):
            for f in range(lo, hi + 1):
                if pred(f):
                    return f
            return default
        run0, run1 = run_of.get(s0), run_of.get(s1)
        if run0 is not None and run0 == run1:
            lift = off = strike = land = s1
        else:
            off = min(runs[run0][1] + 1, s1) if run0 is not None else s0
            strike = max(runs[run1][0], off) if run1 is not None else s1
            lift = first(lambda f: not heel_down[f], s0, off, off)
            land = first(lambda f: heel_down[f] and toe_down[f], strike, s1, strike)
        cycles.append({
            "startFrame": s0, "endFrame": s1,
            "stancePosition": [round(v, 4) for v in base_local[s0]],
            "stanceDirection": round(_wrap(heading_local[s0]), 2),
            "stanceCycle": round(s0 / n, 4),
            "strideLength": round(length, 4),
            "strideYaw": round(_wrap(math.degrees(axis_yaw - root_x[s0][2])), 2),
            "rotationChange": round(yaw1 - yaw0, 2),
            "hasMidpoint": True,
            # Keep this unwrapped until loop normalization below; the local
            # position is looked up from the corresponding extended sample.
            "middleFrame": mid,
            "middlePosition": [round(v, 4) for v in base_local[mid]],
            "middleOffset": [round(v, 4) for v in offsets[mid]],
            "middleProgression": round(progression[mid], 4),
            # vector from the stride's end back to its start, in the character frame at the end (Valve's
            # toStrideStartPos): the runtime knows the end (predicted step) and rebuilds the authored start from it
            "toStrideStartPos": [round(v, 4) for v in to_local((0.0, 0.0, root_x[s1][2]), (start[0] - end[0], 0.0, start[2] - end[2]))],
            "footLiftCycle": round((lift - s0) / span, 4),
            "footOffCycle": round((off - s0) / span, 4),
            "footStrikeCycle": round((strike - s0) / span, 4),
            "footLandCycle": round((land - s0) / span, 4),
            # the landing itself, in clip frames: the stance can sit well after it (a stop's last plant is
            # anchored at the clip's end), and steps-remaining counts landings
            "strikeFrame": strike,
            "stationary": stationary,
            # virtual anchors mark a clip edge, not a real plant: no strike happens there
            "virtualStart": s0 in virtual,
            "virtualEnd": s1 in virtual,
        })

    if loop:
        # keep the middle copy: its cycles start inside the clip and wrap naturally, and every frame there is
        # covered by a cycle that has a periodic image among the kept ones
        kept = [c for c in cycles if n <= c["startFrame"] < 2 * n]
        if not kept:
            # No stance survived into the middle copy.  The old fallback reused one or more cycles from the
            # first copy, then changed only their interval; their strike frames and per-frame arrays still
            # referred to the old (extended) cycles.  Keep the period measurable without inventing a contact:
            # use virtual anchors at the middle copy's endpoints and put every event at the virtual end.
            s0, s1 = n, 2 * n
            start, end = base_world[s0], base_world[s1]
            stride = (end[0] - start[0], end[2] - start[2])
            length = _hypot(*stride)
            stationary = length < MIN_STRIDE
            axis_yaw = root_x[s0][2] if stationary else math.atan2(stride[0], stride[1])
            sa, ca = math.sin(axis_yaw), math.cos(axis_yaw)
            yaw0, yaw1 = unwrapped[s0], unwrapped[s1]
            span = n
            for f in range(s0, s1):
                # A virtual cycle has no contact-derived anchors, so use time for its phase.  The reference
                # still follows the endpoint segment, matching reconstruct() when a non-zero loop displacement
                # is present; stationary cycles retain the existing facing-based offset frame.
                t = (f - s0) / span
                ref = (start[0] + stride[0] * t, 0.0, start[2] + stride[1] * t)
                p = base_world[f]
                dx, dy, dz = p[0] - ref[0], p[1] - floor, p[2] - ref[2]
                progression[f] = t
                offsets[f] = (dx * ca - dz * sa, dy, dx * sa + dz * ca)
                rotation_offset[f] = unwrapped[f] - (yaw0 + (yaw1 - yaw0) * t)
            mid = (s0 + s1) // 2
            kept = [{
                "startFrame": 0, "endFrame": n,
                "stancePosition": [round(v, 4) for v in base_local[s0]],
                "stanceDirection": round(_wrap(heading_local[s0]), 2),
                "stanceCycle": 0.0,
                "strideLength": round(length, 4),
                "strideYaw": round(_wrap(math.degrees(axis_yaw - root_x[s0][2])), 2),
                "rotationChange": round(yaw1 - yaw0, 2),
                "hasMidpoint": True,
                "middleFrame": mid - n,
                "middlePosition": [round(v, 4) for v in base_local[mid]],
                "middleOffset": [round(v, 4) for v in offsets[mid]],
                "middleProgression": round(progression[mid], 4),
                "toStrideStartPos": [round(v, 4) for v in to_local((0.0, 0.0, root_x[s1][2]),
                                                                       (start[0] - end[0], 0.0, start[2] - end[2]))],
                # There is no measured lift/off/strike/land event in this fallback.  The period boundary is
                # the only honest event location and keeps strikeFrame in the cycle's valid loop interval.
                "footLiftCycle": 1.0,
                "footOffCycle": 1.0,
                "footStrikeCycle": 1.0,
                "footLandCycle": 1.0,
                "strikeFrame": n,
                "stationary": stationary,
                "virtualStart": True,
                "virtualEnd": True,
            }]
        by_start = {}
        for c in kept:
            if c["startFrame"] >= n:
                c["startFrame"] -= n
                c["endFrame"] -= n
                c["strikeFrame"] -= n
                c["middleFrame"] -= n
            c["stanceCycle"] = round(c["startFrame"] / n, 4)
            by_start[c["startFrame"]] = len(by_start)
        window = range(n, 2 * n)
        cycle_index = []
        for f in window:
            # the covering cycle may be the copy-1 image of a kept one; plant detection at the very first frame
            # can differ by a frame, so take the kept cycle whose phase is nearest
            phase = stances[frame_cycle[f]] % n
            cycle_index.append(by_start.get(phase, min(by_start.items(), key=lambda kv: min(abs(kv[0] - phase), n - abs(kv[0] - phase)))[1]))
        cycles = kept
    else:
        window = range(n)
        cycle_index = [frame_cycle[f] for f in window]

    frames = {
        "cycle": cycle_index,
        "progression": [round(progression[f], 4) for f in window],
        "translationOffset": [[round(v, 4) for v in offsets[f]] for f in window],
        "rotationOffset": [round(rotation_offset[f], 2) for f in window],
        "footbase": [[round(v, 4) for v in base_local[f]] + [round(_wrap(heading_local[f]), 2)] for f in window],
        "grounded": [1 if grounded[f] else 0 for f in window],
    }
    return {"cycles": cycles, "frames": frames, "floor": round(floor, 4)}


def reconstruct(root, cycle, frame, floor, base_world_start, base_world_end):
    """Runtime rebuild of one frame's world FootBase from stride data and the two (possibly moved) stances.
    Shared with the tests so the stored form is proven to round-trip."""
    stride = (base_world_end[0] - base_world_start[0], base_world_end[2] - base_world_start[2])
    length = _hypot(*stride)
    if cycle["stationary"] or length < MIN_STRIDE:
        axis_yaw = root[2]
    else:
        axis_yaw = math.atan2(stride[0], stride[1])
    sa, ca = math.sin(axis_yaw), math.cos(axis_yaw)
    t = frame["progression"]
    ox, oy, oz = frame["translationOffset"]
    ref = (base_world_start[0] + stride[0] * t, floor, base_world_start[2] + stride[1] * t)
    return (ref[0] + ox * ca + oz * sa, ref[1] + oy, ref[2] - ox * sa + oz * ca)


def steps_remaining(analyses, frames):
    """Per clip frame, how many foot strikes (both feet) are still to come; Valve filters stopping clips on it."""
    strikes = []
    for a in analyses:
        for c in a["cycles"]:
            if not c["stationary"] and not c["virtualEnd"]:
                strikes.append(c["strikeFrame"])
    return [sum(1 for s in strikes if s > f) for f in range(frames)]
