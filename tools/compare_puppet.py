"""Compare two puppet summaries (e.g. foot lock off vs on) step by step."""
import argparse
import json
from pathlib import Path


def core_p50(row):
    bands = row.get("core_slide_mm") or {}
    values = [(stats.get("stances_with_core", 0), stats.get("p50")) for stats in bands.values() if stats.get("p50") is not None]
    if not values:
        return None
    # the band holding most stances represents the step
    return max(values)[1]


def _mismatch(a, b):
    if a is None or b is None:
        return True
    return abs(a - b) > 0.05 * max(a, b)


def compare(baseline, candidate):
    rows = []
    base_steps = baseline["puppet"]["steps"]
    cand_steps = candidate["puppet"]["steps"]
    stops = []
    for base, cand in zip(base_steps, cand_steps):
        if base.get("step") == cand.get("step") and "stop_contacts" in base:
            stops.append({
                "step": base["step"],
                "stop_contact_slide_mm": [base.get("stop_contact_slide_mm_p50"), cand.get("stop_contact_slide_mm_p50")],
                "stop_contacts": [base.get("stop_contacts"), cand.get("stop_contacts")],
                "meters": [base.get("meters"), cand.get("meters")],
                "seconds_to_halt": [base.get("seconds_to_halt"), cand.get("seconds_to_halt")],
                "after_halt_foot_travel_mm": [base.get("after_halt_foot_travel_mm"), cand.get("after_halt_foot_travel_mm")],
            })
        if base.get("step") != cand.get("step") or "stances" not in base:
            continue
        a, b = core_p50(base), core_p50(cand)
        rows.append({
            "step": base["step"],
            "mps": [base.get("steady_mps_p50"), cand.get("steady_mps_p50")],
            "core_slide_p50_mm": [a, b],
            "reduction_pct": None if not a or b is None else round(100.0 * (a - b) / a),
            # same-run animation vs rendered over animation-defined windows; independent of the lock trigger
            "candidate_contact_slide_mm_animated_vs_rendered": cand.get("contact_slide_mm_p50"),
            # rendered slide over each run's own animation-defined contact windows
            "contact_rendered_mm": [None if not base.get("contact_slide_mm_p50") else base["contact_slide_mm_p50"][1],
                                    None if not cand.get("contact_slide_mm_p50") else cand["contact_slide_mm_p50"][1]],
            "end": [base.get("end"), cand.get("end")],
            "start_contact_slide_mm": [base.get("start_contact_slide_mm_p50"), cand.get("start_contact_slide_mm_p50")],
            "start_contacts": [base.get("start_contacts"), cand.get("start_contacts")],
            # a bot's StateSpeedLimit can drop between runs (seen 1.0 -> 0.33), which makes the legs incomparable
            "speed_mismatch": _mismatch(base.get("steady_mps_p50"), cand.get("steady_mps_p50")),
        })
    return {
        "baseline_lock": baseline.get("foot_lock"),
        "candidate_lock": candidate.get("foot_lock"),
        "candidate_correction": candidate.get("stage_deltas", {}).get("lock_correction"),
        "candidate_after_last_hook": candidate.get("stage_deltas", {}).get("after_last_hook"),
        "pops": [baseline.get("correction_pops"), candidate.get("correction_pops")],
        "pose_playback": [baseline.get("pose_playback"), candidate.get("pose_playback")],
        "pose_correction": candidate.get("stage_deltas", {}).get("pose_correction"),
        "pose_to_after_visual": candidate.get("stage_deltas", {}).get("pose_to_after_visual"),
        "knee_pops": [baseline.get("knee_pops"), candidate.get("knee_pops")],
        "straightened": [baseline.get("straightened_by_correction"), candidate.get("straightened_by_correction")],
        "contact_window_slide": [baseline.get("contact_window_slide"), candidate.get("contact_window_slide")],
        "steps": rows,
        "stops": stops,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    args = parser.parse_args()
    load = lambda p: json.loads(p.read_text(encoding="utf-8"))
    print(json.dumps(compare(load(args.baseline), load(args.candidate)), indent=2))


if __name__ == "__main__":
    main()
