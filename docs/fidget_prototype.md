# MotionMagic fidget prototype

The existing plugin now includes an automatic and manual, first-person `fidget1` / `fidget2` / `fidget3` player with
weapon, support-hand and left-finger layers, plus a weapon-only revolver set. Its embedded clips come from the supplied SPAS-12, MP5, sniper, tau and revolver animations, with frame 0
as the neutral pose (user-confirmed for fidget2; endpoint validation for fidget1/3). Existing leg and reaction features remain in
the same plugin. The internal namespace and project directory remain
`Manimal.MotionMatching`; the installed identity comes from `Directory.Build.props`.

## Local test

Install the verified MotionMagic player-test package with the game closed. Use
`tools/Install-MotionMagic.ps1` for the MotionMatching-to-MotionMagic migration.
Do not leave both old and new plugin DLLs loaded.

Enter a raid in first person, stand still with a ready firearm, and press
**Left Alt + F7 / F8 / F9** for fidget1 / fidget2 / fidget3 respectively. Press any fidget key during playback to fade out; press the desired key again after the fade to play it. The key works independently of the
bot developer-control switch. Settings live in the `Fidgets` section of the new
plugin config. `Enabled` controls all fidgets. `Automatic playback` defaults to true;
disable it to use only the manual test keys.

Weapons with `Weapon.CalculateCellSize().X` equal to **3 or 4** use the same pool
of six shotgun/MP5 clips. Widths **5 and above** share a second pool containing all six
sniper/tau clips. Each pool selects its six entries with equal probability.
Widths **1 or 2** share the three revolver clips, using only weapon transforms.
Those entries have no hand/finger gesture resource and do not capture or apply
the additive hand rig; Tarkov retains its native grip and hand IK.
Invalid widths (zero or negative) have no assigned pool
and do not start fidgets, including manual previews. Width is recalculated before
each start, so folding/attachments are reflected on the next playback. The pool
is never changed in the middle of playback. The shared cooldown keeps running
even while holding a weapon with an unsupported width.

The existing **Alt+F7/F8/F9** keys preview the first three clips in the current
size pool (revolver for 1/2, shotgun for 3/4, sniper for 5+). No new keys are added; automatic
playback selects from both source sets in the current pool.
MP5's first source action is named `fidget`.

Automatic playback uses one shared deadline per local-player session, starting
a random 9–15 seconds after the local player becomes available. Weapon switches, reloads,
inspections, ADS and other busy states do not reset or pause it. Once overdue,
the next eligible frame selects a clip from that size's pool with equal probability.
Each actual playback start (including manual playback) draws a fresh uniform
9–15-second delay for the next deadline. It is sampled once per start, not each
frame; an overdue deadline remains due while the weapon is busy. There is no
backlog of missed fidgets. A new raid/player resets it with a new random delay.
Tarkov readiness uses the firearm's `Idling` operation and animator `IsIdling()`;
we do not wait for a looping Tarkov idle sequence to end.

Twelve weapon/gesture pairs and three weapon-only revolver clips are embedded in
the DLL. For the shotgun set at 60 FPS, fidget1 is
141 samples / 2.333 seconds, fidget2 is 45 samples / 0.733 seconds, and fidget3
is 79 samples / 1.300 seconds. Each clip retains its authored timing and uses
the same shared pivot and strength settings. Clip selection is fixed for a playback.
MP5's three clips last approximately 1.667, 0.933 and 0.733 seconds.
All fifteen clips blend back to the native pose over their final 0.25 seconds.
The ending blend uses a smooth
curve shared by weapon, hand and fingers, without changing clip duration or the
shared randomized cooldown.
Manual cancellation still uses the separate 0.10-second fade.

Compare one weapon without a foregrip, then with a foregrip, then a second long
gun. Look for tilt direction, subtle translation, both hands following the gun,
and a clean return to idle. Repeat several times to check for accumulating drift.
Test aiming, movement, reload, inventory, weapon switch, and firing during playback.
The trigger path stops immediately before native firing. ADS, sprint, weapon
actions, weapon replacement, death, leaving first person, and disabling the
feature remove the additive pose as soon as detected. Manual cancellation fades
over 0.1 seconds. Ordinary walking permits both starting and finishing a fidget.

Start is restricted to a free weapon, grounded, right-shoulder use without ADS,
blindfire, mounting, prone, inventory, or a hands interaction. These restrictions
keep the first test focused. Aim/fire/reload/switch behavior still needs in-game
verification with other installed mods.

`Source unit metres` defaults to **0.0254**, a provisional inch-to-metre assumption,
not a completed physical calibration. `Strength` defaults to 1. The converted
rotation uses the neutral Blender camera basis; the live offset uses Tarkov's
current camera orientation and pivots around its animated weapon root. Different
source/target pivots and physical dimensions may require tuning after playback.

### Pivot tuning

`Pivot right metres`, `Pivot up metres`, and `Pivot forward metres` offset the
rotation center from Tarkov's animated weapon root in view-relative metres.
Positive axes mean screen-right, screen-up, and away from the camera. Defaults
are zero, preserving the first prototype as a comparison baseline; no calibrated
grip-based offset is assumed yet. Values are limited to +/-0.5 metres and apply
on the next playback.

- **Left Alt + Page Up:** move the pivot 1 cm farther from the camera.
- **Left Alt + Page Down:** move it 1 cm toward the camera.
- **Left Alt + Home:** reset all three offsets to zero.
- **Left Alt + F7 / F8 / F9:** play fidget1 / fidget2 / fidget3 and compare the arc.

The depth keys persist their values to the BepInEx config and log the new offset.
All three axes are also configurable through the usual config entries. Pivot
values are captured at the start of each playback, so tuning during a fidget
does not introduce a jump. These are shared prototype settings, not per-weapon
profiles or an automatic right-hand anchor.

For a view-relative pivot `c`, blended rotation `R`, and blended authored
translation `t`, the applied translation is `t + c - R*c`. The pivot therefore
follows `t`, while the root rotates around it. The compensation uses the already
blended rotation and is not multiplied by blend weight a second time; neutral
rotation still produces zero compensation. Pivot distances are in metres and
are independent of the provisional source-unit translation scale.

### Hand and finger layers

`Hand strength` and `Finger strength` default to 1 and multiply overall `Strength`.
Set either to 0 to compare against the weapon-only prototype. Both layers use the
same sampled timeline and fade as the weapon. The right hand retains its native
pose: these source clips have no intentional right-hand/finger gesture.

The hand track removes weapon motion in Blender, then expresses the remaining
translation and rotation in a neutral anatomical palm frame. In game, it offsets
the native left-hand IK target **after** Tarkov has selected the attachment grip
and **before** the limb solve. The elbow and forearm are solved by Tarkov.
The numerical target (and any explicit FinalIK target reference) is restored
afterward; shared weapon/grip transforms are not edited.

All 15 left-finger joints receive local rotational deltas **after** native
`HandPoser.ManualUpdate`. Positions and bone lengths are preserved. Mapping is
captured once per playback from the resolved grip: palm direction and across-palm
direction define the hand frame; finger length direction defines each joint frame.
The thumb uses palm normal as its secondary direction. Source geometry is reflected
into Unity handedness before computing equivalent frames, so raw source XYZ axes
are never copied onto target bones. Finger directions come from joint positions,
not Blender's display tails (which point sideways on the imported Source rig).
Leaf joints reuse their chain's verified longitudinal axis convention when no
finger-tip end transform exists.

Mappings are rejected if the expected palm/solver/bones or a non-degenerate frame
are unavailable. Only that playback's hand/finger layer is skipped; weapon motion
continues and the reason is logged. This is a geometric calibration prototype,
not a guarantee of contact on every attachment. Compare a conventional grip and
a vertical foregrip, including curl direction and thumb contact. Reduce the
finger strength if necessary. Cleanup removes the prior local offsets before
native evaluation and on cancel, switch, death or disable.

Camera animation is not included in these additive clips.

## Installed-code update order

Inspection of the target `Assembly-CSharp.dll` shows `Player.VisualPass` runs:

1. `ProceduralWeaponAnimation.ProcessEffectors` (procedural pose and camera).
2. Cache native `PlayerBones.Offset` and `DeltaRotation`.
3. Body/shoulder work and `PlayerBones.ShiftWeaponRoot`.
4. `Player.IkProcess`, which reads hand markers and attachment grip targets.
5. Elbow adjustment and `Player.IkApply`, including `HandPoser.ManualUpdate`.

An SPT `ModulePatch` prefix on `IkProcess` applies this prototype's weapon offset
at step 4, after the native root caches were written. Restore hooks on
`ComplexUpdate`, `ArmsUpdate`, and `VisualPass` remove our last offset before the
next native evaluation; repeated calls therefore do not add another delta onto
the prior frame. Cleanup compares each local channel against the value we wrote
so a newly animated channel is not replaced by a stale saved pose.

A prefix on `FirearmController.SetTriggerPressed(true)` restores the native root
and cancels the clip before the trigger operation can calculate a shot. Direct
shot-entry hooks also cover standard, launcher, flare and rocket shots before
their fireport reads. `WeaponOverlapping` runs later in the visual pass: its
prefix temporarily restores the native pose for obstacle detection, and its
postfix reapplies the same rendered fidget pose if playback and the base transform
are still valid. Thus cosmetic motion does not feed the next frame's native
obstacle/aim restrictions. Only the
captured local player/controller are affected. All patches use SPT's wrapper.

The log records readiness, refused starts, weapon template, playback settings,
and missing hand-IK passes. Conversion validation and runtime unit tests cover
the data and timing; a successful build is not a claim of verified in-game visuals.

