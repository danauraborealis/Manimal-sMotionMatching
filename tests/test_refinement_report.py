import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from refinement_report import report


LEGACY_POLICY = {"id": "legacy-cap-006-baseline-050"}
CURRENT_POLICY = {
    "id": "adaptive-baseline-v2",
    "maximumRequiredClearanceMeters": 0.11,
    "requiredClearanceBaselineFraction": 0.75,
}


class ClearancePrecisionTests(unittest.TestCase):
    def read_report(self, row, fleet, policy=LEGACY_POLICY):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ("capture.jsonl" if fleet else "capture.json")
            if fleet:
                meta = {"k": "meta"}
                if policy is not None:
                    meta["clearancePolicy"] = policy
                path.write_text("\n".join((json.dumps(meta), json.dumps(row))), encoding="utf-8")
            else:
                document = {"Samples": [row]}
                if policy is not None:
                    document["clearancePolicy"] = policy
                path.write_text(json.dumps(document), encoding="utf-8")
            return report(path)

    def read_fleet_records(self, records):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.jsonl"
            path.write_text("\n".join(json.dumps(record) for record in records), encoding="utf-8")
            return report(path)

    def test_legacy_rounded_fleet_threshold_is_not_a_breach(self):
        result = self.read_report({"k": "s", "placed": True, "vis": 1, "refinement": True,
                                  "L": {"clearance0": .025, "clearance": .012}}, True)
        self.assertEqual(result["groups"]["on"]["flags"].get("clearance_floor_violation", 0), 0)
        self.assertEqual(result["clearance_policy_coverage"]["by_policy"][LEGACY_POLICY["id"]]["origin"],
                         "recognized_policy_id")

    def test_legacy_fleet_breach_larger_than_rounding_is_reported(self):
        result = self.read_report({"k": "s", "placed": True, "vis": 1, "refinement": True,
                                  "L": {"clearance0": .025, "clearance": .009}}, True)
        self.assertEqual(result["groups"]["on"]["flags"]["clearance_floor_violation"], 1)

    def test_legacy_full_precision_puppet_threshold_stays_strict(self):
        result = self.read_report({"Stage": "after_lock", "Pose": {"PlacerL": {
            "Applied": True, "Refinement": True, "ClearanceBefore": .025, "ClearanceAfter": .012}}}, False)
        self.assertEqual(result["groups"]["on"]["flags"]["clearance_floor_violation"], 1)

    def test_metadata_less_fleet_segment_does_not_inherit_previous_policy(self):
        sample = lambda: {"k": "s", "placed": True, "vis": 1, "refinement": True,
                          "L": {"clearance0": .2, "clearance": .09}}
        result = self.read_fleet_records([
            {"k": "meta", "clearancePolicy": CURRENT_POLICY},
            sample(),
            {"k": "meta"},
            sample(),
        ])
        self.assertEqual(result["groups"]["on"]["flags"]["clearance_floor_violation"], 1)
        coverage = result["clearance_policy_coverage"]
        self.assertEqual(coverage["by_policy"][CURRENT_POLICY["id"]]["assessed_clearance_pairs"], 1)
        self.assertEqual(coverage["unknown_policy_pairs_by_reason"], {"metadata_missing": 1})

    def test_current_policy_flags_clearance_between_legacy_and_current_limits(self):
        result = self.read_report({"k": "s", "placed": True, "vis": 1, "refinement": True,
                                  "L": {"clearance0": .2, "clearance": .09}}, True,
                                  policy=CURRENT_POLICY)
        self.assertEqual(result["groups"]["on"]["flags"]["clearance_floor_violation"], 1)
        coverage = result["clearance_policy_coverage"]
        self.assertEqual(coverage["by_policy"][CURRENT_POLICY["id"]]["assessed_clearance_pairs"], 1)
        self.assertEqual(coverage["assessed_pair_coverage"], 1)

    def test_current_fleet_threshold_accounts_for_millimeter_rounding(self):
        result = self.read_report({"k": "s", "placed": True, "vis": 1, "refinement": True,
                                  "L": {"clearance0": .1, "clearance": .074}}, True,
                                  policy=CURRENT_POLICY)
        self.assertEqual(result["groups"]["on"]["flags"].get("clearance_floor_violation", 0), 0)
        self.assertEqual(result["clearance_floor_tolerance_m"], .001)

    def test_unversioned_capture_is_explicitly_unassessed(self):
        result = self.read_report({"Stage": "after_lock", "Pose": {"PlacerL": {
            "Applied": True, "Refinement": True, "ClearanceBefore": .2, "ClearanceAfter": .09}}}, False,
            policy=None)
        self.assertEqual(result["groups"]["on"]["flags"].get("clearance_floor_violation", 0), 0)
        coverage = result["clearance_policy_coverage"]
        self.assertEqual(coverage["feet_with_unknown_policy"], 1)
        self.assertEqual(coverage["unassessed_clearance_pairs"], 1)
        self.assertEqual(coverage["unknown_policy_pairs_by_reason"], {"metadata_missing": 1})
        self.assertEqual(coverage["assessed_pair_coverage"], 0)

    def test_future_policy_with_explicit_parameters_is_applied_and_identified(self):
        future_policy = {
            "id": "future-clearance-v3",
            "maximumRequiredClearanceMeters": .12,
            "requiredClearanceBaselineFraction": .8,
        }
        result = self.read_report({"Stage": "after_lock", "Pose": {"PlacerL": {
            "Applied": True, "Refinement": True, "ClearanceBefore": .2, "ClearanceAfter": .119}}}, False,
            policy=future_policy)
        self.assertEqual(result["groups"]["on"]["flags"]["clearance_floor_violation"], 1)
        self.assertEqual(
            result["clearance_policy_coverage"]["by_policy"]["future-clearance-v3"]["origin"],
            "explicit_parameters",
        )


if __name__ == "__main__":
    unittest.main()
