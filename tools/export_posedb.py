"""Write the compact pose database the plugin reads, from a retargeted posedb (with debug joints).

Clips are either seamless loops cut from a window (--loop clip:start:end) or whole one-shots (--oneshot clip)
such as starts and stops. Every clip carries a smoothed per-frame root speed for speed-matched playback.
--role clip:roles:gait marks a one-shot for directional playback (roles: start, stop, cut joined with +; gait: walk
or run); its travel direction(s) relative to facing are measured from root motion, so clip names are never trusted.

Every clip also carries Valve-style foot motion data (see stride_data.py): per foot, stance-to-stance foot cycles
and per-frame stride-relative FootBase offsets, built from heel and toe sole points that ride the Foot bone.

--augment takes an already exported database whose build command is lost: each clip is located by content in
the retarget outputs given (positional plus --merge), resliced the same way, and written out with stride data.

Local-use only: the output contains Valve animation data and belongs under tmp/ or a local game install.
"""
import argparse
import json
import math
from pathlib import Path

import stride_data
from alyx_retarget import EftSkeleton, q_inv, q_mul, q_norm, q_rot, v_add, v_len, v_sub

JOINTS = ["hipL", "kneeL", "ankleL", "toeL", "hipR", "kneeR", "ankleR", "toeR"]
SPEED_SMOOTH_FRAMES = 2
# sole points, fixed in the Foot bone: the heel sits under and slightly behind the ankle, the toe tip past
# the Toe bone; both on the floor in the rest pose (Root_Joint is at ground level)
HEEL_BEHIND = 0.03
TOE_AHEAD = 0.04


def pose_vector(debug):
    target = debug["target"]
    pelvis = target["pelvis"]
    # pelvis-relative so the loop search ignores where the hips are, only what the legs are doing
    return [c - p for joint in JOINTS for c, p in zip(target[joint], pelvis)]


def distance(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)) / (len(a) / 3))


def root_speeds(root, fps):
    raw = [0.0] + [math.dist(root[i][:2], root[i - 1][:2]) * fps for i in range(1, len(root))]
    if len(raw) > 1:
        raw[0] = raw[1]
    out = []
    for i in range(len(raw)):
        window = raw[max(0, i - SPEED_SMOOTH_FRAMES):i + SPEED_SMOOTH_FRAMES + 1]
        out.append(sum(window) / len(window))
    return out


def root_velocities(root, fps):
    """Per-frame character-local root velocity (x right, z forward), smoothed like root_speeds."""
    raw = []
    for i in range(len(root)):
        a, b = root[max(0, i - 1)], root[min(len(root) - 1, max(1, i))]
        vx, vz = (b[0] - a[0]) * fps, (b[1] - a[1]) * fps
        yaw = b[2]
        raw.append((vx * math.cos(yaw) - vz * math.sin(yaw), vx * math.sin(yaw) + vz * math.cos(yaw)))
    out = []
    for i in range(len(raw)):
        window = raw[max(0, i - SPEED_SMOOTH_FRAMES):i + SPEED_SMOOTH_FRAMES + 1]
        out.append([sum(v[0] for v in window) / len(window), sum(v[1] for v in window) / len(window)])
    return out


def heading(velocities):
    x = sum(v[0] for v in velocities)
    z = sum(v[1] for v in velocities)
    return round(math.degrees(math.atan2(x, z)), 1)


TRANSITION_ROLES = ("cut", "sprint_enter", "sprint_exit", "sprint_start", "sprint_stop")


def yaw_progress(root):
    """Body yaw turned since the first frame, unwrapped, in degrees."""
    out = [0.0]
    for i in range(1, len(root)):
        step = math.degrees(root[i][2] - root[i - 1][2])
        out.append(out[-1] + (step + 540.0) % 360.0 - 180.0)
    return out


def describe_roles(clip, roles, gait):
    """Travel direction for starts/stops (fastest stretch) and from/to directions for transitions.

    Sprint transitions turn the body, so they also carry how far it rotates; playback follows that rotation.
    """
    velocity = clip["rootVelocity"]
    speed = [math.hypot(*v) for v in velocity]
    peak = max(speed)
    info = {"roles": roles, "gait": gait}
    fast = [v for v, s in zip(velocity, speed) if s >= 0.6 * peak]
    info["moveYaw"] = heading(fast)
    turning = [r for r in roles if r in TRANSITION_ROLES]
    if turning:
        # the reversal is the slowest frame between the clip's fast in and out stretches
        first = next(i for i, s in enumerate(speed) if s >= 0.6 * peak)
        last = len(speed) - 1 - next(i for i, s in enumerate(reversed(speed)) if s >= 0.6 * peak)
        turn = min(range(first, last + 1), key=lambda i: speed[i]) if "cut" in roles else (first + last) // 2
        before = [v for v, s in zip(velocity[:turn], speed[:turn]) if s >= 0.5 * peak]
        after = [v for v, s in zip(velocity[turn:], speed[turn:]) if s >= 0.5 * peak]
        progress = clip["yawProgress"]
        info.update({"fromYaw": heading(before) if before else 0.0, "toYaw": heading(after) if after else 0.0,
                     "turnFrame": turn, "yawChange": round(progress[-1] - progress[0], 1)})
    return info


def find_loop(clip, window, min_loop, max_loop):
    fps = clip["fps"]
    start_s, end_s = (float(x) for x in window.split(":"))
    lo, hi = int(start_s * fps), min(clip["frames"] - 1, int(end_s * fps))
    vectors = [pose_vector(d) for d in clip["debug"]]
    best = None
    for a in range(lo, hi):
        for length in range(int(min_loop * fps), int(max_loop * fps) + 1):
            b = a + length
            if b > hi:
                break
            # the frame after the loop end should look like the start, so playback wraps from b-1 to a
            d = distance(vectors[a], vectors[b])
            if best is None or d < best[0]:
                best = (d, a, b)
    return best


class SolePoints:
    """Heel and toe-tip points per frame, rebuilt through the EFT leg chain from a retarget clip's rotations."""

    def __init__(self, skeleton_path):
        target = EftSkeleton(skeleton_path)
        self._thigh2 = {}
        self._offsets = {}
        for side in ("L", "R"):
            foot, toe = f"Base Human{side}Foot", f"Base Human{side}Toe"
            ankle_rest, toe_rest, foot_rot = target.pos(foot), target.pos(toe), target.rot(foot)
            heel_point = (ankle_rest[0], 0.0, ankle_rest[2] - HEEL_BEHIND)
            toe_point = (toe_rest[0], 0.0, toe_rest[2] + TOE_AHEAD)
            in_foot = lambda p: q_rot(q_inv(foot_rot), v_sub(p, ankle_rest))
            self._thigh2[side] = target.local_rot(f"Base Human{side}Thigh2")
            self._offsets[side] = (in_foot(heel_point), in_foot(toe_point), in_foot(toe_rest))
        # the same vectors, for the runtime: foot.rotation * offset + foot.position is the sole point in world
        self.in_foot = {side: {"heel": [round(v, 5) for v in self._offsets[side][0]], "toe": [round(v, 5) for v in self._offsets[side][1]]} for side in ("L", "R")}
        self.max_toe_error = 0.0

    def frame(self, clip, f, side):
        r = clip["rotations"]
        world = q_norm(q_mul(q_mul(q_mul(q_mul(tuple(r["Base HumanPelvis"][f]), tuple(r[f"Base Human{side}Thigh1"][f])), self._thigh2[side]),
                                    tuple(r[f"Base Human{side}Calf"][f])), tuple(r[f"Base Human{side}Foot"][f])))
        target = clip["debug"][f]["target"]
        ankle = tuple(target["ankle" + side])
        heel_off, toe_off, toe_bone_off = self._offsets[side]
        # the chain is the retarget's own, so its toe must land on the retarget's toe; anything else is a bug
        self.max_toe_error = max(self.max_toe_error, v_len(v_sub(v_add(ankle, q_rot(world, toe_bone_off)), tuple(target["toe" + side]))))
        return v_add(ankle, q_rot(world, heel_off)), v_add(ankle, q_rot(world, toe_off))


def stride_block(source, sole, a, b, loop, stride_scale=1.0):
    """Foot motion data for frames [a, b) of a retarget clip. The retarget scales joints to EFT leg length but
    leaves the root in source metres, so the root is scaled here too: otherwise every planted foot would creep
    by (1 - scale) of the root speed. stride_scale shrinks the root travel with the feet (see --stride-scale)."""
    scale = source["checks"]["scale"] * stride_scale
    root = [(r[0] * scale, r[1] * scale, r[2]) for r in source["root"][a:b + (1 if loop else 0)]]
    analyses = {}
    for side in ("L", "R"):
        points = [sole.frame(source, f, side) for f in range(a, b)]
        analyses[side] = stride_data.analyse_foot(root, [p[0] for p in points], [p[1] for p in points], source["fps"], loop)
    return {
        "L": analyses["L"],
        "R": analyses["R"],
        "stepsRemaining": None if loop else stride_data.steps_remaining([analyses["L"], analyses["R"]], b - a),
    }


def slice_clip(clip, bones, name, a, b, loop, sole=None, stride_scale=1.0):
    root = clip["root"][a:b + (1 if loop else 0)]
    speeds = [s * stride_scale for s in root_speeds(clip["root"], clip["fps"])[a:b]]
    travelled = sum(math.dist(root[i][:2], root[i - 1][:2]) for i in range(1, len(root))) * stride_scale
    seconds = max((len(root) - 1) / clip["fps"], 1e-6)
    out = {
        "name": name,
        "fps": clip["fps"],
        "frames": b - a,
        "loop": loop,
        "speedMetersPerSecond": travelled / seconds,
        "rootSpeed": speeds,
        "rootVelocity": [[v[0] * stride_scale, v[1] * stride_scale] for v in root_velocities(clip["root"], clip["fps"])[a:b]],
        "yawProgress": yaw_progress(clip["root"])[a:b],
        # Root_Joint-space ankles, for matching entry frames against the pose Tarkov's animator is showing
        "feet": [d["target"]["ankleL"] + d["target"]["ankleR"] for d in clip["debug"][a:b]],
        "rotations": {bone: clip["rotations"][bone][a:b] for bone in bones},
        "pelvisPosition": clip["pelvisPosition"][a:b],
        "contacts": {side: clip["contacts"][side][a:b] for side in ("L", "R")},
        # stops retargeted with --end-stance finish in Tarkov's idle stance, so the plugin hands straight back
        "endsInTarkovIdle": "endStance" in clip and b == clip["frames"],
    }
    if sole is not None:
        out["stride"] = stride_block(clip, sole, a, b, loop, stride_scale)
    out["strideScale"] = stride_scale
    return out


def stride_summary(clip):
    stride = clip.get("stride")
    if not stride:
        return {}
    out = {}
    for side in ("L", "R"):
        cycles = [c for c in stride[side]["cycles"] if not c["stationary"]]
        grounded = stride[side]["frames"]["grounded"]
        out[side] = {"steps": len(cycles), "stride_m": [c["strideLength"] for c in cycles], "planted_frames": sum(grounded)}
    return out


def locate(existing, sources):
    """Find the retarget clip and frame window an exported clip was cut from, by matching its pelvis track."""
    pelvis = existing["pelvisPosition"]
    n = len(pelvis)
    for source in sources:
        if not existing["name"].startswith(source["name"]) or source["frames"] < n:
            continue
        for a in range(source["frames"] - n + 1):
            if all(abs(x - y) < 1e-6 for x, y in zip(source["pelvisPosition"][a], pelvis[0])) and \
                    all(abs(x - y) < 1e-6 for f in range(n) for x, y in zip(source["pelvisPosition"][a + f], pelvis[f])):
                return source, a
    return None, None


def augment(existing_path, sources, bones, sole, stride_scales=None):
    stride_scales = stride_scales or {}
    existing = json.loads(existing_path.read_text(encoding="utf-8"))
    clips, spec = [], []
    for old in existing["clips"]:
        source, a = locate(old, sources)
        if source is None:
            # a re-retargeted clip no longer matches the old database by content; a whole-clip export (the
            # transitions all are) is the same window by name, and the last file given wins as elsewhere
            whole = [s for s in sources if s["name"] == old["name"] and s["frames"] == old["frames"]]
            if not whole:
                raise SystemExit(f"{old['name']}: no retarget clip given contains this exact frame window; pass its source with --merge")
            source, a = whole[-1], 0
            print(json.dumps({"clip": old["name"], "note": "matched by name (re-retargeted), whole clip", "source": source["_file"]}))
        b = a + old["frames"]
        clip = slice_clip(source, bones, old["name"], a, b, old["loop"], sole, stride_scales.get(old["name"], 1.0))
        if old.get("roles"):
            clip.update(describe_roles(clip, old["roles"], old["gait"]))
        drift = {k: (old[k], clip.get(k)) for k in ("speedMetersPerSecond", "moveYaw", "fromYaw", "toYaw", "turnFrame", "yawChange", "endsInTarkovIdle")
                 if k in old and not _same(old[k], clip.get(k))}
        clips.append(clip)
        spec.append({"clip": old["name"], "source": source["_file"], "frames": [a, b], "loop": old["loop"], "roles": old.get("roles"), "gait": old.get("gait")})
        print(json.dumps({"clip": old["name"], "source": source["_file"], "window": [a, b], "loop": old["loop"], **({"drift": drift} if drift else {}), "stride": stride_summary(clip)}))
    return clips, spec


def _same(a, b):
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(a - b) < 1e-6
    return a == b


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("posedb", type=Path, help="retarget output with debug joints")
    parser.add_argument("skeleton", type=Path, help="EFT skeleton json (for Thigh2 rest locals and sole points)")
    parser.add_argument("output", type=Path)
    parser.add_argument("--loop", action="append", default=[], help="clip:start:end seconds to search for a seamless loop")
    parser.add_argument("--oneshot", action="append", default=[], help="clip to export whole, e.g. a start or stop")
    parser.add_argument("--role", action="append", default=[], help="clip:roles:gait, e.g. new_short_hop_0a_tight_e:start+stop:run")
    parser.add_argument("--merge", action="append", default=[], type=Path,
                        help="another retarget output whose clips can also be named (e.g. heavy soldier strafes retargeted with other stance settings)")
    parser.add_argument("--slice", action="append", default=[],
                        help="alias=source:roles:gait:start:end (seconds): cut a one-shot clip out of a take, e.g. a walking start "
                             "from the first two seconds of a motion_match take; roles as for --role (start, stop, start+stop, cut)")
    parser.add_argument("--stride-scale", action="append", default=[],
                        help="clip:factor, shrinks a clip's root travel to match a retarget done with --stance-scale factor: shorter steps at a quicker cadence for the same bot speed (wide heavy sidesteps)")
    parser.add_argument("--augment", type=Path, help="existing exported database to rebuild with stride data (clips located by content in the retarget outputs)")
    parser.add_argument("--min-loop", type=float, default=0.8)
    parser.add_argument("--max-loop", type=float, default=1.6)
    args = parser.parse_args()

    db = json.loads(args.posedb.read_text(encoding="utf-8"))
    sources = []
    for path in [args.posedb] + args.merge:
        other = json.loads(path.read_text(encoding="utf-8")) if path != args.posedb else db
        if other["bones"] != db["bones"]:
            parser.error(f"{path} retargets a different bone list")
        for clip in other["clips"]:
            clip["_file"] = str(path)
            sources.append(clip)
    # later files win a name, as before; augment matches by content and needs every candidate
    by_name = {c["name"]: c for c in sources}
    stride_scales = {spec.split(":")[0]: float(spec.split(":")[1]) for spec in args.stride_scale}
    sole = SolePoints(args.skeleton)
    clips = []
    spec = None
    if args.augment:
        clips, spec = augment(args.augment, sources, db["bones"], sole, stride_scales)
    for spec_text in args.loop:
        name, window = spec_text.split(":", 1)
        error, a, b = find_loop(by_name[name], window, args.min_loop, args.max_loop)
        clip = slice_clip(by_name[name], db["bones"], f"{name}_loop_{a}_{b}", a, b, True, sole)
        clips.append(clip)
        print(json.dumps({"clip": clip["name"], "frames": clip["frames"], "loop_seam_error_m": round(error, 4), "speed_mps": round(clip["speedMetersPerSecond"], 3), "stride": stride_summary(clip)}))
    for name in args.oneshot:
        clip = slice_clip(by_name[name], db["bones"], name, 0, by_name[name]["frames"], False, sole)
        clips.append(clip)
        print(json.dumps({"clip": name, "frames": clip["frames"], "peak_speed_mps": round(max(clip["rootSpeed"]), 2), "end_speed_mps": round(clip["rootSpeed"][-1], 2), "stride": stride_summary(clip)}))

    for spec_text in args.slice:
        alias, rest = spec_text.split("=", 1)
        name, roles, gait, start, end = rest.split(":")
        source = by_name[name]
        a = max(0, int(round(float(start) * source["fps"])))
        b = min(source["frames"], int(round(float(end) * source["fps"])))
        clip = slice_clip(source, db["bones"], alias, a, b, False, sole, stride_scales.get(alias, 1.0))
        clip.update(describe_roles(clip, roles.split("+"), gait))
        clips.append(clip)
        print(json.dumps({"clip": alias, "from": name, "window": [a, b], "roles": clip["roles"], "gait": gait, "peak_speed_mps": round(max(clip["rootSpeed"]), 2),
                          "end_speed_mps": round(clip["rootSpeed"][-1], 2), **{k: clip[k] for k in ("moveYaw",) if k in clip}, "stride": stride_summary(clip)}))

    for spec_text in args.role:
        # clip:roles:gait, optionally :start:end seconds to bound the loop search of a cycle role
        parts = spec_text.split(":")
        name, roles, gait = parts[:3]
        source = by_name[name]
        if "cycle" in roles:
            # cycles are seamless loops: a transition that runs out of frames freezes, so it hands into one
            seconds = source["frames"] / source["fps"]
            if len(parts) < 5 and ("loop" in name or "cycle" in name):
                # authored as a loop already (one period, ~1 s): its last frame is the return point
                vectors = [pose_vector(d) for d in source["debug"]]
                a, b = 0, source["frames"] - 1
                error = distance(vectors[a], vectors[b])
            else:
                window = f"{parts[3]}:{parts[4]}" if len(parts) >= 5 else f"{0.2:.2f}:{seconds - 0.2:.2f}"
                error, a, b = find_loop(source, window, args.min_loop, args.max_loop)
            clip = slice_clip(source, db["bones"], name, a, b, True, sole, stride_scales.get(name, 1.0))
            print(json.dumps({"clip": name, "loop_frames": b - a, "loop_seam_error_m": round(error, 4), "speed_mps": round(clip["speedMetersPerSecond"], 2)}))
        else:
            # starts, stops and cuts take the same --stride-scale as their loops (review: only cycles were scaled,
            # so the lateral starts kept 1.46 m strides against 0.7-scaled lateral loops)
            clip = slice_clip(source, db["bones"], name, 0, source["frames"], False, sole, stride_scales.get(name, 1.0))
        clip.update(describe_roles(clip, roles.split("+"), gait))
        clips.append(clip)
        print(json.dumps({"clip": name, "frames": clip["frames"], "roles": clip["roles"], "gait": gait, "peak_speed_mps": round(max(clip["rootSpeed"]), 2),
                          **{k: clip[k] for k in ("moveYaw", "fromYaw", "toYaw", "turnFrame", "yawChange") if k in clip}, "stride": stride_summary(clip)}))

    skeleton = json.loads(args.skeleton.read_text(encoding="utf-8"))["Bones"]
    rest_local = {bone["Name"]: bone["LocalRotation"] for bone in skeleton if bone["Name"] in ("Base HumanLThigh2", "Base HumanRThigh2")}
    out = {
        "schema": "manimal.motionmatching.posedb.v1",
        "source": "local Half-Life: Alyx extraction; do not redistribute",
        "bones": db["bones"],
        "restLocal": rest_local,
        "solePoints": {"heelBehindAnkle": HEEL_BEHIND, "toeAheadOfToeBone": TOE_AHEAD, **sole.in_foot},
        "clips": clips,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(out), encoding="utf-8")
    if spec is not None:
        spec_path = args.output.with_suffix(".spec.json")
        spec_path.write_text(json.dumps(spec, indent=1), encoding="utf-8")
        print(json.dumps({"spec": str(spec_path)}))
    print(json.dumps({"output": str(args.output), "bytes": args.output.stat().st_size, "clips": len(clips), "max_fk_toe_error_m": round(sole.max_toe_error, 6)}))


if __name__ == "__main__":
    main()
