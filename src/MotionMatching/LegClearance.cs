using System;
using UnityEngine;
namespace Manimal.MotionMatching
{
    // Centerline clearance between the two legs; this does not model mesh collision.
    internal static class LegClearance
    {
        // centerlines, so limb thickness counts: at 6 cm a thigh (~15 cm across) is well inside the other (user:
        // legs really close together); authored clips keep 16-18 cm
        // Keep the capture policy identifier and parameters together so runtime capture metadata and the offline
        // clearance report use the same versioned rule.
        internal const string ClearancePolicyId = "adaptive-baseline-v2";
        internal const float MaximumRequiredClearanceMeters = 0.11f;
        internal const float RequiredClearanceBaselineFraction = 0.75f;
        internal static object CapturePolicy => new
        {
            id = ClearancePolicyId,
            maximumRequiredClearanceMeters = MaximumRequiredClearanceMeters,
            requiredClearanceBaselineFraction = RequiredClearanceBaselineFraction
        };
        internal static float SegmentDistance(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d))
                return float.MaxValue;

            double ux = (double)b.x - a.x, uy = (double)b.y - a.y, uz = (double)b.z - a.z;
            double vx = (double)d.x - c.x, vy = (double)d.y - c.y, vz = (double)d.z - c.z;
            double wx = (double)a.x - c.x, wy = (double)a.y - c.y, wz = (double)a.z - c.z;
            double aa = Dot(ux, uy, uz, ux, uy, uz), cc = Dot(vx, vy, vz, vx, vy, vz);
            if (aa == 0d) return PointSegment(a, c, d);
            if (cc == 0d) return PointSegment(c, a, b);

            double bb = Dot(ux, uy, uz, vx, vy, vz);
            double dd = Dot(ux, uy, uz, wx, wy, wz);
            double ee = Dot(vx, vy, vz, wx, wy, wz);
            double det = aa * cc - bb * bb;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            if (det <= 0d || cx * cx + cy * cy + cz * cz <= aa * cc * 1e-24d)
                return Min(PointSegment(a, c, d), PointSegment(b, c, d), PointSegment(c, a, b), PointSegment(d, a, b));

            double sn = bb * ee - cc * dd, sd = det;
            double tn = aa * ee - bb * dd, td = det;
            if (sn < 0d) { sn = 0d; tn = ee; td = cc; }
            else if (sn > sd) { sn = sd; tn = ee + bb; td = cc; }
            if (tn < 0d)
            {
                tn = 0d;
                if (-dd < 0d) sn = 0d;
                else if (-dd > aa) sn = sd;
                else { sn = -dd; sd = aa; }
            }
            else if (tn > td)
            {
                tn = td;
                if (-dd + bb < 0d) sn = 0d;
                else if (-dd + bb > aa) sn = sd;
                else { sn = -dd + bb; sd = aa; }
            }

            double s = sn == 0d ? 0d : sn / sd;
            double t = tn == 0d ? 0d : tn / td;
            return Distance(wx + s * ux - t * vx, wy + s * uy - t * vy, wz + s * uz - t * vz);
        }
        internal static float Minimum(Vector3 lh, Vector3 lk, Vector3 la, Vector3 rh, Vector3 rk, Vector3 ra)
        {
            if (!Finite(lh) || !Finite(lk) || !Finite(la) || !Finite(rh) || !Finite(rk) || !Finite(ra))
                return float.MaxValue;
            return Min(SegmentDistance(lh, lk, rh, rk), SegmentDistance(lh, lk, rk, ra),
                SegmentDistance(lk, la, rh, rk), SegmentDistance(lk, la, rk, ra));
        }
        internal static float Required(float baseline)
        {
            return !Finite(baseline) || baseline <= 0f ? 0f : Math.Min(MaximumRequiredClearanceMeters, baseline * RequiredClearanceBaselineFraction);
        }

        private static float PointSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            double x = (double)b.x - a.x, y = (double)b.y - a.y, z = (double)b.z - a.z;
            double len = Dot(x, y, z, x, y, z), t = 0d;
            if (len > 0d)
            {
                t = ((double)p.x - a.x) * x + ((double)p.y - a.y) * y + ((double)p.z - a.z) * z;
                t = t / len < 0d ? 0d : t / len > 1d ? 1d : t / len;
            }
            return Distance((double)p.x - (a.x + t * x), (double)p.y - (a.y + t * y), (double)p.z - (a.z + t * z));
        }

        private static double Dot(double ax, double ay, double az, double bx, double by, double bz) => ax * bx + ay * by + az * bz;

        private static float Distance(double x, double y, double z)
        {
            double squared = x * x + y * y + z * z;
            if (double.IsNaN(squared)) return float.MaxValue;
            if (squared <= 0d) return 0f;
            double distance = Math.Sqrt(squared);
            return distance >= float.MaxValue ? float.MaxValue : (float)distance;
        }
        private static float Min(float a, float b, float c, float d)
            => Math.Min(Math.Min(a, b), Math.Min(c, d));

        private static bool Finite(Vector3 p)
            => Finite(p.x) && Finite(p.y) && Finite(p.z);

        private static bool Finite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
