import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from placement_viewer import read_frames, vector


def sample(frame, stage):
    leg = {}
    for name, position in (("Hip", [0, 1, 0]), ("Knee", [0, .5, .1]), ("Foot", [0, 0, 0])):
        leg["Has" + name + "Position"] = True
        leg[name + "Position"] = dict(zip(("X", "Y", "Z"), position))
    return {"Frame": frame, "Time": frame * .03, "Stage": stage,
            "Root": {"Value": {"X": 0, "Y": 0, "Z": 0}},
            "Grounder": {"LeftLeg": leg, "RightLeg": leg.copy()}}


class PlacementViewerTests(unittest.TestCase):
    def test_native_jump_state_is_preserved_without_inventing_ground_data(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.json"
            after = sample(3, "after_lock")
            after["Pose"] = {"SprintEntry": {"HasContext": True, "State": "Jump", "Grounded": False}}
            path.write_text(json.dumps({"Samples": [sample(3, "after_visual"), after]}))
            frame = read_frames(path)[0]
            self.assertEqual(frame["movement_state"], "Jump")
            self.assertIs(frame["grounded"], False)
            after["Pose"]["SprintEntry"]["HasContext"] = False
            path.write_text(json.dumps({"Samples": [sample(3, "after_visual"), after]}))
            self.assertIsNone(read_frames(path)[0]["grounded"])

    def test_pairs_only_same_frame_and_keeps_origin_as_valid_geometry(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.json"
            path.write_text(json.dumps({"Samples": [sample(1, "after_visual"), sample(2, "after_lock"), sample(3, "after_visual"), sample(3, "after_lock")]}))
            frames = read_frames(path)
            self.assertEqual([f["f"] for f in frames], [3])
            self.assertEqual(frames[0]["after"]["L"][2], [0, 0, 0])

    def test_rejects_nonfinite_and_missing_points(self):
        self.assertIsNone(vector([1, float("nan"), 3]))
        self.assertIsNone(vector({"X": 0, "Y": 0}))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.json"
            after = sample(3, "after_lock")
            after["Grounder"]["LeftLeg"]["HasHipPosition"] = False
            path.write_text(json.dumps({"Samples": [sample(3, "after_visual"), after]}))
            self.assertEqual(read_frames(path), [])

    def test_old_fleet_is_not_fabricated_as_zero_joints(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fleet.jsonl"
            path.write_text(json.dumps({"k": "s", "L": {"p": [0, 0, 0]}, "R": {"p": [1, 0, 0]}}))
            self.assertEqual(read_frames(path), [])

    def test_unavailable_root_does_not_render_default_origin(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.json"
            after = sample(3, "after_lock")
            after["Root"]["Available"] = False
            path.write_text(json.dumps({"Samples": [sample(3, "after_visual"), after]}))
            self.assertEqual(read_frames(path), [])

    def test_issue_markers_keep_phase_clearance_raw_final_and_dt(self):
        def staged(frame, stage, foot_x, phase, fraction=1.0, time=None):
            value = sample(frame, stage)
            if time is not None:
                value["Time"] = time
            for side in ("LeftLeg", "RightLeg"):
                value["Grounder"][side]["FootPosition"] = {"X": foot_x, "Y": 0, "Z": 0}
            value["Pose"] = {
                "Phase": phase,
                "PlacerL": {"ClearanceBefore": .10, "ClearanceRequested": .08,
                             "ClearanceAfter": .08 * fraction, "ClearanceFraction": fraction},
                "PlacerR": {"ClearanceBefore": .10, "ClearanceRequested": .08,
                             "ClearanceAfter": .08 * fraction, "ClearanceFraction": fraction},
            }
            return value

        samples = []
        for frame, phase, before_x, after_x, fraction in (
            (0, "Walk", 0.00, 0.00, 1.0),
            (1, "Walk", 0.02, 0.20, .50),
            (2, "Stop", 0.04, 0.22, 1.0),
            (3, "Stop", 0.06, 0.45, 1.0),
        ):
            samples.extend((
                staged(frame, "after_visual", before_x, phase, fraction, time=frame * .03 if frame < 3 else .50),
                staged(frame, "after_lock", after_x, phase, fraction, time=frame * .03 if frame < 3 else .50),
            ))

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.json"
            path.write_text(json.dumps({"Samples": samples}), encoding="utf-8")
            frames = read_frames(path)

        issues = [issue for frame in frames for issue in frame["issues"]]
        clearance = [issue for issue in issues if issue["kind"] == "clearance-onset"]
        self.assertEqual([1], [issue["frame"] for issue in clearance])
        jumps = [issue for issue in issues if issue["kind"] == "foot-jump" and issue["side"] == "L"]
        self.assertEqual([1, 3], [issue["frame"] for issue in jumps])
        self.assertAlmostEqual(.02, jumps[0]["raw_before_m"], places=5)
        self.assertAlmostEqual(.20, jumps[0]["final_after_m"], places=5)
        self.assertAlmostEqual(.03, jumps[0]["dt_s"], places=5)
        self.assertIn("placement-added", jumps[0]["label"])
        self.assertTrue(jumps[1]["hitch_suspect"])
        self.assertAlmostEqual(.23, jumps[1]["final_after_m"], places=5)
        self.assertIn("time gap", jumps[1]["label"])
        stop_entries = [issue for issue in issues if issue["kind"] == "phase-entry"]
        self.assertEqual([(2, "Stop")], [(issue["frame"], issue["phase"]) for issue in stop_entries])


if __name__ == "__main__":
    unittest.main()
