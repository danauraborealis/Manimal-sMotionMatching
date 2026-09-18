# Placement clearance guard and sprint-entry follow-up

## What changed

`FootPlacer` now compares the incoming and requested leg geometry across all four opposite thigh/shin segment pairs. If placement reduces clearance below `min(0.06 m, incoming clearance * 0.5)`, it attenuates that pass's pelvis translation and local leg rotations. Eight coarse samples and seven bisection steps find an accepted correction. Local rotation blending preserves limb lengths. The guard also runs during fade-out.

This is a relative joint-center heuristic, not a body-mesh collision solver or an anatomical stance rule. An already-crossed incoming pose is retained. The 6 cm cap is a diagnostic policy, not a claim that every human gait needs that separation. Interpolation is checked at discrete candidates, not as continuous swept collision detection.

When limited, incompatible plant/cycle anchors are released, the accepted sole feeds the existing residual decay, and cached knee poles are rebuilt on the next solve. This deliberately trades some plant stability for avoiding placement-induced leg collapse. Both legs share the correction fraction; future work may preserve more support contact through a constrained solve. Pending placement flags are cleared on reset so a finished fade cannot revive a stale request.

Captures record `ClearanceBefore`, `ClearanceRequested`, `ClearanceAfter`, `ClearanceFraction`, and failure code 2. Fleet equivalents are `clearance0`, `clearanceWanted`, `clearance`, and `clearanceFraction`; final bot records include `clearanceLimits`. These are sampled on the same frame as the before/after geometry. The normal capture summary includes the full intervention count.

## Evidence for the original defect

Fleet `fleet-20260917-005029.jsonl`, bot -1403596, frame 10687 switched from the 225-degree walking member to the 270-degree member while retaining the left support anchor. The authored contact labels agreed; a bad cycle label was not established. Placement moved the left ankle 0.401 m and knee 0.280 m, reducing opposite-leg clearance from 0.1814 m to 0.00044 m. The dominant defect was endpoint displacement rather than a knee-pole flip. Merely adjusting pole smoothing would not resolve it.

The regression fixture checks that the production distance routine detects this geometry. The historical joint record lacks the full hierarchy rotations needed to rerun that exact frame through Unity; live tests below exercise the integrated guard separately.

## Sprint-entry observations

`SprintEntrySnapshot` records requested sprint, native and managed state, movement direction, context condition flags, speed/pose/ground state, physical sprint availability, and stamina. It does not force native gates. `PhysicalCanSprint` is an entry gate; it can become false below the entry threshold while an existing sprint continues.

Two identical phase-lock-on routes before this guard had 33/1,431 and 817/916 requested samples physically sprinting. Changing phase locking was therefore not necessary for the successful repeat. Those earlier samples lacked physical stamina gates and cannot establish the original refusal's cause.

The complete-controller follow-up recorded 207 requested samples with physical sprint unavailable and sprint off; stamina was 6.618–14.999, below the installed game's strict `>15` entry threshold. This explains those later refusals without bypassing native stamina. It does not retroactively prove the cause of the first run.

## Validation so far

- 59 Python checks and C# sprint-inertia, stride-timing, and new leg-clearance harnesses pass. The new harness compiles the production geometry helper and tests intersecting, parallel, degenerate, endpoint and finite-input cases plus the captured regression geometry. It does not execute Unity's transform hierarchy.
- Two isolated `tarkov_` single-clip tests exercised 20 limited frames with no clearance-floor violations. They do not validate normal directional selection or shared sprint phase.
- Full `startstop` controller capture `motion-capture-2-20260917-023421-014-0003.json`: 4,771 paired samples, 3,760 placement passes, 17 limited frames, no clearance-floor violations. Maximum measured segment-length change on limited frames was 0.033 mm. These tests cover sprinting, directional walking, a walking curve and stops.
- That full-controller run still had 19 anchor failures and 1,750 reach clamps. Nearby ankle steps reached 10.7 cm and include controller movement and authored motion. The guard is not a certificate of smoothness; anchor prediction, transitions and contact continuity remain subjects for diagnosis.
- Arm/leg command phase: 650 comparable samples, median and p90 absolute error zero, with two isolated samples above 0.05 cycles. No sustained visible arm-motion window passed the duration/continuity gates; visible synchronization remains unassessed in this route.

Reports and an offline front/side/top skeleton replay are under `artifacts/sprint-entry-clearance/`. `check_capture.py` reproduces clearance, segment-length and sprint-gate summaries. Bone lines cannot establish clothing/mesh intersection.

Independent code review found no material defect. Independent trace verification reproduced the full-controller report exactly but flagged immediate relocking after six of nine intervention clusters, and ankle/knee step increases up to 46/44 mm over the incoming track. A no-guard counterfactual is needed to isolate attribution. Captures do not explicitly record `Fading`, so actual fade-branch intervention coverage is not proven.

Final-build fleet validation completed 120 seconds across seven bots: 9,765 samples, 111 sampled interventions, no recorded floor violations; 64 interventions had visible/unsuspended geometry and also passed the offline comparison. One usable observed arm-motion window changed by 0.0292 cycles (16.4 ms), below the diagnostic threshold. See the implementation log for timing, build identity and remaining continuity work.
