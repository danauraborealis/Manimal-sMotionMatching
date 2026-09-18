using System;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // The placer rebases an airborne foot from its current position on clip changes.
    // Support feet must still match; a distant instantaneous landing is never admitted.
    internal static class ReactionEntry
    {
        internal static bool Foot(Vector3? from, Vector3? to, bool? planted, bool? nextPlanted,
            out float blendSeconds, out string reason)
        {
            blendSeconds = .12f;
            reason = "missing_foot_or_support";
            if (!from.HasValue || !to.HasValue || !planted.HasValue || !nextPlanted.HasValue) return false;
            Vector3 a = from.Value, b = to.Value;
            double dx = (double)a.x - b.x, dy = (double)a.y - b.y, dz = (double)a.z - b.z;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (double.IsNaN(distance) || double.IsInfinity(distance)) { reason = "invalid_foot"; return false; }
            if (planted.Value && !nextPlanted.Value) { reason = "planted_foot_would_lift"; return false; }
            bool support = planted.Value || nextPlanted.Value;
            // The placer drops anchors beyond 0.60m horizontal correction; keep entry inside that budget.
            if (!support && dx * dx + dz * dz > (double).55f * .55f)
            { reason = "swing_horizontal_rebase_too_far"; return false; }
            double limit = support ? (double).20f : (double).85f;
            if (distance > limit) { reason = support ? "support_displacement" : "swing_rebase_too_far"; return false; }
            if (!support) blendSeconds = (float)Math.Max(.12, Math.Min(.30, distance / 3.0));
            reason = null;
            return true;
        }
    }
}
