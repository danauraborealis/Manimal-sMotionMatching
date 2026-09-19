# MotionMagic player test build

This is a player test build of Manimal-MotionMagic 0.1.2. Source code is available at [Manimal-sMotionMatching](https://github.com/danauraborealis/Manimal-sMotionMatching). This package has not undergone a complete publication-guideline audit.

This build targets solo SPT 4.1.5. Fika raids are unsupported for this test. Install UnityToolkit separately; the build was checked against the installed UnityToolkit 2.0.2.0 baseline.

## Install and play

1. Install the UnityToolkit version required by the mod in your SPT installation. The test package does not include UnityToolkit.
2. Close the game and extract the contents of `Manimal-MotionMagic-0.1.2.zip` into the SPT game folder, preserving its `BepInEx/plugins/Manimal-MotionMagic` path.
3. Start SPT and play a raid normally. Diagnostics collect automatically; there are no tester hotkeys or special bot-control steps.
4. After a raid, look for its diagnostic ZIP in `BepInEx/LogOutput/Manimal-MotionMagic/Reports` inside the SPT folder. Send that ZIP with your feedback so the movement and reaction events can be compared with what you saw.

Reports cover up to eight nearby visible bots and keep about two seconds before and three seconds after selected events. To bound disk use, a report is capped at 64 MiB, and the mod keeps up to ten reports within a 250 MiB total. If the game crashes before a ZIP is finalized, send the newest `.partial.jsonl` checkpoint from that same `Reports` folder instead. Nothing uploads automatically.

The installer archive contains only the plugin DLL and its two runtime animation databases. The adjacent `.manifest.json` records the archive and installed-file SHA-256 hashes; it stays outside the install ZIP.

## Performance settings

Normal raids activate the mod within 80 m of the local player by default. Active bots remain eligible to 95 m to avoid switching repeatedly at the boundary. Outside 20 m, unseen bots deactivate after a two-second grace period. Bots using Tarkov's simplified skeleton also use native animation. Visibility uses Tarkov's existing flag, not a separate line-of-sight raycast.

Adjust **Performance → Animation distance** or **Cull unseen bots** through the BepInEx configuration. Increasing the distance is useful for observing distant bots through scopes, but increases work. Outside the active set, bots continue their normal AI and native animations; mod hit reactions and placement are inactive. Re-entry creates fresh placement state. Manual developer fleet/puppet tests retain their existing coverage.

## Analyzing returned reports

Run `python tools/raid_report.py report.zip --output artifacts/player-report` to generate a summary and a standalone skeleton comparison viewer. The reader also accepts a `.partial.jsonl` checkpoint. Recorded stages distinguish the incoming animation, native visual adjustment, mod placement, and final pre-render pose. The replay does not reconstruct the game mesh, terrain, or weapon model; diagnostic flags identify samples to inspect, rather than proving visual defects.

For foot pitch, `python tools/raid_sole_analysis.py report.zip path/to/alyx_posedb.json --output artifacts/sole-analysis.json` compares incoming and final heel/toe heights in matched locked support samples. Use the database matching the report's recorded hash. Authored toe-off can legitimately raise a heel; the added pitch is the useful comparison. Older reports without a skeleton-root quaternion assume a yaw-only root.
