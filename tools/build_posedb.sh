#!/usr/bin/env bash
# rebuilds tmp/alyx/alyx_posedb_stride.json: the augmented start/stop/cut/sprint set plus the cycle loops.
# later --merge files win a clip name: the lateral heavy loops (stance 0.7, sidesteps shortened) override the
# forward-tuned heavy files for the six sideways directions, and carry a matching 0.7 stride scale.
# OUT=path picks another output; GRUNT_EXTRA=1 also adds the retargeted-but-unused grunt clips, e.g.
#   GRUNT_EXTRA=1 OUT=tmp/alyx/alyx_posedb_stride_v2.json bash tools/build_posedb.sh
cd "$(dirname "$0")/.."
OUT="${OUT:-tmp/alyx/alyx_posedb_stride.json}"
MERGE=""
for f in tmp/alyx/posedb_*.json; do
  case "$f" in *posedb_startstop.json|*posedb_tight_0.45_8.json|*posedb_heavy_loops.json|*posedb_heavy_runs.json|*posedb_heavy_lateral.json) ;; *) MERGE="$MERGE --merge $f";; esac
done
MERGE="$MERGE --merge tmp/alyx/posedb_tight_0.45_8.json --merge tmp/alyx/posedb_heavy_loops.json --merge tmp/alyx/posedb_heavy_runs.json --merge tmp/alyx/posedb_heavy_lateral.json"
ROLES="--role motion_match_walk_01:cycle:walk:3.0:5.5"
for d in n ne e se s sw w nw; do
  ROLES="$ROLES --role com_sol_hev_walk_combat_loop_01_$d:cycle:walk --role com_sol_hev_run_sg_combat_cycle_01_$d:cycle:run"
done
for d in ne e se sw w nw; do
  ROLES="$ROLES --stride-scale com_sol_hev_walk_combat_loop_01_$d:0.7 --stride-scale com_sol_hev_run_sg_combat_cycle_01_$d:0.7"
  # the lateral walk starts and stops hand into those loops, so they carry the same scale (they are retargeted
  # with the loops' stance too: --stance-scale 0.7 --plant-scale 0.6 --knee-inward 10)
  ROLES="$ROLES --stride-scale stand_to_walk_combat_a_$d:0.7 --stride-scale combat_walk_to_stand_a_$d:0.7"
done
# grunt walking starts, stops and loops cut from the motion_match_walk takes (every take starts from a stand and
# ends in one; 17 and 18 walk backwards). the takes are in tmp/alyx/posedb_mm_takes.json (knee 10, plant 0.7)
if [ -f tmp/alyx/posedb_mm_takes.json ]; then
  # windows chosen by tools scan: body yaw held within a few degrees (the take endings and most standstills turn)
  ROLES="$ROLES --slice mm_walk_start_n_01=motion_match_walk_01:start:walk:0.0:2.8"
  ROLES="$ROLES --slice mm_walk_start_n_19=motion_match_walk_19:start:walk:0.0:2.7"
  ROLES="$ROLES --slice mm_walk_stop_n_04=motion_match_walk_04:stop:walk:6.2:9.1"
  ROLES="$ROLES --slice mm_walk_stop_n_05=motion_match_walk_05:stop:walk:3.0:5.9"
  ROLES="$ROLES --slice mm_walk_stop_n_06=motion_match_walk_06:stop:walk:3.4:6.3"
  ROLES="$ROLES --slice mm_walk_start_s_13=motion_match_walk_13:start:walk:4.7:7.6"
  ROLES="$ROLES --slice mm_walk_stop_s_13=motion_match_walk_13:stop:walk:6.7:9.6"
  ROLES="$ROLES --slice mm_walk_stop_s_18=motion_match_walk_18:stop:walk:5.1:8.0"
  ROLES="$ROLES --role motion_match_walk_01:cycle:walk:2.0:4.4 --role motion_match_walk_18:cycle:walk:2.4:4.8 --role motion_match_walk_15:cycle:walk:3.4:5.8"
fi
if [ "${GRUNT_EXTRA:-0}" = "1" ]; then
  # directional run starts/stops, running cuts, hop 8 (single step) and hop 1 (3 m hop) from the knee-inward 10 /
  # plant 0.7 grunt retarget; merged last so its copies win over the older grunt_dirs / sprint_raw ones of the same name
  MERGE="$MERGE --merge tmp/alyx/posedb_missing_grunt.json"
  for d in e ne nw s se sw w; do ROLES="$ROLES --role stand_to_run_down_axis_tight_$d:start:run"; done
  for d in e ne nw se sw w; do ROLES="$ROLES --role run_to_stand_down_axis_tight_$d:stop:run"; done
  ROLES="$ROLES --role run_to_stand_down_west_axis_tight_s:stop:run"
  for c in run_n_to_run_e_down_axis_tight run_n_to_run_w_down_axis_tight run_n_to_run_s_east_down_axis_tight run_n_to_run_s_west_down_axis_tight; do
    ROLES="$ROLES --role $c:cut:run"
  done
  for d in n ne e se s sw w nw; do ROLES="$ROLES --role new_short_hop_8_$d:start+stop:walk --role new_short_hop_1_tight_$d:start+stop:walk"; done
fi
python tools/export_posedb.py tmp/alyx/posedb_startstop.json tmp/alyx/eft_skeleton_bundle.json "$OUT" --augment tmp/alyx/alyx_posedb.json $MERGE $ROLES

# Tarkov's own cycles on the placer (tools/tarkov_clips.py reads the game's bundle; local data). Run
# `TARKOV=1 tools/build_posedb.sh` to append them; the merged file is what gets deployed as alyx_posedb.json
if [ -n "$TARKOV" ]; then
  BUNDLE="D:/SPT41AStar/EscapeFromTarkov_Data/StreamingAssets/Windows/assets/content/characters/animations/character_animations.bundle"
  python tools/tarkov_clips.py "$BUNDLE" tmp/alyx/eft_skeleton_bundle.json tmp/alyx/posedb_tarkov.json --clips run_aim_0,run_aim_45,run_aim_90,run_aim_135,run_aim_180,run_aim_225,run_aim_270,run_aim_315,walk_aim_0,walk_aim_40,walk_aim_90,walk_aim_135,walk_aim_180,walk_aim_225,walk_aim_270,walk_aim_320,sprint_0,sprint_20,sprint_340,sprint_slow_0,sprint_slow_20,sprint_slow_340,Transition_StandIdleAim_to_Sprint,Transition_Sprint_to_Stand
  TROLES=""
  for d in 0 45 90 135 180 225 270 315; do TROLES="$TROLES --role tarkov_run_aim_$d:cycle:run:0:1.6"; done
  for d in 0 40 90 135 180 225 270 320; do TROLES="$TROLES --role tarkov_walk_aim_$d:cycle:walk:0:1.6"; done
  for d in 0 20 340; do TROLES="$TROLES --role tarkov_sprint_$d:cycle:sprint:0:1.2 --role tarkov_sprint_slow_$d:cycle:sprint:0:1.2"; done
  TROLES="$TROLES --role tarkov_transition_standidleaim_to_sprint:start:sprint --role tarkov_transition_sprint_to_stand:stop:sprint"
  (cd tools && python export_posedb.py ../tmp/alyx/posedb_tarkov.json ../tmp/alyx/eft_skeleton_bundle.json ../tmp/alyx/alyx_posedb_tarkov_only.json --min-loop 1.2 --max-loop 1.6 $TROLES)
  python - "$OUT" tmp/alyx/alyx_posedb_tarkov_only.json "${OUT%.json}_tarkov.json" <<'PY'
import json,sys
base=json.load(open(sys.argv[1],encoding="utf-8")); extra=json.load(open(sys.argv[2],encoding="utf-8"))
names={c["name"] for c in extra["clips"]}
base["clips"]=[c for c in base["clips"] if c["name"] not in names]+extra["clips"]
base["source"]=str(base.get("source",""))+" + tarkov clips"
json.dump(base,open(sys.argv[3],"w",encoding="utf-8")); print(sys.argv[3],len(base["clips"]),"clips")
PY
fi
