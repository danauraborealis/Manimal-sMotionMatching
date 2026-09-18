"""Build the movement comparison tables: Tarkov baseline captures vs our Alyx-build captures vs Alyx source clips.

    python tools/movement_report.py runs.json > docs/movement_tables.md

runs.json: {"baseline": {"scenario": "<run dir>", ...}, "alyx": {...}, "glb": {"<model.glb>": ["clip", ...]},
            "retarget": {"<posedb_v0.json>": ["clip", ...]}}
Each run dir must hold off/status.json (LastCapturePath). Rows are per moving segment (steady part), plus the
segment's start and stop windows, so starts, cycles and stops can be compared separately.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import movement_analysis as ma

COLUMNS = [("speed_mps", "speed m/s", None), ("cadence_steps_per_s", "steps/s", None), ("stride_m", "stride m", "p50"),
           ("stance_width_m", "width m", "p50"), ("contact_slide_mm", "slide mm p50", "p50"), ("contact_slide_mm", "slide p90", "p90"), ("contact_travel_mm", "travel p50", "p50"), ("contact_excursion_mm", "excursion p50", "p50"), ("plants", "plants", None),
           ("swing_height_m", "swing m", "p50"), ("knee_flexion_deg", "knee min", "p10"), ("knee_flexion_deg", "knee max", "p90"),
           ("knee_splay_deg", "splay deg", "p50"), ("pelvis_bob_m", "bob m", None), ("pelvis_yaw_deg", "pelvis yaw p10/p90", "range"),
           ("torso_vs_pelvis_yaw_deg", "torso-pelvis yaw", "p50"), ("torso_pitch_deg", "torso pitch", "p50"), ("foot_yaw_deg", "foot yaw p10/p90", "range")]


def cell(metrics, key, sub):
    v = metrics.get(key)
    if v is None:
        return "-"
    if isinstance(v, dict):
        if sub == "range":
            return f"{v['p10']}/{v['p90']}"
        return str(v.get(sub, "-"))
    return str(v)


def table(rows):
    head = "| source | part | " + " | ".join(c[1] for c in COLUMNS) + " |"
    sep = "|" + "---|" * (len(COLUMNS) + 2)
    out = [head, sep]
    for label, part, m in rows:
        out.append(f"| {label} | {part} | " + " | ".join(cell(m, k, s) for k, _, s in COLUMNS) + " |")
    return "\n".join(out)


def capture_rows(label, run_dir):
    status = json.loads((Path(run_dir) / "off/status.json").read_text(encoding="utf-8"))
    report = ma.analyse_capture(status["LastCapturePath"], label)
    rows = []
    for i, seg in enumerate(report["segments"]):
        tag = f"{label} seg{i} ({seg['seconds']}s)"
        if seg["steady"]:
            rows.append((tag, "steady", seg["steady"]))
        rows.append((tag, "start 0.8s", seg["start"]))
        rows.append((tag, "stop 0.8s", seg["stop"]))
    return rows, report


def main():
    spec = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    sections = []
    raw = {}
    for mode in ("baseline", "alyx"):
        rows = []
        for scenario, run_dir in spec.get(mode, {}).items():
            try:
                r, rep = capture_rows(f"{mode}:{scenario}", run_dir)
                rows += r
                raw[f"{mode}:{scenario}"] = rep
            except Exception as ex:
                rows.append((f"{mode}:{scenario}", f"error {ex}", {}))
        sections.append(f"## {mode} captures\n\n" + table(rows))
    rows = []
    for glb, clips in spec.get("glb", {}).items():
        rep = ma.analyse_glb(glb, clips)
        raw["glb:" + Path(glb).name] = rep
        for c in rep["clips"]:
            rows.append((Path(glb).stem, c.get("name", "?"), c if "error" not in c else {}))
    if rows:
        sections.append("## Alyx source clips (scaled to EFT leg length)\n\n" + table(rows))
    rows = []
    for db, clips in spec.get("retarget", {}).items():
        rep = ma.analyse_retarget(db, clips)
        raw["retarget:" + Path(db).name] = rep
        for c in rep["clips"]:
            rows.append((Path(db).stem, c["name"], c))
    if rows:
        sections.append("## Retargeted clips as our database plays them\n\n" + table(rows))
    print("\n\n".join(sections))
    Path("tmp/movement_report_raw.json").write_text(json.dumps(raw, indent=1), encoding="utf-8")


if __name__ == "__main__":
    main()
