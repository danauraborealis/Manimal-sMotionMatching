# Weight and hardened-role reaction tuning — deferred

Design only, requested 2026-09-18. No weight, boss or guard modifiers are implemented.

## Inputs and ownership

Read actual carried kilograms from the bot's inventory total weight, not an inertia or encumbrance percentage. The installed client exposes Inventory.TotalWeight as a deferred value and invalidates it on equipment changes. Its physical bridge subscribes to weight changes; AI encumbrance exceptions do not mean the inventory weight is absent. Prefer the raw inventory total over elite-skill-adjusted physical weight for this design.

Classify roles using Profile.Info.Settings.Role and explicit configurable boss and guard sets for the installed version. Do not use the broad native IsBossOrFollower helper: it also includes PMCs and several special AI roles. Define ordinary fallback behavior for unknown/modded roles. Boss takes precedence if configuration overlaps.

## Proposed model

Keep the existing two-second damage window, per-event random roll, probability cap, and cooldown beginning only when playback starts. Separate the load-dependent baseline from the damage ramp:

```
loadBase = clamp(lightBase + chancePerKg * max(0, carriedKg - lightLoadKg), lightBase, maxLoadBase)
effectiveBase = loadBase * roleBaseMultiplier
chance = min(maxChance, effectiveBase + damageChancePerHP * roleDamageMultiplier * recentHealthDamage)
```

Illustrative starting values, subject to approval/tuning: lightBase 5%, lightLoadKg 10 kg, chancePerKg 0.4 percentage points, maxLoadBase 25%. This yields 5% at 10 kg, 13% at 30 kg and 21% at 50 kg. Preserve the current 0.5 percentage points per point of post-armor health damage initially so only one aspect changes at a time.

Suggested role multipliers for comparison: ordinary 1/1, guards 0.75/0.75, bosses 0.5/0.5 for baseline/damage ramp. At 30 kg after a single 45-health-damage hit, that would mean 35.5% ordinary, 26.625% guard and 17.75% boss. These are proposed values, not game settings. Keep the existing 85% total cap and 2.5-second cooldown initially.

Weight should affect the chance of reacting, not automatically select a more violent clip. Keep clip intensity driven by damage pressure and feasibility; landing recovery has its own trigger and should not roll this hit policy. This avoids heavy equipment accidentally making every response a severe stagger.

## Implementation sequence for later

1. Add a read-only diagnostic of raw kg, role, weight availability, and proposed baseline; compare light/heavy loadouts and loot changes in live bots.
2. Add configuration and a pure chance calculation with bounded, finite inputs. Invalid/unavailable weight falls back to the ordinary light-load baseline with a diagnostic flag. Validate role precedence and unknown-role fallback.
3. Pass a snapshot of kg/role at each hit into the policy. Preserve damage expiration, no queued cooldown hits, and cooldown-on-actual-playback. If reactions expand beyond the selected bot, keep independent policy state per bot and clean it up on despawn/raid end.
4. Expose baseline, damage contribution, final chance, roll and rejection reason in replay diagnostics. Test distributions over many seeded hits, not a handful of visible reactions.
5. Compare ordinary, guard and boss test targets at matched damage and loads. Tune caps/multipliers before enabling broadly. Do not alter damage, aiming, firing, health, armor or native inertia as part of this feature.

Acceptance checks: monotonic baseline with kg until the cap; monotonic chance with recent health damage; boss/guard modifiers reduce both requested components; quiet-window reset, cooldown and failed-entry behavior remain correct; two bots never share accumulated damage or cooldown; changing loadout updates subsequent hit chance without retroactively changing earlier rolls.

Installed-code evidence: tmp/assembly_audit/EFT.Player.cs/EFT.Player.decompiled.cs (Player.Init), tmp/assembly_audit/EFT.BotOwner.cs/EFT.BotOwner.decompiled.cs (Create/AI ownership), tmp/Physical.decompiled.cs (weight limits/OnWeightUpdated), and tmp/assembly_audit/EFT.MovementContext.cs/EFT.MovementContext.decompiled.cs (WeightRelatedValuesUpdated). This is a source audit, not a live weigh-in test. AI overload thresholds are raised separately from weight-derived inertia; some AI speed paths bypass the player ramp.
