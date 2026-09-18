# Foot placement diagnosis

Use paired geometry to distinguish an unusual authored pose from a change introduced by the placer. A flag is a place to inspect, not a verdict that an animation is unnatural.

## Reproduce and inspect

1. Record a puppet scenario with the layer, or a fleet raid with the geometry-enabled build. Record the DLL/database hashes, effective configuration, scenario and movement driver. Keep the same database when comparing runtime changes.
2. Run `python tools/placement_diagnostics.py <capture> --output report.json` for paired measurements. Old fleet streams without joint geometry cannot support these measurements; use their existing reports only as historical context.
3. Run `python tools/placement_viewer.py <capture> --output replay.html`. Open the generated page locally. Scrub to a reported frame, compare the dashed pre-placement skeleton with the solid post-placement skeleton, and inspect all three projections. The page works offline and includes bot selection for fleet streams.
4. Run `python tools/audit_stride_events.py <database.json> --output stride-audit.json` before deploying regenerated clip data. This checks array lengths, cycle indices, event ordering and strike-frame consistency. Clock-disagreement counts explain why distance progression must not be used as an event clock; they are not counts of corrupt frames.

Puppet stages: `after_visual` is after clip playback and the game's visual/IK pass, immediately before our placer; `after_lock` is immediately after our correction. Their difference isolates placement (or the fallback foot lock), not every change made by the animation layer. `before_visual` contains the original animator pose, `after_pose` includes clip playback, and `pre_render` can identify later writes. Do not call `after_visual` an untouched stock-animation baseline when playback is enabled.

Fleet `geometrySchema=1` records same-frame world-space hip/knee/ankle positions as `h/k/a`, before placement as `h0/k0/a0`, heel/toe sole endpoints as `hl/to` and `hl0/to0`, pelvis as `pe/pe0`, and root joint as `rj`. Missing fields mean unavailable geometry, not zero. The old `p` field remains an ankle for `src=bone` and a FootBase for the active placer; do not compare these as identical landmarks. Use matching bone or sole endpoints instead.

Puppet `PlacedHeel/PlacedToe` probes are final-placement observations only when the placer is active; copies present in an earlier/later capture stage are not fresh sole measurements for that stage. If the before-stage sole or a reliable support label is absent, support-drift attribution stays unknown. A frozen landing target is not proof that the foot is on the ground. The native control currently supplies geometry but lacks an equivalent validated per-foot support label, so it cannot by itself establish a matched contact-drift baseline.

## What to measure

- **Trailing:** signed ankle-to-hip distance along measured travel, normalized by leg length, plus the change introduced by placement. A trailing support foot during push-off is expected. Inspect duration, speed, direction, braking, stance and swing together.
- **Leg proximity:** minimum 3D separation between opposite thigh/shin segments, alongside signed ankle ordering and the pre-placement value. Crossing in a front-view image, or negative step width during a crossover, is insufficient evidence of clipping. Joint-center distances are a proximity proxy; mesh thickness and actual intersections require mesh/collision geometry.
- **Reach and knee changes:** hip-to-ankle distance divided by thigh-plus-shin length, knee displacement, and pelvis shift before/after. A straight authored leg is not the same failure as a solver stretching one farther. Keep target reach and final achieved reach separate.
- **Support drift:** follow heel and toe endpoints during continuous support. A heel-to-toe roll can move the ankle and the blended FootBase while retaining a stable contact point. Do not interpret a change in the identity of the lowest sole point as a pop.
- **Attribution:** distinguish authored geometry, placement changes, contact-label disagreement, prediction failures and later animation writes. Preserve clip/phase/driver context and exact frame IDs.

Measure elapsed exposure and continuous per-metric episodes. Break temporal comparisons at missing data, culling, suspension, large sample gaps and teleports. Show transitions as context: transitions can contain real bugs and should not simply disappear from the report. Raw frame counts and the report's flagged-sample `rates_per_minute` vary with capture cadence and are not quality scores; use episode duration and valid exposure for temporal interpretation.

Use matched native-animation captures and the same-frame pre-placement pose as empirical references. Compare similar speed, direction, turn rate and gait; an unmatched control raid is not a causal A/B experiment. Initial numeric alert thresholds are triage settings, not validated human limits. In particular, native animation is a useful reference but is not guaranteed perfect.

## Review decisions

Verified issues fixed in this pass:

- Variation selection previously found a speed-entry frame in the previous start set rather than the chosen hop. The first resumed SAIN raid reproduced an entry with `feet=float.MaxValue`, indicating the search had no valid candidate.
- `footLiftCycle` and `footStrikeCycle` are normalized **time**, while stride `progression` is projected **distance**. Comparing them changes lock-release timing on nonuniform steps. Spatial progression must remain available for reconstructing the authored foot path.
- The loop exporter fallback could replace cycle start/end frames while retaining unrelated strike frames and offsets. The northeast heavy walk's right-foot data contained a strike outside its rewritten cycle.

The repaired development database passes the structural audit for all 133 clips. A fresh long-strafe puppet run completed its full route and produced three valid variation entries, with zero locked feet in 4,485 airborne authored-swing samples. A fresh 180-second Factory layer raid produced another valid variation and zero locks in 11,766 such samples. Its paired 180-second native-control raid also completed; all recorded control pre/post joints and sole endpoints matched, as expected. These checks establish the specific corrected behavior, not overall visual naturalness.

Do not clamp all spatial progression to 0–1: authored overshoot/backtracking is valid and is needed for reconstruction. Do not blindly change hip offsets to per-frame deltas: the animator normally restores the pose every frame, so accumulated-offset claims require a reproduction. The per-foot anchor-failure path deliberately retains a drawn pose and decays a residual; enabling a second solver on that frame would need separate justification.

Still worth measuring: compatibility of stride contacts across blended directional family members, reach-clamp effects during rapid turns, and support-label quality on low shuffles. These are not confirmed fixes in this pass.

### Recorded clearance case

In `fleet-20260917-005029.jsonl`, bot `-1403596`, frame `10687` (68.258 s), clip `tarkov_walk_aim_270`, minimum separation between opposite thigh/shin centerline segments changes from 0.1814 m before placement to 0.000445 m afterward. The left knee moves 0.2797 m. The three-view replay shows the added crossing. This is concrete evidence of a placement-induced clearance problem; the sub-millimeter number is below the recorder's millimeter coordinate precision and should be read as near-zero, not an exact collision depth.

Open `artifacts/diagnostics-resume/fleet-fixed.html`, select that bot and jump to frame 10687; inspect neighboring frames as well. `clearance-case.png` and `fleet-geometry-spots.json` in the same directory preserve the snapshot and other candidates. The current per-foot solver has no explicit inter-leg clearance constraint. Directional-family anchor retention and rapid direction changes are hypotheses to investigate before designing a correction. These fixes do not resolve this remaining case.

A candidate correction should constrain the airborne foot's swing path against the opposite leg, preserve established support contacts, and check the whole swing interval rather than only landing positions. First verify that blended family members agree on contact phase and that retained anchors are still reachable during turns. A blanket minimum left/right foot spacing would erase legitimate crossover steps and can introduce skating; it is not a suitable replacement for 3D clearance checks. This is a proposed next algorithm experiment, not an implemented or validated fix.

## Technical references

[Valve's Character Locomotion in Half-Life: Alyx](https://media.steampowered.com/apps/valve/2021/Half-Life_Alyx_Locomotion_Slides.pdf) describes FootBase, stride-relative paths, predicted steps, ankle targets, hip alignment, and swing-path adjustments for leg intersections.

[NVIDIA Kimodo's metric definitions](https://research.nvidia.com/labs/sil/projects/kimodo/docs/benchmark/metrics.html) pair several foot-skating measures with contact-consistency checks. Its numerical thresholds belong to its evaluation protocol, not this game's rigs.

[Generalizing stepping concepts to non-straight walking](https://doi.org/10.1016/j.jbiomech.2023.111840) explains path-relative step measurements and valid crossover steps. [Task-based Locomotion](https://www.cs.ubc.ca/~van/papers/2016-TOG-taskBasedLocomotion/index.html) includes deliberate pivots, side steps and slides, reinforcing the need for movement context.
