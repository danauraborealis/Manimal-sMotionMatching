"""Movement analysis: the same gait metrics (tools/gait_metrics.py) from three sources.

    python tools/movement_analysis.py capture <capture.json or directory> [--label name]
    python tools/movement_analysis.py glb <model.glb> <clip,clip,...>
    python tools/movement_analysis.py retarget <posedb_v0.json> [clip,...]

capture: builds character-frame tracks from the Bones block (after_lock stage when present, else after_visual),
splits the run into moving segments (speed > 0.3 m/s for > 0.5 s) and reports per segment plus the first and
last 0.8 s of each segment (the start and the stop). glb: the Alyx source skeleton, scaled to EFT leg length like
the retarget, so numbers compare directly. retarget: the retarget's own EFT-space joints (no torso).
Writes JSON to stdout. Local-use research over Valve data; nothing is redistributed.
"""
import json
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gait_metrics
from alyx_retarget import EftSkeleton, GltfSource, q_inv, q_mul, q_norm, q_rot, to_unity, v_len, v_sub

EFT_SKELETON = Path(__file__).resolve().parents[1] / "tmp/alyx/eft_skeleton_bundle.json"
MOVING_SPEED = 0.3
MIN_SEGMENT_SECONDS = 0.5
EDGE_SECONDS = 0.8


def _rot_forward(q):
    return q_rot(q, (0.0, 0.0, 1.0))


# ---------- capture ----------
def capture_track(document, stage_preference=("after_lock", "after_visual")):
    samples = document["Samples"]
    stages = {s.get("Stage") for s in samples}
    stage = next(s for s in stage_preference if s in stages)
    rows = [s for s in samples if s.get("Stage") == stage and s.get("Bones")]
    if not rows:
        raise SystemExit("capture has no Bones block on stage " + stage + "; recapture with the current plugin")
    skeleton = EftSkeleton(EFT_SKELETON)
    rest = {name: skeleton.rot(name) for name in ("Base HumanPelvis", "Base HumanSpine3", "Base HumanRibcage", "Base HumanLFoot", "Base HumanRFoot")}
    times = [s["Time"] for s in rows]
    fps = (len(rows) - 1) / max(times[-1] - times[0], 1e-6)
    track = {"fps": fps, "root": [], "pelvis": [], "pelvisFwd": [], "torsoFwd": [], "footFwdL": [], "footFwdR": [], "time": times, "speed": []}
    for key in ("hipL", "hipR", "kneeL", "kneeR", "ankleL", "ankleR", "toeL", "toeR"):
        track[key] = []
    names = {"hipL": "Base HumanLThigh1", "kneeL": "Base HumanLCalf", "ankleL": "Base HumanLFoot", "toeL": "Base HumanLToe",
             "hipR": "Base HumanRThigh1", "kneeR": "Base HumanRCalf", "ankleR": "Base HumanRFoot", "toeR": "Base HumanRToe"}
    for s in rows:
        bones = {b["N"]: b for b in s["Bones"] if b}
        rj = s.get("RootJoint") or {}
        yaw = math.radians(s.get("RootJointYaw", s.get("BodyYaw", 0.0)))
        track["root"].append((rj.get("X", 0.0), rj.get("Z", 0.0), yaw))
        track["pelvis"].append(tuple(bones["Base HumanPelvis"]["P"]))
        for key, bone in names.items():
            track[key].append(tuple(bones[bone]["P"]))
        def fwd(bone):
            q = tuple(bones[bone]["R"])
            return _rot_forward(q_norm(q_mul(q, q_inv(rest[bone]))))
        track["pelvisFwd"].append(fwd("Base HumanPelvis"))
        track["torsoFwd"].append(fwd("Base HumanRibcage"))
        track["footFwdL"].append(fwd("Base HumanLFoot"))
        track["footFwdR"].append(fwd("Base HumanRFoot"))
        track["speed"].append(s.get("Movement", {}).get("PlayerSpeed"))
    return track, stage


def _slice(track, a, b):
    out = {"fps": track["fps"]}
    for k, v in track.items():
        if isinstance(v, list):
            out[k] = v[a:b]
    return out


def segments(track):
    """Moving segments from root speed."""
    root = track["root"]
    fps = track["fps"]
    speed = [0.0] + [math.hypot(root[f][0] - root[f - 1][0], root[f][1] - root[f - 1][1]) * fps for f in range(1, len(root))]
    # smooth 0.2 s
    w = max(1, int(0.2 * fps))
    smooth = [sum(speed[max(0, f - w):f + 1]) / len(speed[max(0, f - w):f + 1]) for f in range(len(speed))]
    out, start = [], None
    for f, v in enumerate(smooth + [0.0]):
        if v > MOVING_SPEED and start is None:
            start = f
        elif v <= MOVING_SPEED and start is not None:
            if (f - start) / fps >= MIN_SEGMENT_SECONDS:
                out.append((start, f))
            start = None
    return out


def analyse_capture(path, label):
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    track, stage = capture_track(document)
    fps = track["fps"]
    edge = int(EDGE_SECONDS * fps)
    report = {"label": label, "file": str(path), "stage": stage, "fps": round(fps, 1), "segments": []}
    for a, b in segments(track):
        seg = _slice(track, a, b)
        entry = {"start_s": round(track["time"][a] - track["time"][0], 2), "seconds": round((b - a) / fps, 2), "steady": gait_metrics.analyse(_slice(track, a + edge, max(a + edge + 1, b - edge)), "steady") if b - a > 3 * edge else None,
                 "start": gait_metrics.analyse(_slice(track, a, min(b, a + edge)), "start"), "stop": gait_metrics.analyse(_slice(track, max(a, b - edge), b), "stop"),
                 "whole": gait_metrics.analyse(seg, "whole")}
        report["segments"].append(entry)
    return report


# ---------- glb ----------
SOURCE_JOINTS = {"pelvis": "pelvis", "hipL": "leg_upper_L", "kneeL": "leg_lower_L", "ankleL": "ankle_L", "toeL": "ball_L",
                 "hipR": "leg_upper_R", "kneeR": "leg_lower_R", "ankleR": "ankle_R", "toeR": "ball_R"}


def glb_track(source, clip_name, scale):
    tracks, duration = source.tracks(clip_name)
    fps = 30.0
    frames = max(2, int(round(duration * fps)) + 1)
    rest_cache = {}
    rest = {n: source.world(n, {}, 0.0, rest_cache)[1] for n in ("pelvis", "spine_3", "ankle_L", "ankle_R")}
    rest_root_r = source.world("root_motion", {}, 0.0, rest_cache)[1]
    out = {"fps": fps, "root": [], "pelvis": [], "pelvisFwd": [], "torsoFwd": [], "footFwdL": [], "footFwdR": []}
    for key in SOURCE_JOINTS:
        if key != "pelvis":
            out[key] = []
    for f in range(frames):
        t = min(duration, f / fps)
        cache = {}
        root_p, root_r = source.world("root_motion", tracks, t, cache)
        root_u = to_unity(root_p)
        forward = to_unity(q_rot(root_r, (0.0, 0.0, 1.0)))
        yaw = math.atan2(forward[0], forward[2])
        inv_yaw = (0.0, -math.sin(yaw / 2), 0.0, math.cos(yaw / 2))
        out["root"].append((root_u[0] * scale, root_u[2] * scale, yaw))
        def local(name):
            p = to_unity(source.world(name, tracks, t, cache)[0])
            q = q_rot(inv_yaw, v_sub(p, root_u))
            return (q[0] * scale, q[1] * scale, q[2] * scale)
        out["pelvis"].append(local("pelvis"))
        for key, joint in SOURCE_JOINTS.items():
            if key != "pelvis":
                out[key].append(local(joint))
        def fwd(name):
            # rest-relative rotation applied to the character's forward, yaw removed; mirrored like positions
            r = source.world(name, tracks, t, cache)[1]
            delta = q_mul(r, q_inv(rest[name]))
            v = to_unity(q_rot(delta, q_rot(rest_root_r, (0.0, 0.0, 1.0))))
            return q_rot(inv_yaw, v)
        out["pelvisFwd"].append(fwd("pelvis"))
        out["torsoFwd"].append(fwd("spine_3"))
        out["footFwdL"].append(fwd("ankle_L"))
        out["footFwdR"].append(fwd("ankle_R"))
    return out


def analyse_glb(path, clips):
    source = GltfSource(path)
    target = EftSkeleton(EFT_SKELETON)
    target_legs = v_len(v_sub(target.pos("Base HumanLCalf"), target.pos("Base HumanLThigh1"))) + v_len(v_sub(target.pos("Base HumanLFoot"), target.pos("Base HumanLCalf")))
    source_legs = v_len(tuple(source.nodes[source.index["leg_lower_L"]]["translation"])) + v_len(tuple(source.nodes[source.index["ankle_L"]]["translation"]))
    scale = target_legs / source_legs
    report = {"file": str(path), "scale": round(scale, 4), "clips": []}
    for name in clips:
        if name not in source.animations:
            report["clips"].append({"name": name, "error": "missing"})
            continue
        track = glb_track(source, name, scale)
        report["clips"].append(gait_metrics.analyse(track, name))
    return report


# ---------- retarget v0 ----------
def analyse_retarget(path, clips):
    db = json.loads(Path(path).read_text(encoding="utf-8"))
    report = {"file": str(path), "clips": []}
    for clip in db["clips"]:
        if clips and clip["name"] not in clips:
            continue
        scale = clip["checks"]["scale"]
        track = {"fps": clip["fps"], "root": [(r[0] * scale, r[1] * scale, r[2]) for r in clip["root"]], "pelvis": [tuple(d["target"]["pelvis"]) for d in clip["debug"]]}
        for key in ("hipL", "hipR", "kneeL", "kneeR", "ankleL", "ankleR", "toeL", "toeR"):
            track[key] = [tuple(d["target"][key]) for d in clip["debug"]]
        report["clips"].append(gait_metrics.analyse(track, clip["name"]))
    return report


def main():
    mode = sys.argv[1]
    if mode == "capture":
        path = Path(sys.argv[2])
        if path.is_dir():
            # a harness run folder: its status.json names the capture
            status = path / "status.json"
            path = Path(json.loads(status.read_text(encoding="utf-8"))["LastCapturePath"]) if status.exists() else max(path.glob("motion-capture*.json"), key=lambda p: p.stat().st_mtime)
        label = sys.argv[sys.argv.index("--label") + 1] if "--label" in sys.argv else path.stem
        print(json.dumps(analyse_capture(path, label), indent=1))
    elif mode == "glb":
        print(json.dumps(analyse_glb(sys.argv[2], sys.argv[3].split(",")), indent=1))
    elif mode == "retarget":
        print(json.dumps(analyse_retarget(sys.argv[2], sys.argv[3].split(",") if len(sys.argv) > 3 else []), indent=1))


if __name__ == "__main__":
    main()
