"""Add direction-retargeted upper-body rotations to a local reaction database.

Native bone lengths, finger poses and wrist rotations are retained. Valve data stays local.
"""
import argparse
import json
import math
from pathlib import Path
from alyx_retarget import (GltfSource, EftSkeleton, frame, bend_axis, to_unity,
                          q_inv, q_mul, q_norm, q_rot, v_sub)

SPINE = [("Spine1", "spine_0", "Spine2", "spine_1"),
         ("Spine2", "spine_1", "Spine3", "spine_2"),
         ("Spine3", "spine_2", "Ribcage", "spine_3"),
         ("Ribcage", "spine_3", "Neck", "neck_0")]


def augment(database, source, target, role=None):
    specs = [("Base Human"+t, s, "Base Human"+tc, sc, None) for t, s, tc, sc in SPINE]
    for side in ("L", "R"):
        prefix = "Base Human" + side
        specs.extend([(prefix+"Collarbone", "clavicle_"+side, prefix+"Upperarm", "arm_upper_"+side, None),
                      (prefix+"Upperarm", "arm_upper_"+side, prefix+"Forearm1", "arm_lower_"+side, side),
                      (prefix+"Forearm1", "arm_lower_"+side, prefix+"Palm", "hand_"+side, side)])
    target_side = v_sub(target.pos("Base HumanLUpperarm"), target.pos("Base HumanRUpperarm"))
    corrections = {}
    for name, _, child, _, side in specs:
        axis = target_side
        if side:
            prefix = "Base Human"+side
            axis = bend_axis(target.pos(prefix+"Upperarm"), target.pos(prefix+"Forearm1"), target.pos(prefix+"Palm"), target_side)
        corrections[name] = q_mul(q_inv(frame(v_sub(target.pos(child), target.pos(name)), axis)), target.rot(name))
    for clip in database["clips"]:
        if role and (role not in clip.get("roles", []) or clip["name"] not in source.animations):
            continue
        tracks, duration = source.tracks(clip["name"])
        if abs((clip["frames"]-1)/clip["fps"] - duration) > 1/clip["fps"]:
            raise ValueError("Upper-body export requires unsliced clips: " + clip["name"])
        upper = {name: [] for name, *_ in specs}
        for f in range(clip["frames"]):
            at = min(duration, f/clip["fps"])
            cache = {}
            root_p, root_r = source.world("root_motion", tracks, at, cache)
            forward = to_unity(q_rot(root_r, (0, 0, 1)))
            yaw = math.atan2(forward[0], forward[2])
            inv_yaw = (0, -math.sin(yaw/2), 0, math.cos(yaw/2))
            def pos(name):
                return q_rot(inv_yaw, v_sub(to_unity(source.world(name, tracks, at, cache)[0]), to_unity(root_p)))
            side_axis = v_sub(pos("arm_upper_L"), pos("arm_upper_R"))
            worlds = {"Base HumanPelvis": tuple(clip["rotations"]["Base HumanPelvis"][f])}
            for name, src, child, src_child, side in specs:
                axis = side_axis if side is None else bend_axis(pos("arm_upper_"+side), pos("arm_lower_"+side), pos("hand_"+side), side_axis)
                world = q_mul(frame(v_sub(pos(src_child), pos(src)), axis), corrections[name])
                parent = target.bones[target.bones[target.index[name]]["Parent"]]["Name"]
                parent_world = worlds.get(parent, target.rot(parent))
                upper[name].append(q_norm(q_mul(q_inv(parent_world), world)))
                worlds[name] = world
        clip["upperBodyRotations"] = upper
    return database


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database")
    parser.add_argument("source")
    parser.add_argument("skeleton")
    parser.add_argument("output")
    parser.add_argument("--role", help="Only augment source-matched clips carrying this role")
    args = parser.parse_args()
    result = augment(json.loads(Path(args.database).read_text(encoding="utf-8-sig")), GltfSource(args.source), EftSkeleton(args.skeleton), args.role)
    Path(args.output).write_text(json.dumps(result, separators=(",", ":")), encoding="utf-8")
    print("Exported upper-body tracks for", sum("upperBodyRotations" in c for c in result["clips"]), "clips")
