# Sniper and tau fidgets

These two sets share a six-entry pool for calculated inventory widths 5 and 6.
Widths 3 and 4 retain the shotgun/MP5 pool. All entries use a 0.25-second ending
blend and the shared randomized 9–15-second cooldown. No new keybinds are added.

Frame 0 is used as neutral. The source files are opened in background Blender
with auto-execution disabled and are never saved.

| Set | Source | Weapon bone | Source actions | Inclusive sample counts |
| --- | --- | --- | --- | --- |
| Sniper | `G:/Projects/vance/sniper/alyx_sniper.blend` | `Body` | `fidget1`, `fidget2`, `fidget3` | 141, 67, 73 |
| Tau | `G:/Projects/vance/tau/alyx_tau_NEW_lolol.blend` | `body` | `fidget 1`, `fidget 2`, `fidget 3` | 63, 73, 69 |

Sniper runs at 60 FPS; tau's effective rate is 59.9999991 FPS. Literal spaces in
tau's action names are preserved in filenames, JSON metadata and resource names.
Compact weapon/gesture data live in `src/MotionMatching/Data/sniper/` and
`src/MotionMatching/Data/tau/`; full diagnostic tracks live under
`artifacts/fidgets/sniper/` and `artifacts/fidgets/tau/`.

Source SHA-256 values:

- Sniper: `3e35ed6efe15b847ee12f107f24c1e898ebce120587c0b3ce913c7c452965db7`
- Tau: `1b5a614ae851227765c97438923c224906ab4a1918d6942be5c41c333a892d31`

All six exports passed reconstruction, neutral/end and quaternion-continuity
checks. Source hashes stayed unchanged. Right-hand rotations and finger deltas
are numerical noise; positional drift peaks at about 0.001168 source units
(0.0297 mm at the current scale). The exporter allows up to 0.002 source units
for omitting that negligible right-hand motion, while retaining the strict
rotation and endpoint checks. Weapon/support-hand/left-finger layers remain paired.

Runtime tests cover all four eligible widths, exact pool membership, all twelve
clip pairs, matching durations, neutral endpoints and 0.25-second ending blends.
