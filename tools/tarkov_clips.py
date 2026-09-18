"""Convert Tarkov's own third-person locomotion AnimationClips into the retarget output alyx_retarget.py writes,
so export_posedb.py builds stride data for them unchanged.

Reads clips straight out of character_animations.bundle (UnityPy, generic transform curves: streamed cubic
keys, dense samples and constants), rebuilds the pelvis + leg chain in Root_Joint space and writes the same
per-frame fields the Alyx retarget does: root [x, z, yaw], pelvisPosition, the seven bone rotations the runtime
drives (Thigh2 held at rest, its animated twist folded into the calf), contacts, and debug joint positions.

Usage:
  python tools/tarkov_clips.py <character_animations.bundle> <eft_skeleton.json> <out.json> --clips run_aim_0,sprint_0
  python tools/tarkov_clips.py <bundle> <skeleton> - --verify-euler walk_aim_0   (rotation-order check on an euler clip)

Local-use only: the output is derived from the game's own animation data and belongs under tmp/.
"""
import argparse
import json
import math
import struct
import zlib
from pathlib import Path

import UnityPy

from alyx_retarget import EftSkeleton, q_inv, q_mul, q_norm, q_rot, v_add, v_sub, v_len

FPS = 30.0
BONES = ["Base HumanPelvis", "Base HumanLThigh1", "Base HumanLCalf", "Base HumanLFoot", "Base HumanRThigh1", "Base HumanRCalf", "Base HumanRFoot"]
CHAIN = ["Thigh1", "Thigh2", "Calf", "Foot", "Toe"]
TRANSFORM_ATTR = {1: ("localPosition", 3), 2: ("localRotation", 4), 3: ("localScale", 3), 4: ("localEulerAnglesRaw", 3)}
# humanoid muscle-curve bindings (customType 8, path 0): 7..13 are RootT.xyz / RootQ.xyzw
MUSCLE_NAMES = ["MotionT.x", "MotionT.y", "MotionT.z", "MotionQ.x", "MotionQ.y", "MotionQ.z", "MotionQ.w",
                "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w"]
CONTACT_HEIGHT = 0.03


# ---------- euler ----------
def q_axis(axis, degrees):
    half = math.radians(degrees) / 2
    s = math.sin(half)
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(half))


def q_euler(e, order="XYZ"):
    # order lists the axes as applied, first to last. these curves are NOT in Quaternion.Euler's zxy order:
    # only x-then-y-then-z (q = qz * qy * qx, the 3ds max default) keeps planted feet still in world space
    # and toes on the floor (--verify-euler)
    axes = {"X": ((1, 0, 0), e[0]), "Y": ((0, 1, 0), e[1]), "Z": ((0, 0, 1), e[2])}
    q = (0.0, 0.0, 0.0, 1.0)
    for name in order:
        q = q_mul(q_axis(*axes[name]), q)
    return q_norm(q)


# ---------- clip reading ----------
def parse_streamed(data_u32):
    # StreamedClip.data is a uint32 list of frames {time f32, numKeys i32, keys[]{index i32, coeff f32[4]}}
    raw = struct.pack("<%dI" % len(data_u32), *data_u32)
    frames, off = [], 0
    while off < len(raw):
        t, nk = struct.unpack_from("<fi", raw, off)
        off += 8
        keys = []
        for _ in range(nk):
            idx, a, b, c, d = struct.unpack_from("<iffff", raw, off)
            off += 20
            keys.append((idx, (a, b, c, d)))
        frames.append((t, keys))
    return frames


class Clip:
    """One AnimationClip object: samples any curve index at any time, resolves transform bindings by path hash."""

    def __init__(self, obj, tt, tos):
        self.path_id = obj.path_id
        self.name = tt["m_Name"]
        mc = tt["m_MuscleClip"]
        data = mc["m_Clip"]["data"]
        sc, dc, cc = data["m_StreamedClip"], data["m_DenseClip"], data["m_ConstantClip"]
        self.n_stream, self.n_dense = sc["curveCount"], dc["m_CurveCount"]
        self.curves = [[] for _ in range(self.n_stream)]
        for t, keys in parse_streamed(sc["data"]):
            for idx, coeff in keys:
                self.curves[idx].append((t, coeff))
        self.dense = (dc["m_FrameCount"], dc["m_BeginTime"], dc["m_SampleRate"], dc["m_SampleArray"])
        self.const = cc["data"]
        self.start, self.stop = mc["m_StartTime"], mc["m_StopTime"]
        self.loop_time = mc["m_LoopTime"]
        self.average_speed = mc["m_AverageSpeed"]
        self.bindings = []  # (kind, key, first curve index, count)
        ci = 0
        for b in tt["m_ClipBindingConstant"]["genericBindings"]:
            if b["typeID"] == 4 and b["customType"] == 0:
                prop, n = TRANSFORM_ATTR[b["attribute"]]
                self.bindings.append(("transform", (tos.get(b["path"], "0x%08x" % b["path"]), prop), ci, n))
                ci += n
            elif b["typeID"] == 95 and b["customType"] == 8:
                name = MUSCLE_NAMES[b["attribute"]] if b["attribute"] < len(MUSCLE_NAMES) else "muscle_%d" % b["attribute"]
                self.bindings.append(("root", name, ci, 1))
                ci += 1
            else:
                self.bindings.append(("other", None, ci, 1))
                ci += 1
        self.transform_count = sum(1 for k, _, _, _ in self.bindings if k == "transform")
        self.index = {key: (ci, n) for kind, key, ci, n in self.bindings if kind in ("transform", "root")}

    def value(self, ci, t):
        if ci < self.n_stream:
            curve = self.curves[ci]
            if not curve:
                return 0.0
            seg = curve[0]
            for s in curve:
                if s[0] <= t + 1e-6:
                    seg = s
                else:
                    break
            dt = max(0.0, t - seg[0])
            a, b, c, d = seg[1]
            return ((a * dt + b) * dt + c) * dt + d
        ci -= self.n_stream
        if ci < self.n_dense:
            count, begin, rate, arr = self.dense
            f = (t - begin) * rate
            f0 = max(0, min(count - 1, int(math.floor(f))))
            f1 = min(count - 1, f0 + 1)
            v0, v1 = arr[f0 * self.n_dense + ci], arr[f1 * self.n_dense + ci]
            return v0 + (v1 - v0) * (f - f0)
        return self.const[ci - self.n_dense]

    def sample(self, key, t):
        ci, n = self.index[key]
        return tuple(self.value(ci + k, t) for k in range(n))

    def has(self, key):
        return key in self.index

    def root_moves(self):
        keys = [k for k in ("RootT.x", "RootT.y", "RootT.z") if self.has(k)]
        if not keys:
            return False
        a = [self.sample(k, self.start) for k in keys]
        b = [self.sample(k, self.stop) for k in keys]
        return any(abs(x[0] - y[0]) > 1e-4 for x, y in zip(a, b))


def skeleton_tos(skeleton):
    # unity binds transform curves by crc32 of the path below the animator root ("Skeleton" here is that root)
    bones = skeleton.bones
    paths = {}
    for i, bone in enumerate(bones):
        parent = bone["Parent"]
        if parent < 0:
            continue
        paths[i] = bone["Name"] if paths.get(parent) is None else paths[parent] + "/" + bone["Name"]
    return {zlib.crc32(p.encode()) & 0xFFFFFFFF: p for p in paths.values()}


def load_clips(bundle_path, names, tos):
    env = UnityPy.load(str(bundle_path))
    wanted = set(names)
    found = {n: [] for n in names}
    container = {}
    for obj in env.objects:
        if obj.type.name == "AssetBundle":
            # which source fbx each object came from; a clip named after its own fbx is that fbx's main clip
            for path, info in obj.read_typetree()["m_Container"]:
                container[info["asset"]["m_PathID"]] = path.rsplit("/", 1)[-1].rsplit(".", 1)[0]
        if obj.type.name != "AnimationClip":
            continue
        tt = obj.read_typetree()
        if tt["m_Name"] in wanted:
            found[tt["m_Name"]].append(Clip(obj, tt, tos))
    for clips in found.values():
        for c in clips:
            c.fbx = container.get(c.path_id)
    return found


# ---------- conversion ----------
def local_pose(clip, path, t):
    """(position, rotation) of one bone's local transform at time t, from whichever curves the clip has."""
    position = clip.sample((path, "localPosition"), t) if clip.has((path, "localPosition")) else None
    if clip.has((path, "localRotation")):
        rotation = q_norm(clip.sample((path, "localRotation"), t))
    elif clip.has((path, "localEulerAnglesRaw")):
        rotation = q_euler(clip.sample((path, "localEulerAnglesRaw"), t))
    else:
        rotation = None
    return position, rotation


def signature(clip, paths):
    """Coarse content hash of the leg pose at a few times, to tell identical objects from different animations."""
    values = []
    for t in (clip.start, (clip.start + clip.stop) / 2, clip.stop):
        for name, path in paths.items():
            p, r = local_pose(clip, path, t)
            values += [round(v, 3) for v in (p or ())] + [round(v, 3) for v in (r or ())]
    return tuple(values)


def choose(candidates, paths, path_id=None):
    """The object whose root travels, on the fullest rig; among several, the main clip of the fbx named after it,
    then the animation most objects agree on (the bundle carries re-encoded copies of one clip beside a
    differently authored one under the same name, e.g. sprint_slow_20 inside sprint_slow_0.fbx), lowest path id
    on a tie. An explicit path id wins."""
    if path_id is not None:
        return next(c for c in candidates if c.path_id == path_id)
    moving = [c for c in candidates if c.root_moves()] or candidates
    fullest = max(c.transform_count for c in moving)
    moving = [c for c in moving if c.transform_count == fullest]
    own = [c for c in moving if (c.fbx or "").lower() == c.name.lower()]
    moving = own or moving
    families = {}
    for c in moving:
        families.setdefault(signature(c, paths), []).append(c)
    best = max(families.values(), key=lambda group: (len(group), -min(c.path_id for c in group)))
    return sorted(best, key=lambda c: c.path_id)[0]


def convert(clip, target, paths, out_name):
    frames = int(round((clip.stop - clip.start) * FPS)) + 1
    rest_pos = {n: tuple(target.bones[target.index[n]]["LocalPosition"]) for n in paths}
    rest_rot = {n: tuple(target.bones[target.index[n]]["LocalRotation"]) for n in paths}
    out = {"name": out_name, "fps": FPS, "frames": frames, "root": [], "pelvisPosition": [], "rotations": {b: [] for b in BONES},
           "contacts": {"L": [], "R": []}, "debug": [], "checks": {}}
    toe_height = {"L": [], "R": []}
    position_drift = 0.0
    root_yaw = 0.0
    for f in range(frames):
        t = min(clip.stop, clip.start + f / FPS)
        local = {}
        for name, path in paths.items():
            p, r = local_pose(clip, path, t)
            if p is None:
                p = rest_pos[name]
            elif name not in ("Root_Joint", "Base HumanPelvis"):
                position_drift = max(position_drift, v_len(v_sub(p, rest_pos[name])))
            if r is None:
                r = rest_rot[name]
            local[name] = (p, r)
        # root: RootT is the same curve as Root_Joint's position, in the animator root's (Skeleton's) frame, and
        # that frame is the character's facing. walk_aim_40/320 and sprint_20/340 yaw Root_Joint itself by the
        # travel direction (constant over the clip), so its rotation is folded into the pose and yaw stays 0 —
        # otherwise their hips would read as bladed the wrong way and the exporter would call them forward clips
        root_p = clip.sample("RootT.x", t)[0], clip.sample("RootT.z", t)[0]
        root_r = local["Root_Joint"][1]
        forward = q_rot(root_r, (0.0, 0.0, 1.0))
        root_yaw = max(root_yaw, abs(math.degrees(math.atan2(forward[0], forward[2]))), key=abs)
        out["root"].append([root_p[0], root_p[1], 0.0])

        pelvis_p, pelvis_r = local["Base HumanPelvis"]
        pelvis_p, pelvis_r = q_rot(root_r, pelvis_p), q_norm(q_mul(root_r, pelvis_r))
        out["pelvisPosition"].append(list(pelvis_p))
        out["rotations"]["Base HumanPelvis"].append(list(pelvis_r))
        debug = {"pelvis": list(pelvis_p)}
        for side in ("L", "R"):
            prefix = f"Base Human{side}"
            # fk down the chain in character space, with the clip's own thigh2 twist
            p, r = pelvis_p, pelvis_r
            world = {}
            for bone in CHAIN:
                lp, lr = local[prefix + bone]
                p = v_add(p, q_rot(r, lp))
                r = q_norm(q_mul(r, lr))
                world[bone] = (p, r)
            thigh1 = world["Thigh1"][1]
            # the runtime keeps thigh2 at rest, so the calf carries thigh2's animated twist
            thigh2_rest = q_norm(q_mul(thigh1, rest_rot[prefix + "Thigh2"]))
            out["rotations"][prefix + "Thigh1"].append(list(q_norm(q_mul(q_inv(pelvis_r), thigh1))))
            out["rotations"][prefix + "Calf"].append(list(q_norm(q_mul(q_inv(thigh2_rest), world["Calf"][1]))))
            out["rotations"][prefix + "Foot"].append(list(q_norm(q_mul(q_inv(world["Calf"][1]), world["Foot"][1]))))
            debug["hip" + side] = list(world["Thigh1"][0])
            debug["knee" + side] = list(world["Calf"][0])
            debug["ankle" + side] = list(world["Foot"][0])
            debug["toe" + side] = list(world["Toe"][0])
            toe_height[side].append(world["Toe"][0][1])
        out["debug"].append({"target": debug, "source": dict(debug)})
    for side in ("L", "R"):
        floor = min(toe_height[side])
        out["contacts"][side] = [1 if h <= floor + CONTACT_HEIGHT else 0 for h in toe_height[side]]
    out["checks"] = {
        "scale": 1.0,
        "duration": round(clip.stop - clip.start, 4),
        "path_id": clip.path_id,
        "source_clip": clip.name,
        "loopTime": clip.loop_time,
        "averageSpeed": [round(clip.average_speed[k], 4) for k in ("x", "y", "z")],
        "curves": "euler" if clip.has((paths["Base HumanPelvis"], "localEulerAnglesRaw")) else "quat",
        "leg_local_position_drift_m": round(position_drift, 6),
        "root_joint_yaw_deg_folded": round(root_yaw, 2),
        "toe_floor_m": {side: round(min(toe_height[side]), 4) for side in ("L", "R")},
    }
    return out


def chain_paths(target):
    """bone name -> animation path, for the pelvis and both leg chains."""
    paths = {}
    for i, bone in enumerate(target.bones):
        parts, j = [], i
        while target.bones[j]["Parent"] >= 0:
            parts.append(target.bones[j]["Name"])
            j = target.bones[j]["Parent"]
        paths[bone["Name"]] = "/".join(reversed(parts))
    names = ["Root_Joint", "Base HumanPelvis"] + [f"Base Human{s}{b}" for s in "LR" for b in CHAIN]
    return {n: paths[n] for n in names}


def verify_euler(clip, target, paths):
    """Per rotation order: how fast a planted toe drifts in world space and how high the lowest toe sits.

    The right order keeps a planted toe still (< 0.1 m/s) with its floor at the rest toe height (~0.02 m);
    a wrong order slides it at root speed or lifts the whole leg.
    """
    frames = int(round((clip.stop - clip.start) * FPS)) + 1
    rest_pos = {n: tuple(target.bones[target.index[n]]["LocalPosition"]) for n in paths}
    report = {}
    for order in ("XYZ", "XZY", "YXZ", "YZX", "ZXY", "ZYX"):
        result = {}
        for side in ("L", "R"):
            toes = []
            for f in range(frames):
                t = clip.start + f / FPS
                p, r = local_pose(clip, paths["Base HumanPelvis"], t)
                r = r if r is not None else q_euler(clip.sample((paths["Base HumanPelvis"], "localEulerAnglesRaw"), t), order)
                if clip.has((paths["Base HumanPelvis"], "localEulerAnglesRaw")):
                    r = q_euler(clip.sample((paths["Base HumanPelvis"], "localEulerAnglesRaw"), t), order)
                for bone in CHAIN:
                    path = paths[f"Base Human{side}{bone}"]
                    lp = clip.sample((path, "localPosition"), t) if clip.has((path, "localPosition")) else rest_pos[f"Base Human{side}{bone}"]
                    if clip.has((path, "localEulerAnglesRaw")):
                        lr = q_euler(clip.sample((path, "localEulerAnglesRaw"), t), order)
                    else:
                        lr = local_pose(clip, path, t)[1]
                    p = v_add(p, q_rot(r, lp))
                    r = q_norm(q_mul(r, lr))
                root = clip.sample("RootT.x", t)[0], clip.sample("RootT.z", t)[0]
                toes.append((root[0] + p[0], p[1], root[1] + p[2]))
            floor = min(p[1] for p in toes)
            speeds = sorted(math.hypot(toes[f][0] - toes[f - 1][0], toes[f][2] - toes[f - 1][2]) * FPS
                            for f in range(1, frames) if toes[f][1] <= floor + CONTACT_HEIGHT)
            result[side] = {"planted_toe_speed_mps": round(speeds[len(speeds) // 2], 3) if speeds else None, "toe_floor_m": round(floor, 3)}
        report[order] = result
    return {"path_id": clip.path_id, "by_order": report}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("skeleton", type=Path, help="EFT skeleton json (rest pose; also resolves the curve path hashes)")
    parser.add_argument("output", type=Path, help="retarget-format json ('-' with --verify-euler)")
    parser.add_argument("--clips", default="", help="comma-separated clip names; outputs are named tarkov_<name lowercased>")
    parser.add_argument("--path-id", action="append", default=[], help="name=path_id to pick a specific object when a name is shared")
    parser.add_argument("--verify-euler", help="euler clip to test every rotation order on (planted-toe drift and toe floor height)")
    args = parser.parse_args()

    target = EftSkeleton(args.skeleton)
    paths = chain_paths(target)
    tos = skeleton_tos(target)
    names = [n.strip() for n in args.clips.split(",") if n.strip()]
    if args.verify_euler:
        names.append(args.verify_euler)
    found = load_clips(args.bundle, names, tos)
    if args.verify_euler:
        print(json.dumps({"verify_euler": args.verify_euler, **verify_euler(choose(found[args.verify_euler], paths), target, paths)}))
        if str(args.output) == "-":
            return
    forced = {spec.split("=")[0]: int(spec.split("=")[1]) for spec in args.path_id}
    clips = []
    for name in [n for n in names if n != args.verify_euler or n in args.clips.split(",")]:
        candidates = found[name]
        if not candidates:
            raise SystemExit(f"clip not found: {name}")
        clip = choose(candidates, paths, forced.get(name))
        converted = convert(clip, target, paths, "tarkov_" + name.lower())
        converted["checks"]["candidates"] = [{"path_id": c.path_id, "fbx": c.fbx, "transforms": c.transform_count, "root_moves": c.root_moves(),
                                              "same_animation": signature(c, paths) == signature(clip, paths)} for c in candidates]
        clips.append(converted)
        print(json.dumps({"clip": converted["name"], "frames": converted["frames"], **converted["checks"]}))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"schema": "manimal.motionmatching.posedb.v0", "bones": BONES, "clips": clips}), encoding="utf-8")


if __name__ == "__main__":
    main()
