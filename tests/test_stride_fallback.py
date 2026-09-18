import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from stride_data import analyse_foot, loop_extend, reconstruct, steps_remaining, to_world


FPS = 30.0


class LoopFallbackTests(unittest.TestCase):
    def test_no_middle_contact_uses_virtual_period_and_round_trips(self):
        # The sole follows a body moving at 2 m/s, so its contact speed is always above the detector's stance
        # threshold.  There is consequently no repeatable contact in any of the three analysis copies.
        n = 12
        root = [(0.0, 2.0 * f / FPS, 0.0) for f in range(n + 1)]
        heel = [(0.0, 0.0, -0.1)] * n
        toe = [(0.0, 0.0, 0.15)] * n
        result = analyse_foot(root, heel, toe, FPS, loop=True)

        self.assertEqual(1, len(result["cycles"]), result["cycles"])
        cycle = result["cycles"][0]
        self.assertEqual((0, n), (cycle["startFrame"], cycle["endFrame"]))
        self.assertEqual(n, cycle["strikeFrame"])
        self.assertTrue(cycle["virtualStart"] and cycle["virtualEnd"])
        self.assertEqual([1.0] * 4, [cycle[key] for key in (
            "footLiftCycle", "footOffCycle", "footStrikeCycle", "footLandCycle")])
        self.assertEqual([0] * n, result["frames"]["cycle"])
        self.assertEqual([0] * n, result["frames"]["grounded"])

        # Validate the serialized trajectory against the middle analysis copy, including the virtual end anchor.
        extended_root, _ = loop_extend(root, [None] * n, 3)
        frames = result["frames"]
        start = to_world(extended_root[n], frames["footbase"][0][:3])
        end = to_world(extended_root[2 * n], frames["footbase"][0][:3])
        for f in range(n):
            at = extended_root[n + f]
            expected = to_world(at, frames["footbase"][f][:3])
            rebuilt = reconstruct(at, cycle, {
                "progression": frames["progression"][f],
                "translationOffset": frames["translationOffset"][f],
            }, result["floor"], start, end)
            for actual, authored in zip(rebuilt, expected):
                self.assertAlmostEqual(actual, authored, places=3, msg=f"frame {f}")

        # A virtual boundary is not a landing and must not create a future strike.
        self.assertEqual([0] * n, steps_remaining([result], n))


if __name__ == "__main__":
    unittest.main()
