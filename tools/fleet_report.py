"""Fleet report: anomalies per bot and per situation from a fleet stream (LogOutput/.../fleet-*.jsonl).

    python tools/fleet_report.py <fleet.jsonl> [--episodes N]

The stream holds one sample line per rigged bot every other frame: playback phase/clip/rate/weight/driver, body
position/velocity/yaw/speed/sprint/pose level, combat context (enemy, shooting, aiming), both feet from the placer
(placed sole, target, next/prev anchors, lock, correction, failure, authority, heading) and both legs' bend
(straightness, knee forward/lateral of the hip-ankle line). Events carry the playback's own log lines.

Detectors (per bot, per sample, counted per situation):
  slide      a sole locked at both ends of the sample interval moving over 0.25 m/s
  lockjump   the sample in which a lock engaged moved the sole over 0.25 m/s (part landing motion, part pop)
  freeze     both soles still (< 0.05 m/s) while the body moves over 0.6 m/s with the layer active
  trailing   a placed sole more than 0.6 m behind the root along the travel direction while the body moves
  leading    a placed sole more than 0.9 m ahead of the root along the travel direction
  stretch    leg straightness over 0.985 (locked knee) while the layer draws the leg
  kneeback   knee behind the hip-ankle line by over 3 cm
  kneeout    knee more than 20 cm outside the hip-ankle line
  bigcorr    placer correction over 0.3 m on a foot
  failure    anchor failure flagged on a foot
  hurried    playback rate over 1.6 or under 0.5 while moving
  tiergait   clip gait tier against body speed (walk clip over 1.9 m/s, run clip under 1.2 m/s)
  cutspam    more than two cut events within 3 s
  sprintlayer a clip other than Tarkov's own sprint cycles drawn while the bot sprints
  snap       a sole within 15 cm of the ground changed speed by over 3 m/s between consecutive samples (90 m/s^2
             at 33 ms: a landing foot decelerates at about 40; anything faster is a pop, in either raid kind)
Situations: patrol (no enemy), combat (enemy known), shooting, aiming, sprint, crouch (pose level < 0.6).
"""
import json
import math
import sys
from collections import Counter, defaultdict
from pathlib import Path


def situation(s):
    tags = []
    if s.get("spr"): tags.append("sprint")
    if s.get("shoot"): tags.append("shooting")
    elif s.get("aim"): tags.append("aiming")
    tags.append("combat" if s.get("enemy") else "patrol")
    if (s.get("pose") or 1.0) < 0.6: tags.append("crouch")
    return tags


def gait_of(clip):
    if not clip: return None
    n = clip.lower()
    # Tarkov's walk_aim cycles are its 2.5 m/s run-band gait, not a walk
    if "sprint" in n or "hop_0a" in n or "run" in n or "tarkov_walk" in n: return "run"
    if "walk" in n or "hop_2" in n or "stand_to_walk" in n or "walk_to_stand" in n or "mm_walk" in n: return "walk"
    return None


def main():
    path = Path(sys.argv[1])
    episodes_wanted = int(sys.argv[sys.argv.index("--episodes") + 1]) if "--episodes" in sys.argv else 12
    meta = None
    bots = {}
    per_bot = defaultdict(lambda: defaultdict(list))
    events = defaultdict(list)
    perf = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.startswith("{"): continue
        try: d = json.loads(line)
        except json.JSONDecodeError: continue
        k = d.get("k")
        if k == "meta": meta = d
        elif k == "bot": bots[d["b"]] = d
        elif k == "gone": bots.setdefault(d["b"], {}).update({"gone": d})
        elif k == "e": events[d["b"]].append(d)
        elif k == "perf": perf.append(d)
        elif k == "s": per_bot[d["b"]]["s"].append(d)
    report = {"file": str(path), "meta": meta, "bots": {}, "totals": Counter(), "by_situation": defaultdict(Counter), "moving_seconds_by_situation": Counter(), "episodes": []}
    if perf:
        ms = sorted(p["msPerFrame"] for p in perf)
        report["perf_ms_per_frame_p50_max"] = [ms[len(ms) // 2], max(p.get("peakMs", 0) for p in perf)]
    for b, data in per_bot.items():
        S = data["s"]
        S.sort(key=lambda s: s["t"])
        flags = Counter(); situ_flags = defaultdict(Counter); moving = Counter(); clips = Counter(); phases = Counter(); clip_flags = defaultdict(Counter); coverage = Counter()
        cuts = [e["t"] for e in events[b] if " Cut " in e.get("text", "")]
        cutspam = sum(1 for i in range(2, len(cuts)) if cuts[i] - cuts[i - 2] < 3.0)
        episodes = []
        prev = None
        sole_speeds = {}
        for s in S:
            tags = situation(s)
            dt = (s["t"] - prev["t"]) if prev else 0.0
            # body speed and travel from position deltas: Player.Velocity over-reported 3.4-4.8 m/s on a bot moving
            # at 2.1-2.6 (fleet batch 2), and the playback itself measures from positions
            if prev and 0 < dt < 0.5:
                vx, vz = (s["x"] - prev["x"]) / dt, (s["z"] - prev["z"]) / dt
            else:
                vx, vz = s.get("vx", 0.0), s.get("vz", 0.0)
            body = math.hypot(vx, vz)
            if body > 8.0:
                body = 0.0; vx = vz = 0.0  # a teleport or respawn, not movement
            phases[s.get("ph")] += 1
            if s.get("clip"): clips[s["clip"]] += 1
            found = []
            # invisible or simplified skeletons do not animate; suspended rigs are Tarkov's legs by design. neither
            # counts as exposure: Customs raids (most bots culled) diluted batch 2's pooled rates ten-fold
            if s.get("vis") == 0 or s.get("simp") == 1:
                coverage["not_animated"] += dt; prev = s; sole_speeds = {}; continue
            if s.get("sus") == 1:
                coverage["suspended"] += dt; prev = s; sole_speeds = {}; continue
            if body > 0.3:
                for tg in tags: moving[tg] += dt
            # control raids never place; their feet come from the bones and the body checks still apply
            active = s.get("placed") == 1 or (s.get("L") or {}).get("src") == "bone"
            if prev and dt > 0 and active and (prev.get("placed") == 1 or (prev.get("L") or {}).get("src") == "bone"):
                travel = (vx / body, vz / body) if body > 0.3 else None
                still = 0
                for side in ("L", "R"):
                    f, pf = s.get(side), prev.get(side)
                    if not f or not pf or not f.get("p") or not pf.get("p"): continue
                    p, pp = f["p"], pf["p"]
                    sole_speed = math.hypot(p[0] - pp[0], p[2] - pp[2]) / dt
                    # the blended base hops heel -> toe on a roll-off (one frame on Tarkov's flat plants); when the
                    # stream carries heel and toe, a plant is judged by whichever of them moved least
                    if f.get("hl") and pf.get("hl") and f.get("to") and pf.get("to"):
                        sole_speed = min(sole_speed, math.hypot(f["hl"][0] - pf["hl"][0], f["hl"][2] - pf["hl"][2]) / dt, math.hypot(f["to"][0] - pf["to"][0], f["to"][2] - pf["to"][2]) / dt)
                    last = sole_speeds.get(side)
                    # near the ground only: a swing foot at head height is not a pop the eye reads as one
                    if last is not None and dt < 0.06 and last[0] < 0.06 and abs(sole_speed - last[1]) > 3.0 and p[1] - s["y"] < 0.15: found.append("snap")
                    sole_speeds[side] = (dt, sole_speed)
                    # support is only known when the placer ran; bone-sourced feet (control raids, idle) skip slide
                    # support = the placer's lock (review: never from low sole speed); authority alone ramps in swings
                    planted = f.get("src") != "bone" and bool(f.get("lk"))
                    # sustained slide needs both ends of the interval locked; the engagement sample still holds a
                    # sample's worth of landing motion and is counted apart (review)
                    if planted and sole_speed > 0.25: found.append("slide" if pf.get("lk") else "lockjump")
                    if sole_speed < 0.05: still += 1
                    if travel and body > 0.5:
                        along = (p[0] - s["x"]) * travel[0] + (p[2] - s["z"]) * travel[1]
                        # a stride scales with speed: Tarkov's own sprint trails 0.6-0.9 m behind the root
                        # (control raids: 188 trailing flags a minute of sprint with a fixed 0.6 m bound)
                        bound = 0.45 + 0.15 * body
                        if along < -bound: found.append("trailing")
                        if along > bound + 0.3: found.append("leading")
                    if f.get("st") is not None and f["st"] > 0.985: found.append("stretch")
                    if f.get("kf") is not None and f["kf"] < -0.03: found.append("kneeback")
                    if f.get("kl") is not None and abs(f["kl"]) > 0.2: found.append("kneeout")
                    if (f.get("corr") or 0) > 0.3: found.append("bigcorr")
                    if f.get("fail"): found.append("failure")
                if still == 2 and body > 0.6: found.append("freeze")
                rt = s.get("rt")
                if rt is not None and body > 0.5 and s.get("ph") in ("Start", "SprintCycle", "Stop", "Cut") and (rt > 1.6 or rt < 0.5): found.append("hurried")
                g = gait_of(s.get("clip"))
                if g == "walk" and body > 1.9 or g == "run" and 0.3 < body < 1.2: found.append("tiergait")
                # Tarkov keeps its sprint: any clip still drawn under a sprinting body is a hand-off that did not happen
                # Tarkov's own sprint cycles on the placer are the sprint pass, not a missed hand-off
                if s.get("spr") and s.get("placed") == 1 and s.get("ph") not in ("Transition", "HandOff", "Idle") and not (s.get("clip") or "").startswith("tarkov_sprint"): found.append("sprintlayer")
            for fl in set(found):
                flags[fl] += 1
                for tg in tags: situ_flags[tg][fl] += 1
                if s.get("clip"): clip_flags[s["clip"]][fl] += 1
            if found:
                if episodes and s["t"] - episodes[-1]["end"] < 0.3 and set(found) & set(episodes[-1]["flags"]):
                    episodes[-1]["end"] = s["t"]; episodes[-1]["frames"] += 1
                    for fl in found: episodes[-1]["flags"][fl] = episodes[-1]["flags"].get(fl, 0) + 1
                else:
                    episodes.append({"bot": b, "start": s["t"], "end": s["t"], "frames": 1, "flags": {fl: 1 for fl in found}, "phase": s.get("ph"), "clip": s.get("clip"), "situation": tags, "frame": s.get("f")})
            prev = s
        flags["cutspam"] = cutspam
        # sustained anomalies (review: windows on elapsed time, not sample counts): episodes lasting 0.25 s or more
        sustained = Counter()
        for e in episodes:
            if e["end"] - e["start"] >= 0.25:
                for fl in e["flags"]: sustained[fl] += 1
        info = bots.get(b, {})
        gone = info.get("gone", {})
        report["bots"][str(b)] = {
            "role": info.get("role"), "name": info.get("name"), "samples": len(S), "seconds": round(S[-1]["t"] - S[0]["t"], 1) if S else 0,
            "moving_seconds": {k: round(v, 1) for k, v in moving.items()}, "phases": dict(phases), "clips": dict(clips.most_common(12)),
            "flags": dict(flags), "sustained_episodes": dict(sustained), "flags_by_situation": {k: dict(v) for k, v in situ_flags.items()},
            "flags_by_clip": {k: dict(v) for k, v in sorted(clip_flags.items(), key=lambda kv: -sum(kv[1].values()))[:10]},
            "hurried_frames": gone.get("hurried"), "reach_clamps": gone.get("reachClamps"), "anchor_failures": gone.get("anchorFailures"), "peak_hip_shift": gone.get("peakHipShift"),
            "events": len(events[b]), "coverage_seconds": {k: round(v, 1) for k, v in coverage.items()},
        }
        for fl, n in flags.items(): report["totals"][fl] += n
        for tg, c in situ_flags.items():
            for fl, n in c.items(): report["by_situation"][tg][fl] += n
        for tg, v in moving.items(): report["moving_seconds_by_situation"][tg] += v
        report["episodes"].extend(episodes)
    # pooled per-clip table across bots (counts, not averages of rates)
    pooled = defaultdict(Counter)
    for b in report["bots"].values():
        for clip, c in b["flags_by_clip"].items():
            for fl, n in c.items(): pooled[clip][fl] += n
    report["flags_by_clip"] = {k: dict(v) for k, v in sorted(pooled.items(), key=lambda kv: -sum(kv[1].values()))[:15]}
    pooled_sustained = Counter()
    for b in report["bots"].values():
        for fl, n in b["sustained_episodes"].items(): pooled_sustained[fl] += n
    report["sustained_episodes"] = dict(pooled_sustained)
    report["episodes"].sort(key=lambda e: -(e["frames"] * (1 + len(e["flags"]))))
    report["episodes"] = report["episodes"][:episodes_wanted]
    report["totals"] = dict(report["totals"])
    report["by_situation"] = {k: dict(v) for k, v in report["by_situation"].items()}
    report["moving_seconds_by_situation"] = {k: round(v, 1) for k, v in report["moving_seconds_by_situation"].items()}
    # rate per minute of movement in each situation
    report["flags_per_moving_minute"] = {tg: {fl: round(n / max(report["moving_seconds_by_situation"].get(tg, 0), 1e-6) * 60, 1) for fl, n in c.items()} for tg, c in report["by_situation"].items()}
    print(json.dumps(report, indent=1, default=str))


if __name__ == "__main__":
    main()
