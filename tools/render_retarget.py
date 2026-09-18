"""Contact sheet of retargeted legs: grunt (scaled, grey) under EFT (colored), side and front views."""
import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw

CELL = 220
SCALE = 170  # pixels per meter
BONES = [("pelvis", "hipL"), ("pelvis", "hipR"), ("hipL", "kneeL"), ("kneeL", "ankleL"), ("ankleL", "toeL"),
         ("hipR", "kneeR"), ("kneeR", "ankleR"), ("ankleR", "toeR")]


def project(point, view, cx, cy):
    # side view looks along -X (forward +Z to the right); front view looks along -Z (character's left on screen right)
    h = point[2] if view == "side" else -point[0]
    return cx + h * SCALE, cy - point[1] * SCALE


def draw_pose(draw, pose, view, cx, cy, colors):
    for a, b in BONES:
        side = "L" if b.endswith("L") else "R"
        draw.line([project(pose[a], view, cx, cy), project(pose[b], view, cx, cy)], fill=colors[side], width=3)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("posedb", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--clip", required=True)
    parser.add_argument("--frames", type=int, default=8)
    parser.add_argument("--start", type=float, default=0.0, help="seconds")
    parser.add_argument("--step", type=float, default=0.1, help="seconds between cells")
    args = parser.parse_args()
    db = json.loads(args.posedb.read_text(encoding="utf-8"))
    clip = next(c for c in db["clips"] if c["name"] == args.clip)
    image = Image.new("RGB", (CELL * args.frames, CELL * 2 + 20), "white")
    draw = ImageDraw.Draw(image)
    for i in range(args.frames):
        f = min(clip["frames"] - 1, int(round((args.start + i * args.step) * clip["fps"])))
        debug = clip["debug"][f]
        for row, view in enumerate(("side", "front")):
            cx, cy = CELL * i + CELL / 2, CELL * (row + 1) - 20
            draw.line([(CELL * i, cy), (CELL * (i + 1), cy)], fill=(210, 210, 210))
            draw_pose(draw, debug["source"], view, cx, cy, {"L": (170, 170, 170), "R": (200, 200, 200)})
            draw_pose(draw, debug["target"], view, cx, cy, {"L": (30, 90, 220), "R": (220, 60, 40)})
            contact = "".join(side for side in ("L", "R") if clip["contacts"][side][f])
            draw.text((CELL * i + 4, CELL * row + 4), f"{view} f{f} contact:{contact or '-'}", fill="black")
    draw.text((4, CELL * 2 + 4), f"{args.clip}  grey=grunt scaled  blue=EFT left  red=EFT right", fill="black")
    image.save(args.output)
    print(args.output)


if __name__ == "__main__":
    main()
