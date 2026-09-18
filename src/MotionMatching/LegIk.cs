using UnityEngine;

namespace Manimal.MotionMatching
{
    // analytic two-bone ik (theorangeduck form), shared by the foot lock and the placer: fix the knee angle
    // for the new reach, then swing the chain onto the target. lengths are preserved, the foot keeps its rotation
    internal static class LegIk
    {
        // Result returned by the opt-in target-rotation overload. Existing Solve/SolveToward callers keep their
        // current behavior; this helper is for a second pass after the caller has recomputed a sole-preserving
        // ankle position and constrained the authored foot rotation.
        internal struct TargetRotationResult
        {
            public bool Solved;
            public Vector3 Target;
            public Quaternion RequestedFootRotation;
            public Quaternion AppliedFootRotation;
        }

        public static void Solve(Transform thigh, Transform calf, Transform foot, Vector3 target, Vector3 bendFallback)
        {
            Vector3 a = thigh.position, b = calf.position, c = foot.position;
            float lab = (b - a).magnitude, lcb = (c - b).magnitude;
            if (lab < 1e-4f || lcb < 1e-4f)
                return;
            float lat = Mathf.Clamp((target - a).magnitude, 0.01f, lab + lcb - 0.001f);

            Vector3 ac = (c - a).normalized, ab = (b - a).normalized, ba = (a - b).normalized, bc = (c - b).normalized, at = (target - a).normalized;
            float acAb0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac, ab), -1f, 1f));
            float baBc0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ba, bc), -1f, 1f));
            float acAt0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac, at), -1f, 1f));
            float acAb1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
            float baBc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

            Vector3 bend = Vector3.Cross(c - a, b - a);
            Vector3 axis = bend.sqrMagnitude > 1e-8f ? bend.normalized : bendFallback;
            Vector3 swing = Vector3.Cross(c - a, target - a);

            Quaternion footRotation = foot.rotation;
            thigh.rotation = Quaternion.AngleAxis((acAb1 - acAb0) * Mathf.Rad2Deg, axis) * thigh.rotation;
            calf.rotation = Quaternion.AngleAxis((baBc1 - baBc0) * Mathf.Rad2Deg, axis) * calf.rotation;
            if (swing.sqrMagnitude > 1e-10f)
                thigh.rotation = Quaternion.AngleAxis(acAt0 * Mathf.Rad2Deg, swing.normalized) * thigh.rotation;
            foot.rotation = footRotation;
        }

        // pole form: straighten the leg, swing it onto the target, then bend the knee toward `pole` (the foot's
        // forward). the current pose's bend plane flips sign on a nearly straight or twisted leg, and with it the
        // knee went backwards (user); a pole makes the knee direction explicit
        public static void SolveToward(Transform thigh, Transform calf, Transform foot, Vector3 target, Vector3 pole, Vector3 bendFallback)
        {
            Vector3 a = thigh.position, b = calf.position, c = foot.position;
            float lab = (b - a).magnitude, lcb = (c - b).magnitude;
            if (lab < 1e-4f || lcb < 1e-4f)
                return;
            float lat = Mathf.Clamp((target - a).magnitude, 0.01f, lab + lcb - 0.001f);
            Quaternion footRotation = foot.rotation;

            // 1. straighten about the current bend
            Vector3 bend = Vector3.Cross(c - a, b - a);
            if (bend.sqrMagnitude > 1e-8f)
            {
                Vector3 ac0 = (c - a).normalized, ab = (b - a).normalized, ba = (a - b).normalized, bc = (c - b).normalized;
                float acAb0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac0, ab), -1f, 1f));
                float baBc0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ba, bc), -1f, 1f));
                Vector3 axis0 = bend.normalized;
                thigh.rotation = Quaternion.AngleAxis(-acAb0 * Mathf.Rad2Deg, axis0) * thigh.rotation;
                calf.rotation = Quaternion.AngleAxis((Mathf.PI - baBc0) * Mathf.Rad2Deg, axis0) * calf.rotation;
            }

            // 2. swing the straight leg onto the target direction
            Vector3 straight = (foot.position - a).normalized;
            Vector3 at = (target - a).normalized;
            Vector3 swing = Vector3.Cross(straight, at);
            if (swing.sqrMagnitude > 1e-10f)
                thigh.rotation = Quaternion.AngleAxis(Mathf.Acos(Mathf.Clamp(Vector3.Dot(straight, at), -1f, 1f)) * Mathf.Rad2Deg, swing.normalized) * thigh.rotation;

            // 3. bend toward the pole
            Vector3 axis = Vector3.Cross(at, pole);
            axis = axis.sqrMagnitude > 1e-8f ? axis.normalized : bendFallback;
            float acAb1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
            float baBc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));
            thigh.rotation = Quaternion.AngleAxis(acAb1 * Mathf.Rad2Deg, axis) * thigh.rotation;
            calf.rotation = Quaternion.AngleAxis((baBc1 - Mathf.PI) * Mathf.Rad2Deg, axis) * calf.rotation;
            foot.rotation = footRotation;
        }

        // Solve the chain with the existing pole solver, then apply the caller's already-constrained authored foot
        // target. The explicit overload avoids changing the historical solver's preservation of its input rotation.
        public static TargetRotationResult SolveTowardWithTargetRotation(
            Transform thigh,
            Transform calf,
            Transform foot,
            Vector3 target,
            Vector3 pole,
            Vector3 bendFallback,
            Quaternion targetFootRotation)
        {
            TargetRotationResult result = new TargetRotationResult
            {
                Solved = false,
                Target = target,
                RequestedFootRotation = targetFootRotation,
                AppliedFootRotation = foot != null ? foot.rotation : Quaternion.identity
            };

            if (thigh == null || calf == null || foot == null)
                return result;

            Vector3 hip = thigh.position;
            Vector3 knee = calf.position;
            Vector3 ankle = foot.position;
            if ((knee - hip).sqrMagnitude < 1e-8f || (ankle - knee).sqrMagnitude < 1e-8f)
                return result;

            SolveToward(thigh, calf, foot, target, pole, bendFallback);
            foot.rotation = targetFootRotation;
            result.Solved = true;
            result.AppliedFootRotation = foot.rotation;
            return result;
        }
    }
}
