"""Measure both palm-to-weapon transforms through reaction and render stages."""
import argparse
import json
import math
from pathlib import Path
from analyze_reaction_exit import angle

def analyze(path):
    data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    grouped = {}
    for row in data['Samples']:
        if row.get('Pose', {}).get('Grip'):
            grouped.setdefault(row['Frame'], {})[row['Stage']] = row
    result = dict(capture=str(path), applied_frames=0, peak_support_solver_error_m=0,
                  peak_trigger_solver_error_m=0, minimum_arm_weight=1, comparisons={})
    for frame, stages in grouped.items():
        final = stages.get('after_lock')
        if not final or final['Pose']['Grip']['AppliedFrame'] != frame:
            continue
        grip = final['Pose']['Grip']
        result['applied_frames'] += 1
        result['peak_support_solver_error_m'] = max(result['peak_support_solver_error_m'], grip['SupportError'])
        result['peak_trigger_solver_error_m'] = max(result['peak_trigger_solver_error_m'], grip['TriggerError'])
        result['minimum_arm_weight'] = min(result['minimum_arm_weight'], grip['ArmWeight'])
        for a, b in [('after_visual', 'after_lock'), ('after_lock', 'pre_render')]:
            if a not in stages or b not in stages:
                continue
            pair = result['comparisons'].setdefault(a+'->'+b, dict(pairs=0, left_m=0, right_m=0, left_degrees=0, right_degrees=0))
            pair['pairs'] += 1
            old, new = stages[a]['Pose']['Grip'], stages[b]['Pose']['Grip']
            for side in ('Left', 'Right'):
                key = side.lower()
                pair[key+'_m'] = max(pair[key+'_m'], math.dist(old[side+'Position'], new[side+'Position']))
                pair[key+'_degrees'] = max(pair[key+'_degrees'], angle(old[side+'Rotation'], new[side+'Rotation']))
    return result

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('capture')
    parser.add_argument('--output')
    args = parser.parse_args()
    result = json.dumps(analyze(args.capture), indent=2)
    if args.output: Path(args.output).write_text(result, encoding='utf-8')
    else: print(result)
