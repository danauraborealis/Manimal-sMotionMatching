# Fidget extraction experiment

`tools/extract_fidget.py` extracts `fidget2` from the user's SPAS-12 Blender
file, using the user-confirmed frame 0 neutral pose. It does not save or modify
the source file. Run it in a separate background Blender process:

```powershell
& 'C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe' --background --disable-autoexec 'G:\Projects\vance\shotgun\fugchk.blend' --python tools/extract_fidget.py -- --output artifacts/fidgets/fidget2.motion.json
```

The generated JSON is a local development artifact (the artifacts directory is
gitignored). It is not a Unity clip or a runtime mod package.

## Sampling and reconstruction

The exporter disables armature NLA evaluation, selects the action's single slot,
resets unkeyed pose channels, and samples evaluated bones after constraints/IK.
There are 45 samples, frames 0 through 44 inclusive, at the scene's effective
60 FPS: 0.733333 seconds. The original blend's enabled NLA track must not be
mixed into the action export.

Weapon transforms use the `Body` bone in armature space. Both hands are sampled
in the evaluated Body bone's coordinate system, removing inherited weapon
movement. Finger transforms are sampled relative to their evaluated parent bones.
The right hand is retained as a diagnostic/control track. Camera motion is not
included in this first extraction.

Each track includes its absolute reference transform and per-frame offsets:

```
delta.position = inverse(reference.rotation) * (current.position - reference.position)
delta.rotation = inverse(reference.rotation) * current.rotation
current.position = reference.position + reference.rotation * delta.position
current.rotation = reference.rotation * delta.rotation
```

Quaternions are WXYZ, normalized and sign-continuous. Positions are source Blender
units, not assumed meters. Bone axes are the source rig's axes, not Unity axes.
The source file's scene scale is 1 but that is not sufficient evidence of its
physical scale. Retargeting must explicitly calibrate scale and bone bases.
Finger positional offsets are retained for faithful reconstruction; a target
adapter should normally preserve target finger lengths and apply mapped rotations.

## Verified on the supplied file

- Neutral offsets are zero/identity within numerical tolerance.
- Reconstructing evaluated transforms gives maximum position error below
  0.000000000003 source units and quaternion component distance below 0.0000003.
- Weapon movement reaches approximately 0.2853 source units and 2.759 degrees.
- Left-hand movement relative to the weapon reaches approximately 0.02858 units.
- Right-hand positional variation is approximately 0.00003915 units; treat this
  as evaluation noise pending visual verification, not an intentional gesture.
- Weapon and both hands return to their reference transforms at frame 44.

These checks validate extraction, not target-rig compatibility or in-game playback.
Next: map the weapon axes/scale and identify a suitable Tarkov update hook, then
test the weapon offset before adding hand IK and finger rotation offsets over the
resolved grip pose. No runtime integration is included in this experiment.

## Runtime weapon clip conversion

The optional `--runtime-output` argument converts the evaluated `weapon` Body
track to the compact Unity-camera resource used by MotionMatching:

```powershell
& 'C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe' --background --disable-autoexec 'G:\Projects\vance\shotgun\fugchk.blend' --python tools/extract_fidget.py -- --output artifacts/fidgets/fidget2.motion.json --runtime-output src/MotionMatching/Data/fidget2.weapon.json
```

The converter captures the camera, armature, and neutral Body transforms at
frame 0. It removes the camera object's scale before deriving the neutral
Body-to-camera basis, so animated camera motion is not included. For each
evaluated Body sample it first computes the source delta

```
delta.position = inverse(neutralBody.rotation) * (current.position - neutral.position)
delta.rotation = inverse(neutralBody.rotation) * current.rotation
```

It then rotates the position by the neutral camera basis and conjugates the
rotation by the quaternion from
`inverse(neutralCamera) * armatureWorld * neutralBody`. Finally it reflects
camera Z into Unity forward: position `(x, y, -z)` and quaternion `(-x, -y,
z, w)`. The output quaternion is serialized as Unity `xyzw`, while the source
artifact remains WXYZ.

`src/MotionMatching/Data/fidget2.weapon.json` contains 45 samples for frames
0 through 44 at 60 FPS and a computed duration of `(45 - 1) / 60 =
0.7333333333333333` seconds. Positions retain source Blender units. The
resource records `metersPerSourceUnit: 0.0254` as a provisional adjustable
default; the source scene has not been physically calibrated, so this is an
explicit assumption rather than a measured scale.

The generated resource records the source blend SHA-256 and conversion
validation metadata. On the supplied file, frame 0 and frame 44 are identity,
the minimum adjacent quaternion dot is `0.9998658895492554`, and independent
source/camera-space reconstruction errors are below `5e-10` for rotations and
`1e-12` source units for positions. The blend file is only read in Blender
background mode and is never saved or modified.
