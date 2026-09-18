"""Retarget Half-Life: Alyx grunt locomotion (VRF glTF export) onto EFT's body skeleton.

Local-use research tool: it reads the user's own exported GLB and writes a pose database under tmp/.
Valve animation data must not be committed or redistributed.

Retargeting is direction-based, not rest-relative: EFT's rest pose is a relaxed carry stance (knees bent,
arms forward) while the grunt's is not, so each target bone is aimed where the source bone points, with
twist taken from a shared secondary axis.
"""
import argparse
import json
import math
import struct
from pathlib import Path

FPS = 30.0

# target bone -> (source joint, source child, target child, secondary axis kind)
LEG_SPECS = {
    "L": ("leg_upper_L", "leg_lower_L", "ankle_L", "ball_L", "ball_end_L"),
    "R": ("leg_upper_R", "leg_lower_R", "ankle_R", "ball_R", "ball_end_R"),
}
MIN_PLANT_FRAMES = 4
MIN_SWING_FRAMES = 5
# below this knee bend (as a sine) the bend plane is noise, so twist falls back to the pelvis side axis
MIN_BEND_SINE = 0.05


# ---------- small quaternion / vector helpers (x, y, z, w) ----------
def v_add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def v_sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def v_scale(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def v_dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def v_cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def v_len(a): return math.sqrt(v_dot(a, a))


def v_norm(a):
    length = v_len(a)
    return (0.0, 0.0, 0.0) if length < 1e-12 else v_scale(a, 1.0 / length)


def q_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def q_inv(q): return (-q[0], -q[1], -q[2], q[3])


def q_rot(q, v):
    return q_mul(q_mul(q, (v[0], v[1], v[2], 0.0)), q_inv(q))[:3]


def q_norm(q):
    length = math.sqrt(sum(c * c for c in q))
    return tuple(c / length for c in q)


def q_slerp(a, b, t):
    dot = sum(x * y for x, y in zip(a, b))
    if dot < 0:
        b, dot = tuple(-c for c in b), -dot
    if dot > 0.9995:
        return q_norm(tuple(x + (y - x) * t for x, y in zip(a, b)))
    theta = math.acos(dot)
    s = math.sin(theta)
    wa, wb = math.sin((1 - t) * theta) / s, math.sin(t * theta) / s
    return tuple(wa * x + wb * y for x, y in zip(a, b))


def q_from_basis(x, y, z):
    """Rotation whose columns are the given orthonormal axes."""
    m00, m01, m02 = x[0], y[0], z[0]
    m10, m11, m12 = x[1], y[1], z[1]
    m20, m21, m22 = x[2], y[2], z[2]
    trace = m00 + m11 + m22
    if trace > 0:
        s = 0.5 / math.sqrt(trace + 1.0)
        return q_norm(((m21 - m12) * s, (m02 - m20) * s, (m10 - m01) * s, 0.25 / s))
    if m00 > m11 and m00 > m22:
        s = 2.0 * math.sqrt(1.0 + m00 - m11 - m22)
        return q_norm((0.25 * s, (m10 + m01) / s, (m02 + m20) / s, (m21 - m12) / s))
    if m11 > m22:
        s = 2.0 * math.sqrt(1.0 + m11 - m00 - m22)
        return q_norm(((m10 + m01) / s, 0.25 * s, (m21 + m12) / s, (m02 - m20) / s))
    s = 2.0 * math.sqrt(1.0 + m22 - m00 - m11)
    return q_norm(((m02 + m20) / s, (m21 + m12) / s, 0.25 * s, (m10 - m01) / s))


def frame(primary, secondary):
    """Orthonormal frame: z along primary, x toward secondary."""
    z = v_norm(primary)
    x = v_norm(v_sub(secondary, v_scale(z, v_dot(secondary, z))))
    y = v_cross(z, x)
    return q_from_basis(x, y, z)


# ---------- glTF source ----------
class GltfSource:
    def __init__(self, path):
        with open(path, "rb") as f:
            _, _, _ = struct.unpack("<III", f.read(12))
            json_length, _ = struct.unpack("<II", f.read(8))
            self.gltf = json.loads(f.read(json_length))
            pad = (4 - json_length % 4) % 4
            f.read(pad)
            bin_length, _ = struct.unpack("<II", f.read(8))
            self.bin = f.read(bin_length)
        self.nodes = self.gltf["nodes"]
        self.parent = {}
        for index, node in enumerate(self.nodes):
            for child in node.get("children", []):
                self.parent[child] = index
        self.index = {}
        for index, node in enumerate(self.nodes):
            self.index.setdefault(node.get("name"), index)
        self.animations = {a["name"]: a for a in self.gltf.get("animations", [])}

    def accessor(self, i):
        acc = self.gltf["accessors"][i]
        view = self.gltf["bufferViews"][acc["bufferView"]]
        width = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}[acc["type"]]
        if acc["componentType"] != 5126:
            raise ValueError("only float accessors are supported")
        start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        values = struct.unpack_from("<%df" % (acc["count"] * width), self.bin, start)
        return [values[k:k + width] for k in range(0, len(values), width)]

    def tracks(self, clip_name):
        clip = self.animations[clip_name]
        tracks = {}
        for channel in clip["channels"]:
            sampler = clip["samplers"][channel["sampler"]]
            times = [t[0] for t in self.accessor(sampler["input"])]
            values = self.accessor(sampler["output"])
            tracks[(channel["target"]["node"], channel["target"]["path"])] = (times, values)
        duration = max((times[-1] for times, _ in tracks.values() if times), default=0.0)
        return tracks, duration

    def local(self, node, tracks, t):
        rest = self.nodes[node]
        translation = tuple(rest.get("translation", (0.0, 0.0, 0.0)))
        rotation = tuple(rest.get("rotation", (0.0, 0.0, 0.0, 1.0)))
        if (node, "translation") in tracks:
            translation = sample(tracks[(node, "translation")], t, False)
        if (node, "rotation") in tracks:
            rotation = sample(tracks[(node, "rotation")], t, True)
        return translation, rotation

    def world(self, name, tracks, t, cache):
        node = self.index[name]
        return self._world(node, tracks, t, cache)

    def _world(self, node, tracks, t, cache):
        if node in cache:
            return cache[node]
        translation, rotation = self.local(node, tracks, t)
        if node in self.parent:
            parent_position, parent_rotation = self._world(self.parent[node], tracks, t, cache)
            result = (v_add(parent_position, q_rot(parent_rotation, translation)), q_norm(q_mul(parent_rotation, rotation)))
        else:
            result = (translation, q_norm(rotation))
        cache[node] = result
        return result


def sample(track, t, rotation):
    times, values = track
    if len(times) == 1 or t <= times[0]:
        return tuple(values[0])
    if t >= times[-1]:
        return tuple(values[-1])
    lo, hi = 0, len(times) - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if times[mid] <= t:
            lo = mid
        else:
            hi = mid
    u = (t - times[lo]) / (times[hi] - times[lo])
    a, b = tuple(values[lo]), tuple(values[hi])
    return q_slerp(a, b, u) if rotation else tuple(x + (y - x) * u for x, y in zip(a, b))


def to_unity(position):
    # glTF is right-handed; mirroring X lands the grunt's +X left leg on Unity's -X left
    return (-position[0], position[1], position[2])


# ---------- EFT target ----------
class EftSkeleton:
    def __init__(self, path):
        bones = json.loads(Path(path).read_text(encoding="utf-8"))["Bones"]
        self.bones = bones
        self.index = {b["Name"]: i for i, b in enumerate(bones)}
        self.world = {}
        for i, bone in enumerate(bones):
            local_p, local_r = tuple(bone["LocalPosition"]), tuple(bone["LocalRotation"])
            if bone["Parent"] < 0:
                self.world[i] = (local_p, local_r)
            else:
                pp, pr = self.world[bone["Parent"]]
                self.world[i] = (v_add(pp, q_rot(pr, local_p)), q_norm(q_mul(pr, local_r)))
        root_p, root_r = self.world[self.index["Root_Joint"]]
        self.root = (root_p, root_r)

    def pos(self, name):
        p, _ = self.world[self.index[name]]
        # express relative to Root_Joint so poses stay character-local
        return q_rot(q_inv(self.root[1]), v_sub(p, self.root[0]))

    def rot(self, name):
        _, r = self.world[self.index[name]]
        return q_norm(q_mul(q_inv(self.root[1]), r))

    def local_rot(self, name):
        return tuple(self.bones[self.index[name]]["LocalRotation"])


def leg_length(points, hip, knee, ankle):
    return v_len(v_sub(points[knee], points[hip])) + v_len(v_sub(points[ankle], points[knee]))


def bend_axis(hip, knee, ankle, side):
    thigh, shin = v_sub(knee, hip), v_sub(ankle, knee)
    normal = v_cross(thigh, shin)
    if v_len(normal) > MIN_BEND_SINE * v_len(thigh) * v_len(shin):
        return v_norm(normal)
    # nearly straight: twist from the pelvis side axis, flipped to match a forward-bending knee's normal
    return v_norm(side)


def v_rotate_axis(v, axis, degrees):
    half = math.radians(degrees) / 2
    q = (axis[0] * math.sin(half), axis[1] * math.sin(half), axis[2] * math.sin(half), math.cos(half))
    return q_rot(q, v)


def tighten_legs(pts, stance_scale, knee_inward_degrees):
    """Pull the feet toward the centerline and turn the knees inward, re-solving each knee with two-bone IK.

    EFT bots walk with ankles ~0.12 m apart in a combat stance; the grunt walk lands them ~0.30 m apart after
    retargeting, which reads as bowed-out knees even though knee direction matches. Lengths are preserved.
    """
    if stance_scale == 1.0 and knee_inward_degrees == 0.0:
        return pts
    pts = dict(pts)
    side_axis = v_sub(pts["leg_upper_L"], pts["leg_upper_R"])
    side_axis = v_norm((side_axis[0], 0.0, side_axis[2]))
    hip_center = v_scale(v_add(pts["leg_upper_L"], pts["leg_upper_R"]), 0.5)
    for side, (hip_n, knee_n, ankle_n, ball_n, end_n) in LEG_SPECS.items():
        hip, knee, ankle = pts[hip_n], pts[knee_n], pts[ankle_n]
        lateral = v_dot(v_sub(ankle, hip_center), side_axis)
        shift = v_scale(side_axis, lateral * (stance_scale - 1.0))
        new_ankle = v_add(ankle, shift)
        l1, l2 = v_len(v_sub(knee, hip)), v_len(v_sub(ankle, knee))
        to_ankle = v_sub(new_ankle, hip)
        d = min(max(v_len(to_ankle), 1e-4), l1 + l2 - 1e-4)
        direction = v_norm(to_ankle)
        old_direction = v_norm(v_sub(ankle, hip))
        pole = v_sub(v_sub(knee, hip), v_scale(old_direction, v_dot(v_sub(knee, hip), old_direction)))
        if v_len(pole) < 1e-5:
            pole = (0.0, 0.0, 1.0)
        pole = v_sub(pole, v_scale(direction, v_dot(pole, direction)))
        if knee_inward_degrees:
            # inward is toward the other leg: of the two rotations, keep the one whose pole points less outward
            outward = side_axis if side == "L" else v_scale(side_axis, -1.0)
            candidates = [v_rotate_axis(pole, direction, knee_inward_degrees), v_rotate_axis(pole, direction, -knee_inward_degrees)]
            pole = min(candidates, key=lambda c: v_dot(v_norm(c), outward))
        pole = v_norm(pole)
        a = (l1 * l1 - l2 * l2 + d * d) / (2 * d)
        h = math.sqrt(max(l1 * l1 - a * a, 0.0))
        pts[knee_n] = v_add(hip, v_add(v_scale(direction, a), v_scale(pole, h)))
        pts[ankle_n] = new_ankle
        # the foot rides along unchanged in orientation
        pts[ball_n] = v_add(pts[ball_n], shift)
        pts[end_n] = v_add(pts[end_n], shift)
    return pts


def resolve_knee(pts, original, side):
    """Re-solve one knee after its hip or ankle moved, keeping segment lengths and the original bend direction."""
    hip_n, knee_n, ankle_n, _, _ = LEG_SPECS[side]
    hip0, knee0, ankle0 = original[hip_n], original[knee_n], original[ankle_n]
    l1, l2 = v_len(v_sub(knee0, hip0)), v_len(v_sub(ankle0, knee0))
    hip, ankle = pts[hip_n], pts[ankle_n]
    to_ankle = v_sub(ankle, hip)
    d = min(max(v_len(to_ankle), 1e-4), l1 + l2 - 1e-4)
    direction = v_norm(to_ankle)
    old_direction = v_norm(v_sub(ankle0, hip0))
    pole = v_sub(v_sub(knee0, hip0), v_scale(old_direction, v_dot(v_sub(knee0, hip0), old_direction)))
    if v_len(pole) < 1e-5:
        pole = (0.0, 0.0, 1.0)
    pole = v_norm(v_sub(pole, v_scale(direction, v_dot(pole, direction))))
    a = (l1 * l1 - l2 * l2 + d * d) / (2 * d)
    h = math.sqrt(max(l1 * l1 - a * a, 0.0))
    pts[knee_n] = v_add(hip, v_add(v_scale(direction, a), v_scale(pole, h)))


def q_from_to(a, b):
    """Shortest rotation taking direction a onto direction b."""
    a, b = v_norm(a), v_norm(b)
    d = v_dot(a, b)
    if d > 0.999999:
        return (0.0, 0.0, 0.0, 1.0)
    if d < -0.999999:
        axis = v_norm(v_cross((1.0, 0.0, 0.0), a)) if abs(a[0]) < 0.9 else v_norm(v_cross((0.0, 1.0, 0.0), a))
        return (axis[0], axis[1], axis[2], 0.0)
    c = v_cross(a, b)
    return q_norm((c[0], c[1], c[2], 1.0 + d))


def turn_yaw_pitch(v, yaw_degrees, pitch_degrees):
    """Re-aim v by adding yaw (about up) and pitch (toward up) in degrees, length kept."""
    length = v_len(v)
    yaw = math.atan2(v[0], v[2]) + math.radians(yaw_degrees)
    pitch = math.atan2(v[1], math.hypot(v[0], v[2])) + math.radians(pitch_degrees)
    return (length * math.cos(pitch) * math.sin(yaw), length * math.sin(pitch), length * math.cos(pitch) * math.cos(yaw))


def smoothstep(t):
    t = min(max(t, 0.0), 1.0)
    return t * t * (3 - 2 * t)


def plant_runs(frame_data, side):
    """(first, last) frame of each window where this ankle is down and still in world space."""
    ankle_n = LEG_SPECS[side][2]
    count = len(frame_data)
    world = []
    for root_p, yaw, pts in frame_data:
        yaw_q = (0.0, math.sin(yaw / 2), 0.0, math.cos(yaw / 2))
        world.append(v_add(root_p, q_rot(yaw_q, pts[ankle_n])))
    speed = [0.0] + [math.hypot(world[f][0] - world[f - 1][0], world[f][2] - world[f - 1][2]) * FPS for f in range(1, count)]
    low = min(p[1] for p in world)
    planted = [speed[f] < 0.25 and world[f][1] < low + 0.05 for f in range(count)]
    runs, start = [], None
    for f, flag in enumerate(planted + [False]):
        if flag and start is None:
            start = f
        elif not flag and start is not None:
            runs.append((start, f - 1))
            start = None
    # a flicker out of a plant isn't a step: a 1-frame gap once became a "swing" carrying a 13 cm end-stance shift
    merged = []
    for run in runs:
        if merged and run[0] - merged[-1][1] - 1 < MIN_SWING_FRAMES:
            merged[-1] = (merged[-1][0], run[1])
        else:
            merged.append(run)
    runs = merged
    # one-frame "plants" mid-swing are noise; treating one as the previous step squeezed a 14 cm shift into a frame
    return [r for r in runs[:-1] if r[1] - r[0] + 1 >= MIN_PLANT_FRAMES] + runs[-1:]


def narrow_plants(frame_data, scale):
    """Narrow the stance without shortening sidesteps: each plant gets one constant sideways shift.

    The user found the heavy soldier's stance far wider than Tarkov's. Scaling every frame's ankle offset from the
    hips (tighten_legs) would slide planted feet during strafes, where that offset is the stride itself. Here a
    planted foot moves by a fixed amount for its whole plant (scale applied to its offset at mid-plant), and the
    shift only changes while the foot is in the air.
    """
    if scale == 1.0:
        return frame_data, None
    count = len(frame_data)
    shifts = {}
    report = {}
    for side in ("L", "R"):
        ankle_n = LEG_SPECS[side][2]
        runs = plant_runs(frame_data, side)
        keys = []
        for first, last in runs:
            mid = (first + last) // 2
            pts = frame_data[mid][2]
            hip_center_x = (pts["leg_upper_L"][0] + pts["leg_upper_R"][0]) * 0.5
            keys.append((first, last, (scale - 1.0) * (pts[ankle_n][0] - hip_center_x)))
        per_frame = [0.0] * count
        if keys:
            for f in range(count):
                if f <= keys[0][1]:
                    per_frame[f] = keys[0][2]
                elif f >= keys[-1][0]:
                    per_frame[f] = keys[-1][2]
                else:
                    for (a0, a1, sa), (b0, b1, sb) in zip(keys, keys[1:]):
                        if a0 <= f <= a1:
                            per_frame[f] = sa
                            break
                        if a1 < f < b0:
                            per_frame[f] = sa + (sb - sa) * smoothstep((f - a1) / max(b0 - a1, 1))
                            break
        shifts[side] = per_frame
        report[side] = {"plants": len(keys), "shift_m": [round(k[2], 3) for k in keys]}
    out = []
    for root_p, yaw, pts in frame_data:
        out.append((root_p, yaw, dict(pts)))
    for f in range(count):
        original = frame_data[f][2]
        pts = out[f][2]
        for side in ("L", "R"):
            dx = shifts[side][f]
            for name in LEG_SPECS[side][2:]:
                q = pts[name]
                pts[name] = (q[0] + dx, q[1], q[2])
            resolve_knee(pts, original, side)
    return out, report


def apply_end_stance(frame_data, stance, to_source, foot_turn=None):
    """Stretch each foot's final step so the clip ends in Tarkov's idle stance, and turn the hips into its blade.

    The user wanted stops to step into Tarkov's idle rather than freeze or slide there. Each foot's last swing
    (lift-off to final landing) carries a growing offset that reaches Tarkov's idle foot spot at landing;
    the hips yaw and shift toward the idle hips over the same window; knees are re-solved with lengths kept.
    Stance values are EFT character-local (x right, y up from root, z forward) measured from stock captures.
    foot_turn adds per-foot (yaw, pitch) degrees over the same swing so feet also land at Tarkov's idle rotation.
    """
    foot_turn = foot_turn or {"L": (0.0, 0.0), "R": (0.0, 0.0)}
    count = len(frame_data)
    fps = FPS
    report = {}
    windows = {}
    for side in ("L", "R"):
        ankle_n = LEG_SPECS[side][2]
        runs = plant_runs(frame_data, side)
        landing = runs[-1][0] if runs else count - 1
        lift = runs[-2][1] + 1 if len(runs) >= 2 else max(0, landing - 15)
        windows[side] = (lift, landing)
        end = frame_data[-1][2][ankle_n]
        goal = to_source(stance["ankle" + side])
        windows[side + "delta"] = (goal[0] - end[0], goal[2] - end[2])
        report[side] = {"lift": lift, "landing": landing, "shift_m": round(math.hypot(*windows[side + "delta"]), 3)}

    last = frame_data[-1][2]
    end_hips = v_sub(last["leg_upper_L"], last["leg_upper_R"])
    idle_hips = v_sub(to_source(stance["hipL"]), to_source(stance["hipR"]))
    yaw_delta = math.atan2(idle_hips[0], idle_hips[2]) - math.atan2(end_hips[0], end_hips[2])
    yaw_delta = (yaw_delta + math.pi) % (2 * math.pi) - math.pi
    end_center = v_scale(v_add(last["leg_upper_L"], last["leg_upper_R"]), 0.5)
    idle_center = to_source(stance["hipCenter"])
    # height too: the stop ends ~3 cm lower than Tarkov's idle hips, which would pop up on handoff
    center_delta = (idle_center[0] - end_center[0], idle_center[1] - end_center[1], idle_center[2] - end_center[2])
    hip_start = min(windows["L"][0], windows["R"][0])
    hip_end = max(windows["L"][1], windows["R"][1])
    report["hips"] = {"yaw_deg": round(math.degrees(yaw_delta), 1), "shift_m": round(v_len(center_delta), 3), "start": hip_start, "end": hip_end}

    out = []
    for f, (root_p, yaw, pts) in enumerate(frame_data):
        original = dict(pts)
        pts = dict(pts)
        w_hips = smoothstep((f - hip_start) / max(hip_end - hip_start, 1))
        if w_hips > 0:
            center = v_scale(v_add(pts["leg_upper_L"], pts["leg_upper_R"]), 0.5)
            angle = yaw_delta * w_hips
            spin = (0.0, math.sin(angle / 2), 0.0, math.cos(angle / 2))
            for name in ("leg_upper_L", "leg_upper_R", "pelvis", "spine_3"):
                pts[name] = v_add(v_add(center, q_rot(spin, v_sub(pts[name], center))), v_scale(center_delta, w_hips))
        for side in ("L", "R"):
            lift, landing = windows[side]
            w = smoothstep((f - lift) / max(landing - lift, 1))
            dx, dz = windows[side + "delta"]
            if w > 0:
                for name in LEG_SPECS[side][2:]:
                    p = pts[name]
                    pts[name] = (p[0] + dx * w, p[1], p[2] + dz * w)
                dyaw, dpitch = foot_turn[side]
                if dyaw or dpitch:
                    ankle_n, ball_n, end_n = LEG_SPECS[side][2:]
                    ankle = pts[ankle_n]
                    to_ball = v_sub(pts[ball_n], ankle)
                    turned = turn_yaw_pitch(to_ball, dyaw * w, dpitch * w)
                    # the toe tip rides the same rotation so the foot turns as one piece
                    spin = q_from_to(to_ball, turned)
                    pts[ball_n] = v_add(ankle, turned)
                    pts[end_n] = v_add(ankle, q_rot(spin, v_sub(pts[end_n], ankle)))
            if w > 0 or w_hips > 0:
                resolve_knee(pts, original, side)
        out.append((root_p, yaw, pts))
    return out, report


def foot_axis_rest(source):
    """Per side: rest ankle rotation and the ball/ball_end offsets turned onto the ankle bone's forward axis.

    Valve's ball joint sits 14.1 deg off the ankle bone's forward axis in the bind pose (both soldiers), so mapping
    EFT's Foot->Toe onto ankle->ball toed every foot out by that much on top of what the clip does. The offsets are
    rotated about the bind pose's up axis so the segment points where the bone points; each frame carries them with
    the ankle's rotation from rest.
    """
    cache = {}
    root_p, root_r = source.world("root_motion", {}, 0.0, cache)
    forward = to_unity(q_rot(root_r, (0.0, 0.0, 1.0)))
    root_yaw = math.atan2(forward[0], forward[2])
    yaw_q = (0.0, math.sin(root_yaw / 2), 0.0, math.cos(root_yaw / 2))
    out = {}
    for side, (_, _, ankle_n, ball_n, end_n) in LEG_SPECS.items():
        ankle_p, ankle_r = source.world(ankle_n, {}, 0.0, cache)
        segment = q_rot(q_inv(yaw_q), to_unity(v_sub(source.world(ball_n, {}, 0.0, cache)[0], ankle_p)))
        theta = math.atan2(segment[0], segment[2])
        fix = (0.0, math.sin(-theta / 2), 0.0, math.cos(-theta / 2))
        def corrected(name):
            local = q_rot(q_inv(yaw_q), to_unity(v_sub(source.world(name, {}, 0.0, cache)[0], ankle_p)))
            return to_unity(q_rot(yaw_q, q_rot(fix, local)))
        out[side] = (ankle_r, corrected(ball_n), corrected(end_n), math.degrees(theta))
    return out


def retarget_clip(source, target, clip_name, stance_scale=1.0, knee_inward_degrees=0.0, end_stance=None, plant_scale=1.0, foot_axis="bone"):
    tracks, duration = source.tracks(clip_name)
    frames = max(2, int(round(duration * FPS)) + 1)
    names = ["root_motion", "pelvis", "spine_3", "leg_upper_L", "leg_upper_R"]
    for spec in LEG_SPECS.values():
        names += list(spec)
    axis_rest = foot_axis_rest(source) if foot_axis == "bone" else None

    # target rest frames, built with the same definitions the source uses each frame
    t_side = v_sub(target.pos("Base HumanLThigh1"), target.pos("Base HumanRThigh1"))
    rest = {
        "Base HumanPelvis": frame(v_sub(target.pos("Base HumanSpine3"), target.pos("Base HumanPelvis")), t_side),
    }
    for side, prefix in (("L", "Base HumanL"), ("R", "Base HumanR")):
        hip, knee, ankle, toe = (target.pos(prefix + n) for n in ("Thigh1", "Calf", "Foot", "Toe"))
        axis = bend_axis(hip, knee, ankle, t_side)
        rest[prefix + "Thigh1"] = frame(v_sub(knee, hip), axis)
        rest[prefix + "Calf"] = frame(v_sub(ankle, knee), axis)
        rest[prefix + "Foot"] = frame(v_sub(toe, ankle), axis)
    target_legs = leg_length({n: target.pos(n) for n in ("Base HumanLThigh1", "Base HumanLCalf", "Base HumanLFoot")}, "Base HumanLThigh1", "Base HumanLCalf", "Base HumanLFoot")

    out = {"name": clip_name, "fps": FPS, "frames": frames, "root": [], "pelvisPosition": [], "rotations": {}, "contacts": {"L": [], "R": []}, "checks": {}}
    bones = ["Base HumanPelvis", "Base HumanLThigh1", "Base HumanLCalf", "Base HumanLFoot", "Base HumanRThigh1", "Base HumanRCalf", "Base HumanRFoot"]
    for bone in bones:
        out["rotations"][bone] = []
    ankle_error = []
    # rest segment lengths, not an animated frame where a bent knee would shorten the leg
    source_legs = v_len(tuple(source.nodes[source.index["leg_lower_L"]]["translation"])) + v_len(tuple(source.nodes[source.index["ankle_L"]]["translation"]))
    scale = target_legs / source_legs
    # hips, not the pelvis bone, anchor height: the grunt's hip joints sit 7 cm below its pelvis bone while
    # EFT's sit just above theirs, so scaling pelvis height floated EFT's legs
    rest_cache = {}
    source_rest_ankle = to_unity(source.world("ankle_L", {}, 0.0, rest_cache)[0])[1]
    target_rest_ankle = target.pos("Base HumanLFoot")[1]
    target_hip_center = v_scale(v_add(target.pos("Base HumanLThigh1"), target.pos("Base HumanRThigh1")), 0.5)
    target_hip_in_pelvis = q_rot(q_inv(target.rot("Base HumanPelvis")), v_sub(target_hip_center, target.pos("Base HumanPelvis")))
    def solve_frame(pts):
        s_side = v_sub(pts["leg_upper_L"], pts["leg_upper_R"])
        pelvis_world = q_mul(frame(v_sub(pts["spine_3"], pts["pelvis"]), s_side), q_mul(q_inv(rest["Base HumanPelvis"]), target.rot("Base HumanPelvis")))
        world_rot = {"Base HumanPelvis": pelvis_world}
        for side, prefix in (("L", "Base HumanL"), ("R", "Base HumanR")):
            hip_n, knee_n, ankle_n, ball_n, _ = LEG_SPECS[side]
            hip, knee, ankle, ball = pts[hip_n], pts[knee_n], pts[ankle_n], pts[ball_n]
            axis = bend_axis(hip, knee, ankle, s_side)
            for bone, primary in (("Thigh1", v_sub(knee, hip)), ("Calf", v_sub(ankle, knee)), ("Foot", v_sub(ball, ankle))):
                name = prefix + bone
                world_rot[name] = q_mul(frame(primary, axis), q_mul(q_inv(rest[name]), target.rot(name)))
        source_hip_center = v_scale(v_add(pts["leg_upper_L"], pts["leg_upper_R"]), 0.5)
        hip_target = (source_hip_center[0] * scale, (source_hip_center[1] - source_rest_ankle) * scale + target_rest_ankle, source_hip_center[2] * scale)
        pelvis_position = v_sub(hip_target, q_rot(pelvis_world, target_hip_in_pelvis))
        return pelvis_world, world_rot, pelvis_position

    def foot_angles(pts):
        # EFT foot heading and toe pitch (degrees, char-local) from the Foot->Toe rest segment
        _, world_rot, _ = solve_frame(pts)
        angles = {}
        for side, prefix in (("L", "Base HumanL"), ("R", "Base HumanR")):
            rest_toe = q_rot(q_inv(target.rot(prefix + "Foot")), v_sub(target.pos(prefix + "Toe"), target.pos(prefix + "Foot")))
            d = q_rot(world_rot[prefix + "Foot"], rest_toe)
            angles[side] = (math.degrees(math.atan2(d[0], d[2])), math.degrees(math.atan2(d[1], math.hypot(d[0], d[2]))))
        return angles

    frame_data = []
    for f in range(frames):
        t = min(duration, f / FPS)
        cache = {}
        world = {n: source.world(n, tracks, t, cache) for n in names}
        if axis_rest:
            for side, (_, _, ankle_n, ball_n, end_n) in LEG_SPECS.items():
                rest_r, ball_off, end_off, _ = axis_rest[side]
                ankle_p, ankle_r = world[ankle_n]
                turn = q_mul(ankle_r, q_inv(rest_r))
                world[ball_n] = (v_add(ankle_p, q_rot(turn, ball_off)), world[ball_n][1])
                world[end_n] = (v_add(ankle_p, q_rot(turn, end_off)), world[end_n][1])
        root_p, root_r = world["root_motion"]
        root_p = to_unity(root_p)
        # heading from the root_motion forward axis, in unity space
        forward = to_unity(q_rot(root_r, (0.0, 0.0, 1.0)))
        yaw = math.atan2(forward[0], forward[2])
        yaw_q = (0.0, math.sin(yaw / 2), 0.0, math.cos(yaw / 2))
        # character-local joint positions
        pts = {n: q_rot(q_inv(yaw_q), v_sub(to_unity(world[n][0]), root_p)) for n in names}
        frame_data.append((root_p, yaw, tighten_legs(pts, stance_scale, knee_inward_degrees)))
    frame_data, narrowed = narrow_plants(frame_data, plant_scale)
    if narrowed:
        out["plantStance"] = narrowed
    if end_stance:
        to_source = lambda p: (p[0] / scale, (p[1] - target_rest_ankle) / scale + source_rest_ankle, p[2] / scale)
        base = frame_data
        turn = {"L": [0.0, 0.0], "R": [0.0, 0.0]}
        frame_data, report = apply_end_stance(base, end_stance, to_source, turn)
        if "toeYawL" in end_stance:
            # the source->EFT foot mapping isn't exactly linear in yaw/pitch, so converge on the measured idle angles
            for _ in range(4):
                angles = foot_angles(frame_data[-1][2])
                for side in ("L", "R"):
                    turn[side][0] += (end_stance["toeYaw" + side] - angles[side][0] + 180.0) % 360.0 - 180.0
                    turn[side][1] += end_stance["toePitch" + side] - angles[side][1]
                frame_data, report = apply_end_stance(base, end_stance, to_source, turn)
            final = foot_angles(frame_data[-1][2])
            report["footAngles"] = {side: {"yaw": round(final[side][0], 1), "pitch": round(final[side][1], 1),
                                           "target": [end_stance["toeYaw" + side], end_stance["toePitch" + side]],
                                           "turn": [round(turn[side][0], 1), round(turn[side][1], 1)]} for side in ("L", "R")}
        out["endStance"] = report

    for f, (root_p, yaw, pts) in enumerate(frame_data):
        out["root"].append([root_p[0], root_p[2], yaw])

        pelvis_world, world_rot, pelvis_position = solve_frame(pts)

        # locals against the EFT hierarchy: Root_Joint -> Pelvis -> Thigh1 -> Thigh2 (rest) -> Calf -> Foot
        out["rotations"]["Base HumanPelvis"].append(list(pelvis_world))
        for prefix in ("Base HumanL", "Base HumanR"):
            thigh = world_rot[prefix + "Thigh1"]
            thigh2 = q_mul(thigh, target.local_rot(prefix + "Thigh2"))
            calf = world_rot[prefix + "Calf"]
            foot = world_rot[prefix + "Foot"]
            out["rotations"][prefix + "Thigh1"].append(list(q_norm(q_mul(q_inv(pelvis_world), thigh))))
            out["rotations"][prefix + "Calf"].append(list(q_norm(q_mul(q_inv(thigh2), calf))))
            out["rotations"][prefix + "Foot"].append(list(q_norm(q_mul(q_inv(calf), foot))))
        out["pelvisPosition"].append(list(pelvis_position))

        # FK check: rebuilt EFT ankle vs the scaled source ankle (differences come from segment proportions)
        feet = {}
        debug_target = {"pelvis": list(pelvis_position)}
        for side, prefix in (("L", "Base HumanL"), ("R", "Base HumanR")):
            p = v_add(pelvis_position, q_rot(pelvis_world, q_rot(q_inv(target.rot("Base HumanPelvis")), v_sub(target.pos(prefix + "Thigh1"), target.pos("Base HumanPelvis")))))
            debug_target["hip" + side] = list(p)
            # walk the chain with rest segment offsets expressed in each bone's world frame
            chain = [("Thigh1", "Calf", "knee"), ("Calf", "Foot", "ankle"), ("Foot", "Toe", "toe")]
            for bone, child, label in chain:
                rest_offset = q_rot(q_inv(target.rot(prefix + bone)), v_sub(target.pos(prefix + child), target.pos(prefix + bone)))
                p = v_add(p, q_rot(world_rot[prefix + bone], rest_offset))
                debug_target[label + side] = list(p)
                if label == "ankle":
                    feet[side] = p
            source_ankle = pts[LEG_SPECS[side][2]]
            expected = (source_ankle[0] * scale, (source_ankle[1] - source_rest_ankle) * scale + target_rest_ankle, source_ankle[2] * scale)
            ankle_error.append(v_len(v_sub(feet[side], expected)))
        lift = lambda q: [q[0] * scale, (q[1] - source_rest_ankle) * scale + target_rest_ankle, q[2] * scale]
        debug_source = {"pelvis": lift(pts["pelvis"])}
        for side in ("L", "R"):
            hip_n, knee_n, ankle_n, ball_n, end_n = LEG_SPECS[side]
            debug_source.update({"hip" + side: lift(pts[hip_n]), "knee" + side: lift(pts[knee_n]), "ankle" + side: lift(pts[ankle_n]), "toe" + side: lift(pts[ball_n])})
        out.setdefault("debug", []).append({"target": debug_target, "source": debug_source})

        # contacts from the source: ball near its lowest height and barely moving in character space
        for side in ("L", "R"):
            ball = pts[LEG_SPECS[side][3]]
            out["contacts"][side].append(ball[1])

    for side in ("L", "R"):
        heights = out["contacts"][side]
        floor = min(heights)
        out["contacts"][side] = [1 if h <= floor + 0.03 else 0 for h in heights]
    out["checks"] = {
        "scale": round(scale, 4),
        "ankle_error_m_p50": round(sorted(ankle_error)[len(ankle_error) // 2], 4),
        "ankle_error_m_max": round(max(ankle_error), 4),
        "duration": round(duration, 3),
    }
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("glb", type=Path)
    parser.add_argument("skeleton", type=Path, help="EFT skeleton json (from skeleton.bundle)")
    parser.add_argument("output", type=Path)
    parser.add_argument("--clips", required=True, help="comma-separated clip names")
    parser.add_argument("--stance-scale", type=float, default=1.0, help="scale each ankle's sideways offset from the hip center (1 = source)")
    parser.add_argument("--knee-inward", type=float, default=0.0, help="degrees to turn each knee toward the other leg")
    parser.add_argument("--plant-scale", type=float, default=1.0, help="narrow the stance per plant, keeping sidestep length (strafes)")
    parser.add_argument("--foot-axis", choices=("bone", "ball"), default="bone", help="foot heading from the ankle bone's forward axis (default) or the ankle->ball segment (old behaviour, 14 deg toed out)")
    parser.add_argument("--end-stance", type=Path, help="Tarkov idle stance json; stretches the final steps of --end-stance-clips into it")
    parser.add_argument("--end-stance-clips", default="", help="comma-separated clips (e.g. stops) that should step into the idle stance")
    args = parser.parse_args()
    source = GltfSource(args.glb)
    target = EftSkeleton(args.skeleton)
    clips = []
    for name in args.clips.split(","):
        stance = json.loads(args.end_stance.read_text(encoding="utf-8")) if args.end_stance and name.strip() in args.end_stance_clips.split(",") else None
        clip = retarget_clip(source, target, name.strip(), args.stance_scale, args.knee_inward, stance, args.plant_scale, args.foot_axis)
        clips.append(clip)
        print(json.dumps({"clip": clip["name"], "frames": clip["frames"], **clip["checks"], **({"endStance": clip["endStance"]} if "endStance" in clip else {}), **({"plantStance": clip["plantStance"]} if "plantStance" in clip else {})}))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"schema": "manimal.motionmatching.posedb.v0", "bones": list(clips[0]["rotations"]), "clips": clips}), encoding="utf-8")


if __name__ == "__main__":
    main()
