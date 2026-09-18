"""Summarize native jump/landing episodes from a puppet capture; unknown ground data stays unknown."""
import argparse
import json
import math
from collections import defaultdict
from pathlib import Path

NATIVE_STATES = {"Jump", "JumpLanding", "FallDown", "ClimbOver", "ClimbUp", "VaultingFallDown", "VaultingLanding"}

def point(leg):
    if not leg.get("HasFootPosition"): return None
    p = leg.get("FootPosition", {})
    v = [p.get(k) for k in ("X", "Y", "Z")]
    return v if all(type(x) in (int, float) and math.isfinite(x) for x in v) else None

def distance(a, b):
    return math.dist(a, b) if a is not None and b is not None else None

def analyze(document):
    groups = defaultdict(dict)
    for sample in document.get("Samples", []):
        groups[sample.get("Frame")][sample.get("Stage")] = sample
    events, event = [], None
    state_counts = defaultdict(int)
    for frame, stages in sorted(groups.items()):
        row = stages.get("after_lock") or stages.get("after_visual") or stages.get("before_visual")
        if not row: continue
        pose = row.get("Pose") or {}
        entry = pose.get("SprintEntry") or {}
        if not entry.get("HasContext"):
            if event:
                event["complete"] = False
                events.append(event); event = None
            continue
        state, grounded = entry.get("State"), entry.get("Grounded")
        state_counts[str(state)] += 1
        native = grounded is False or state in NATIVE_STATES or entry.get("ManagedState") in NATIVE_STATES
        if native and event is None:
            event = dict(start_frame=frame, start_time=row.get("Time"), end_frame=frame, end_time=row.get("Time"),
                         states=[], airborne_frames=0, weighted_playback_frames=0,
                         peak_playback_ankle_change_m=None, peak_placer_ankle_change_m=None,
                         paired_playback_frames=0, paired_placer_frames=0, complete=False)
        if event and native:
            event["end_frame"], event["end_time"] = frame, row.get("Time")
            if state not in event["states"]: event["states"].append(state)
            event["airborne_frames"] += int(grounded is False)
            event["weighted_playback_frames"] += int((pose.get("Weight") or 0) > 0.001)
            for stage_a, stage_b, name in (("before_visual", "after_pose", "playback"), ("after_visual", "after_lock", "placer")):
                if stage_a not in stages or stage_b not in stages: continue
                values = []
                for leg in ("LeftLeg", "RightLeg"):
                    a = point((stages[stage_a].get("Grounder") or {}).get(leg) or {})
                    b = point((stages[stage_b].get("Grounder") or {}).get(leg) or {})
                    d = distance(a,b)
                    if d is not None: values.append(d)
                if values:
                    event["paired_" + name + "_frames"] += 1
                    key = "peak_" + name + "_ankle_change_m"
                    event[key] = max(event[key] or 0.0, max(values))
        elif event:
            event["complete"] = True
            event["resume_frame"] = frame
            event["resume_time"] = row.get("Time")
            events.append(event); event = None
    if event: events.append(event)
    scripted = []
    for step in (document.get("Puppet") or {}).get("Steps", []):
        if not step.get("JumpExpected"): continue
        passed = (step.get("EndReason") == "completed" and step.get("JumpSawAirborne") is True
                  and (step.get("JumpTriggerFrame") or -1) >= 0 and (step.get("JumpLandingFrame") or -1) >= 0)
        scripted.append(dict(step=step.get("Step"), passed=passed, end_reason=step.get("EndReason"),
                             trigger_frame=step.get("JumpTriggerFrame"), landing_frame=step.get("JumpLandingFrame")))
    return dict(episodes=events, state_frames=dict(state_counts), scripted_jumps=scripted,
                all_scripted_jumps_completed=all(step["passed"] for step in scripted) if scripted else None,
                note="Same-frame ankle changes measure writes, not mesh quality. Missing pairs cannot prove zero correction. Ground return does not alone end an authored landing state.")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = analyze(json.loads(args.capture.read_text(encoding="utf-8-sig")))
    text = json.dumps(result, indent=2)
    if args.output: args.output.write_text(text + "\n", encoding="utf-8")
    else: print(text)
if __name__ == "__main__": main()
