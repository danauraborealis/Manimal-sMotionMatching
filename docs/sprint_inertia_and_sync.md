# Sprint inertia and arm/leg synchronization

This pass separates controller behavior from animation synchronization. Restoring sprint acceleration does not establish that the arm and leg poses agree.

## Controller scope

The installed SPT 4.1.5 `MovementContext.SprintAcceleration(float)` already evaluates the player's sprint acceleration, physical state, speed limits and yaw-dependent inertia curve. Stock `BotMover.MovePlayer` also writes `SprintSpeed = 2` repeatedly, including one direct property assignment. The installed SAIN movement controller and our puppet driver call `EnableSprint` and `Move` without those forced speed writes.

The implementation intercepts sprint-speed writes only for attached, enabled AI rigs. Native acceleration and exit-reset writes remain authoritative; lower driver requests must remain effective. Attaching mid-sprint must preserve the current speed. Native sprint exits, braking animation, collision handling and route termination remain in control. Controls and human players are excluded. The non-sprint `SpeedRampPatch` is a separate feature.

Test both the policy and actual intercepted-write counters. A puppet A/B alone cannot prove this fixes stock AI acceleration because the puppet already follows the native sprint path.

## Two synchronization measurements

- **Playback phase agreement:** animator base-state phase, state identity, transition status, state duration, actual played leg phase, circular phase error and the phase-lock decision/reason. This diagnoses lost or inappropriate phase locking. It is not proof of visible arm synchronization.
- **Observed motion:** fresh Root_Joint-local sagittal angles from upper arm to elbow and thigh to knee, at the native pre-playback stage and after the animation/placement pass. Compare continuous periodic motion against the native reference. Low-amplitude arms, aiming/shooting, transitions, missing data, culling and short windows cannot establish synchronization and must remain explicitly unassessable.

Puppet `MotionSync` signals are sampled independently at each capture stage. `Pose.Sync` describes the playback/animator sample and carries its sampling context. Fleet `syncBefore` is captured before `Pose.Apply`, and `syncAfter` is after placement on the same frame; `sync` is the playback-phase snapshot. This before/after pair spans playback and the game's visual/IK pass as well as placement. It does not isolate the foot placer alone. Existing `h0/k0/a0` versus `h/k/a` geometry still brackets the placement pass specifically.

Angles are in degrees. Missing-angle flags must not be treated as measured zeros. A lag is a diagnostic observation, not a universal gait rule: an armed character need not swing its arms like an unarmed walking reference.

## Test controls

`Run-PuppetTest.ps1` accepts `-SprintInertia On|Off|Keep` and `-AnimatorPhaseLock On|Off|Keep`. Phase-lock test overrides are process-local and do not rewrite the user's saved phase-lock setting. Effective modes must be recorded with captures. Restore normal phase locking after deliberately disabling it to test the detector.

The follow-up adds a relative placement-clearance guard and physical sprint-entry diagnostics; see [placement guard evidence](placement_clearance_guard.md). Exact historical-frame replay and broad visual acceptance remain open. Turning-stop selection and new running-turn clips require separate controller/animation acceptance checks.

## Offline report

Run `python tools/arm_sync_report.py <capture.json-or-fleet.jsonl> --output <report.json>`.
NumPy is needed for observed-motion fits; without it those windows are explicitly unavailable.

The command report compares only `SprintCycle` clips with the `tarkov_` prefix, whose phase basis is shared with the native animator. Held stop poses and Alyx clocks do not have that correspondence. It reports signed/absolute circular error and episodes exceeding 0.05 cycles with bot IDs, clips, frame ranges and times. Deliberately disabling phase locking still permits this measurement.

Observed motion uses timestamp-based sinusoidal least squares, so capture intervals need not be uniform. It requires at least 24 samples, two seconds and 2.5 fitted cycles; all four native/final arm/thigh signals must pass amplitude and periodic-fit checks. The fit includes a linear trend but requires the periodic component itself to explain the motion. Travel is measured over about 0.1 seconds because controller position updates can be less frequent than rendered frames. Gaps, teleports, state/clip/driver changes, transitions, culling and unknown combat context break usable windows. Windows are at most six seconds and may overlap.

The report labels changes exceeding 0.08 cycles relative to the same-frame native reference. These thresholds are diagnostic heuristics, not anatomical limits. A positive arm-minus-thigh phase means the arm leads in the fitted cosine cycle. Small offsets do not certify a natural animation, and an unavailable window does not establish synchronization. Inspect the reported frame range alongside the placement replay before changing animation data.

## Verification

The production-source `tests/runtime_sprint_inertia` harness checks repeated external writes, native updates and exits, lower-request caps and expiry, human/inactive scope exclusions, detach/reattach, exception cleanup, nested scopes and isolation between contexts. `tests/runtime_stride_timing` protects the earlier temporal contact fix. Restore harness projects with the repository `NuGet.Config`, then run with `--no-restore`.

Build/package validation and both harnesses pass. The full Python suite passes 59 tests, including ten arm-sync tests for signed/wrapped phase, irregular timestamps, quantized movement, nonperiodic or quiet arms, missing pairs, context boundaries, combat exclusions and unavailable NumPy. The game startup log confirms that all three sprint patches load against SPT 4.1.5.

The first live capture exposed incorrect arm bone names. The probe now uses installed typed BipedReferences (`leftUpperArm`, `leftForearm`, and right equivalents), with verified Tarkov names as fallback. Diagnostic bone snapshots were corrected too. Repeated Woods captures have both arm/thigh signals available in all 2,647 and 2,116 final-pose samples respectively.

Recorded Woods comparison: `motion-capture-6-20260917-015540-558-0001.json` (phase lock on) and `motion-capture-6-20260917-015714-955-0002.json` (off). On: 958 comparable native-cycle samples, median/p90 command error zero. Off: 723 comparable samples, median 0.1777 and p90 0.1977 cycles. Physical sprint coverage differs, so these are not a controlled naturalness or acceleration A/B. The on run has no assessable observed-motion window (short or quiet arms). The off run has two windows with native-relative changes of 0.0514 and 0.0537 cycles, below the alert threshold; this is not a claim that every frame is synchronized.

The unlocked puppet recorded 779 native acceleration writes, two native resets and zero patch exceptions. A subsequent Woods fleet recorded 6,875 rows across eight bots, with 2,064 native writes, 11 resets and zero exceptions. No external upward writes were observed in that SAIN-controlled sample, so it cannot demonstrate suppression by itself. The raid ended early; its recorder was explicitly finalized and the discovered missing raid-exit cleanup was fixed. Reports are in `artifacts/sprint-sync/`.

Some initial puppet sprint commands did not enter sustained physical sprint. The available captures cannot distinguish all native entry gates; they omit native state, movement direction, `CanSprint` and physical-condition flags. Do not attribute this to phase locking or claim it fixed from these runs. Broader sprint-entry coverage and the existing turning-walk clearance defect remain open.

Final Factory check (`motion-capture-2-20260917-020607-846-0001.json`): during the attached puppet's stationary step, a single bridge request to raise `SprintSpeed` from 1.0 to 1.5 left it at 1.0. The capture recorded exactly one suppressed external write and zero exceptions. Both arm signals were available in all 2,788 final samples. This verifies live setter interception, not a claim of improved sprint motion in that short lane. `live-setter-check.json` and `final-factory-checks.json` preserve the evidence.

The final recorder exit test (`fleet-20260917-020645.jsonl`) automatically finalized 1,364 samples with `Disabled or raid unavailable` after a normal raid exit; its terminal JSONL record and `raid-exit-check.json` confirm cleanup. Final built/installed DLL SHA-256 matches (`0f2be965a50c2b0e...`), compiled metadata/runtime-only package checks pass, and the final game log has no Error/Fatal entries. Normal phase locking and native sprint inertia were restored; the test client was stopped.
