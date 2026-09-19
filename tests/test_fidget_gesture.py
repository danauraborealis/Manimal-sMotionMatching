import json
import math
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RESOURCE = ROOT / "src" / "MotionMatching" / "Data" / "fidget2.gesture.json"


def quaternion(values):
    return math.sqrt(sum(value * value for value in values))


class FidgetGestureResourceTests(unittest.TestCase):
    def test_anatomical_gesture_schema_and_sampling(self):
        resource = json.loads(RESOURCE.read_text(encoding="utf-8"))

        self.assertEqual(1, resource["schemaVersion"])
        self.assertEqual("fidget2", resource["action"])
        self.assertEqual(60, resource["fps"])
        self.assertEqual("anatomical-local", resource["coordinateSystem"])
        self.assertRegex(resource["sourceSha256"], re.compile(r"^[0-9a-f]{64}$"))
        self.assertEqual([0, 44], resource["sourceFrameRange"])
        self.assertEqual(0.0254, resource["metersPerSourceUnit"])

        hand_samples = resource["hand"]["samples"]
        self.assertEqual(45, len(hand_samples))
        for sample in hand_samples:
            self.assertEqual(3, len(sample["position"]))
            self.assertEqual(4, len(sample["rotation"]))
            self.assertTrue(all(math.isfinite(value) for value in sample["position"]))
            self.assertTrue(all(math.isfinite(value) for value in sample["rotation"]))
            self.assertAlmostEqual(1.0, quaternion(sample["rotation"]), places=6)

        self.assertEqual([0.0, 0.0, 0.0], hand_samples[0]["position"])
        self.assertEqual([0.0, 0.0, 0.0, 1.0], hand_samples[0]["rotation"])
        self.assertEqual([0.0, 0.0, 0.0], hand_samples[-1]["position"])
        self.assertEqual([0.0, 0.0, 0.0, 1.0], hand_samples[-1]["rotation"])

        tracks = resource["fingers"]
        self.assertEqual(15, len(tracks))
        self.assertEqual(
            {(digit, segment) for digit in range(5) for segment in range(3)},
            {(track["digit"], track["segment"]) for track in tracks},
        )
        for track in tracks:
            samples = track["samples"]
            self.assertEqual(45, len(samples))
            for sample in samples:
                self.assertEqual(4, len(sample["rotation"]))
                self.assertTrue(all(math.isfinite(value) for value in sample["rotation"]))
                self.assertAlmostEqual(1.0, quaternion(sample["rotation"]), places=6)
            self.assertEqual([0.0, 0.0, 0.0, 1.0], samples[0]["rotation"])
            self.assertEqual([0.0, 0.0, 0.0, 1.0], samples[-1]["rotation"])

        validation = resource["validation"]
        self.assertTrue(validation["neutralAndEndIdentityVerified"])
        self.assertTrue(validation["quaternionContinuityVerified"])
        self.assertTrue(validation["rightHandOmittedAsNonIntentional"])
        self.assertLess(validation["maxHandPositionReconstructionError"], 1e-5)
        self.assertLess(validation["maxHandQuaternionReconstructionError"], 1e-5)
        self.assertLess(validation["maxFingerQuaternionReconstructionError"], 1e-5)

    def test_nonleaf_canonical_y_matches_evaluated_child_direction(self):
        resource = json.loads(RESOURCE.read_text(encoding="utf-8"))
        validation = resource["validation"]
        self.assertTrue(validation["sourceLocalChainDirectionVerified"])
        self.assertGreater(validation["minimumSourceLocalChainDirectionDot"], 0.98)
        self.assertTrue(validation["canonicalYChildDirectionVerified"])
        self.assertGreater(
            validation["minimumNonLeafCanonicalYChildDirectionDot"],
            1.0 - 1e-5,
        )


if __name__ == "__main__":
    unittest.main()
