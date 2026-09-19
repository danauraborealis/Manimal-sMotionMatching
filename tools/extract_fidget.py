"""Run in Blender background mode with --disable-autoexec; never saves the blend.

blender -b --disable-autoexec source.blend --python tools/extract_fidget.py -- --output output.json

Pass ``--runtime-output`` as well to write the compact Unity-camera weapon clip
alongside the source-space extraction.  The runtime output is derived from the
same evaluated samples and uses only the camera axes at the reference frame.
Use ``--weapon-bone`` when the weapon is attached to a differently named bone;
the default remains ``Body`` for the original fidget source.
"""
import argparse
import hashlib
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Quaternion, Vector


RUNTIME_METERS_PER_SOURCE_UNIT = 0.0254
DEFAULT_WEAPON_BONE = 'Body'


def transform(matrix):
    position, rotation, scale = matrix.decompose()
    if max(abs(v - 1) for v in scale) > 1e-4:
        raise ValueError("Non-unit scale requires an explicit conversion policy")
    return {"position": list(position), "rotationWxyz": list(rotation.normalized())}


def remove_scale(matrix):
    """Return the same transform with translation and rotation but unit scale."""
    position, rotation, scale = matrix.decompose()
    if min(abs(v) for v in scale) <= 1e-8:
        raise ValueError("Camera scale must be non-zero")
    return Matrix.Translation(position) @ rotation.normalized().to_matrix().to_4x4()


def quaternion_error(left, right):
    """Component distance while treating q and -q as the same rotation."""
    direct = math.sqrt(sum((a - b) ** 2 for a, b in zip(left, right)))
    negated = math.sqrt(sum((a + b) ** 2 for a, b in zip(left, right)))
    return min(direct, negated)


def unity_camera_sample(position, rotation):
    """Reflect Blender camera axes into Unity camera axes and serialize XYZW."""
    def clean(value):
        value = float(value)
        return 0.0 if value == 0.0 else value

    return {
        'position': [clean(position.x), clean(position.y), clean(-position.z)],
        'rotation': [clean(-rotation.x), clean(-rotation.y), clean(rotation.z), clean(rotation.w)],
    }


def extract(action_name, reference_frame, build_runtime=False,
            weapon_bone=DEFAULT_WEAPON_BONE, weapon_only=False):
    scene = bpy.context.scene
    rigs = [o for o in scene.objects if o.type == 'ARMATURE']
    if len(rigs) != 1:
        raise ValueError("Expected exactly one armature")
    rig = rigs[0]
    action = bpy.data.actions[action_name]
    start, end = map(int, action.frame_range)
    if not start <= reference_frame <= end:
        raise ValueError("Reference frame outside action")
    animation = rig.animation_data_create()
    animation.use_nla = False
    animation.action = action
    if hasattr(action, 'slots') and len(action.slots):
        if len(action.slots) != 1:
            raise ValueError("Select an action slot explicitly for multi-slot actions")
        animation.action_slot = action.slots[0]
    animation.action_blend_type = 'REPLACE'
    animation.action_influence = 1.0
    # Avoid inheriting unkeyed pose channels from the file's currently active action.
    for bone in rig.pose.bones:
        bone.matrix_basis = Matrix.Identity(4)
    fingers = [] if weapon_only else [b.name for b in rig.pose.bones if '_Finger' in b.name]
    fps = scene.render.fps / scene.render.fps_base

    def sample(frame):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
        bones = evaluated.pose.bones
        try:
            body = bones[weapon_bone].matrix.copy()
        except KeyError as error:
            raise ValueError(f"Required evaluated weapon bone not found: {weapon_bone}") from error
        matrices = {'weapon': body}
        if not weapon_only:
            for side in ('L', 'R'):
                matrices['hand' + side] = body.inverted() @ bones['ValveBiped.Bip01_' + side + '_Hand'].matrix
            for name in fingers:
                bone = bones[name]
                matrices[name] = bone.parent.matrix.inverted() @ bone.matrix
        return {name: transform(matrix) for name, matrix in matrices.items()}

    reference = sample(reference_frame)
    reference_body = None
    neutral_camera = None
    neutral_camera_basis = None
    if build_runtime:
        # Capture the evaluated neutral transforms once.  The camera transform
        # remains frozen here even if the source file animates its camera.
        scene.frame_set(reference_frame)
        bpy.context.view_layer.update()
        evaluated_rig = rig.evaluated_get(bpy.context.view_layer.depsgraph)
        try:
            reference_body = evaluated_rig.pose.bones[weapon_bone].matrix.copy()
        except KeyError as error:
            raise ValueError(f"Required evaluated weapon bone not found: {weapon_bone}") from error
        if scene.camera is None:
            raise ValueError("Runtime output requires an active scene camera")
        neutral_camera = remove_scale(scene.camera.matrix_world.copy())
        neutral_camera_basis = (
            neutral_camera.inverted() @ evaluated_rig.matrix_world.copy() @ reference_body
        ).decompose()[1].normalized()
    tracks = {name: [] for name in reference}
    max_position_error = 0.0
    max_rotation_error = 0.0
    for frame in range(start, end + 1):
        pose = sample(frame)
        for name, current in pose.items():
            neutral = reference[name]
            base_q = Quaternion(neutral['rotationWxyz'])
            offset_p = base_q.inverted() @ (Vector(current['position']) - Vector(neutral['position']))
            offset_q = (base_q.inverted() @ Quaternion(current['rotationWxyz'])).normalized()
            previous = Quaternion(tracks[name][-1]['rotationWxyz']) if tracks[name] else Quaternion()
            if offset_q.dot(previous) < 0:
                offset_q.negate()
            tracks[name].append({'position': list(offset_p), 'rotationWxyz': list(offset_q)})
            rebuilt_p = Vector(neutral['position']) + base_q @ offset_p
            rebuilt_q = base_q @ offset_q
            max_position_error = max(max_position_error, (rebuilt_p - Vector(current['position'])).length)
            expected_q = Quaternion(current['rotationWxyz'])
            # Component distance avoids acos precision loss near identity.
            max_rotation_error = max(max_rotation_error, min(
                math.sqrt(sum((a-b)**2 for a,b in zip(rebuilt_q, expected_q))),
                math.sqrt(sum((a+b)**2 for a,b in zip(rebuilt_q, expected_q)))))
    reference_index = reference_frame - start
    for track in tracks.values():
        neutral = track[reference_index]
        assert Vector(neutral['position']).length < 1e-5
        assert abs(abs(neutral['rotationWxyz'][0]) - 1) < 1e-5
    assert max_position_error < 1e-4
    assert max_rotation_error < 1e-5
    result = {
        'schemaVersion': 1, 'action': action_name, 'weaponBone': weapon_bone,
        'sourceSha256': hashlib.sha256(Path(bpy.data.filepath).read_bytes()).hexdigest(),
        'blenderVersion': bpy.app.version_string, 'rig': rig.name,
        'referenceFrame': reference_frame, 'startFrame': start, 'endFrame': end,
        'fps': fps, 'durationSeconds': (end-start)/fps,
        'sampleTimesSeconds': [(f-start)/fps for f in range(start, end+1)],
        'coordinateSystem': 'Blender source bone bases; no Unity axis conversion',
        'units': 'Source Blender units; physical scale must be calibrated before runtime use',
        'sceneUnitScale': scene.unit_settings.scale_length,
        'spaces': {'weapon': 'armature'},
        'composition': 'p = reference.p + reference.q * delta.p; q = reference.q * delta.q (wxyz)',
        'reference': reference, 'tracks': tracks,
        'validation': {'sampleCount': end-start+1, 'maxPositionReconstructionError': max_position_error,
                       'maxQuaternionComponentReconstructionError': max_rotation_error,
                       'neutralOffsetsVerified': True, 'nlaDisabled': True},
    }
    if not weapon_only:
        result['spaces'].update({
            'handL': f'weapon {weapon_bone} bone',
            'handR': f'weapon {weapon_bone} bone',
            'fingers': 'evaluated parent bone',
        })

    if build_runtime:
        # Keep the source-space extraction as the source of truth, then apply
        # the neutral Body-to-camera basis to each weapon delta.  This keeps
        # animated camera motion out of the clip and retains source units.
        source_position, source_rotation, _ = reference_body.decompose()
        runtime_samples = []
        max_source_position_error = 0.0
        max_source_rotation_error = 0.0
        max_camera_position_error = 0.0
        max_camera_rotation_error = 0.0
        previous_runtime_rotation = None

        for index, frame in enumerate(range(start, end + 1)):
            # Re-evaluate the weapon bone independently from the serialized source
            # track.  The comparison below guards against a conversion that
            # merely reproduces already-converted values.
            scene.frame_set(frame)
            bpy.context.view_layer.update()
            evaluated = rig.evaluated_get(bpy.context.view_layer.depsgraph)
            try:
                current_body = evaluated.pose.bones[weapon_bone].matrix.copy()
            except KeyError as error:
                raise ValueError(f"Required evaluated weapon bone not found: {weapon_bone}") from error
            current_position, current_rotation, _ = current_body.decompose()
            source_delta_position = source_rotation.inverted() @ (current_position - source_position)
            source_delta_rotation = (
                source_rotation.inverted() @ current_rotation
            ).normalized()

            source_track = tracks['weapon'][index]
            serialized_source_position = Vector(source_track['position'])
            serialized_source_rotation = Quaternion(source_track['rotationWxyz'])
            max_source_position_error = max(
                max_source_position_error,
                (source_delta_position - serialized_source_position).length,
            )
            max_source_rotation_error = max(
                max_source_rotation_error,
                quaternion_error(source_delta_rotation, serialized_source_rotation),
            )

            camera_position = neutral_camera_basis @ source_delta_position
            camera_rotation = (
                neutral_camera_basis @ source_delta_rotation @ neutral_camera_basis.inverted()
            ).normalized()
            runtime_sample = unity_camera_sample(camera_position, camera_rotation)
            runtime_rotation = Quaternion((
                runtime_sample['rotation'][3],
                runtime_sample['rotation'][0],
                runtime_sample['rotation'][1],
                runtime_sample['rotation'][2],
            ))
            if previous_runtime_rotation is not None:
                if runtime_rotation.dot(previous_runtime_rotation) < 0:
                    runtime_rotation.negate()
                    runtime_sample['rotation'] = [
                        float(runtime_rotation.x), float(runtime_rotation.y),
                        float(runtime_rotation.z), float(runtime_rotation.w),
                    ]
            previous_runtime_rotation = runtime_rotation
            runtime_samples.append(runtime_sample)

            # Undo the prescribed reflection and compare with the independently
            # reconstructed source camera-space delta.
            reconstructed_camera_position = Vector((
                runtime_sample['position'][0],
                runtime_sample['position'][1],
                -runtime_sample['position'][2],
            ))
            reconstructed_runtime_rotation = Quaternion((
                runtime_sample['rotation'][3],
                runtime_sample['rotation'][0],
                runtime_sample['rotation'][1],
                runtime_sample['rotation'][2],
            ))
            reconstructed_camera_rotation = Quaternion((
                reconstructed_runtime_rotation.w,
                -reconstructed_runtime_rotation.x,
                -reconstructed_runtime_rotation.y,
                reconstructed_runtime_rotation.z,
            ))
            expected_camera_rotation = (
                neutral_camera_basis @ source_delta_rotation @ neutral_camera_basis.inverted()
            ).normalized()
            max_camera_position_error = max(
                max_camera_position_error,
                (reconstructed_camera_position - camera_position).length,
            )
            max_camera_rotation_error = max(
                max_camera_rotation_error,
                quaternion_error(reconstructed_camera_rotation, expected_camera_rotation),
            )

        if not runtime_samples:
            raise ValueError("Runtime output requires at least one action sample")
        first = runtime_samples[0]
        last = runtime_samples[-1]
        neutral_position_error = max(
            math.sqrt(sum(component * component for component in first['position'])),
            math.sqrt(sum(component * component for component in last['position'])),
        )
        identity_rotation = [0.0, 0.0, 0.0, 1.0]
        neutral_rotation_error = max(
            quaternion_error(first['rotation'], identity_rotation),
            quaternion_error(last['rotation'], identity_rotation),
        )
        min_rotation_dot = 1.0
        for previous, current in zip(runtime_samples, runtime_samples[1:]):
            previous_q = Quaternion((
                previous['rotation'][3], previous['rotation'][0],
                previous['rotation'][1], previous['rotation'][2],
            ))
            current_q = Quaternion((
                current['rotation'][3], current['rotation'][0],
                current['rotation'][1], current['rotation'][2],
            ))
            min_rotation_dot = min(min_rotation_dot, previous_q.dot(current_q))

        if max_source_position_error >= 1e-4 or max_source_rotation_error >= 1e-5:
            raise AssertionError(
                "Runtime source reconstruction exceeded tolerance: "
                f"position={max_source_position_error}, rotation={max_source_rotation_error}"
            )
        if max_camera_position_error >= 1e-5 or max_camera_rotation_error >= 1e-5:
            raise AssertionError(
                "Runtime camera reconstruction exceeded tolerance: "
                f"position={max_camera_position_error}, rotation={max_camera_rotation_error}"
            )
        if neutral_position_error >= 1e-5 or neutral_rotation_error >= 1e-5:
            raise AssertionError(
                "Runtime neutral/end sample is not identity: "
                f"position={neutral_position_error}, rotation={neutral_rotation_error}"
            )
        if min_rotation_dot < 0:
            raise AssertionError(f"Runtime quaternion signs are discontinuous: dot={min_rotation_dot}")

        runtime_fps = int(fps) if fps.is_integer() else fps
        result['_runtime'] = {
            'schemaVersion': 1,
            'action': action_name,
            'fps': runtime_fps,
            'sourceSha256': result['sourceSha256'],
            'coordinateSystem': 'unity-camera',
            'samples': runtime_samples,
            'durationSeconds': (len(runtime_samples) - 1) / fps,
            'units': 'Source Blender units',
            'metersPerSourceUnit': RUNTIME_METERS_PER_SOURCE_UNIT,
            'scaleAssumption': 'Provisional 0.0254 meters per source unit; not calibrated.',
            'sourceFrameRange': [start, end],
            'referenceFrame': reference_frame,
            'validation': {
                'sampleCount': len(runtime_samples),
                'neutralAndEndIdentityVerified': True,
                'quaternionContinuityVerified': True,
                'minimumAdjacentQuaternionDot': min_rotation_dot,
                'maxSourcePositionReconstructionError': max_source_position_error,
                'maxSourceQuaternionReconstructionError': max_source_rotation_error,
                'maxCameraPositionReconstructionError': max_camera_position_error,
                'maxCameraQuaternionReconstructionError': max_camera_rotation_error,
                'cameraMotionIncluded': False,
            },
        }
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--action', default='fidget2')
    parser.add_argument('--reference-frame', type=int, default=0)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--runtime-output', type=Path,
                        help='Optional compact Unity-camera weapon clip output path')
    parser.add_argument('--weapon-bone', default=DEFAULT_WEAPON_BONE,
                        help=f'Weapon attachment bone (default: {DEFAULT_WEAPON_BONE})')
    parser.add_argument('--weapon-only', action='store_true',
                        help='Extract only the weapon track; omit hands and fingers')
    args = parser.parse_args(sys.argv[sys.argv.index('--')+1:])
    result = extract(args.action, args.reference_frame,
                     build_runtime=args.runtime_output is not None,
                     weapon_bone=args.weapon_bone,
                     weapon_only=args.weapon_only)
    runtime = result.pop('_runtime', None)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False) + '\n', encoding='utf-8')
    if runtime is not None:
        args.runtime_output.parent.mkdir(parents=True, exist_ok=True)
        args.runtime_output.write_text(
            json.dumps(runtime, separators=(',', ':'), allow_nan=False) + '\n',
            encoding='utf-8',
        )
    print(json.dumps({'output': str(args.output), **result['validation'],
                      **({'runtimeOutput': str(args.runtime_output),
                          'runtimeValidation': runtime['validation']} if runtime else {})}))
