"""Extract the anatomical left-hand gesture from a Blender fidget action.

This script is intentionally separate from :mod:`extract_fidget`.  The existing
extractor remains the source of the evaluated local tracks; this converter only
retargets those tracks into neutral, anatomical bone bases.  It is safe to run
against the source blend in background mode because it never saves the file::

    blender -b --disable-autoexec source.blend \
        --python tools/extract_fidget_gesture.py -- \
        --output src/MotionMatching/Data/fidget2.gesture.json

    The same converter handles the other authored clips by selecting an action::

        blender -b --disable-autoexec source.blend \
        --python tools/extract_fidget_gesture.py -- \
        --action fidget1 --output src/MotionMatching/Data/fidget1.gesture.json

The source action uses Blender's WXYZ quaternion order.  The output resource
uses Unity's conventional XYZW order.  Reflection is applied to the source
armature-space vectors and rotations before they are expressed in the
canonical anatomical bases.  Use ``--weapon-bone`` when the source weapon is
attached to a differently named bone; the default remains ``Body``.
"""

import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Quaternion, Vector


SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import extract_fidget  # noqa: E402  (Blender supplies bpy before this import.)


ACTION_NAME = "fidget2"
REFERENCE_FRAME = 0
RUNTIME_METERS_PER_SOURCE_UNIT = 0.0254
EPSILON = 1e-8
POSITION_TOLERANCE = 1e-5
ROTATION_TOLERANCE = 1e-5
CHAIN_DIRECTION_MIN_DOT = 0.98
CANONICAL_DIRECTION_TOLERANCE = 1e-5
# Sniper/tau right-hand tracks drift by up to 0.00117 source units with stationary
# rotations/fingers. 0.002 source units is 0.0508 mm at the current runtime scale,
# below a meaningful rendered gesture. Rotation and endpoint checks stay strict.
RIGHT_HAND_POSITION_TOLERANCE = 2e-3
RIGHT_HAND_ROTATION_TOLERANCE = 1e-4


def quaternion_error(left, right):
    """Return component distance while treating q and -q as equivalent."""

    direct = math.sqrt(sum((a - b) ** 2 for a, b in zip(left, right)))
    negated = math.sqrt(sum((a + b) ** 2 for a, b in zip(left, right)))
    return min(direct, negated)


def normalize_quaternion(value, label):
    value = Quaternion(value)
    if value.magnitude <= EPSILON:
        raise ValueError(f"Degenerate quaternion for {label}")
    return value.normalized()


def normalize_vector(value, label):
    value = Vector(value)
    if value.length <= EPSILON:
        raise ValueError(f"Degenerate vector for {label}")
    return value.normalized()


def reflect_vector(value):
    """Reflect Blender global Z into the Unity-handed coordinate system."""

    value = Vector(value)
    return Vector((value.x, value.y, -value.z))


def reflect_quaternion(value):
    """Reflect a rotation across global Z (mathutils stores quaternions WXYZ)."""

    value = normalize_quaternion(value, "reflected rotation")
    # In XYZW notation this is (-x, -y, z, w), as required by the resource
    # conversion.  mathutils constructors take WXYZ.
    return Quaternion((value.w, -value.x, -value.y, value.z)).normalized()


def quaternion_to_xyzw(value):
    value = normalize_quaternion(value, "serialized rotation")
    if quaternion_error(value, Quaternion()) < 1e-7:
        value = Quaternion()
    return [float(value.x), float(value.y), float(value.z), float(value.w)]


def vector_to_xyz(value):
    value = Vector(value)
    if value.length < 1e-7:
        value = Vector((0.0, 0.0, 0.0))
    return [float(value.x), float(value.y), float(value.z)]


def project_off(value, axis, label):
    """Project *value* off *axis* and reject a degenerate result."""

    value = Vector(value)
    axis = normalize_vector(axis, f"{label} projection axis")
    return normalize_vector(value - axis * value.dot(axis), label)


def frame_quaternion(x_axis, y_axis, z_axis, label):
    """Build a canonical-to-armature quaternion from its three world axes."""

    x_axis = normalize_vector(x_axis, f"{label} +X")
    y_axis = normalize_vector(y_axis, f"{label} +Y")
    z_axis = normalize_vector(z_axis, f"{label} +Z")
    # Matrix rows are axis vectors; transpose makes them columns, i.e. the
    # canonical basis axes expressed in reflected armature space.
    basis = Matrix((x_axis, y_axis, z_axis)).transposed()
    determinant = basis.determinant()
    if not math.isfinite(determinant) or determinant <= 0.0 or abs(determinant - 1.0) > 1e-4:
        raise ValueError(f"Invalid {label} basis determinant: {determinant}")
    return basis.to_quaternion().normalized()


def prepare_animation(action_name):
    """Select the action exactly as the source extractor does."""

    scene = bpy.context.scene
    rigs = [obj for obj in scene.objects if obj.type == "ARMATURE"]
    if len(rigs) != 1:
        raise ValueError("Expected exactly one armature")
    rig = rigs[0]
    try:
        action = bpy.data.actions[action_name]
    except KeyError as error:
        raise ValueError(f"Action not found: {action_name}") from error

    animation = rig.animation_data_create()
    animation.use_nla = False
    animation.action = action
    if hasattr(action, "slots") and len(action.slots):
        if len(action.slots) != 1:
            raise ValueError("Select an action slot explicitly for multi-slot actions")
        animation.action_slot = action.slots[0]
    animation.action_blend_type = "REPLACE"
    animation.action_influence = 1.0
    for bone in rig.pose.bones:
        bone.matrix_basis = Matrix.Identity(4)
    return scene, rig, action


def evaluated_rig_at(scene, rig, frame):
    scene.frame_set(frame)
    bpy.context.view_layer.update()
    return rig.evaluated_get(bpy.context.evaluated_depsgraph_get())


def neutral_bases(scene, rig, reference_frame):
    """Capture all canonical bases from one evaluated neutral geometry frame."""

    evaluated = evaluated_rig_at(scene, rig, reference_frame)
    bones = evaluated.pose.bones

    def bone(name):
        try:
            return bones[name]
        except KeyError as error:
            raise ValueError(f"Required evaluated bone not found: {name}") from error

    hand = bone("ValveBiped.Bip01_L_Hand")
    index = bone("ValveBiped.Bip01_L_Finger1")
    middle = bone("ValveBiped.Bip01_L_Finger2")
    little = bone("ValveBiped.Bip01_L_Finger4")

    # PoseBone.head and PoseBone.matrix are in evaluated armature space.
    # Reflect the geometry before deriving any anatomical directions.
    palm_across = normalize_vector(reflect_vector(little.head - index.head), "palm across")
    palm_forward = normalize_vector(reflect_vector(middle.head - hand.head), "palm forward")
    palm_x = project_off(palm_across, palm_forward, "palm +X")
    palm_z = normalize_vector(palm_x.cross(palm_forward), "palm +Z")
    palm_frame = frame_quaternion(palm_x, palm_forward, palm_z, "palm")

    finger_pose_bones = {}
    neutral_rotations = {}
    for digit in range(5):
        for segment in range(3):
            suffix = str(digit) if segment == 0 else f"{digit}{segment}"
            name = f"ValveBiped.Bip01_L_Finger{suffix}"
            pose_bone = bone(name)
            finger_pose_bones[(digit, segment)] = pose_bone
            neutral_rotations[name] = normalize_quaternion(
                pose_bone.matrix.decompose()[1], f"neutral rotation {name}"
            )

    # The source rig's evaluated bone tail is not its animation longitudinal
    # axis.  Its child joints lie along local +X, so use evaluated head-to-child
    # directions for the first two segments and validate that convention.  A
    # distal segment has no child; infer its armature-space direction by applying
    # the distal neutral rotation to the measured middle-to-distal parent-local
    # +X direction.
    plus_x = Vector((1.0, 0.0, 0.0))
    chain_direction_dots = []
    nonleaf_alignment_dots = []
    distal_alignment_dots = []
    finger_frames = {}
    for digit in range(5):
        for segment in range(3):
            name = f"ValveBiped.Bip01_L_Finger{digit if segment == 0 else f'{digit}{segment}'}"
            pose_bone = finger_pose_bones[(digit, segment)]
            if segment < 2:
                child = finger_pose_bones[(digit, segment + 1)]
                child_displacement = child.head - pose_bone.head
                parent_local_direction = normalize_vector(
                    neutral_rotations[name].inverted() @ child_displacement,
                    f"{name} parent-local child direction",
                )
                chain_dot = parent_local_direction.dot(plus_x)
                chain_direction_dots.append(chain_dot)
                if chain_dot <= CHAIN_DIRECTION_MIN_DOT:
                    raise ValueError(
                        f"{name} child direction is not source local +X: dot={chain_dot}"
                    )
                source_direction = child_displacement
            else:
                middle_name = f"ValveBiped.Bip01_L_Finger{digit}1"
                middle = finger_pose_bones[(digit, 1)]
                child_displacement = pose_bone.head - middle.head
                parent_local_direction = normalize_vector(
                    neutral_rotations[middle_name].inverted() @ child_displacement,
                    f"{name} inferred parent-local child direction",
                )
                chain_dot = parent_local_direction.dot(plus_x)
                chain_direction_dots.append(chain_dot)
                if chain_dot <= CHAIN_DIRECTION_MIN_DOT:
                    raise ValueError(
                        f"{name} inferred direction is not source local +X: dot={chain_dot}"
                    )
                source_direction = neutral_rotations[name] @ parent_local_direction

            y_axis = normalize_vector(
                reflect_vector(source_direction),
                f"{name} +Y",
            )
            if digit == 0:
                z_axis = project_off(palm_z, y_axis, f"{name} +Z")
                x_axis = normalize_vector(y_axis.cross(z_axis), f"{name} +X")
            else:
                x_axis = project_off(palm_across, y_axis, f"{name} +X")
                z_axis = normalize_vector(x_axis.cross(y_axis), f"{name} +Z")
            frame = frame_quaternion(x_axis, y_axis, z_axis, name)
            finger_frames[name] = frame
            canonical_y = frame @ Vector((0.0, 1.0, 0.0))
            if segment < 2:
                expected_y = normalize_vector(
                    reflect_vector(child_displacement),
                    f"{name} expected canonical child direction",
                )
                alignment_dot = canonical_y.dot(expected_y)
                nonleaf_alignment_dots.append(alignment_dot)
                if alignment_dot < 1.0 - CANONICAL_DIRECTION_TOLERANCE:
                    raise ValueError(
                        f"{name} canonical +Y does not align with child direction: dot={alignment_dot}"
                    )
            else:
                expected_y = normalize_vector(
                    reflect_vector(source_direction),
                    f"{name} expected canonical inferred direction",
                )
                distal_alignment_dots.append(canonical_y.dot(expected_y))

    hand_neutral_rotation = normalize_quaternion(
        hand.matrix.decompose()[1], "neutral palm rotation"
    )
    return {
        "palm": palm_frame,
        "hand": hand_neutral_rotation,
        "fingers": finger_frames,
        "neutralRotations": neutral_rotations,
        "chainDirectionMinimumDot": min(chain_direction_dots),
        "nonLeafCanonicalYMinimumDot": min(nonleaf_alignment_dots),
        "distalCanonicalYMinimumDot": min(distal_alignment_dots),
    }


def convert_rotation(source_delta, neutral_rotation, canonical_frame):
    """Map a source local delta through the neutral armature and canonical frame."""

    source_delta = normalize_quaternion(source_delta, "source delta")
    neutral_rotation = normalize_quaternion(neutral_rotation, "neutral bone rotation")
    world_delta = neutral_rotation @ source_delta @ neutral_rotation.inverted()
    reflected_world_delta = reflect_quaternion(world_delta)
    return (canonical_frame.inverted() @ reflected_world_delta @ canonical_frame).normalized()


def reconstruct_source_rotation(canonical_delta, neutral_rotation, canonical_frame):
    """Undo :func:`convert_rotation` for independent validation."""

    reflected_world_delta = (
        canonical_frame @ normalize_quaternion(canonical_delta, "canonical delta")
        @ canonical_frame.inverted()
    ).normalized()
    source_world_delta = reflect_quaternion(reflected_world_delta)
    neutral_rotation = normalize_quaternion(neutral_rotation, "neutral bone rotation")
    return (neutral_rotation.inverted() @ source_world_delta @ neutral_rotation).normalized()


def convert_position(source_delta, neutral_rotation, canonical_frame):
    """Map a hand source-local translation through armature and palm bases."""

    source_delta = Vector(source_delta)
    neutral_rotation = normalize_quaternion(neutral_rotation, "neutral palm rotation")
    armature_delta = neutral_rotation @ source_delta
    reflected_delta = reflect_vector(armature_delta)
    return canonical_frame.inverted() @ reflected_delta


def reconstruct_source_position(canonical_delta, neutral_rotation, canonical_frame):
    """Undo :func:`convert_position` for independent validation."""

    reflected_delta = canonical_frame @ Vector(canonical_delta)
    armature_delta = reflect_vector(reflected_delta)
    neutral_rotation = normalize_quaternion(neutral_rotation, "neutral palm rotation")
    return neutral_rotation.inverted() @ armature_delta


def sign_continuous(values, label):
    """Normalize and choose quaternion signs without introducing discontinuities."""

    result = []
    previous = None
    for value in values:
        value = normalize_quaternion(value, label)
        if previous is None:
            if value.w < 0.0:
                value.negate()
        elif value.dot(previous) < 0.0:
            value.negate()
        result.append(value)
        previous = value
    return result


def convert(action_name=ACTION_NAME, reference_frame=REFERENCE_FRAME,
            weapon_bone=extract_fidget.DEFAULT_WEAPON_BONE):
    """Return a compact resource and conversion validation report."""

    source = extract_fidget.extract(
        action_name,
        reference_frame,
        build_runtime=False,
        weapon_bone=weapon_bone,
    )
    scene, rig, action = prepare_animation(action_name)
    bases = neutral_bases(scene, rig, reference_frame)
    tracks = source["tracks"]
    start_frame = source["startFrame"]
    end_frame = source["endFrame"]
    sample_count = end_frame - start_frame + 1
    if sample_count < 2:
        raise ValueError(f"Expected at least two inclusive samples, got {sample_count}")

    right_hand_source = tracks.get("handR")
    if right_hand_source is None or len(right_hand_source) != sample_count:
        raise ValueError("Missing or incomplete source track: handR")
    max_right_hand_position_delta = max(
        (Vector(sample["position"]).length for sample in right_hand_source),
        default=0.0,
    )
    max_right_hand_rotation_delta = max(
        (
            quaternion_error(
                normalize_quaternion(sample["rotationWxyz"], "right hand rotation"),
                Quaternion(),
            )
            for sample in right_hand_source
        ),
        default=0.0,
    )
    if max_right_hand_position_delta >= RIGHT_HAND_POSITION_TOLERANCE:
        raise AssertionError(
            "Right hand position contains meaningful gesture motion: "
            f"maxDelta={max_right_hand_position_delta}"
        )
    if max_right_hand_rotation_delta >= RIGHT_HAND_ROTATION_TOLERANCE:
        raise AssertionError(
            "Right hand rotation contains meaningful gesture motion: "
            f"maxDelta={max_right_hand_rotation_delta}"
        )

    palm_frame = bases["palm"]
    hand_neutral = bases["hand"]
    hand_source = tracks["handL"]
    hand_positions = []
    hand_rotations = []
    max_position_error = 0.0
    max_rotation_error = 0.0
    for sample in hand_source:
        source_position = Vector(sample["position"])
        source_rotation = Quaternion(sample["rotationWxyz"])
        canonical_position = convert_position(source_position, hand_neutral, palm_frame)
        canonical_rotation = convert_rotation(source_rotation, hand_neutral, palm_frame)
        hand_positions.append(canonical_position)
        hand_rotations.append(canonical_rotation)
        reconstructed_position = reconstruct_source_position(
            canonical_position, hand_neutral, palm_frame
        )
        reconstructed_rotation = reconstruct_source_rotation(
            canonical_rotation, hand_neutral, palm_frame
        )
        max_position_error = max(max_position_error, (reconstructed_position - source_position).length)
        max_rotation_error = max(
            max_rotation_error,
            quaternion_error(reconstructed_rotation, source_rotation),
        )

    hand_rotations = sign_continuous(hand_rotations, "hand rotation")
    hand_samples = [
        {"position": vector_to_xyz(position), "rotation": quaternion_to_xyzw(rotation)}
        for position, rotation in zip(hand_positions, hand_rotations)
    ]

    finger_resources = []
    finger_rotation_error = 0.0
    expected_finger_count = 0
    for digit in range(5):
        for segment in range(3):
            suffix = str(digit) if segment == 0 else f"{digit}{segment}"
            name = f"ValveBiped.Bip01_L_Finger{suffix}"
            source_track = tracks.get(name)
            if source_track is None or len(source_track) != sample_count:
                raise ValueError(f"Missing or incomplete source track: {name}")
            expected_finger_count += 1
            neutral_rotation = bases["neutralRotations"][name]
            canonical_frame = bases["fingers"][name]
            canonical_rotations = []
            for sample in source_track:
                source_rotation = Quaternion(sample["rotationWxyz"])
                canonical_rotation = convert_rotation(
                    source_rotation, neutral_rotation, canonical_frame
                )
                canonical_rotations.append(canonical_rotation)
                reconstructed_rotation = reconstruct_source_rotation(
                    canonical_rotation, neutral_rotation, canonical_frame
                )
                finger_rotation_error = max(
                    finger_rotation_error,
                    quaternion_error(reconstructed_rotation, source_rotation),
                )
            canonical_rotations = sign_continuous(canonical_rotations, f"finger {digit}/{segment}")
            finger_resources.append(
                {
                    "digit": digit,
                    "segment": segment,
                    "samples": [
                        {"rotation": quaternion_to_xyzw(rotation)}
                        for rotation in canonical_rotations
                    ],
                }
            )

    if len(finger_resources) != 15 or expected_finger_count != 15:
        raise AssertionError(f"Expected exactly 15 left finger tracks, got {len(finger_resources)}")

    neutral_position_error = max(
        hand_positions[0].length,
        hand_positions[-1].length,
    )
    neutral_rotation_error = max(
        quaternion_error(hand_rotations[0], Quaternion()),
        quaternion_error(hand_rotations[-1], Quaternion()),
    )
    for resource in finger_resources:
        rotations = [
            Quaternion((
                sample["rotation"][3],
                sample["rotation"][0],
                sample["rotation"][1],
                sample["rotation"][2],
            ))
            for sample in resource["samples"]
        ]
        neutral_rotation_error = max(
            neutral_rotation_error,
            quaternion_error(rotations[0], Quaternion()),
            quaternion_error(rotations[-1], Quaternion()),
        )

    min_adjacent_dot = 1.0
    for samples in [hand_rotations] + [
        [
            Quaternion((
                sample["rotation"][3],
                sample["rotation"][0],
                sample["rotation"][1],
                sample["rotation"][2],
            ))
            for sample in resource["samples"]
        ]
        for resource in finger_resources
    ]:
        min_adjacent_dot = min(
            min_adjacent_dot,
            *(left.dot(right) for left, right in zip(samples, samples[1:])),
        )

    if max_position_error >= POSITION_TOLERANCE:
        raise AssertionError(f"Hand source position reconstruction exceeded tolerance: {max_position_error}")
    if max(max_rotation_error, finger_rotation_error) >= ROTATION_TOLERANCE:
        raise AssertionError(
            "Source rotation reconstruction exceeded tolerance: "
            f"hand={max_rotation_error}, fingers={finger_rotation_error}"
        )
    if neutral_position_error >= POSITION_TOLERANCE or neutral_rotation_error >= ROTATION_TOLERANCE:
        raise AssertionError(
            "Gesture neutral/end sample is not identity: "
            f"position={neutral_position_error}, rotation={neutral_rotation_error}"
        )
    if min_adjacent_dot < 0.0:
        raise AssertionError(f"Gesture quaternion signs are discontinuous: dot={min_adjacent_dot}")

    fps = source["fps"]
    runtime_fps = int(fps) if float(fps).is_integer() else fps
    result = {
        "schemaVersion": 1,
        "action": action_name,
        "weaponBone": weapon_bone,
        "fps": runtime_fps,
        "sourceSha256": source["sourceSha256"],
        "coordinateSystem": "anatomical-local",
        "hand": {"samples": hand_samples},
        "fingers": finger_resources,
        "units": "Source Blender units",
        "metersPerSourceUnit": RUNTIME_METERS_PER_SOURCE_UNIT,
        "sourceFrameRange": [start_frame, end_frame],
        "referenceFrame": reference_frame,
        "durationSeconds": (sample_count - 1) / float(fps),
        "validation": {
            "sampleCount": sample_count,
            "fingerTrackCount": len(finger_resources),
            "neutralAndEndIdentityVerified": True,
            "quaternionContinuityVerified": True,
            "minimumAdjacentQuaternionDot": min_adjacent_dot,
            "maxHandPositionReconstructionError": max_position_error,
            "maxHandQuaternionReconstructionError": max_rotation_error,
            "maxFingerQuaternionReconstructionError": finger_rotation_error,
            "rightHandOmittedAsNonIntentional": True,
            "maxRightHandPositionDelta": max_right_hand_position_delta,
            "rightHandPositionOmissionTolerance": RIGHT_HAND_POSITION_TOLERANCE,
            "maxRightHandQuaternionDelta": max_right_hand_rotation_delta,
            "neutralGeometryCapturedOnce": True,
            "sourceLocalChainDirectionVerified": True,
            "minimumSourceLocalChainDirectionDot": bases["chainDirectionMinimumDot"],
            "canonicalYChildDirectionVerified": True,
            "minimumNonLeafCanonicalYChildDirectionDot": bases["nonLeafCanonicalYMinimumDot"],
            "minimumDistalCanonicalYInferredDirectionDot": bases["distalCanonicalYMinimumDot"],
            "sourceAction": action.name,
            "nlaDisabled": True,
        },
    }
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--action", default=ACTION_NAME)
    parser.add_argument("--reference-frame", type=int, default=REFERENCE_FRAME)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--weapon-bone",
        default=extract_fidget.DEFAULT_WEAPON_BONE,
        help=f"Weapon attachment bone (default: {extract_fidget.DEFAULT_WEAPON_BONE})",
    )
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :])

    result = convert(args.action, args.reference_frame, args.weapon_bone)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(result, separators=(",", ":"), allow_nan=False) + "\n",
        encoding="utf-8",
    )
    print(json.dumps({"output": str(args.output), **result["validation"]}))


if __name__ == "__main__":
    main()
