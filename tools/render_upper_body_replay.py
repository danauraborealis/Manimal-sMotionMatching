"""Render a captured start/reaction and its recovery as a full-skeleton GIF."""
import argparse
import json
from pathlib import Path
from PIL import Image, ImageDraw

CHAINS = [
    ["Pelvis", "Spine1", "Spine2", "Spine3", "Ribcage", "Neck", "Head"],
    ["Pelvis", "LThigh1", "LCalf", "LFoot", "LToe"],
    ["Pelvis", "RThigh1", "RCalf", "RFoot", "RToe"],
    ["Ribcage", "LUpperarm", "LForearm1", "HandL"],
    ["Ribcage", "RUpperarm", "RForearm1", "HandR"],
]


def render(capture, output, phase, occurrence):
    data = json.loads(Path(capture).read_text(encoding="utf-8-sig"))
    final = {}
    for sample in data["Samples"]:
        if sample["Stage"] in ("after_visual", "after_lock") and sample.get("Bones"):
            final[sample["Frame"]] = sample
    frames = sorted(final.values(), key=lambda s: s["Time"])
    def matches(s):
        pose = s.get("Pose", {})
        if phase in ("landing", "hit"):
            return (pose.get("UpperOverlay") or "").startswith(phase+":")
        return pose.get("Phase") == phase
    starts = [i for i, s in enumerate(frames) if matches(s) and (i == 0 or not matches(frames[i-1]))]
    start = starts[occurrence-1]
    end = next((i for i in range(start+1, len(frames)) if not matches(frames[i])), len(frames)-1)
    chosen = [s for s in frames if frames[start]["Time"]-.1 <= s["Time"] <= frames[end]["Time"]+.7]
    images = []
    times = []
    for s in chosen:
        if times and s["Time"]-times[-1] < 1/30:
            continue
        bones = {b["N"].replace("Base Human", ""): b["P"] for b in s["Bones"] if b}
        for hand in ("HandL", "HandR"):
            if s.get("Pose", {}).get(hand):
                bones[hand] = s["Pose"][hand]
        im = Image.new("RGB", (900, 550), "#17202b")
        draw = ImageDraw.Draw(im)
        for view, axis in enumerate((0, 2)):
            def xy(point):
                return 225+450*view+(point[axis]-bones["Pelvis"][axis])*210, 510-point[1]*230
            for index, chain in enumerate(CHAINS):
                for a, b in zip(chain, chain[1:]):
                    if a in bones and b in bones:
                        draw.line([xy(bones[a]), xy(bones[b])], fill="#75c8ff" if index<3 else "#ffa850", width=4)
            draw.text((20+450*view, 35), ("Front", "Side")[view], fill="white")
        label = s.get("Pose", {}).get("UpperOverlay") or s.get("Pose", {}).get("Clip") or s.get("Pose", {}).get("Phase")
        draw.text((20, 12), f'{s["Time"]:.3f}s | {label} | frame {s["Frame"]}', fill="white")
        images.append(im)
        times.append(s["Time"])
    durations = [max(1, round((b-a)*1000)) for a, b in zip(times, times[1:])] + [34]
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    images[0].save(output, save_all=True, append_images=images[1:], duration=durations, loop=0)
    images[len(images)//2].save(output.with_suffix(".png"))
    print(json.dumps({"output": str(output), "frames": len(images), "time": [times[0], times[-1]]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture")
    parser.add_argument("output")
    parser.add_argument("--phase", choices=("Start", "Reaction", "landing", "hit"), default="Reaction")
    parser.add_argument("--occurrence", type=int, default=1)
    args = parser.parse_args()
    render(args.capture, args.output, args.phase, args.occurrence)
