import json
import math
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import arm_sync_report as report


def point(x, y=0.0, z=0.0):
    return {"X": x, "Y": y, "Z": z}


def motion_sync(left_arm, left_thigh, right_arm=None, right_thigh=None,
                missing_sides=()):
    if right_arm is None:
        right_arm = left_arm
    if right_thigh is None:
        right_thigh = left_thigh
    missing_sides = set(missing_sides)
    values = {
        "LeftArmAngle": left_arm,
        "LeftThighAngle": left_thigh,
        "RightArmAngle": right_arm,
        "RightThighAngle": right_thigh,
    }
    result = {"HasRoot": True}
    for key, value in values.items():
        side = "Left" if key.startswith("Left") else "Right"
        result["Has" + key] = side not in missing_sides
        result[key] = 0.0 if side in missing_sides else value
    return result


def phase_sync(frame, time, frequency=0.85, phase_error=0.0,
               phase_locked=True, state_hash=101):
    animator = (frequency * time) % 1.0
    return {
        "SampleFrame": frame,
        "SampleTime": time,
        "HasAnimatorPhase": True,
        "AnimatorPhase": animator,
        "AnimatorLength": 1.0 / frequency,
        "HasAnimatorStateHash": state_hash is not None,
        "AnimatorStateHash": 0 if state_hash is None else state_hash,
        "HasTransition": True,
        "InTransition": False,
        "HasLegPhase": True,
        "LegPhase": (animator + phase_error) % 1.0,
        "PhaseLocked": phase_locked,
        "PhaseLockEligible": True,
        "PhaseLockReason": "locked" if phase_locked else "phase_error",
    }


def sample(frame, time, stage, motion, sync, *, position=None,
           phase="SprintCycle", clip="tarkov_synthetic_cycle", driver="synthetic",
           weight=1.0, active=True, visible=True, simplified=False,
           aiming=False, shooting=False, aiming_known=True,
           shooting_known=True):
    if position is None:
        position = time
    return {
        "Index": frame * 2 + (0 if stage == "before_visual" else 1),
        "Stage": stage,
        "Frame": frame,
        "Time": time,
        "UnscaledTime": time,
        "Root": {"Available": True, "Value": point(position)},
        "MotionSync": motion,
        "Pose": {
            "Active": active,
            "Phase": phase,
            "Clip": clip,
            "Driver": driver,
            "Weight": weight,
            "Sync": sync,
        },
        "HasIsVisible": True,
        "IsVisible": visible,
        "HasUsedSimplifiedSkeleton": True,
        "UsedSimplifiedSkeleton": simplified,
        "HasAiming": aiming_known,
        "IsAiming": aiming,
        "HasShooting": shooting_known,
        "IsShooting": shooting,
    }


def synthetic_samples(times, *, frequency=0.85, native_offset=0.0,
                      final_offset=0.0, arm_amplitude=18.0,
                      thigh_amplitude=22.0, position_fn=None,
                      phase_error=0.0, phase_locked=True,
                      state_hash=101, phase="SprintCycle",
                      aiming=False, shooting=False, aiming_known=True,
                      shooting_known=True, missing_sides=(),
                      include_before=None):
    samples = []
    include_before = include_before or set()
    for frame, time in enumerate(times):
        theta = 2.0 * math.pi * frequency * time
        native_thigh = thigh_amplitude * math.cos(theta)
        native_arm = arm_amplitude * math.cos(theta + 2.0 * math.pi * native_offset)
        final_thigh = thigh_amplitude * math.cos(theta)
        final_arm = arm_amplitude * math.cos(theta + 2.0 * math.pi * final_offset)
        native = motion_sync(native_arm, native_thigh, missing_sides=missing_sides)
        final = motion_sync(final_arm, final_thigh, missing_sides=missing_sides)
        sync = phase_sync(frame, time, frequency, phase_error, phase_locked, state_hash)
        position = time if position_fn is None else position_fn(time)
        if frame not in include_before:
            samples.append(sample(frame, time, "before_visual", native, sync,
                                  position=position, phase=phase,
                                  aiming=aiming, shooting=shooting,
                                  aiming_known=aiming_known,
                                  shooting_known=shooting_known))
        samples.append(sample(frame, time, "after_lock", final, sync,
                              position=position, phase=phase,
                              aiming=aiming, shooting=shooting,
                              aiming_known=aiming_known,
                              shooting_known=shooting_known))
    return samples


def rows_from_samples(samples, player_id="synthetic-bot"):
    document = {"Schema": "manimal.motionmatching.diagnostic.v2",
                "PlayerId": player_id, "Samples": samples}
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "capture.json"
        path.write_text(json.dumps(document), encoding="utf-8")
        rows, source = report.load_rows(path)
    if source != "puppet":
        raise AssertionError("synthetic fixture did not use puppet parser")
    return rows


class ArmSyncReportTests(unittest.TestCase):
    def test_wrapped_offsets_and_long_window_are_measured(self):
        times = [index / 20.0 for index in range(73)]
        rows = rows_from_samples(
            synthetic_samples(times, native_offset=0.45, final_offset=-0.45))

        measurement, reason = report.measure_window(rows, "Left")

        self.assertIsNone(reason)
        self.assertIsNotNone(measurement)
        self.assertGreater(measurement["cycles"], report.MIN_CYCLES)
        self.assertLess(abs(report.circular_delta(
            measurement["native_arm_minus_thigh_cycles"], 0.45)), 0.03)
        self.assertLess(abs(report.circular_delta(
            measurement["final_arm_minus_thigh_cycles"], -0.45)), 0.03)
        self.assertLess(abs(report.circular_delta(
            measurement["change_cycles"], 0.10)), 0.03)
        self.assertTrue(measurement["changed_over_threshold"])

    def test_irregular_capture_times_are_supported(self):
        times = [0.0]
        increments = (0.037, 0.052, 0.044, 0.061, 0.041)
        for index in range(1, 80):
            times.append(times[-1] + increments[index % len(increments)])
        rows = rows_from_samples(
            synthetic_samples(times, frequency=0.86,
                              native_offset=-0.19, final_offset=-0.03))

        measurement, reason = report.measure_window(rows, "Left")

        self.assertIsNone(reason)
        self.assertIsNotNone(measurement)
        self.assertAlmostEqual(measurement["frequency_hz"], 0.86, delta=0.06)
        self.assertLess(abs(report.circular_delta(
            measurement["change_cycles"], 0.16)), 0.04)

    def test_staircase_positions_keep_a_moving_segment_continuous(self):
        times = [0.0]
        increments = (0.018, 0.022, 0.019, 0.021, 0.020, 0.023, 0.017)
        for index in range(1, 161):
            times.append(times[-1] + increments[index % len(increments)])

        def staircase(time):
            # Fixed updates advance the root every 100 ms while render samples
            # arrive at 50 Hz. Offset the staircase from t=0 so the first
            # 80 ms travel window also observes a step.
            return math.floor((time + 0.04) / 0.1) * 0.12

        rows = rows_from_samples(
            synthetic_samples(times, frequency=0.8, position_fn=staircase))
        runs, excluded = report.segments(rows, "Left")

        self.assertEqual(len(runs), 1)
        self.assertGreaterEqual(runs[0][0]["time"], 0.08)
        self.assertNotIn("not_moving", excluded)
        self.assertNotIn("teleport", excluded)
        self.assertTrue(all(row["travel_speed"] >= 0.2 for row in runs[0]))

    def test_quiet_arms_are_unassessable(self):
        times = [index / 20.0 for index in range(73)]
        rows = rows_from_samples(
            synthetic_samples(times, arm_amplitude=1.0, thigh_amplitude=22.0))

        measurement, reason = report.measure_window(rows, "Left")

        self.assertIsNone(measurement)
        self.assertEqual(reason, "low_amplitude")

    def test_monotonic_trend_is_not_periodic_motion(self):
        times = [index / 20.0 for index in range(73)]
        samples = synthetic_samples(times, frequency=0.85)
        for item in samples:
            value = -32.0 + 18.0 * item["Time"]
            item["MotionSync"]["LeftArmAngle"] = value
            item["MotionSync"]["LeftThighAngle"] = value
            item["MotionSync"]["RightArmAngle"] = value
            item["MotionSync"]["RightThighAngle"] = value
        rows = rows_from_samples(samples)

        measurement, reason = report.measure_window(rows, "Left")

        self.assertIsNone(measurement)
        self.assertIn(reason, {"nonperiodic_or_unstable_cadence",
                               "insufficient_cycles", "low_amplitude"})

    def test_missing_angles_and_missing_paired_stages_are_unassessable(self):
        times = [index / 20.0 for index in range(30)]
        missing_angles = synthetic_samples(times, missing_sides=("Left",))
        rows = rows_from_samples(missing_angles)
        runs, excluded = report.segments(rows, "Left")
        self.assertEqual(runs, [])
        self.assertEqual(excluded["missing_paired_angles"], len(rows))

        missing_stage = synthetic_samples(times)
        missing_stage = [item for item in missing_stage
                         if not (item["Frame"] == 10 and
                                 item["Stage"] == "before_visual")]
        rows = rows_from_samples(missing_stage)
        self.assertIsNone(next(row for row in rows if row["frame"] == 10)["before"])
        runs, excluded = report.segments(rows, "Left")
        self.assertEqual(excluded["missing_paired_angles"], 1)
        self.assertEqual(len(runs), 2)

    def test_gaps_bot_separation_and_state_changes_split_segments(self):
        bot_a = rows_from_samples(synthetic_samples(
            [0.0, 0.1, 0.2, 0.3, 0.8, 0.9, 1.0, 1.1],
            position_fn=lambda time: time), player_id="bot-a")
        bot_b = rows_from_samples(synthetic_samples(
            [0.0, 0.1, 0.2, 0.3], position_fn=lambda time: time),
            player_id="bot-b")
        for row in bot_a:
            if row["time"] >= 1.0:
                row["sync"]["AnimatorStateHash"] = 202
        rows = bot_a + bot_b

        runs, excluded = report.segments(rows, "Left")
        starts = [(run[0]["id"], run[0]["time"]) for run in runs]

        self.assertEqual([item[0] for item in starts],
                         ["bot-a", "bot-a", "bot-a", "bot-b"])
        self.assertEqual(starts[0][1], 0.1)
        self.assertEqual(starts[1][1], 0.9)
        self.assertEqual(starts[2][1], 1.0)
        self.assertEqual(starts[3][1], 0.1)
        self.assertGreaterEqual(excluded["missing_travel_window"], 2)
        self.assertNotIn("teleport", excluded)

    def test_unknown_and_active_weapon_contexts_are_excluded(self):
        times = [index / 20.0 for index in range(30)]
        for label, kwargs, expected in (
            ("unknown_aiming", {"aiming_known": False}, "unknown_weapon_context"),
            ("unknown_shooting", {"shooting_known": False}, "unknown_weapon_context"),
            ("aiming", {"aiming": True}, "aiming_or_shooting"),
            ("shooting", {"shooting": True}, "aiming_or_shooting"),
        ):
            with self.subTest(label=label):
                rows = rows_from_samples(synthetic_samples(times, **kwargs))
                runs, excluded = report.segments(rows, "Left")
                self.assertEqual(runs, [])
                self.assertEqual(excluded[expected], len(rows))

    def test_phase_lock_flag_does_not_claim_visible_sync(self):
        times = [index * 0.1 for index in range(4)]
        locked_rows = rows_from_samples(synthetic_samples(
            times, phase_locked=True, phase_error=0.0))
        unlocked_rows = rows_from_samples(synthetic_samples(
            times, phase_locked=False, phase_error=0.2))

        locked = report.command_report(locked_rows)
        unlocked = report.command_report(unlocked_rows)
        self.assertEqual(locked["signed_error_cycles"]["n"], len(locked_rows))
        self.assertAlmostEqual(locked["absolute_error_cycles"]["p50"], 0.0)
        self.assertEqual(unlocked["signed_error_cycles"]["n"], len(unlocked_rows))
        self.assertAlmostEqual(unlocked["signed_error_cycles"]["p50"], 0.2)
        self.assertTrue(unlocked["episodes_over_0_05_cycles"])
        self.assertIn("does not prove visible arm synchronization", locked["note"])

        document = {"Schema": "manimal.motionmatching.diagnostic.v2",
                    "PlayerId": "phase-proof-bot",
                    "Samples": synthetic_samples(times, phase_locked=True,
                                                   phase_error=0.0)}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.json"
            path.write_text(json.dumps(document), encoding="utf-8")
            analysed = report.analyse(path)
        self.assertEqual(analysed["observed_motion"]["assessable_windows"], 0)
        self.assertEqual(analysed["observed_motion"]["excluded_windows"]["short_window"], 2)

    def test_numpy_unavailable_makes_observed_fit_unassessable(self):
        rows = rows_from_samples(synthetic_samples(
            [index / 20.0 for index in range(73)]))
        original = report.np
        report.np = None
        try:
            measurement, reason = report.measure_window(rows, "Left")
        finally:
            report.np = original
        self.assertIsNone(measurement)
        self.assertEqual(reason, "numpy_unavailable")


class SprintScopeTests(unittest.TestCase):
    def test_internal_cycle_phase_is_not_physical_sprint(self):
        samples = synthetic_samples([i * .05 for i in range(100)], phase_error=.2)
        for item in samples:
            item["Movement"] = {"HasSprintEnabled": True, "SprintEnabled": False}
        rows = rows_from_samples(samples)
        self.assertTrue(all(report.motion_context(row) == "non_sprint" for row in rows))
        self.assertIsNone(report.command_report(rows, "sprint")["absolute_error_cycles"])
        self.assertIsNotNone(report.command_report(rows, "non_sprint")["absolute_error_cycles"])

    def test_missing_physical_state_stays_unknown(self):
        rows = rows_from_samples(synthetic_samples([i * .05 for i in range(100)]))
        self.assertTrue(all(report.motion_context(row) == "unknown" for row in rows))
        self.assertIsNone(report.command_report(rows, "sprint")["absolute_error_cycles"])

    def test_motion_windows_split_at_sprint_state_changes(self):
        samples = synthetic_samples([i * .05 for i in range(160)], phase_error=.2)
        for item in samples:
            item["Movement"] = {"HasSprintEnabled": True, "SprintEnabled": int(item["Time"] / 1.5) % 2 == 0}
        rows = rows_from_samples(samples)
        runs, _ = report.segments(rows, "Left")
        self.assertGreater(len(runs), 3)
        self.assertTrue(all(len({report.motion_context(row) for row in run}) == 1 for run in runs))
        episodes = report.command_report(rows, "sprint")["episodes_over_0_05_cycles"]
        self.assertGreater(len(episodes), 2)
        self.assertTrue(all(e["end_time"] - e["start_time"] < 1.5 for e in episodes))


if __name__ == "__main__":
    unittest.main()
