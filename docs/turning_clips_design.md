# Turning starts, stops and cuts: design note (stage F)

Status note (2026-09-16): this preserves the original stage F proposal. Turning starts have since been implemented; the blanket "nothing implemented" status is obsolete. Turning-stop selection and additional running-cut clips still need implementation and acceptance checks. See `implementation_log.md` for current evidence and `sprint_inertia_and_sync.md` for the active sprint/arm-sync work.

## Why

The grunt set (the one Valve motion-matched, and the one the user wants preferred) has 7 directional run starts,
7 directional run stops, 4 running cuts and 8-direction hops beyond the straight pair we use. `tools/build_posedb.sh`
with `GRUNT_EXTRA=1` builds them into `tmp/alyx/alyx_posedb_stride_v2.json` (119 clips), but the runtime cannot
use them yet: measured in the export, every directional start and stop turns the body by its named angle while
travel relative to facing stays near zero (`stand_to_run_down_axis_tight_s` turns 180 and runs forward), and the
cuts turn 93 to 180 degrees with travel ending forward. Our start/stop/cut roles assume the body heading is what
the controller says and only follow travel direction; the sprint transitions are the one place a clip's yaw
progress (`yawProgress`) is already used.

## Who owns the body's yaw

In Tarkov the controller does, and it will keep doing so: `BotSteering` (stock) rotates toward the next corner at
its configured degrees per second, SAIN rotates through `Player.Rotate` itself, the puppet turns in place through
its turn steps. None of them will wait for an animation. So a turning clip cannot drive the body's yaw the way
Valve's root motion does; it can only be chosen and timed so that its authored yaw matches the yaw the controller
is going to produce anyway. That is still what Valve's matcher does (facing at the future step is a metric, weight 3):
select the clip whose yaw trajectory fits, then let the foot layer keep the feet honest.

## Proposal

1. **Yaw prediction from the driver.** `MoveIntent` gains `Func<float?> GoalYaw`: the yaw the body will end up
   facing, relative to the current facing. Puppet: the step's target heading. Stock mover: direction to the current
   corner (already there as `Yaw`), or to the next corner when the current one is within a metre. SAIN: direction to
   its next path corner. Measured fallback: yaw rate times 0.5 s.

2. **Turning starts.** A start is picked by two numbers: travel direction relative to facing at the end of the clip
   (today's `moveYaw`) and the body yaw change over the clip (`yawChange` = last minus first `yawProgress`). When the
   driver's goal yaw differs from the facing by more than 30 degrees, prefer the start whose `yawChange` is nearest
   that difference; otherwise the straight start. While it plays, the body turns at the controller's rate and the
   clip at its own; the placer already predicts each landing with the smoothed body yaw rate (`YawRate`, slide 51),
   so the feet land on the body's actual path. The clip's pelvis yaw is written as before (the spine is restored to
   Tarkov's), so the torso keeps aiming where the controller looks while the hips and legs follow the clip.

3. **Rate from yaw, not distance, during the turn.** A turning start's first part covers little distance, so the
   distance-driven frame advance would stall it. Advance the frame so the clip's accumulated `yawProgress` tracks the
   body's accumulated yaw (same slew band as today), switching to distance matching once the clip's yaw change is
   90 percent done. Same idea as the stop being distance-driven.

4. **Turning stops.** Chosen when the driver stops with a large heading change pending (rare in the stock mover,
   common for the puppet's turn steps and for SAIN peeking). The named stop turns after braking, so entry stays the
   braking-distance rule; the turn part plays after the body has stopped, yaw-driven as in 3 against the
   controller's in-place rotation. If the controller does not turn, the clip's turn is skipped: hand off to idle
   at the end of braking as today.

5. **Running cuts.** `TryBeginCut` today matches from/to travel yaw within 35 degrees. Add the body yaw change over
   the next 0.5 s (from `GoalYaw` or measured) as the second key and pick the `run_n_to_run_*` clip whose `yawChange`
   is nearest; play yaw-driven until the turn is done, then distance-driven. The placer's arc march (yaw rate look-
   ahead) covers the feet.

6. **Speed.** The clip-driven start ceiling applies to turning starts too: the body may not outrun the clip's root
   speed, which for the directional starts is low during the turn, so the controller's fast 270 deg/s turn is what
   gets tempered, not the clip.

## What this does not do

- It does not make the body turn like the clip. If the controller turns in 0.6 s and the clip in 1.1 s, the clip
  is rate-matched up to the same bands as today (0.5 to 2.5), and hurried frames are counted.
- It does not add in-place turns (`turn_left/right_*`); those need the controller to be standing, which the puppet
  does but bots rarely do for long.

## Acceptance

Puppet scenario `turns` and a new `turnstart` (stop, turn 90 while starting, move) measured with the same tools:
in-plant pops 0, plant hold under 10 mm, hurried frames not above the straight start's, knee frames added by the
placer under 20 per raid, and the events log showing the directional clips chosen with `yawChange` within 30 degrees
of the body's measured turn.

## Review (Codex, 2026-09-16 night, `tmp/codex/turning-design-review.md`)

Verdicts, kept here so the implementation starts from them:

1. Controller-owned yaw is right; playback writes local rotations with the root yaw removed at retarget and the
   spine restored, so there is no double turn. Authored pelvis counter-twist can still read wrong against the kept
   torso when timing is off. **Proposal 6 is wrong**: the start ceiling limits acceleration through `ChangeSpeed`,
   not angular rate; it cannot temper a 270 deg/s turn.
2. Reuse the slew primitive, not the start policy: yaw matching needs zero-progress handling (no positive minimum
   rate), bounded overshoot, plateaus and cancellation. Sprint transitions already use max(yaw target, distance
   target) rather than exclusive yaw following. At the handover, keep the frame and smoothed rate, snapshot the
   actual distance and clip path at that frame and match increments from there; `PathBehind` is a diagnostic, not a
   debt. 90 percent of a 180 leaves 18 degrees unfinished.
3. Path corners are travel intent, not facing intent; `moveYaw` in the export is the fastest stretch, not the final
   travel. Corner prediction fails worse while peeking (confidently predicts an unrelated turn); measured yaw rate
   lags reversals and overpredicts near completion. Prefer an explicit steering target plus rate and horizon; abort
   through the inertialized hand-off on sustained opposite rotation, stalled progress, changed target,
   incompatible travel or unachievable timing; never rewind; compare signed, unwrapped remaining turns with hard
   eligibility bounds, including handedness at 180.
4. Stock steering does not turn below 0.04 m/s, so an arrived bot keeps the facing it reached; turning stops need
   an actual pending steering command. The current stop tail plays at 1x after braking, not a hand-off.
5. `FootPlacer` (landing facing, ~line 593) adds the clip's authored yaw delta AND the measured body turn; under
   this design they are the same turn, so one coherent facing prediction is needed. Keep the plant locks and the
   wrap-only stride boundary; rewinds or skipped contacts misidentify strides; locks cannot promise millimetre
   acceptance numbers when the mismatch is large.
6. Minimum safe step for database v2: emit and validate yaw metadata (net change and maximum excursion) for every
   clip, not only transitions (missing values load as zero); reject turning starts/stops/cuts before `BuildSets`
   (first-wins grouping) until yaw following exists. Loading v2 safely is achievable now; using its turning clips is
   not.

## Addendum from the first live-bot capture (2026-09-16, 04:30)

A stock scav running a 31 m path swung its body yaw by about 170 degrees every 1.5-2 s while the world travel
direction stayed put (bots look around while they move; vanilla covers it with the directional blend tree). Two
consequences for this design:

- Facing and travel must be separate inputs everywhere. The cut trigger now keys on world travel yaw; the
  directional cycles (8 directions of walk, run and sprint) are what a facing swing selects. A turning start or
  cut may only be chosen when the WORLD travel direction is what changes, never on a facing swing alone.
- The yaw prediction in section 1 has to come from what the controller intends to face (steering target), not from
  the path corners, as the review said; the measured yaw rate during a look-around would otherwise select a
  turning clip for a body that is about to swing back.
