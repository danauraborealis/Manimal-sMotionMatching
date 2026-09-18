"""Compare incoming and placed heel/toe pitch in retained automatic raid frames."""
import argparse
import collections
import json
import statistics
from pathlib import Path
from raid_report import read_report


def rotate(q, v):
    x, y, z, w = q
    tx, ty, tz = 2*(y*v[2]-z*v[1]), 2*(z*v[0]-x*v[2]), 2*(x*v[1]-y*v[0])
    return [v[0]+w*tx+y*tz-z*ty, v[1]+w*ty+z*tx-x*tz, v[2]+w*tz+x*ty-y*tx]


def analyze(report, sole_points):
    frames = read_report(report).get('frames.jsonl', [])
    paired = collections.defaultdict(dict)
    for row in frames:
        paired[(row['botId'], row['time'])][row['stage']] = row['snapshot']
    groups = collections.defaultdict(list)
    for stages in paired.values():
        before, after = stages.get('after_visual'), stages.get('after_lock')
        if not before or not after:
            continue
        for side, prefix in [('left', 'L'), ('right', 'R')]:
            placement = after.get('legs', {}).get(side, {}).get('placement', {})
            if not placement.get('active') or placement.get('swing') or not placement.get('locked'):
                continue
            heights = []
            for snapshot in [before, after]:
                foot = snapshot.get('body', {}).get(side+'Foot')
                if not foot:
                    break
                delta = [h-t for h, t in zip(sole_points[prefix]['heel'], sole_points[prefix]['toe'])]
                world = rotate(snapshot.get('skeletonRotation', [0, 0, 0, 1]), rotate(foot['q'], delta))
                heights.append(world[1])
            if len(heights) == 2:
                clip = after.get('reaction', {}).get('clip', 'unknown')
                groups[clip].append((heights[0], heights[1], heights[1]-heights[0]))
    result = {}
    for clip, rows in sorted(groups.items()):
        result[clip] = {'samples': len(rows), 'incomingHeelAboveToeMedianM': statistics.median(r[0] for r in rows),
                        'finalHeelAboveToeMedianM': statistics.median(r[1] for r in rows),
                        'addedPitchHeightMedianM': statistics.median(r[2] for r in rows),
                        'addedPitchOver2cmFraction': sum(r[2] > .02 for r in rows)/len(rows)}
    return {'report': str(report), 'clips': result,
            'interpretation': 'Matched active locked nonswing samples only. Positive height means heel above toe. Older reports without skeletonRotation assume a yaw-only root. Different raids are not identical workloads; authored toe-off is not itself a defect.'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path)
    parser.add_argument('database', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    database = json.loads(args.database.read_text(encoding='utf-8-sig'))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(analyze(args.report, database['solePoints']), indent=2), encoding='utf-8')
