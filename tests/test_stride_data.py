import math
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import stride_data
from stride_data import analyse_foot, footbase, reconstruct, to_local, to_world

FPS = 30.0
FOOT_LENGTH = 0.25


def smoothstep(t):
    t = min(max(t, 0.0), 1.0)
    return t * t * (3 - 2 * t)


def walking_foot(seconds=3.0, speed=1.0, stride=0.8, plant=0.4, swing=0.4, yaw=0.0, turn_per_stride=0.0, lift=0.1):
    """One foot of a walk along +z (rotated by yaw): plants of `plant` s spaced `stride` apart, smooth swings.
    Returns root, heel, toe (character-local) and the world footbase per frame for checking."""
    frames = int(seconds * FPS)
    root, heel, toe, world = [], [], [], []
    period = plant + swing
    for f in range(frames):
        t = f / FPS
        k = int(t // period)
        phase = t - k * period
        start_z = k * stride
        heading = yaw
        if phase < plant:
            z, y = start_z, 0.0
            heading = yaw + k * turn_per_stride
        else:
            u = (phase - plant) / swing
            z = start_z + stride * smoothstep(u)
            y = lift * math.sin(math.pi * u)
            heading = yaw + (k + u) * turn_per_stride
        # the path itself runs along the walk's yaw, like the root
        base = (math.sin(math.radians(yaw)) * z, y, math.cos(math.radians(yaw)) * z)
        rad = math.radians(heading)
        forward = (math.sin(rad), 0.0, math.cos(rad))
        # sole points either side of the base along the foot heading; the base is the lower one, here flat
        heel_w = (base[0] - forward[0] * FOOT_LENGTH * 0.4, y, base[2] - forward[2] * FOOT_LENGTH * 0.4)
        toe_w = (base[0] + forward[0] * FOOT_LENGTH * 0.6, y, base[2] + forward[2] * FOOT_LENGTH * 0.6)
        r = (math.sin(math.radians(yaw)) * speed * t, math.cos(math.radians(yaw)) * speed * t, math.radians(yaw))
        root.append(r)
        heel.append(to_local(r, heel_w))
        toe.append(to_local(r, toe_w))
        world.append(footbase(heel_w, toe_w))
    return root, heel, toe, world


class FootbaseTests(unittest.TestCase):
    def test_lower_sole_point_wins_and_heading_points_heel_to_toe(self):
        position, heading, weight = footbase((0.0, 0.1, -0.1), (0.0, 0.0, 0.15))
        self.assertAlmostEqual(position[2], 0.15)
        self.assertAlmostEqual(heading, 0.0)
        self.assertEqual(weight, 0.0)
        position, heading, weight = footbase((0.1, 0.0, 0.0), (0.0, 0.1, 0.0))
        self.assertAlmostEqual(position[0], 0.1)
        self.assertAlmostEqual(heading, -90.0)
        self.assertEqual(weight, 1.0)

    def test_flat_sole_preserves_contact_endpoint_until_roll(self):
        heel, toe = (0.0, 0.0, -0.1), (0.0, 0.0, 0.15)
        for previous, expected in ((0.0, toe), (1.0, heel)):
            position, _, weight = footbase(heel, toe, previous)
            self.assertEqual(position, expected)
            self.assertEqual(weight, previous)
        position, _, weight = footbase((0.0, 0.03, -0.1), toe, 1.0)
        self.assertEqual(position, toe)
        self.assertEqual(weight, 0.0)

    def test_local_world_round_trip(self):
        root = (2.0, -1.0, 0.7)
        p = (0.3, 0.05, -0.2)
        back = to_local(root, to_world(root, p))
        for a, b in zip(p, back):
            self.assertAlmostEqual(a, b, places=9)


class WalkAnalysisTests(unittest.TestCase):
    def check_round_trip(self, root, result, world, loop=False):
        n = len(root) - (1 if loop else 0)
        frames = result["frames"]
        if loop:
            # three periods laid out; the clip is the middle one, so a frame before its cycle's start is covered
            # by the previous period's image of that cycle
            ext_root, _ = stride_data.loop_extend(root, [None] * n, 3)
        for f in range(n):
            cycle = result["cycles"][frames["cycle"][f]]
            s0, s1 = cycle["startFrame"], cycle["endFrame"]
            if loop:
                # the stance frame itself ends the previous image (progression 1), so it goes with that one
                shift = 0 if f <= s0 else n
                start = to_world(ext_root[shift + s0], frames["footbase"][s0][:3])
                end = to_world(ext_root[shift + s1], frames["footbase"][s1 % n][:3])
                at = ext_root[n + f]
                expected = to_world(at, frames["footbase"][f][:3])
                for a, b in zip(frames["footbase"][f][:3], to_local(root[f], world[f][0])):
                    self.assertAlmostEqual(a, b, places=3)
            else:
                start = to_world(root[s0], cycle["stancePosition"])
                end = to_world(root[s1], frames["footbase"][s1][:3])
                at = root[f]
                expected = world[f][0]
            frame = {"progression": frames["progression"][f], "translationOffset": frames["translationOffset"][f]}
            rebuilt = reconstruct(at, cycle, frame, result["floor"], start, end)
            for a, b in zip(rebuilt, expected):
                self.assertAlmostEqual(a, b, places=3, msg=f"frame {f}")

    def test_straight_walk_cuts_one_cycle_per_step(self):
        root, heel, toe, world = walking_foot()
        result = analyse_foot(root, heel, toe, FPS)
        real = [c for c in result["cycles"] if not c["stationary"] and not c["virtualEnd"]]
        tail = [c for c in result["cycles"] if c["virtualEnd"]]
        # 3 s at 0.8 s per step: the foot starts planted (anchored at frame 0), three full strides, then the
        # clip ends mid-swing, which is a partial stride to a virtual anchor
        self.assertEqual(3, len(real), result["cycles"])
        self.assertEqual(1, len(tail))
        self.assertFalse(real[0]["virtualStart"])
        self.assertEqual(0, real[0]["startFrame"])
        for c in real:
            self.assertAlmostEqual(0.8, c["strideLength"], delta=0.02)
            self.assertAlmostEqual(0.0, c["strideYaw"], delta=1.0)
            self.assertTrue(0.0 <= c["footLiftCycle"] <= c["footOffCycle"] <= c["footStrikeCycle"] <= c["footLandCycle"] <= 1.0, c)
            self.assertLess(c["footOffCycle"], 0.7)
            self.assertGreater(c["footStrikeCycle"], 0.3)
            self.assertLess(abs(c["stancePosition"][1]), 0.01)
        frames = result["frames"]
        # a shared stance frame stays with the cycle it ends (progression 1); the next cycle owns the frame after
        self.assertAlmostEqual(0.0, frames["progression"][0], delta=1e-3)
        for c in real:
            self.assertAlmostEqual(1.0, frames["progression"][c["endFrame"]], delta=1e-3)
            self.assertLess(max(abs(v) for v in frames["translationOffset"][c["startFrame"]]), 1e-3)
            self.assertLess(max(abs(v) for v in frames["translationOffset"][c["endFrame"]]), 1e-3)
            self.assertEqual(result["cycles"].index(c), frames["cycle"][c["startFrame"] + 1])
        self.assertGreater(max(o[1] for o in frames["translationOffset"]), 0.09)
        self.check_round_trip(root, result, world)

    def test_offsets_are_stride_relative_not_world(self):
        straight = analyse_foot(*walking_foot(yaw=0.0)[:3], FPS)
        turned = analyse_foot(*walking_foot(yaw=135.0)[:3], FPS)
        for a, b in zip(straight["frames"]["translationOffset"], turned["frames"]["translationOffset"]):
            for x, y in zip(a, b):
                self.assertAlmostEqual(x, y, delta=2e-3)
        for a, b in zip(straight["cycles"], turned["cycles"]):
            self.assertAlmostEqual(a["strideYaw"], b["strideYaw"], delta=1.0)

    def test_turning_stride_keeps_rotation_past_180(self):
        root, heel, toe, world = walking_foot(turn_per_stride=270.0)
        result = analyse_foot(root, heel, toe, FPS)
        real = [c for c in result["cycles"] if not c["stationary"] and not c["virtualEnd"]]
        self.assertTrue(real)
        for c in real:
            self.assertAlmostEqual(270.0, c["rotationChange"], delta=2.0)
        # the reference rotation follows the authored turn, so the offset from it stays small
        self.assertLess(max(abs(r) for r in result["frames"]["rotationOffset"]), 45.0)
        self.check_round_trip(root, result, world)

    def test_stationary_clip_is_one_stride_with_time_progression(self):
        frames = 20
        root = [(0.0, 0.0, 0.0)] * frames
        heel = [(0.0, 0.0, -0.1)] * frames
        toe = [(0.0, 0.0, 0.15)] * frames
        result = analyse_foot(root, heel, toe, FPS)
        self.assertEqual(1, len(result["cycles"]))
        self.assertTrue(result["cycles"][0]["stationary"])
        self.assertFalse(result["cycles"][0]["virtualStart"] or result["cycles"][0]["virtualEnd"])
        self.assertAlmostEqual(0.0, result["frames"]["progression"][0])
        self.assertAlmostEqual(1.0, result["frames"]["progression"][-1])
        self.assertEqual([1] * frames, result["frames"]["grounded"])

    def test_loop_cycle_wraps(self):
        # one full period per loop: 24 frames of plant + swing, root return point appended
        root, heel, toe, world = walking_foot(seconds=0.8 + 1.0 / FPS)
        n = 24
        loop_root = root[: n + 1]
        result = analyse_foot(loop_root, heel[:n], toe[:n], FPS, loop=True)
        self.assertEqual(1, len(result["cycles"]), result["cycles"])
        c = result["cycles"][0]
        self.assertTrue(0 <= c["startFrame"] < n)
        self.assertEqual(c["startFrame"] + n, c["endFrame"])
        self.assertAlmostEqual(0.8, c["strideLength"], delta=0.02)
        self.assertEqual(n, len(result["frames"]["progression"]))
        self.check_round_trip(loop_root, result, world[:n], loop=True)

    def test_steps_remaining_counts_future_strikes(self):
        root, heel, toe, _ = walking_foot()
        left = analyse_foot(root, heel, toe, FPS)
        remaining = stride_data.steps_remaining([left], len(root))
        self.assertEqual(3, remaining[0])
        self.assertEqual(0, remaining[-1])
        # the count drops at the landing, which comes before the stance frame in the middle of the plant
        for c in left["cycles"]:
            if not c["virtualEnd"] and not c["stationary"]:
                self.assertLess(c["strikeFrame"], c["endFrame"])
                self.assertEqual(remaining[c["strikeFrame"] - 1] - 1, remaining[c["strikeFrame"]])
        self.assertTrue(all(a >= b for a, b in zip(remaining, remaining[1:])))

    def test_cycles_export_explicit_midpoint_frame_and_local_position(self):
        root, heel, toe, _ = walking_foot()
        result = analyse_foot(root, heel, toe, FPS)
        frames = result["frames"]
        for cycle in result["cycles"]:
            self.assertTrue(cycle["hasMidpoint"])
            expected = cycle["startFrame"] + (cycle["endFrame"] - cycle["startFrame"]) // 2
            self.assertEqual(expected, cycle["middleFrame"])
            index = cycle["middleFrame"]
            self.assertEqual(cycle["middlePosition"], frames["footbase"][index][:3])
            self.assertEqual(cycle["middleOffset"], frames["translationOffset"][index])
            self.assertEqual(cycle["middleProgression"], frames["progression"][index])

    def test_loop_midpoint_frame_stays_unwrapped_after_cycle_normalization(self):
        root, heel, toe, _ = walking_foot(seconds=0.8 + 1.0 / FPS)
        n = 24
        result = analyse_foot(root[: n + 1], heel[:n], toe[:n], FPS, loop=True)
        cycle = result["cycles"][0]
        self.assertTrue(cycle["hasMidpoint"])
        self.assertEqual(cycle["startFrame"] + (cycle["endFrame"] - cycle["startFrame"]) // 2,
                         cycle["middleFrame"])
        index = cycle["middleFrame"] % n
        self.assertEqual(cycle["middlePosition"], result["frames"]["footbase"][index][:3])
        self.assertEqual(cycle["middleOffset"], result["frames"]["translationOffset"][index])


if __name__ == "__main__":
    unittest.main()
