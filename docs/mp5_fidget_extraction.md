# MP5 fidget extraction

Source: `G:/Projects/vance/mp5/alyx_mp5_lessintense3.6.blend`.
Frame 0 is the user-confirmed neutral pose. The `MP5` bone is the weapon root;
hand motion is measured relative to it, as it was relative to `Body` on the SPAS-12.
The source file is opened with `--disable-autoexec` and never saved.

Source SHA-256: `7b0e280570d91e96572a82d985ab02cede2f8cf162f8a65a5b4a61223f204175`.

| Source action | Inclusive frames | Samples | Duration (approximately) |
| --- | --- | --- | --- |
| `fidget` | 0–100 | 101 | 1.667 s |
| `fidget2` | 0–56 | 57 | 0.933 s |
| `fidget3` | 0–44 | 45 | 0.733 s |

The effective source rate is 59.9999991 FPS. Preserve that timing in the export.
The source's first action is named `fidget`, not `fidget1`; exports preserve its name.

Full source-space exports are in `artifacts/fidgets/mp5/`. Compact weapon and
anatomical hand/finger data are in `src/MotionMatching/Data/mp5/`. These are separate
from the existing SPAS-12 set. Both sets are embedded and share one six-clip
runtime pool for weapons whose calculated inventory width is exactly 3 or 4 cells.
Neither width is assigned exclusively to one source weapon.

Export each action with `--action <name> --reference-frame 0 --weapon-bone MP5`.
Use `tools/extract_fidget.py` with `--output` and `--runtime-output` for source and
weapon data, then `tools/extract_fidget_gesture.py --output` for gesture data.
Both scripts run inside background Blender with the source file opened.

Validation passed for all three pairs: matching sample counts/timing, neutral
start/end transforms, continuous quaternion signs, and reconstruction within the
exporter's numerical tolerances. Each gesture resource contains the support-hand
track and all 15 left-finger rotation tracks. Right-hand/finger variations were
only numerical noise (right-finger quaternion component error below 1.24e-7),
so the same left-side runtime format is sufficient. The source hash was unchanged
after export. Visual compatibility on Tarkov's compact-weapon grips remains to test.
