# Valve locomotion principles: implementation audit

Follow-up implementation: [Valve audit refinements](valve_refinements.md). The comparison below records the pre-change findings; follow-up verification belongs to the implementation log and artifacts.

Reviewed 2026-09-16 against the current source and local 133-clip database. Runtime baseline: DLL SHA-256 `5F6A8B76EA09B9F9C5F4216424C49B411D3E459B4730020A61CE7F81EE040F21`.

Source: [Valve, Character Locomotion in Half-Life: Alyx, SIGGRAPH 2021](https://media.steampowered.com/apps/valve/2021/Half-Life_Alyx_Locomotion_Slides.pdf). All 89 pages were text-extracted; the relevant stride, IK, blending, prediction and selection diagrams were inspected. The PDF is a design reference, not a complete executable specification. PDF SHA-256: `188ECE20E295131C30A9EFB63D64F6E20FEEF88E4C1A72BEC751E0C33677125A`.

## Conclusion

The implementation follows much of the stride-data and transition structure, but is a partial adaptation. Several runtime omissions sit upstream of the recent clearance guard. The guard should remain a fallback while we reduce the corrections that trigger it. Its successful clearance checks do not establish that the complete animation system follows the reference or looks natural.

This pass changes the implementation priorities and records evidence. It does not modify the runtime, install a new build, or claim a new visual comparison.

## Comparison

The reference principles relevant here are: footbase trajectories (12-19); staged ankle alignment and IK (25-29); midpoint-aware curves (34); synchronized blending versus transitions (36-43); stable landing prediction (47-58); constrained selection and correction-aware scoring (72-80); footbase, cyclic progression and remaining-step features (82-86). Page numbers below use the printed slide numbers.

| Area | Current implementation and status |
| --- | --- |
| Footbase and clocks, 12-19 | **Substantially present.** `tools/stride_data.py:40` and `FootPlacer.Foot` use heel/toe sole geometry. Exported stance cycles, stride-relative offsets and unwrapped rotation feed reconstruction. `StrideCycle.CycleTime` separates temporal contact events from spatial progression. Keep the recently fixed distinction. |
| Transitions, 41-43 | **Present with exceptions.** `FootPlacer.Enter:779` and `StartFromReference:812` adjust the prior anchor, clamp reconstructed stride length and carry residual position. Late/landed entry branches use different approximations. The clearance guard invalidates both cycles/locks and can immediately re-enter; that recovery policy is our addition. |
| Landing persistence, 55 | **Present, weakened by recovery.** `UpdateFoot:476` retains/finalizes anchors, propagates the previous landing and pins heel/toe points. Anchor failures and clearance intervention deliberately release support. The prior live trace's immediate re-locking after six of nine limited clusters warrants a continuity test. |
| Curved stride retargeting, 34 | **Incomplete.** `Reconstruct:968` uses a straight anchor reference plus rotated per-frame authored offsets. That already preserves some source curvature; it is not an entirely straight foot trajectory. However, the runtime has no explicit constraint preserving the authored midpoint when anchors change. Exported `middleOffset`/`middleProgression` exist in all 643 cycles but are absent from `StrideCycle` and its parser. These fields are not automatically a world-space midpoint; a correct conversion must be designed and tested. |
| Directional blend consistency, 36-38 | **Incomplete.** `PosePlayback.Write:2761` blends the partner's bone rotations and pelvis. `TryGetStrideState:753` exposes only one clip/frame and no partner/weight. `FootPlacer.UpdateFoot` consequently samples one member's offsets, headings and contact gates for a blended pose. Active-member swaps can change the data source while support anchors persist. This is a confirmed data-flow mismatch, not proof that it alone caused the captured crossing. |
| Family synchronization, 38-39 | **Assumption needs validation.** `BuildFamilies` uses equal frame count and a left-foot height event. `FamilyFrame:2180` bypasses offsets while native animator phase locking is active. Same-family anchors are retained based on matching cycle indices. The local 225/270 walk pair differs in raw contact labels on 4 left and 6 right frames out of 48; those differences do not by themselves prove native phase locking wrong. Check both feet's event correspondence before changing the shared animator clock. |
| Ankle orientation/terrain, 25-29 | **Incomplete.** `LegIk.SolveToward:39` restores the original world foot rotation at line 74. `FootPlacer.Place:985` then applies a bounded yaw. The correction pass has no explicit hip-relative foot-angle preservation, flatness-dependent sole alignment, terrain-oriented footbase or second ankle-limit solve. EFT's earlier grounder is not equivalent to rechecking orientation after our final displacement. |
| Prediction, 47-58 | **Partial adaptation.** `Predict:826` integrates clip distance, marches path corners, estimates future yaw and smooths the landing estimate. `Inertializer` decays pose/pelvis offsets, but no transition velocity residual is integrated into this landing prediction. Actual movement stays owned by EFT/SAIN; do not transplant an animation-driven root controller without accounting for that boundary. |
| Selection, 72-86 | **Partial.** Role/gait/direction constraints, speed and foot-position costs, current-loop retention, distance-to-stop checks, support mismatch penalties and stop-entry deferral exist. General loop repicks remain timer/drift-driven, without a support-contact gate. `FeetCost:2431` compares ankles, not sole footbases. There is no cyclic progression feature in that cost. `StepsRemaining` is loaded for 83 clips but never read after loading. Scalar clip path length and distance matching are not a multi-distance trajectory score. Clearance/reach/residual demand is not fed back into current-loop selection. |

## What this means for the recorded defects

The audit also found a concrete state-invalidation gap: `Foot.Reset:1141` leaves `HasLast`, `LastAnkle` and `LastPlanted` intact. `LastAnkle`/`LastPlanted` can therefore expose a retained result after reset, and `PosePlayback.ReadShownFeet:2372` accepts those callbacks when playback weight exceeds 0.5. The stale feedback is code-confirmed; whether it caused a particular bad entry has not been reproduced. Clear or generation-check this feedback, including fade completion and disable/re-enable paths, before changing the larger algorithm.

The historical 225-to-270 family switch pulled a retained planted ankle about 0.40 m and the knee about 0.28 m. Our evidence identifies excessive endpoint displacement; it does not establish an invalid source contact label or a pole flip. Blended-pose/data disagreement and missing midpoint preservation are plausible contributors that can now be isolated. Calling either the proven sole cause would exceed the evidence.

The clearance guard acts after placement and can sacrifice plant continuity. Increasing its threshold, its iteration count, or the planted correction cap would not repair the upstream data mismatch. The guard's 6 cm/half-baseline policy is project-specific and is not a Valve parameter.

Native sprint inertia, stamina gates, weapon/torso preservation and arm-sync telemetry are also integration decisions. The deck does not validate our arm-phase thresholds. Keep native/final arm measurements and mark short, nonperiodic or combat-constrained windows unassessable.

## Revised implementation order

0. **Invalidate stale placement feedback.** Make reset invalidate the last ankle/support result, verify the playback fallback uses a fresh pose, and test disable/re-enable and fade-to-new-motion lifecycles. Preserve the distinction between the last successfully placed pose and a currently valid feedback sample.
1. **Make family pose and stride sampling agree.** Introduce a single sampled stride description carrying active/partner identity, frames, phase mapping, weights and per-foot contact provenance. Validate both feet's cycles; blend compatible spatial/orientation data without numerically averaging incompatible progression clocks. Use an explicit transition when correspondence fails. Preserve the native arm clock unless evidence demonstrates an incorrect mapping.
2. **Add authored-midpoint-preserving retargeting.** Extend the data contract deliberately, reconstruct the midpoint in a documented coordinate frame, and curve the changed reference while preserving the source animation when anchors are unchanged. Handle stationary strides, cycle wrap and extrapolated progression explicitly. Keep the clearance guard as a last resort.
3. **Correct ankle orientation through the retarget.** Preserve the authored foot-to-leg relationship, then align the sole according to its contact state and usable surface information. Verify toe/heel switching and ankle limits after final placement. Do not simply remove the world-rotation restore; doing so would also carry the solver's artificial straightening/pole rotations into the foot.
4. **Improve selection and stop eligibility.** Use compatible footbase/phase features and the existing remaining-step data before adding more weights. Compare corrected current motion with candidates on the same basis. Then validate turning-stop selection and additional running-turn clips against the better sampling/placement path.

## Acceptance evidence required

- Identity reconstruction with unchanged anchors; exact reference endpoints and a verified midpoint under retargeting. An authored crossover must not be globally prohibited.
- Sweep a fixed family blend continuously across a member boundary; measure target, sole and knee continuity separately from body/controller motion. Include native phase-lock on/off as separate modes, with both feet's contact events and arm phase recorded.
- Capture unmodified, guard-only and revised-retarget variants on the same route. Compare plant drift, foot/body lag, knee velocity, centerline clearance, correction magnitude, anchor churn and support release/re-lock timing. A clearance improvement must not hide a larger plant-slip regression.
- Add explicit `Fading`/recovery-state observations. Test contact handoff and fade/reset, including immediate new movement, rather than inferring branch coverage from an `Idle` phase label.
- Inspect the skeleton replay and rendered character, including uneven ground and weapon use. Record frame cost separately from the existing inclusive fleet timer. Geometry proxies and a low phase error cannot alone certify visual naturalness.

Data audit: `artifacts/valve-principles-review/data-audit.json`. Prior live evidence remains under `artifacts/sprint-entry-clearance/`; no new runtime acceptance claim is made in this audit.
