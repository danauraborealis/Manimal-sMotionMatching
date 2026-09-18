"""Collect harness runs into the spec that movement_report.py reads.

    python tools/movement_runs.py <first run dir name, e.g. 20260915-235324> > tmp/movement_runs.json

Runs at or after the given folder name are grouped as baseline (no pose clip in the StartPuppet line) or alyx,
keyed by their scenario. The Alyx source clip and retarget sets for the comparison are fixed here.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    since = sys.argv[1]
    spec = {"baseline": {}, "alyx": {}, "glb": {}, "retarget": {}}
    for run in sorted((ROOT / "artifacts/puppet").iterdir()):
        if run.name < since or not (run / "run.log").exists() or not (run / "off/status.json").exists():
            continue
        log = (run / "run.log").read_text(encoding="utf-8", errors="replace")
        m = re.search(r"Puppet running: (.+?)(?: with pose| with foot lock|\. Ctrl)", log)
        if not m:
            continue
        scenario = m.group(1).strip()
        if scenario.startswith("stop:1;move:0.3:6;curve"):
            scenario = "turns"
        mode = "alyx" if "with pose" in log else "baseline"
        spec[mode][scenario] = str(run)
    spec["glb"] = {
        str(ROOT / "tmp/alyx/combine_grunt.glb"): ["sprint_alt_tight_n", "sprint_alt_tight_e", "stand_to_run_down_axis_tight_n", "run_to_stand_down_axis_tight_n",
                                                   "new_short_hop_0a_tight_n", "new_short_hop_2_tight_e", "strafe_n_to_strafe_tight_s", "strafe_e_to_strafe_tight_w",
                                                   "run_n_to_run_e_down_axis_tight", "run_large_turn_tight_e", "turn_left_90_rifle_up", "turn_right_180_rifle_up",
                                                   "strafe_to_run_tight_e", "run_down_axis_to_strafe_tight_e"],
        str(ROOT / "tmp/alyx/combine_soldier_heavy.glb"): ["com_sol_hev_walk_combat_loop_01_n", "com_sol_hev_walk_combat_loop_01_e", "com_sol_hev_run_sg_combat_cycle_01_n",
                                                           "com_sol_hev_run_sg_combat_cycle_01_e", "stand_to_walk_combat_a_n", "combat_walk_to_stand_a_n"],
    }
    spec["retarget"] = {
        str(ROOT / "tmp/alyx/posedb_heavy_runs.json"): ["com_sol_hev_run_sg_combat_cycle_01_n"],
        str(ROOT / "tmp/alyx/posedb_heavy_loops.json"): ["com_sol_hev_walk_combat_loop_01_n"],
        str(ROOT / "tmp/alyx/posedb_startstop.json"): ["stand_to_run_down_axis_tight_n", "run_to_stand_down_axis_tight_n"],
    }
    print(json.dumps(spec, indent=1))


if __name__ == "__main__":
    main()
