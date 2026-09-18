import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from audit_stride_events import audit, cycle_time


def fixture():
    cycle = {"startFrame": 0, "endFrame": 4, "strikeFrame": 3,
             "footLiftCycle": .25, "footOffCycle": .25,
             "footStrikeCycle": .75, "footLandCycle": 1}
    foot = {"cycles": [cycle], "frames": {
        "cycle": [0] * 5, "progression": [0, .01, .02, .99, 1],
        "translationOffset": [[0, 0, 0]] * 5, "rotationOffset": [0] * 5,
        "footbase": [[0, 0, 0, 0]] * 5, "grounded": [1, 0, 0, 1, 1]}}
    return {"clips": [{"name": "accelerating", "frames": 5, "loop": False,
                       "stride": {"L": foot, "R": copy.deepcopy(foot)}}]}


class StrideEventAuditTests(unittest.TestCase):
    def test_time_distance_disagreement_is_not_data_corruption(self):
        report = audit(fixture())
        self.assertEqual(report["errors"], [])
        self.assertEqual(report["clock_disagreement_frames"], 4)
        self.assertEqual(report["clips_with_clock_disagreement"], 1)

    def test_wrapped_cycle_has_continuous_time(self):
        self.assertEqual(cycle_time(9, 8, 12, 10, True), .25)
        self.assertEqual(cycle_time(0, 8, 12, 10, True), .5)
        self.assertEqual(cycle_time(2, 8, 12, 10, True), 1)

    def test_stale_strike_frame_is_invalid(self):
        db = fixture()
        db["clips"][0]["stride"]["R"]["cycles"][0]["strikeFrame"] = 61
        self.assertTrue(any("invalid strike frame" in e for e in audit(db)["errors"]))

    def test_wrong_strike_time_is_invalid(self):
        db = fixture()
        db["clips"][0]["stride"]["R"]["cycles"][0]["footStrikeCycle"] = .7
        self.assertTrue(any("strike time disagrees" in e for e in audit(db)["errors"]))

    def test_bad_array_and_nonfinite_progression_are_reported(self):
        db = fixture()
        db["clips"][0]["stride"]["L"]["frames"]["grounded"] = []
        db["clips"][0]["stride"]["R"]["frames"]["progression"][2] = float("nan")
        errors = audit(db)["errors"]
        self.assertTrue(any("grounded" in e for e in errors))
        self.assertTrue(any("nonfinite" in e for e in errors))


if __name__ == "__main__":
    unittest.main()
