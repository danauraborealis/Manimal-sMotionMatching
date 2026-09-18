"""Pull the locomotion-relevant nodes out of a decompiled Source 2 animgraph (VRF .vanmgrph KV3 text).

Prints, per node class of interest, the node's scalar fields and nested metric/filter lists: the motion matching
node with its metrics and weights, the foot lock node, the stride length adjuster, the path motors, and the
motion clip groups. Local-use research tool over Valve data; nothing here is redistributed.

    python tools/animgraph_extract.py tmp/alyx/animgraphs/animgraphs/combine_grunt_footlock.vanmgrph
"""
import re
import sys
from pathlib import Path

CLASSES = ("CMotionMatchingAnimNode", "CFootLockAnimNode", "CStrideLengthAdjusterAnimNode", "CDampedPathAnimMotor",
           "CPathAnimMotor", "CMotionClipGroup", "CFollowPathAnimNode", "CGroundIKSolveAnimNode", "CSpeedScaleAnimNode",
           "CPathHelperAnimNode", "CSetFacingAnimNode", "CTwoBoneIKAnimNode")
METRIC_HINT = re.compile(r'_class = "(C\w*(Metric|Filter))"')


def blocks(text, cls):
    """Yield the balanced {...} block that contains each `_class = "cls"` occurrence."""
    for m in re.finditer(r'_class = "%s"' % re.escape(cls), text):
        start = text.rfind("{", 0, m.start())
        depth, i = 0, start
        while i < len(text):
            c = text[i]
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        yield text[start:i + 1]


def scalars(block, depth_limit=1):
    """Top-level `key = value` lines of a block (skips nested blocks)."""
    out = []
    depth = 0
    for line in block.splitlines():
        s = line.strip()
        depth_before = depth
        depth += s.count("{") + s.count("[") - s.count("}") - s.count("]")
        if depth_before <= depth_limit and "=" in s and not s.endswith("{") and not s.endswith("["):
            key, _, value = s.partition("=")
            key, value = key.strip(), value.strip().rstrip(",")
            if len(value) < 120:
                out.append((key, value))
    return out


def main():
    path = Path(sys.argv[1])
    text = path.read_text(encoding="utf-8", errors="replace")
    print("#", path.name, len(text), "chars")
    for cls in CLASSES:
        for n, block in enumerate(blocks(text, cls)):
            print(f"\n== {cls} #{n}")
            for key, value in scalars(block):
                if key not in ("_class", "m_nNodeID", "m_sName") and not key.startswith("m_vecPosition"):
                    print(f"   {key} = {value}")
                elif key == "m_sName":
                    print(f"   name = {value}")
            if cls == "CMotionMatchingAnimNode":
                for mm in METRIC_HINT.finditer(block):
                    mcls = mm.group(1)
                    sub = next(blocks(block[mm.start() - 400:], mcls), "")
                    fields = [(k, v) for k, v in scalars(sub) if k not in ("_class",)]
                    print("   metric", mcls, dict(fields))
            if cls == "CMotionClipGroup":
                names = re.findall(r'm_name = "([^"]+)"', block)
                clips = re.findall(r'm_sequenceName = "([^"]+)"', block)
                print("   group name(s):", names[:2], "clips:", len(clips))
                print("   ", ", ".join(sorted(set(clips)))[:1500])


if __name__ == "__main__":
    main()
