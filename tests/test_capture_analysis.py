import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from summarize_capture import summarize

STAGES = ("after_body", "before_visual", "after_ik", "after_visual")


def _point(x, y=0.0, z=0.0):
    return {"X": x, "Y": y, "Z": z}


def gait_capture(seconds=3.0, fps=60, root_speed=1.0, plant_slide_speed=0.05, stride=0.6, schema="manimal.motionmatching.diagnostic.v2"):
    """Root walks +X; the FootStep curve sign marks the stance foot, which drifts at plant_slide_speed."""
    dt = 1.0 / fps
    half = stride / 2.0
    samples = []
    anchors = {"Left": 0.3, "Right": 0.0}
    for frame in range(int(seconds * fps)):
        t = frame * dt
        root_x = root_speed * t
        step = int(t // half)
        stance = "Left" if step % 2 == 0 else "Right"
        swing = "Right" if stance == "Left" else "Left"
        step_start = step * half
        # stance foot lands ahead of the root at its step start, then slides slowly
        stance_x = root_speed * step_start + 0.3 + plant_slide_speed * (t - step_start)
        # swing foot travels to where it will land at the next step start
        progress = (t - step_start + dt) / half
        swing_from = root_speed * (step_start - half) + 0.3 + plant_slide_speed * half
        swing_to = root_speed * (step_start + half) + 0.3
        feet = {stance: stance_x, swing: swing_from + (swing_to - swing_from) * min(progress, 1.0)}
        for stage in STAGES:
            samples.append({
                "Index": len(samples), "Frame": frame, "Stage": stage, "Time": t,
                "Root": {"Available": True, "Value": _point(root_x)},
                "Pelvis": {"Available": True, "Value": _point(root_x, 0.9)},
                "Animator": {"HasFootStepCurve": True, "FootStepCurve": -1.0 if stance == "Left" else 1.0},
                "Grounder": {side + "Leg": {"HasFootPosition": True, "FootPosition": _point(feet[side], 0.1)} for side in ("Left", "Right")},
            })
    return {"Schema": schema, "Samples": samples, "CapturedSamples": len(samples),
            "StageCounts": {s: len(samples) // 4 for s in STAGES}}


class CaptureAnalysisTests(unittest.TestCase):
    def test_gait_capture_measures_planted_slide_from_footstep_sign(self):
        report = summarize(gait_capture())
        self.assertEqual([], report["warnings"])
        gait = report["footstep_sliding"]
        band = gait["bands"]["0.3-1.8 m/s"]
        self.assertGreaterEqual(band["stances"], 8)
        self.assertGreater(gait["signal_agreement"], 0.9)
        # 0.05 m/s drift over a ~0.3 s plant is ~15 mm
        self.assertTrue(8 <= band["core_slide_mm"]["p50"] <= 20, band)
        self.assertIn("heuristic", report["interpretation"])

    def test_faster_drift_reports_more_slide(self):
        slow = summarize(gait_capture(plant_slide_speed=0.02))["footstep_sliding"]["bands"]["0.3-1.8 m/s"]
        fast = summarize(gait_capture(plant_slide_speed=0.2))["footstep_sliding"]["bands"]["0.3-1.8 m/s"]
        self.assertGreater(fast["core_slide_mm"]["p50"], slow["core_slide_mm"]["p50"] * 3)

    def test_run_speed_lands_in_fast_band(self):
        report = summarize(gait_capture(root_speed=2.7, plant_slide_speed=0.3))
        self.assertEqual(0, report["footstep_sliding"]["bands"]["0.3-1.8 m/s"]["stances"])
        self.assertGreater(report["footstep_sliding"]["bands"][">=1.8 m/s"]["stances"], 0)

    def test_v1_captures_still_accepted(self):
        report = summarize(gait_capture(schema="manimal.motionmatching.diagnostic.v1"))
        self.assertGreater(report["footstep_sliding"]["stances"], 0)

    def test_missing_footstep_curve_is_not_reported_as_no_sliding(self):
        capture = gait_capture()
        for sample in capture["Samples"]:
            sample["Animator"]["HasFootStepCurve"] = False
        report = summarize(capture)
        self.assertEqual(0, report["footstep_sliding"]["stances"])
        self.assertTrue(any("zero is not evidence of no sliding" in w for w in report["warnings"]))

    def test_empty_capture_is_not_reported_as_success(self):
        report = summarize({"Schema": "manimal.motionmatching.diagnostic.v2", "Samples": [], "CapturedSamples": 0, "StageCounts": {}})
        self.assertTrue(any("movement cannot" in w for w in report["warnings"]))
        self.assertEqual(0, report["final_pose_frames"])

    def test_partial_capture_reports_missing_foot_and_read_errors(self):
        capture = gait_capture()
        capture["Samples"][-1]["Grounder"]["LeftLeg"]["HasFootPosition"] = False
        capture["Samples"][0]["CaptureError"] = "TargetInvocationException"
        capture["BufferFull"] = True
        report = summarize(capture)
        self.assertEqual(1, report["capture_errors"]["TargetInvocationException"])
        self.assertEqual(3, len(report["warnings"]))

    def test_inconsistent_counts_and_indices_are_detected(self):
        capture = gait_capture()
        capture["CapturedSamples"] = 10
        capture["StageCounts"] = {}
        capture["Samples"][0]["Index"] = 99
        self.assertEqual(3, len(summarize(capture)["warnings"]))

    def test_unknown_schema_rejected(self):
        with self.assertRaises(ValueError):
            summarize({"Schema": "future"})

    def test_whole_body_ik_offset_is_distinguished_from_leg_ik(self):
        capture = gait_capture(seconds=1.0)
        after_ik = [s for s in capture["Samples"] if s["Stage"] == "after_ik"]
        for sample in after_ik[:5]:
            for leg in ("LeftLeg", "RightLeg"):
                sample["Grounder"][leg]["FootPosition"]["Y"] -= 0.1
            sample["Pelvis"]["Value"]["Y"] -= 0.1
        after_ik[5]["Grounder"]["LeftLeg"]["FootPosition"]["Z"] += 0.05
        ik = summarize(capture)["ik_stage"]
        self.assertEqual(6, ik["frames_changed_over_1mm"])
        self.assertEqual(5, ik["frames_with_identical_offset_on_feet_and_pelvis"])

    def test_puppet_steps_report_speed_and_slide_per_step(self):
        capture = gait_capture(seconds=4.0, plant_slide_speed=0.05)
        final = [s for s in capture["Samples"] if s["Stage"] == "after_visual"]
        half = len(final) // 2
        # second half: body stands still while the feet keep shuffling
        for sample in [s for s in capture["Samples"] if s["Frame"] >= half]:
            sample["Root"]["Value"]["X"] = final[half]["Root"]["Value"]["X"]
        capture["Puppet"] = {"Scenario": "test", "Steps": [
            {"Step": "move:0.3:2", "Kind": "Move", "StartFrame": 0, "EndFrame": half - 1, "StartTime": 0.0, "EndTime": final[half - 1]["Time"], "Meters": 2.0, "EndReason": "completed"},
            {"Step": "stop:2", "Kind": "Stop", "StartFrame": half, "EndFrame": len(final) - 1, "StartTime": final[half]["Time"], "EndTime": final[-1]["Time"], "Meters": 0.0, "EndReason": "completed"},
        ]}
        steps = summarize(capture)["puppet"]["steps"]
        self.assertAlmostEqual(1.0, steps[0]["steady_mps_p50"], places=1)
        self.assertGreater(steps[0]["stances"], 0)
        self.assertIn("0.3-1.8 m/s", steps[0]["core_slide_mm"])
        self.assertEqual(0, steps[1]["root_travel_mm"])
        self.assertGreater(steps[1]["left_foot_travel_mm"], 100)

    def test_rendered_stage_is_measured_and_lock_correction_reported(self):
        capture = gait_capture(seconds=3.0, plant_slide_speed=0.1)
        extra = []
        for sample in capture["Samples"]:
            if sample["Stage"] != "after_visual":
                continue
            locked = json.loads(json.dumps(sample))
            locked["Stage"] = "after_lock"
            stance = "LeftLeg" if sample["Animator"]["FootStepCurve"] < 0 else "RightLeg"
            # pretend the lock cancelled most of the drift
            locked["Grounder"][stance]["FootPosition"]["X"] -= 0.004
            rendered = json.loads(json.dumps(locked))
            rendered["Stage"] = "pre_render"
            extra += [locked, rendered]
        capture["Samples"] += extra
        for index, sample in enumerate(capture["Samples"]):
            sample["Index"] = index
        capture["CapturedSamples"] = len(capture["Samples"])
        capture["StageCounts"].update({"after_lock": len(extra) // 2, "pre_render": len(extra) // 2})
        report = summarize(capture)
        self.assertEqual("pre_render", report["final_stage"])
        self.assertEqual(0, report["stage_deltas"]["after_last_hook"]["changed_over_1mm"])
        self.assertAlmostEqual(4.0, report["stage_deltas"]["lock_correction"]["changed_p50_mm"], places=0)

    def test_route_that_ignored_reduced_speed_is_flagged(self):
        capture = gait_capture()
        capture["Route"] = {"RequestedMoveSpeed": 0.35, "HasMeasuredSpeed": True, "SteadySpeedMetersPerSecond": 2.7}
        self.assertTrue(any("still moved at run speed" in w for w in summarize(capture)["warnings"]))


if __name__ == "__main__":
    unittest.main()
