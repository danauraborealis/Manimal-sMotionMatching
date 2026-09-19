import json
import math
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RESOURCE = ROOT / "src" / "MotionMatching" / "Data" / "fidget2.weapon.json"


class FidgetConversionTests(unittest.TestCase):
    def test_runtime_weapon_resource_schema_and_sampling(self):
        resource = json.loads(RESOURCE.read_text(encoding="utf-8"))

        self.assertEqual(1, resource["schemaVersion"])
        self.assertEqual("fidget2", resource["action"])
        self.assertEqual(60, resource["fps"])
        self.assertEqual("unity-camera", resource["coordinateSystem"])
        self.assertRegex(resource["sourceSha256"], r"^[0-9a-f]{64}$")
        self.assertEqual([0, 44], resource["sourceFrameRange"])
        self.assertEqual("Source Blender units", resource["units"])
        self.assertEqual(0.0254, resource["metersPerSourceUnit"])

        samples = resource["samples"]
        self.assertEqual(45, len(samples))
        self.assertAlmostEqual((len(samples) - 1) / resource["fps"], resource["durationSeconds"])

        for sample in samples:
            self.assertEqual(3, len(sample["position"]))
            self.assertEqual(4, len(sample["rotation"]))
            self.assertTrue(all(math.isfinite(value) for value in sample["position"]))
            self.assertTrue(all(math.isfinite(value) for value in sample["rotation"]))
            self.assertAlmostEqual(1.0, math.sqrt(sum(value * value for value in sample["rotation"])), places=6)

        self.assertEqual([0.0, 0.0, 0.0], samples[0]["position"])
        self.assertEqual([0.0, 0.0, 0.0, 1.0], samples[0]["rotation"])
        self.assertEqual([0.0, 0.0, 0.0], samples[-1]["position"])
        self.assertEqual([0.0, 0.0, 0.0, 1.0], samples[-1]["rotation"])

        adjacent_dots = []
        for previous, current in zip(samples, samples[1:]):
            adjacent_dots.append(sum(a * b for a, b in zip(previous["rotation"], current["rotation"])))
        self.assertGreaterEqual(min(adjacent_dots), 0.0)

        validation = resource["validation"]
        self.assertTrue(validation["neutralAndEndIdentityVerified"])
        self.assertTrue(validation["quaternionContinuityVerified"])
        self.assertFalse(validation["cameraMotionIncluded"])


if __name__ == "__main__":
    unittest.main()
