import math
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import gait_metrics

FPS = 60.0


def smoothstep(t):
    t = min(max(t, 0.0), 1.0)
    return t * t * (3 - 2 * t)


def synthetic_walk(seconds=4.0, speed=1.2, stride=1.2, width=0.14, lift=0.1, leg=0.9):
    """Two feet alternating plants half a stride apart, the root moving at constant speed along +z."""
    frames = int(seconds * FPS)
    period = stride / speed  # one foot cycle
    track = {"fps": FPS, "root": [], "pelvis": [], "pelvisFwd": [], "torsoFwd": [], "footFwdL": [], "footFwdR": []}
    for k in ("hipL", "hipR", "kneeL", "kneeR", "ankleL", "ankleR", "toeL", "toeR"):
        track[k] = []
    for f in range(frames):
        t = f / FPS
        rz = speed * t
        track["root"].append((0.0, rz, 0.0))
        track["pelvis"].append((0.0, 0.93 + 0.02 * math.sin(4 * math.pi * t / period), 0.0))
        track["pelvisFwd"].append((0.0, 0.0, 1.0))
        track["torsoFwd"].append((math.sin(math.radians(5)), 0.0, math.cos(math.radians(5))))
        for side, x, phase in (("L", -width / 2, 0.0), ("R", width / 2, 0.5)):
            u = ((t / period) + phase) % 1.0
            # half the cycle planted, half swinging one stride forward
            k = math.floor((t / period) + phase)
            plant_z = k * stride - stride / 2
            if u < 0.5:
                wz, y = plant_z, 0.0
            else:
                s = (u - 0.5) / 0.5
                wz, y = plant_z + stride * smoothstep(s), lift * math.sin(math.pi * s)
            local_z = wz - rz
            ankle = (x, y + 0.06, local_z)
            hip = (x, 0.93, 0.0)
            # knee: on the hip-ankle line, pushed forward so the leg bends
            mid = ((hip[0] + ankle[0]) / 2, (hip[1] + ankle[1]) / 2, (hip[2] + ankle[2]) / 2)
            dist = math.dist(hip, ankle)
            bend = max(0.0, math.sqrt(max(leg * leg / 4 - dist * dist / 4, 0.0)))
            knee = (mid[0], mid[1], mid[2] + bend)
            track["hip" + side].append(hip)
            track["knee" + side].append(knee)
            track["ankle" + side].append(ankle)
            track["toe" + side].append((x, y + 0.02, local_z + 0.15))
            track["footFwd" + side].append((0.0, 0.0, 1.0))
    return track


class GaitMetricsTests(unittest.TestCase):
    def test_synthetic_walk_measures_its_own_parameters(self):
        r = gait_metrics.analyse(synthetic_walk(), "walk")
        self.assertAlmostEqual(1.2, r["speed_mps"], delta=0.05)
        self.assertAlmostEqual(1.2, r["stride_m"]["p50"], delta=0.05)
        self.assertAlmostEqual(0.14, r["stance_width_m"]["p50"], delta=0.01)
        self.assertLess(r["contact_slide_mm"]["p50"], 10)
        self.assertAlmostEqual(0.1, r["swing_height_m"]["p50"], delta=0.02)
        # two feet, one plant per cycle each: cadence = 2 * speed / stride
        self.assertAlmostEqual(2.0, r["cadence_steps_per_s"], delta=0.3)
        self.assertLess(abs(r["knee_splay_deg"]["p50"]), 1.0)
        self.assertAlmostEqual(0.04, r["pelvis_bob_m"], delta=0.005)
        self.assertAlmostEqual(5.0, r["torso_vs_pelvis_yaw_deg"]["p50"], delta=0.1)
        self.assertLess(r["knee_flexion_deg"]["min"], 179.0)


if __name__ == "__main__":
    unittest.main()
