import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import placement_diagnostics as diagnostics


def point(x, y, z):
    return {"X": x, "Y": y, "Z": z}


def puppet_leg(x, z, shifted_knee=0.0, heel=None, toe=None):
    hip = point(x, 0.9, z)
    knee = point(x - 0.18 + shifted_knee, 0.48, z + 0.08)
    ankle = point(x - 0.48, 0.08, z)
    leg = {
        "HasHipPosition": True, "HipPosition": hip,
        "HasKneePosition": True, "KneePosition": knee,
        "HasFootPosition": True, "FootPosition": ankle,
        "HasToePosition": True, "ToePosition": point(x - 0.48, 0.03, z + 0.16),
    }
    probe = {"Active": True, "Locked": True, "Frozen": True, "Correction": 0.0}
    if heel is not None:
        probe["PlacedHeel"] = point(heel, 0.02, z)
    if toe is not None:
        probe["PlacedToe"] = point(toe, 0.02, z + 0.16)
    return leg, probe


def puppet_capture(frames=5, body_yaw=90.0, pelvis_shift=0.05, knee_shift=0.09):
    samples = []
    for frame in range(frames):
        t = frame * 0.05
        root_x = t
        before_l, _ = puppet_leg(root_x - 0.10, -0.08)
        before_r, _ = puppet_leg(root_x + 0.10, 0.08)
        after_l, probe_l = puppet_leg(root_x - 0.10, -0.08, knee_shift, root_x - 0.58, root_x - 0.48)
        after_r, probe_r = puppet_leg(root_x + 0.10, 0.08, knee_shift, root_x - 0.38, root_x - 0.28)
        for stage, legs, probes, pelvis_y in (
            ("after_visual", (before_l, before_r), ({}, {}), 0.9),
            ("after_lock", (after_l, after_r), (probe_l, probe_r), 0.9 + pelvis_shift),
        ):
            samples.append({
                "Index": len(samples), "Frame": frame, "Stage": stage, "Time": t,
                "Root": {"Available": True, "Value": point(root_x, 0.0, 0.0)},
                "Pelvis": {"Available": True, "Value": point(root_x, pelvis_y, 0.0)},
                "BodyYaw": body_yaw,
                "Movement": {"HasPlayerSpeed": True, "PlayerSpeed": 1.0, "HasSprintEnabled": False, "SprintEnabled": False},
                "Grounder": {
                    "LeftLeg": legs[0], "RightLeg": legs[1],
                },
                "Pose": {
                    "Phase": "Walk", "Clip": "walk_loop", "Driver": "puppet",
                    "ContactL": True, "ContactR": True,
                    "PlacerL": probes[0], "PlacerR": probes[1],
                },
            })
    return {"Schema": "manimal.motionmatching.diagnostic.v2", "LocalBotId": "puppet-1", "Samples": samples}


def fleet_line(frame, t, geometry=True, x=0.0, suspended=False, teleport=False):
    row = {"k": "s", "t": t, "f": frame, "b": 7, "x": x, "y": 0.0, "z": 0.0,
           "yaw": 0.0, "spd": 1.0, "spr": 0, "pose": 1.0, "vis": 1, "simp": 0, "sus": int(suspended),
           "ph": "Walk", "clip": "walk_loop", "drv": "fleet", "placed": 1}
    if not geometry:
        return row
    # World-space geometry with a small lateral crossing of the ankles. The
    # segment clearance remains large because the thighs/shins stay apart.
    row.update({"rj": [x, 0.0, 0.0], "pe": [x, 0.9, 0.0], "pe0": [x, 0.9, 0.0]})
    row["L"] = {"h": [x - 0.10, 0.9, -0.08], "k": [x - 0.28, 0.48, -0.08], "a": [x - 0.58, 0.08, 0.02],
                "h0": [x - 0.10, 0.9, -0.08], "k0": [x - 0.28, 0.48, -0.08], "a0": [x - 0.58, 0.08, 0.02],
                "hl": [x - 0.62, 0.02, 0.02], "to": [x - 0.48, 0.02, 0.18], "hl0": [x - 0.62, 0.02, 0.02], "to0": [x - 0.48, 0.02, 0.18], "lk": 1}
    row["R"] = {"h": [x + 0.10, 0.9, 0.08], "k": [x + 0.28, 0.48, 0.08], "a": [x + 0.58, 0.08, -0.02],
                "h0": [x + 0.10, 0.9, 0.08], "k0": [x + 0.28, 0.48, 0.08], "a0": [x + 0.58, 0.08, -0.02],
                "hl": [x + 0.54, 0.02, -0.02], "to": [x + 0.68, 0.02, 0.14], "hl0": [x + 0.54, 0.02, -0.02], "to0": [x + 0.68, 0.02, 0.14], "lk": 1}
    if teleport:
        row["x"] = x + 2.0
    return row


class PlacementDiagnosticsTests(unittest.TestCase):
    def test_puppet_pairs_exact_frame_and_reports_post_placement_changes(self):
        result = diagnostics.analyse(puppet_capture())
        self.assertEqual("puppet", result["source"])
        self.assertEqual(5, result["frames"])
        self.assertEqual(5, result["input_meta"]["coverage"]["paired_after_lock"])
        trailing = result["metrics"]["trailing_ankle"]["L"]["after"]
        self.assertGreater(trailing["p50"], 0.35)
        self.assertGreater(result["metrics"]["pelvis_shift"]["post_placement"]["p50"], 0.04)
        self.assertGreater(result["metrics"]["knee_displacement"]["post_placement"]["p50"], 0.08)
        self.assertTrue(any(e["metric"] == "pelvis_shift" and e["side"] == "both" for e in result["heuristic_flags"]["episodes"]))

    def test_body_yaw_does_not_replace_measured_travel_and_width_is_signed(self):
        result = diagnostics.analyse(puppet_capture(body_yaw=90.0, pelvis_shift=0.0, knee_shift=0.0))
        row = result["frame_metrics"][1]
        self.assertGreater(row["after"]["by_side"]["L"]["trailing_ratio"], 0.35)
        width = row["after"]["ankle_width_m"]
        self.assertIsNotNone(width)
        self.assertIn("ankle_width", result["metrics"])
        self.assertEqual({}, result["heuristic_flags"]["counts"].get("ankle_width:both", {}))

    def test_rolled_sole_uses_stable_heel_or_toe_endpoint(self):
        document = puppet_capture(frames=4, pelvis_shift=0.0, knee_shift=0.0)
        # Keep the final toe still but move the heel every frame. A base
        # measurement would see the roll; stable endpoint should be zero.
        for sample in document["Samples"]:
            if sample["Stage"] != "after_lock":
                continue
            frame = sample["Frame"]
            sample["Pose"]["PlacerL"]["PlacedHeel"]["X"] += frame * 0.10
            sample["Pose"]["PlacerR"]["PlacedHeel"]["X"] += frame * 0.10
            sample["Pose"]["PlacerL"]["PlacedToe"]["X"] = -0.48
            sample["Pose"]["PlacerR"]["PlacedToe"]["X"] = 0.48
        result = diagnostics.analyse(document)
        values = [row["post_placement"]["by_side"]["L"]["sole_contact_drift_m"] for row in result["frame_metrics"]]
        values = [value for value in values if value is not None]
        self.assertTrue(values)
        self.assertAlmostEqual(0.0, max(values), places=6)

    def test_pre_placer_probe_sole_points_are_not_used_as_before_geometry(self):
        document = puppet_capture(frames=1)
        before_sample = next(sample for sample in document["Samples"] if sample["Stage"] == "after_visual")
        before_sample["Pose"]["PlacerL"] = {"Active": True, "PlacedHeel": point(99.0, 0.0, 99.0), "PlacedToe": point(99.0, 0.0, 99.0)}
        frames, _ = diagnostics.load_frames_from_document(document) if hasattr(diagnostics, "load_frames_from_document") else diagnostics._load_puppet_document(document)
        self.assertNotIn("heel", frames[0]["before"]["legs"]["L"])
        self.assertNotIn("toe", frames[0]["before"]["legs"]["L"])

    def test_inactive_probe_unavailable_root_and_frozen_state_do_not_fake_support(self):
        document = puppet_capture(frames=2, pelvis_shift=0.0, knee_shift=0.0)
        for sample in document["Samples"]:
            if sample["Stage"] != "after_lock":
                continue
            sample["Pose"]["PlacerL"].update({"Active": False, "Locked": False, "Frozen": True,
                                                "AuthoredGrounded": False,
                                                "PlacedHeel": point(0.0, 0.0, 0.0),
                                                "PlacedToe": point(0.0, 0.0, 0.0)})
            sample["Pose"]["ContactL"] = False
        first_after = next(sample for sample in document["Samples"] if sample["Stage"] == "after_lock")
        first_after["Root"] = {"Available": False, "Value": point(0.0, 0.0, 0.0)}
        frames, _ = diagnostics.load_frames_from_document(document)
        self.assertIsNone(frames[0]["after"]["root"])
        self.assertNotIn("heel", frames[0]["after"]["legs"]["L"])
        self.assertNotIn("toe", frames[0]["after"]["legs"]["L"])
        result = diagnostics.analyse(document)
        self.assertFalse(result["frame_metrics"][1]["post_placement"]["by_side"]["L"]["contact_known"])
        self.assertIsNone(result["metrics"]["sole_contact_drift"]["L"]["after"])

    def test_unpaired_frame_is_marked_and_does_not_report_placement_delta(self):
        document = puppet_capture(frames=2)
        document["Samples"] = [sample for sample in document["Samples"] if sample["Stage"] != "after_lock"]
        result = diagnostics.analyse(document)
        self.assertEqual(2, result["input_meta"]["coverage"]["unpaired_frames"])
        self.assertEqual(2, result["coverage"]["frame_counts"]["unpaired"])
        self.assertIsNone(result["metrics"]["pelvis_shift"]["post_placement"])
        self.assertIsNone(result["metrics"]["trailing_ankle"]["after"])
        self.assertTrue(all(row["pair_status"] == "unpaired_after_visual" for row in result["frame_metrics"]))

    def test_before_visual_cannot_substitute_for_before_placement(self):
        document = puppet_capture(frames=2)
        for sample in document["Samples"]:
            if sample["Stage"] == "after_visual":
                sample["Stage"] = "before_visual"
        result = diagnostics.analyse(document)
        self.assertEqual(2, result["input_meta"]["coverage"]["omitted_missing_after_visual_frames"])
        self.assertEqual([], result["frame_metrics"])
        self.assertIsNone(result["metrics"]["pelvis_shift"]["post_placement"])

    def test_omitted_before_stage_breaks_temporal_continuity(self):
        document = puppet_capture(frames=3)
        for sample in document["Samples"]:
            if sample["Stage"] == "after_visual" and sample["Frame"] == 1:
                sample["Stage"] = "before_visual"
        result = diagnostics.analyse(document)
        self.assertEqual([0, 2], [row["frame"] for row in result["frame_metrics"]])
        self.assertFalse(result["frame_metrics"][1]["geometry_continuity"])
        self.assertIn("missing_after_visual", result["frame_metrics"][1]["continuity_break_reasons"])

    def test_pre_render_drift_omits_probe_soles_and_speed_uses_body_motion(self):
        document = puppet_capture(frames=3, pelvis_shift=0.0, knee_shift=0.0)
        for sample in document["Samples"]:
            sample["Movement"]["PlayerSpeed"] = 0.35
        after = next(sample for sample in document["Samples"] if sample["Stage"] == "after_lock" and sample["Frame"] == 1)
        render = json.loads(json.dumps(after))
        render["Stage"] = "pre_render"
        render["Index"] = 1000
        render["Grounder"]["LeftLeg"]["FootPosition"]["X"] += 0.03
        render["Pose"]["PlacerL"]["PlacedHeel"] = point(100.0, 0.0, 100.0)
        render["Pose"]["PlacerL"]["PlacedToe"] = point(100.0, 0.0, 100.0)
        document["Samples"].append(render)
        result = diagnostics.analyse(document)
        row = next(row for row in result["frame_metrics"] if row["frame"] == 1)
        self.assertAlmostEqual(1.0, row["context"]["speed"], places=6)
        self.assertAlmostEqual(0.35, row["context"]["command_speed"], places=6)
        self.assertIsNone(row["pre_render_drift"]["by_side"]["L"]["heel_drift_m"])
        self.assertGreater(row["pre_render_drift"]["by_side"]["L"]["ankle_drift_m"], 0.02)

    def test_missing_fleet_pre_pelvis_stays_unavailable_and_episode_has_no_fake_peak(self):
        records = [{"k": "meta", "geometrySchema": 1}]
        for frame in range(5):
            row = fleet_line(frame, frame * 0.05, x=frame * 0.05)
            row.pop("pe0", None)
            records.append(row)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "missing-pre-pelvis.jsonl"
            path.write_text("\n".join(json.dumps(line) for line in records), encoding="utf-8")
            result = diagnostics.analyse(path)
        self.assertIsNone(result["metrics"]["pelvis_shift"]["post_placement"])
        self.assertTrue(all("peak_time" not in episode and "peak_frame" not in episode
                            for episode in result["heuristic_flags"]["episodes"]))

    def test_fleet_geometry_and_old_fleet_coverage_are_explicit(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fleet.jsonl"
            lines = [{"k": "meta", "geometrySchema": 1}]
            lines.extend(fleet_line(frame, frame * 0.05, x=frame * 0.05) for frame in range(5))
            path.write_text("\n".join(json.dumps(line) for line in lines), encoding="utf-8")
            frames, meta = diagnostics.load_frames(path)
            self.assertEqual(5, len(frames))
            self.assertEqual(1, meta["geometry_schema"])
            result = diagnostics.analyse(path)
            self.assertIsNotNone(result["metrics"]["inter_leg_separation"]["after"])

            old_path = Path(directory) / "old.jsonl"
            old_path.write_text("\n".join(json.dumps(line) for line in [{"k": "meta"}, fleet_line(0, 0.0, geometry=False)]), encoding="utf-8")
            old_result = diagnostics.analyse(old_path)
            self.assertEqual(1, old_result["coverage"]["unsupported_geometry"])
            self.assertIsNone(old_result["metrics"]["straightness"]["after"])
            self.assertTrue(any("geometrySchema=1" in warning for warning in old_result["warnings"]))

    def test_culled_geometry_is_excluded_from_distributions(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "culled.jsonl"
            valid = fleet_line(0, 0.0, x=0.0)
            culled = fleet_line(1, 0.05, x=0.05)
            culled["vis"] = 0
            path.write_text("\n".join(json.dumps(line) for line in [{"k": "meta", "geometrySchema": 1}, valid, culled]), encoding="utf-8")
            result = diagnostics.analyse(path)
        self.assertEqual(1, result["coverage"]["frame_counts"]["culled"])
        self.assertEqual(1, result["metrics"]["straightness"]["L"]["after"]["n"])

    def test_episodes_do_not_bridge_healthy_rows_or_bots(self):
        records = [{"k": "meta", "geometrySchema": 1}]
        for frame in range(6):
            first = fleet_line(frame, frame * 0.05, x=frame * 0.05)
            if frame == 2:
                # A healthy row in the L side ends the preceding episode.
                first["L"]["a"] = [first["L"]["h"][0] + 0.5, 0.08, first["L"]["h"][2]]
            records.append(first)
            second = fleet_line(frame, frame * 0.05, x=frame * 0.05)
            second["b"] = 8
            records.append(second)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bots.jsonl"
            path.write_text("\n".join(json.dumps(line) for line in records), encoding="utf-8")
            result = diagnostics.analyse(path)
        trailing = [episode for episode in result["heuristic_flags"]["episodes"] if episode["metric"] == "trailing_ankle" and episode["side"] == "L"]
        self.assertIn("8", {str(episode["id"]) for episode in trailing})
        self.assertTrue(all(not (episode["id"] == 7 and episode["start_frame"] < 2 < episode["end_frame"]) for episode in trailing))
        self.assertTrue(all(episode["duration_s"] >= diagnostics.MIN_EPISODE_SECONDS for episode in trailing))

    def test_true_segment_proximity_is_distinguished_from_lateral_crossing(self):
        far = diagnostics.segment_segment_distance((0, 0, 0), (0, 1, 0), (0.2, 0, 0), (0.2, 1, 0))
        touching = diagnostics.segment_segment_distance((0, 0, 0), (0, 1, 0), (0, 0, 0), (0, 1, 0))
        self.assertAlmostEqual(0.2, far, places=6)
        self.assertAlmostEqual(0.0, touching, places=6)

    def test_gap_cull_and_teleport_break_sustained_episode(self):
        records = [{"k": "meta", "geometrySchema": 1}]
        for frame in range(8):
            t = frame * 0.05
            if frame == 3:
                records.append(fleet_line(frame, t, geometry=False, x=frame * 0.05))
            elif frame == 5:
                records.append(fleet_line(frame, t, x=2.0, teleport=True))
            else:
                records.append(fleet_line(frame, t, x=frame * 0.05))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "gap.jsonl"
            path.write_text("\n".join(json.dumps(line) for line in records), encoding="utf-8")
            result = diagnostics.analyse(path)
        self.assertTrue(any(row["coverage"] in {"unsupported", "missing_geometry"} for row in result["frame_metrics"]))
        self.assertTrue(any("teleport" in row["continuity_break_reasons"] for row in result["frame_metrics"]))
        for episode in result["heuristic_flags"]["episodes"]:
            self.assertGreaterEqual(episode["duration_s"], diagnostics.MIN_EPISODE_SECONDS)
            self.assertFalse(episode["start_frame"] <= 3 <= episode["end_frame"])


if __name__ == "__main__":
    unittest.main()
