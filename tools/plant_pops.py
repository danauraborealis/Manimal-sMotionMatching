"""In-plant pops: single-frame foot jumps inside detected contact intervals, stage-matched.

    python tools/plant_pops.py <run dir or capture.json> [--segment N] [--threshold 0.02]

For the steady part of one moving segment (first long one by default): run gait_metrics.contacts on the final
stage track (after_lock when present, else after_visual), take the still cores, and count consecutive-sample
jumps of the lower sole point above the threshold. For each jump report the root-joint move, the ankle move in
the character frame, the clip and clip-frame step, and the same foot's WORLD-space move between the same two
samples at the after_visual stage (before the placer). Character-frame moves include the body's own travel
(about 2.5 cm per 100 Hz frame at run speed), so the world column is the one to compare. Also prints travel and
excursion for the segment decimated by 3 at each of the three offsets, because sparse sampling aliases pops.
"""
import json
import math
import statistics
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gait_metrics as gm
import movement_analysis as ma

THRESHOLD = 0.02


def _world_foot(sample, side):
    bones = {b["N"]: b for b in sample["Bones"] if b}
    rj = sample.get("RootJoint") or {}
    yaw = math.radians(sample.get("RootJointYaw", sample.get("BodyYaw", 0.0)))
    p = bones["Base Human%sFoot" % side]["P"]
    c, s = math.cos(yaw), math.sin(yaw)
    return (rj.get("X", 0.0) + p[0] * c + p[2] * s, p[1], rj.get("Z", 0.0) - p[0] * s + p[2] * c)


def _decimate(track, k, offset):
    out = {"fps": track["fps"] / k}
    for key, v in track.items():
        if isinstance(v, list):
            out[key] = v[offset::k]
    return out


def analyse(path, segment_index=None, threshold=THRESHOLD):
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    track, stage = ma.capture_track(document)
    rows = [s for s in document["Samples"] if s.get("Stage") == stage and s.get("Bones")]
    before = {round(s["Time"], 4): s for s in document["Samples"] if s.get("Stage") == "after_visual" and s.get("Bones")}
    edge = int(0.8 * track["fps"])
    all_segments = ma.segments(track)
    long_segments = [(a, b) for a, b in all_segments if b - a > 3 * edge]
    if long_segments:
        a, b = long_segments[segment_index or 0]
        a, b = a + edge, b - edge
    else:
        # curve scenarios never run straight for long; take the longest segment whole
        a, b = max(all_segments, key=lambda s: s[1] - s[0])
    seg = ma._slice(track, a, b)
    seg_rows = rows[a:b]
    jumps = []
    core_frames = 0
    for side in ("L", "R"):
        flags, runs, low, cores = gm.contacts(seg, side)
        for c0, c1 in cores:
            core_frames += c1 - c0
            for f in range(c0 + 1, c1 + 1):
                d = gm._hyp(low[f], low[f - 1])
                if d <= threshold:
                    continue
                r0, r1 = seg["root"][f - 1], seg["root"][f]
                k0, k1 = seg["ankle" + side][f - 1], seg["ankle" + side][f]
                p0, p1 = seg_rows[f - 1].get("Pose") or {}, seg_rows[f].get("Pose") or {}
                t0, t1 = round(seg_rows[f - 1]["Time"], 4), round(seg_rows[f]["Time"], 4)
                before_move = None
                if t0 in before and t1 in before:
                    w0, w1 = _world_foot(before[t0], side), _world_foot(before[t1], side)
                    before_move = math.hypot(w0[0] - w1[0], w0[2] - w1[2])
                returns = any(gm._hyp(low[g], low[f - 1]) < 0.4 * d for g in range(f + 1, min(c1 + 1, f + 4)))
                probe = p1.get("Placer" + side) or {}
                previous = p0.get("Placer" + side) or {}

                def correction(pr):
                    # horizontal target-minus-shown; the plugin writes it as Correction from this revision on
                    if pr.get("Correction") is not None:
                        return round(pr["Correction"] * 1000)
                    if pr.get("Target") and pr.get("Shown"):
                        return round(math.hypot(pr["Target"][0] - pr["Shown"][0], pr["Target"][2] - pr["Shown"][2]) * 1000)
                    return None

                jumps.append({
                    "time": t1, "side": side, "jump_mm": round(d * 1000), "root_move_mm": round(math.hypot(r1[0] - r0[0], r1[1] - r0[1]) * 1000),
                    "ankle_local_move_mm": round(math.hypot(k1[0] - k0[0], k1[2] - k0[2]) * 1000),
                    "before_stage_world_move_mm": None if before_move is None else round(before_move * 1000),
                    "same_clip": p0.get("Clip") == p1.get("Clip"), "clip_frame_step": round(abs((p1.get("Frame") or 0) - (p0.get("Frame") or 0)), 2),
                    "phase": p1.get("Phase"), "placer_active": probe.get("Active"), "frozen": probe.get("Frozen"), "cycle": probe.get("Cycle"),
                    "progression": probe.get("Progression"), "returns_within_3": returns,
                    # the bounded diagnostic: what the placer asked for and applied on the jump frame and the one before
                    "correction_prev_mm": correction(previous), "correction_mm": correction(probe),
                    "authority_prev": previous.get("Authority"), "authority": probe.get("Authority"),
                    "failure": probe.get("Failure"), "release_mm": None if probe.get("Release") is None else round(probe["Release"] * 1000),
                    "target": probe.get("Target"), "placed": probe.get("Placed"),
                })
    med = lambda key: statistics.median([j[key] for j in jumps if j[key] is not None]) if any(j[key] is not None for j in jumps) else None
    report = {"file": str(path), "stage": stage, "fps": round(track["fps"], 1), "segment": [a, b], "core_frames": core_frames, "jumps": len(jumps),
              "jump_p50_mm": med("jump_mm"), "jump_max_mm": max((j["jump_mm"] for j in jumps), default=None),
              "root_move_p50_mm": med("root_move_mm"), "ankle_local_move_p50_mm": med("ankle_local_move_mm"),
              "before_stage_world_move_p50_mm": med("before_stage_world_move_mm"),
              "returns_within_3": sum(1 for j in jumps if j["returns_within_3"]),
              "jumps_with_failure": sum(1 for j in jumps if j.get("failure")),
              "jumps_with_correction_dropped": sum(1 for j in jumps if (j.get("correction_prev_mm") or 0) > 10 and (j.get("correction_mm") or 0) <= 2),
              "events": jumps, "decimated": []}
    g = lambda m, k, s: (m.get(k) or {}).get(s)
    for offset in (0, 1, 2):
        m = gm.analyse(_decimate(seg, 3, offset), "d")
        report["decimated"].append({"offset": offset, "fps": round(seg["fps"] / 3, 1), "plants": m["plants"], "travel_p50_p90": [g(m, "contact_travel_mm", "p50"), g(m, "contact_travel_mm", "p90")],
                                    "excursion_p50_p90": [g(m, "contact_excursion_mm", "p50"), g(m, "contact_excursion_mm", "p90")], "slide_p50_p90": [g(m, "contact_slide_mm", "p50"), g(m, "contact_slide_mm", "p90")]})
    return report


def main():
    path = Path(sys.argv[1])
    if path.is_dir():
        status = path / "off/status.json" if (path / "off/status.json").exists() else path / "status.json"
        path = Path(json.loads(status.read_text(encoding="utf-8"))["LastCapturePath"])
    seg = int(sys.argv[sys.argv.index("--segment") + 1]) if "--segment" in sys.argv else None
    thr = float(sys.argv[sys.argv.index("--threshold") + 1]) if "--threshold" in sys.argv else THRESHOLD
    print(json.dumps(analyse(path, seg, thr), indent=1))


if __name__ == "__main__":
    main()
