# What Half-Life: Alyx's own animgraphs say about the locomotion system

Source: `animgraphs/*.vanmgrph_c` in the local HLA install, decompiled with Source2Viewer-CLI (`-d`) into
`tmp/alyx/animgraphs/` and summarised with `tools/animgraph_extract.py`. These are the shipped node parameters
behind the SIGGRAPH 2021 deck. Values are Source units (1 unit = 1 inch = 0.0254 m) unless noted. The graph
data stays local; only parameter values are recorded here.

## Which NPCs use what

| Graph | Locomotion |
|---|---|
| `combine_grunt_footlock` (grunt, captain, recon, suppressor share the clip set) | motion matching node + foot lock node + stride length adjuster + damped path motor |
| `combine_suppressor_latest` | newer motion matching node (distance-based path metric, foot cycle metric, goal assist, distance scaling) + foot lock with hip shift |
| `combine_soldier_heavy` | no motion matching at all: state machines, direct playback, ground IK, two-bone IK, path helpers |
| `combine_soldier_new_content_procedural_rebuild` | no motion matching: procedural build with path helpers and two-bone IK |

So the heavy's walk and run loops are plain cycles in Valve's own game too; the grunt family is the stride-retargeted one.

## Grunt motion matching node ("Strafing & Down Axis")

| Setting | Value |
|---|---|
| prediction time | 1.0 s |
| sample rate | 0.1 s |
| blend time | 0.3 s |
| responsiveness | 0.5 |
| selection threshold | 0.1 |
| search on steps | true |

Metrics and weights:

| Metric | Weight | Notes |
|---|---|---|
| current velocity | 3.0 | |
| ankle_L / ankle_R velocity | 1.0 each | |
| path velocity | 2.0 | samples at 1.0, 0.5, 0.25 of the prediction time |
| path position | 2.0 | same samples |
| ankle_L / ankle_R position | 1.0 each | |
| facing | 3.0 | |
| steps remaining | 0.0 | filter only: min 1.0 step |
| goal distance | 0.0 | filter only: min 0 |

Clip groups (game logic picks the group): **Strafe** (36 clips: `new_short_hop_0/1/8` x 8 directions, the 12
`strafe_X_to_strafe_Y` cuts) and **Run Down Axis** (41 clips: `stand_to_run_down_axis_*`, `run_to_stand_down_axis_*`,
`strafe_to_run_*`, `run_down_axis_to_strafe_*`, `run_n_to_run_*` cuts, tightening and large running turns).
Loop flags are false on all of them: every clip is a one-shot the matcher can enter anywhere.

## Suppressor (newer) motion matching node

| Setting | Value |
|---|---|
| prediction time | 1.5 s |
| sample rate | 0.1 s (0.2 in one instance) |
| blend time | 0.3 s (0.5 in one instance) |
| selection threshold | 0.1 |
| search on steps | true |
| goal assist | on, distance 40 units (1.0 m), tolerance 2 units |
| distance scaling | on: outer radius 120 (3.0 m), inner 90 (2.3 m), scale 0.6 to 1.2 |

Metrics: current velocity 1.0; ankle velocities 1.0; ankle positions 1.0; foot cycle 1.0; path 2.0 over 200
units (5.1 m) sampled at 25/50/75/100 percent, extrapolating below 20 units/s; future facing 1.0 at 100 units
(2.5 m); future velocity 2.0 at 100 units with stopping distance 100 and auto target speed; distance remaining
0.0 with goal filtering from 100 units; steps remaining filter min 1.5; time remaining filter min 0.3 s. This is
the slide-87 table.

## Foot lock node (grunt)

| Setting | Value |
|---|---|
| solver | two-bone, hip bone `pelvis_translations` |
| feet | `foot_L_IK_target_ytrans` / `foot_R_IK_target_ytrans`, chains `IKChain_Leg_Left/Right` |
| max yaw from forward | 55 degrees per foot |
| reflect target push-in / max excess | 2 units each |
| position damping | constant, speed scale 30, min speed 10, max tension 1000 |
| rotation damping | spring, speed scale 8, min speed 10 |
| foot rotation limits | on |
| step limits | off |
| motion limits | off (stretch scale 0.293, motion falloff bias 0.7) |
| stride curve scale / limit scale | 1.0 / 0.25 |
| modulate step height | on: increase scale 0, decrease scale 1.0 |
| tilt | off (pitch/roll springs 5) |
| reachability limit | on, max 0.99 |

Suppressor's newer foot lock adds: blend time 0.2 s, always use fallback hinge, max Z offset from hip 999, **hip
shift on at 0.75**, reachability limit off, ground tracing off.

## Stride length adjuster (grunt)

Outer radius 150 units (3.8 m), inner radius 120 (3.0 m), scale 0.8 to 1.2, rotation assist on, spring damping
speed 8. Strides shorten toward a goal inside the last 3 to 4 metres.

## Path motors

Default: damped path motor, lock to path, anticipation time 0.5 s, spring constant 10, tension 10 to 100, facing
damping spring 16 with min speed 200. Injured variant: anticipation 0.65 s, spring 5. Follow-path node stopping
distance 30 units (0.76 m).

## What this changes for the Tarkov implementation

- **Damp the corrections.** Valve's foot lock moves its targets through a damped input (position speed scale 30,
  rotation spring 8), and the newer version blends in over 0.2 s. Our placer applies the target instantly.
- **Hip shift.** The newer foot lock moves the pelvis toward the feet (0.75). Our pelvis is fixed to the clip.
- **Prediction horizon and path samples.** 1.0 to 1.5 s ahead, path sampled to 5 m; ours predicts one stride.
- **Stride scaling near the goal** (0.8 to 1.2 inside 3 to 4 m, later 0.6 to 1.2) is a dedicated node; ours has
  no notion of the goal distance yet.
- **Search only on steps, selection threshold 0.1** matches our re-pick-on-plant plus hysteresis idea.
- **Foot yaw limit 55 degrees** from forward is their answer to twisted ankles; ours caps the heading correction at 30.
- **Heavy soldier is not motion matched in Alyx either**, which justifies using its loops as plain cycles.
