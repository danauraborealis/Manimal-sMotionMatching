# Valve audit implementation

This follows the [reference audit](valve_principles_audit.md). The slides are a design reference; EFT/SAIN still own root movement, collision, aiming, and the native upper-body animation.

## Runtime changes

- Foot feedback expires after one render frame, is invalidated by reset, and is republished from the accepted post-clearance pose during release as well as active playback.
- Directional pose and stride sampling share the same member pair, mapped frames, and weight. A stable reference supplies cycle identity and progression. The blended source trajectory is re-decomposed against that reference; member offsets with different progressions cannot simply be averaged. Both feet's cycle/contact correspondence is checked before a family is formed.
- Midpoint data is loaded, including derivation for existing databases, and exported explicitly for new databases. A smooth endpoint-zero correction guides the stride through its predicted midpoint. Landing and midpoint use the same path/turn and bounded decaying velocity-difference forecast. Midpoint prediction freezes after its time is reached.
- Ankle orientation starts with the shortest change in hip-to-ankle direction applied to the original foot rotation. Sole axes come from exported geometry. Alignment uses fresh per-foot Grounder surface data and contact/flatness weight. Relative ankle deviation is limited around the authored calf-relative orientation; coupled IK passes preserve the sole target where reachable. Final residual error is recorded after the clearance guard.
- Selection uses sole footbases, optional circular progression, remaining-step filtering, future trajectory positions at equal traveled distances, and fresh current-placement demand. Nonurgent loop changes wait for support while dwell continues through flight. Native phase-locked candidates are evaluated at the phase that will actually play.
- Arm/leg command-phase and observed upper-arm/thigh tracking remain active. Neither an unavailable observed window nor matching commanded phase proves natural arm movement.
- Stop entry retains a future real landing for **each** foot. Final airborne steering uses the touchdown clock and the database's native end stance where available. A stopped root no longer retains a fictitious braking remainder. Heel/toe lock acquisition and failed-anchor recovery retain the previous rendered sole pair in the current footbase reference. Failed-anchor recovery preserves vertical displacement through re-entry, then decays it; it no longer rebuilds the held foot at clip height.
- A bounded one-foot corrective step can finish a mismatched native stop endpoint. It moves the sole center through a lifted trajectory while holding the support foot; it does not spread planted feet by fading to idle. The target is the freshly sampled native animator sole center and heading before playback; native handoff waits for measured rendered stance agreement, rather than comparing fixed database ankles. Subsequent EFT IK can still alter that native pose. Failed settlement is recorded, and its old timed slide into idle is disabled.
- Swing clearance planning tests an approximate two-bone path against the supporting leg and chooses a bounded outward detour. Contact endpoints stay fixed. Blocked endpoints, late-entry limits, and unresolved plans are recorded. This is a sampled bone-line estimate, not continuous or mesh collision detection. Reactive clearance recovery first tries preserving the support leg and pelvis while attenuating the swing correction.

## Limits and interpretation

The midpoint correction cap (0.20 m), velocity-difference contribution cap (0.15 m over a 0.25 s decay), and ankle deviation bound (45 degrees around the authored pose) are explicit project policies, not universal anatomical limits. Unreachable targets prioritize reach and ankle bounds and report sole error. The clearance guard remains a relative bone-line safeguard; it does not detect skinned mesh collisions.

The test API `SetLocomotionRefinement(bool)` and `Run-PuppetTest.ps1 -LocomotionRefinement On|Off` enable process-local comparisons. Off restores legacy selection/placement algorithms while retaining the feedback lifecycle fix. It is not a byte-identical comparison against the saved baseline DLL. Captures record the active setting, stride source/partner/weight, fade state, ground-normal coverage, ankle limitation, midpoint correction, and final sole target error.

## Diagnostic tools

- `tools/placement_diagnostics.py`: paired pre/post geometry, trailing feet relative to body and travel, straightness/reach, leg proximity, sole drift, later render writes, and sustained episodes with coverage exclusions.
- `tools/placement_viewer.py`: standalone front/side/top skeleton replay with before/after overlays, frame navigation, clearance intervention and final sole-target error.
- `tools/refinement_report.py`: solver coverage, target-error distributions, midpoint/ankle interventions and clearance-floor checks, separated by refinement setting.
- `tools/arm_sync_report.py`: commanded phase and observed arm/thigh phase windows, with combat and signal-quality exclusions.
- `tools/Run-PuppetTest.ps1` automatically generates geometry, solver, arm-sync reports and a replay for puppet, observe, fleet and A/B modes. Replay issue navigation includes stop/hold entry, clearance intervention onset and heuristic foot jumps, with raw/final movement and frame duration shown separately.

Use these together. A short proximity event during a valid crossover step is different from sustained penetration introduced by correction. Geometry measurements and skeleton replays do not replace animation/mesh inspection.

## Verification

Offline verification and live comparison results are recorded in `implementation_log.md` and the corresponding `artifacts/valve-implementation` reports. Runtime tests use production algorithm code; the hierarchical transform harness approximates Unity transform/IK behavior and does not substitute for the installed game.
