# Weapon-only revolver fidgets

Source: `G:/Projects/vance/revolver/uhhh1.blend`, weapon bone `Body`, frame 0 neutral.
SHA-256: `460dfb83905b8d332d788dca25085d77a106f10221fe05f6509131815faff549`.

| Action | Frames | Samples at 60 FPS | Duration |
| --- | --- | --- | --- |
| `fidget` | 0–148 | 149 | 2.467 s |
| `fidget2` | 0–54 | 55 | 0.900 s |
| `fidget3` | 0–94 | 95 | 1.567 s |

Export with `tools/extract_fidget.py --weapon-only --weapon-bone Body`, specifying
the action, `--reference-frame 0`, source `--output` and compact `--runtime-output`.
Run in background Blender with `--disable-autoexec --python-exit-code 1` and never
save the source blend. This mode samples only the weapon track; hand and finger
tracks are not exported. No gesture converter is used.

The three compact files live in `src/MotionMatching/Data/revolver/`; source-space
weapon-only diagnostics live in `artifacts/fidgets/revolver/`.
Widths 1 and 2 share all three entries, selected uniformly. Each has a null gesture
track, so playback never captures or applies additive hand/finger motion for these
clips. Native Tarkov hand IK still follows the weapon and retains its grip poses.
All endings use 0.25 seconds and the shared randomized 9–15-second cooldown.
The package check rejects revolver gesture resources. No new keys are added.

All three exports passed neutral/end, quaternion continuity and reconstruction
checks. Independent validation confirmed the full source exports contain exactly
one track (`weapon`), and the source hash is unchanged. Runtime tests cover both
widths, all three durations, null gesture entries and the existing size 3–6 pools.
