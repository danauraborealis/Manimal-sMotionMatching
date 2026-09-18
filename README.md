# Manimal MotionMatching — player test build

This test build applies clip-based locomotion, foot placement, hit reactions and landing recovery to normal SPT bots. Raids automatically collect bounded local diagnostic reports; players do not need hotkeys. Developer puppet tools remain available separately.

## Target

Built against the installed SPT 4.1.5 client in `D:\SPT41Dev`, with UnityToolkit 2.0.2 (ZLinq 1.5.3). Fika authority and replication are not implemented. The source URL is intentionally unset until a real repository exists; this is not a publication-compliant release.

## Installation

Extract the runtime archive from `artifacts` into the matching SPT installation. It contains the plugin DLL and two required animation databases under `BepInEx/plugins/Manimal-MotionMatching`. UnityToolkit must already be installed. Restart the game after installing or replacing the DLL. See [player test instructions](docs/player_test_release.md).

Play a normal solo raid. On raid end, death, or plugin disable, a local ZIP is saved under `BepInEx/LogOutput/Manimal-MotionMatching/Reports`. Send that ZIP with feedback. Nothing uploads automatically. To analyze a returned report, run `python tools/raid_report.py report.zip --output artifacts/player-report`; open the generated `replay.html` to compare recorded native/final skeleton stages.

Detailed replay coverage is limited to up to eight nearby visible bots. Selected windows retain roughly two seconds before and three seconds after a trigger, subject to recorded frame/output limits. Anomaly flags identify frames to inspect; they are not automatic proof of a visible defect. A crash may leave a `.partial.jsonl` recovery checkpoint; the report reader accepts that file too. The recorder retains up to ten reports within a 250 MiB disk budget.

## Developer capture (optional)

Enable **Player test → Developer hotkeys** and the overlay for this workflow. Starting a developer capture ends automatic reporting for that raid so two controllers cannot own the same bot.

1. Enter a local raid with bots. Find a nearby living bot and stay close enough to see its animation.
2. Press **Left Ctrl+F6** to select the nearest bot within 35 metres. The overlay marks it as TEST BOT.
3. Press **Left Ctrl+F7** to record its normal AI movement, or **Left Ctrl+F8** for a controlled walking route.
4. The capture finishes automatically. **Left Ctrl+F9** stops immediately and releases the bot.

The F12 configuration manager exposes duration, selection range, route length, route move speed, overlay, master enable, and configurable shortcuts. Defaults use Left Control, not Right Control.

Route move speed is the game's normalized 0–1 `Player.Speed`, not metres per second. A value of 1 ran at about 2.7 m/s in the first captures. The mapping of lower values to walking has not been verified. When the route ends, the overlay and log report requested speed, averaged `Player.Speed`, measured steady m/s, and how many times something else rewrote the target speed.

The walking route requires enough traversable space. A rejected route should produce a clear status message; use another location rather than forcing the bot through obstacles. Other bots can still attack the selected bot. This is a local diagnostic tool, not a combat encounter controller.

Controlled routes require an active, standing bot outside combat and abort if it acquires an enemy. Observation mode still works during normal AI combat. The selected bot's brain is temporarily detached, then reattached when still valid; the test does not destroy or recreate it. SAIN handling is limited to the selected bot and the inspected installed API, with live compatibility awaiting the first raid test.

## Puppet mode

Live bots sprint, stall, and change plans on their own, so repeatable measurements use puppet mode instead. **Left Ctrl+F10** takes the nearest bot, skips its AI update (`BotOwner.UpdateManual`), and moves its body from a script. The script uses the same `Player.Move`, `ChangeSpeed`, `Rotate`, and `EnableSprint` calls as `BotMover`, so the real animator, IK, and visual pass still run. By default, puppet mode freezes every other bot and teleports the puppet to a clear, visible lane in front of you. The capture ends when the script finishes, and each step's timing and result are recorded in the capture's `Puppet` block.

Scenarios are set under F12 → Puppet. The named options are `sweep` (speed legs from 0.1 to 0.625, sprint, and curves), `walk`, `turns`, and `stops`. You can also write custom steps: `move:speed:metres`, `sprint:metres`, `stop:seconds`, `turn:degrees:degPerSec`, `curve:speed:degrees:degPerSec`. Captures showed a maximum `Player.Speed` of 0.625, so higher speed values should behave the same (not verified). Move steps shorten themselves before NavMesh edges, and they end early if blocked or stuck.

### Automated runs through SPT AI Bridge

`tools/Run-PuppetTest.ps1` performs a complete run through the bridge on the clean test root (`D:\SPT41AStar` by default):

1. Deploys the DLL and starts the server and client (`-Launch`).
2. Enters an offline Factory raid with bot amount Low, after checking the 10 GiB free-RAM floor.
3. Waits for an active bot.
4. Calls `MotionMatchingTestApi.StartPuppet`.
5. Records step progress and a screenshot.
6. Writes the capture summary to `artifacts/puppet/<timestamp>/`.

`-LeaveRaid` exits the raid afterwards. The static `Manimal.MotionMatching.MotionMatchingTestApi` (`Status`, `Scenarios`, `StartPuppet`, `Stop`) returns JSON and can also be invoked directly through the bridge.

## Foot lock prototype (Milestone 3)

F12 → Correction → Foot lock, or `StartPuppet(..., footLock: true)`, applies a foot lock to the test bot only. It runs in the `VisualPass` postfix. A planted foot locks horizontally where it lands, keeping its animated height and foot rotation. A two-bone IK bends the thigh and calf to reach it. The lock releases at toe-off, when the FootStep curve flips, or after 12 cm of drift. The remaining offset then decays at no more than 0.6 m/s so the foot never snaps back.

When a lock is active, captures add an `after_lock` stage. They also add a `pre_render` stage from the first camera cull of the frame, which records what actually renders. In the first A/B test, nothing wrote the feet after `VisualPass`. `summarize_capture.py` reports `lock_correction`, `after_last_hook`, and `correction_pops` (rendered foot speed beyond the clip's, above 1.5 m/s). `tools/Run-PuppetTest.ps1 -FootLock ab` runs lock off and lock on in the same raid and writes `comparison.json` with `tools/compare_puppet.py`. `tools/Stop-TestClient.ps1` exits the raid and closes the client before a redeploy.

Latest A/B test (`artifacts/puppet/20260915-024902`), with every leg completing in both runs. Core planted-foot slide (p50) with the lock off → on:

| Leg | Off | On |
|---|---|---|
| Speed 0.1 | 20 mm | 3 mm |
| Speed 0.2 | 25 mm | 3 mm |
| Speed 0.3 | 28 mm | 3 mm |
| Speed 0.4 | 62 mm | 3 mm |
| Speed 0.5 | 71 mm | 7 mm |
| Speed 0.625 | 99 mm | 16 mm |
| Sprint | 92 mm | 14 mm |

Movement speeds matched between runs, and zero frames exceeded the 1.5 m/s pop threshold (max 0.81 m/s).

The lock and the summarizer use the same heuristic to decide stance, so the slide reductions they report are partly true by construction. Visual review and pop counts must back up any claimed improvement. Pelvis adjustment, hitbox effects, slopes, and stairs remain untested.

## Stride data (Milestone 2 output)

`tools/export_posedb.py` now attaches Valve-style foot motion data to every clip (`stride` per clip, built by `tools/stride_data.py`). Per foot: foot cycles between stance frames (slowest frame of each contact; a foot planted at a clip edge is anchored there, a foot in the air at an edge gets a virtual anchor) with stance position and heading, stride length and yaw, rotation change, lift/off/strike/land fractions and the strike frame; per frame: cycle index, progression along the stride, stride-relative translation offset, rotation offset, FootBase and a planted flag. `stepsRemaining` counts landings still to come (null for loops). FootBase is the lower of two sole points fixed in the Foot bone (heel 3 cm behind the ankle, toe tip 4 cm past the Toe bone, both on the floor at rest). The runtime rebuild is `foot = start + progression * stride + R(stride) * offset`, and `tests/test_stride_data.py` proves the stored form round-trips.

Contact detection is tuned to the retarget's errors: a sole point counts as touching within 10 cm of the clip's lowest frame while moving under 1 m/s. On the current database that gives run strides of 1.65 m (p50) and walk strides of 1.10 m; a stop's landings end 25 frames before its root stops.

`tools/alyx_retarget.py` maps foot heading from the ankle bone's forward axis (`--foot-axis bone`, the default since 2026-09-16). Valve's ball joint sits 14 deg outside that axis in the bind pose of both soldiers, so the earlier ankle-to-ball mapping toed every EFT foot out by 14 deg on top of the clip; `--foot-axis ball` reproduces those files. The per-file stance, plant and knee arguments used for the current database are listed in `docs/implementation_log.md` (2026-09-16, foot axis).

`tools/build_posedb.sh` rebuilds the current database (the augmented start/stop/cut/sprint set plus the cycle loops in three speed tiers, with the lateral heavy loops stride-scaled). The build command for the original database was never recorded, so `--augment existing.json` locates each clip by content in the retarget outputs passed (positional plus every `--merge`), reslices it identically and writes a `.spec.json` beside the output with the recovered windows:

    python tools/export_posedb.py tmp/alyx/posedb_startstop.json tmp/alyx/eft_skeleton_bundle.json tmp/alyx/alyx_posedb_stride.json --augment tmp/alyx/alyx_posedb.json --merge tmp/alyx/posedb_grunt_strafe.json ... (every tmp/alyx/posedb_*.json)

## Foot placer and Alyx cycles

F12 → Correction → Foot placer (default on) replaces the foot lock and stride warp whenever a pose clip is showing. It is Valve's stride retargeting: each foot keeps a previous step frozen where it landed and a predicted next step (clip root distance marched along the bot's travel), and rebuilds the foot between them from the stride data, then solves the leg. Anchored feet do not move; the correction it applies is the prediction error. `tools/placer_report.py <capture>` reports per phase the correction size, anchored-foot drift, prediction wander and prediction error against the clip's own foot. General → Debug draw shows red previous steps, blue predicted steps (lighter once frozen), green current feet, the cyan travel path and yellow correction lines.

Pose playback → Alyx cycles (default on) keeps the legs on Alyx loops between starts, cuts and stops (the loop is picked by direction and speed and re-picked on drift); sprinting still bridges into Tarkov's sprint cycle. `Walk clips` (0-2) picks the walking-speed family (heavy soldier walks by default, grunt takes, or grunt hops). `Run band legs` (0-2) picks what plays at Tarkov's plain run speed straight ahead, 2.3-2.85 m/s not sprinting: 0 the heavy walk loop sped up to a brisk walk, 1 Tarkov's own run (default: no steady non-sprint run cycle exists in the Valve data, bots spend most of their moving time here, and both Alyx options looked wrong on live bots), 2 the Alyx run and sprint loops. `Run-PuppetTest.ps1 -Daytime` picks the daytime raid slot on any map.

Saved captures go under `BepInEx/LogOutput/Manimal-MotionMatching`. The overlay and BepInEx log report the exact output path. The developer can inspect these files; the user does not need to identify contact frames manually.

Developer analysis: `python tools/summarize_capture.py <capture.json>` reports sample integrity, missing signals, moving speed, IK-stage offsets, and stance sliding by speed band. Passing a capture directory reads its newest capture. Run its checks with `python -m unittest discover -s tests -v`.

For paired foot/leg/body measurements, use `python tools/placement_diagnostics.py <capture> --output report.json`.
Create an offline three-view before/after skeleton replay with `python tools/placement_viewer.py <capture> --output replay.html`.
Audit regenerated stride event data with `python tools/audit_stride_events.py <database> --output audit.json`.
See [placement diagnostics](docs/placement_diagnostics.md) for coordinate meanings, interpretation limits and references.

Captures use schema v2: compact JSON with animator layers on final-pose samples only; full layer records appear only when state changes. The summarizer also reads v1 captures.

## In-plant pop check

`python tools/plant_pops.py <run dir>` counts single-frame jumps of the sole inside detected contact intervals of
a capture's steady segment and, for each, records the root-joint move, the pose-stage (`after_visual`) world move,
the clip frame step and the placer probe (authority, correction before and on the jump frame, failure code,
release). The placer no longer drops a correction in one frame when its anchors drift too far from the clip
(`MaxCorrection`): it holds the sole where it was drawn and blends the offset out through the residual on re-entry.

`python tools/plant_hold.py <run dir>` measures the placed sole directly from the placer probe over each plant
(frozen or locked, full authority, stride at its stance): endpoint, excursion and travel per plant. Use it next to
the gait metric, which judges contacts from bone heights and cannot tell a heel-to-toe roll from a slide.

`python tools/knee_pops.py <run dir>` counts frames where a knee moves faster than 4 m/s at the pose stage and at
the final stage on the same sample times; the excess is what the placer added (a running knee legitimately passes
4 m/s in swing, so the pose-stage count is the baseline).

## Movement intent

`MoveIntent` answers three questions for the pose layer regardless of who drives the bot: desired travel yaw
(null to stand), metres left to the goal, upcoming path corners. Sources: the puppet (planned step), the stock
mover (`BotMover` corners and remaining distance), SAIN's combat path (`BotComponent.Mover.ActivePath`, read by
reflection so the mod loads without SAIN), and a measured-velocity fallback. The playback uses the goal distance
to start a stop clip where its braking run ends at the goal plus EFT's own brake; the placer marches steps along
the corners.

`Run-PuppetTest.ps1 -Scenario observe[:seconds]` records a live bot under its own AI with the clip set attached
through the adapter (`MotionMatchingTestApi.StartObserve`). It waits up to 180 s for a bot whose mover is moving
(two early captures recorded a standing bot for their whole window) and should run first in a fresh raid: after
puppet runs the remaining bots tend to stand. The first such capture showed stock bots swinging their body yaw
about 170 deg while running straight (looking round), which is why cuts are detected on world travel yaw and a
facing change only re-picks the directional cycle. A stock bot's `Path.MoverType` reads `BotMoverImpostor`.

## Fleet mode: every live bot, several raids

`Run-PuppetTest.ps1 -Scenario fleet:<seconds>:<maxBots>` rigs every live bot in the raid with the layer
(`src/MotionMatching/Fleet.cs`, one `BotRig` per bot; prone and crouch suspend it, culled skeletons are marked) and
streams one JSONL per raid to `LogOutput/Manimal-MotionMatching/fleet-*.jsonl`: playback state, body and combat
context, both feet against the placer's predictions and anchors, leg bend, visibility, events. `fleet:...:stock`
records Tarkov's own legs the same way as a control. `tools/Run-FleetRaids.ps1 -Maps factory4_day,bigmap -Seconds 300`
runs alternating layer and control raids with a manifest under `artifacts/fleet/<stamp>/`; `tools/fleet_report.py`
scores a stream (sliding on locked feet, freezes, trailing/leading, knee bend, big corrections, anchor failures,
hurried playback, wrong gait tier, clips under sprint, cut spam) per bot, situation and clip, with the worst
episodes and frame ids; `tools/fleet_aggregate.py <manifest.json>` pools counts and exposure across raids, layer
against control, per moving minute. Speeds come from positions (`Player.Velocity` over-reports). Six Factory bots
cost about 0.25 ms per frame, sixteen on Customs about 0.7 ms.

## Tarkov's own clips on the placer

`tools/tarkov_clips.py <character_animations.bundle> <eft skeleton json> <out.json> --clips run_aim_0,...` reads the
third-person locomotion clips straight from the game's bundle (UnityPy; per-bone euler and quaternion curves plus
RootT root motion) and writes the same retarget shape `export_posedb.py` consumes, so Tarkov's run, walk and sprint
cycles get stride data, contacts and predictions like the Alyx clips (names `tarkov_<clip>`). Euler curves compose
X, Y, Z (the only order with a still planted toe); the four cycles with a constant root yaw are folded into the
facing frame. With `Run band legs` 1 the placer plays `tarkov_run_aim_*` in Tarkov's plain run band when the
database carries them, and hands to Tarkov's animator otherwise. `Animator phase lock` (default on) keeps a Tarkov cycle on the placer at the phase Tarkov's animator is playing
the same clip on the arms and torso, so legs and arm swing stay in step. `Sprint legs` 1 (default) keeps a sprinting bot on
Tarkov's sprint cycles with locks and predicted steps, entered through Tarkov's stand-to-sprint transition or picked
mid-sprint; a sprint stop is still Tarkov's power slide. Tarkov's 8-way sets are phase-aligned families: a facing
swing under a straight run changes blend weights between two neighbours at one phase instead of restarting a
cycle (member switches are logged as `family a -> b`, the fleet stream records the partner as `bl`/`bw`). `Turn in
place` (default off: judged not worth it) plays Valve's turn clips (22, 90, 180 degrees, either hand) matched to the turn the controller is
making while a bot stands: the clip follows the body's yaw (never drives it), planted feet pivot, turns chain, and
the stance is held afterwards instead of blending into Tarkov's idle (a long idle releases it slowly). Game data
stays local like the Valve data.

## Second opinion from Codex

`tools/Ask-Codex.ps1` hands a brief plus a list of repo files to the Codex CLI that ships inside the Codex desktop
app (found under `%LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe`, or `codex` on PATH) and writes its reply to
`tmp/codex/<timestamp>-<slug>.md`, with the full event log (`.log`) and the session id (`.session`) beside it.
Read-only sandbox by default; `-Sandbox workspace-write` lets it edit; `-Resume <session id>` continues an
exchange. Claude Code uses it to get reports and diffs reviewed without shuttling documents by hand.

```powershell
tools/Ask-Codex.ps1 -Brief "Review docs/movement_analysis_report.md against the code it cites; list factual errors with file:line" -Files docs/movement_analysis_report.md,src/MotionMatching/FootPlacer.cs
```

## What is measured

Samples are taken after body animation, before the visual pass, after the full-body IK call, and after the visual pass. They are observational hooks using SPT's `ModulePatch` wrapper.

Bot grounder legs were empty in the first captures, so stance is inferred from the animator `FootStep` curve sign: about −1 for the left foot and +1 for the right. The stance foot was the slower foot in 92–94% of moving frames. This remains a heuristic contact indicator, not a validated planted-foot label, and sliding statistics based on it are preliminary. A hook being called also does not prove the solver ran: distance, visibility, culling, and simplified skeleton state can affect animation updates.

## Build

Run `./build.ps1` from PowerShell. To use another compatible installation, pass `-SptRoot 'D:\YourSptInstall'`. References come from that installation; the build does not download packages. Identity/version are defined in `Directory.Build.props`.

Packaging also requires the main and reaction animation databases. They are local build inputs, currently defaulting to `tmp/alyx/start_upper_posedb.json` and `tmp/alyx/reaction_upper_posedb_expanded.json`; `tmp/` is not committed. Supply existing compatible databases explicitly when building from a fresh checkout:

```powershell
./build.ps1 -SptRoot 'D:\YourSptInstall' `
  -MainPoseDatabasePath 'C:\YourAssets\alyx_posedb.json' `
  -ReactionPoseDatabasePath 'C:\YourAssets\reaction_posedb.json'
```

The export and retargeting tools are retained in `tools/`, but a complete clean-checkout asset-generation workflow is not yet documented. A source checkout alone therefore cannot recreate the install package. To compile only the plugin against an existing SPT/UnityToolkit installation, use `dotnet build src/MotionMatching/MotionMatching.csproj -c Release -p:SptRoot='D:\YourSptInstall'`.

The script checks compiled plugin metadata and creates a runtime-only ZIP. Source, this guide, diagnostic captures, debug symbols, and dependency DLLs are excluded from the install archive.

## Verification status

The September 16 evening test pass on `D:\SPT41AStar` completed the interrupted variation and SAIN/ORBIT work, then tested fixes for variation entry selection, temporal contact gates and an invalid exported stride cycle. The new puppet run completed four 12 m strafes, a 6 m walk and an 8 m sprint; its three variations had valid entries. A subsequent 180-second Factory layer/control pair completed with complete paired leg/sole geometry. No foot was locked in 4,485 puppet or 11,766 fleet airborne authored-swing samples. The control's recorded pre/post geometry was identical. These are targeted behavioral checks, not proof that all animation is natural; reach clamps and anchor failures still occur.

The development build verifies compiled identity, UnityToolkit dependency metadata and runtime-only package contents. See [placement diagnostics](docs/placement_diagnostics.md) for the analysis/replay workflow. Detailed capture history and exploratory notes remain local and are excluded from Git. This is a local development build, not a publication-compliance claim.
