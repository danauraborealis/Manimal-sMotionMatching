import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from jump_report import analyze

def row(frame, state, ground, known=True):
    return {"Frame": frame, "Time": frame * .02, "Stage": "after_lock", "Pose": {
        "Weight": 0, "SprintEntry": {"HasContext": known, "State": state, "Grounded": ground}}}

class JumpReportTests(unittest.TestCase):
    def test_completed_scenario_does_not_hide_blocked_jump(self):
        result = analyze({"Reason": "Puppet scenario completed", "Puppet": {"Steps": [
            {"Step": "jumpmove:0.3:8:2", "JumpExpected": True, "EndReason": "blocked",
             "JumpSawAirborne": False, "JumpTriggerFrame": -1, "JumpLandingFrame": -1}]}})
        self.assertFalse(result["all_scripted_jumps_completed"])
        self.assertFalse(result["scripted_jumps"][0]["passed"])

    def test_landing_owns_pose_after_ground_return(self):
        result = analyze({"Samples": [row(1,"Run",True),row(2,"Jump",False),row(3,"JumpLanding",True),row(4,"Run",True)]})
        event = result["episodes"][0]
        self.assertEqual(event["start_frame"], 2)
        self.assertEqual(event["end_frame"], 3)
        self.assertEqual(event["airborne_frames"], 1)
        self.assertEqual(event["resume_frame"], 4)
        self.assertTrue(event["complete"])
        self.assertEqual(event["paired_placer_frames"], 0)

    def test_unknown_samples_break_episode_without_claiming_landing(self):
        result = analyze({"Samples": [row(1,"Jump",False),row(2,"Run",False,False),row(3,"Jump",False)]})
        self.assertEqual(len(result["episodes"]), 2)
        self.assertFalse(any(x["complete"] for x in result["episodes"]))

    def test_missing_ground_context_is_not_airborne(self):
        self.assertEqual(analyze({"Samples": [row(1,"Run",False,False)]})["episodes"], [])
