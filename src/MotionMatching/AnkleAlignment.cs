using UnityEngine;

namespace Manimal.MotionMatching
{
    // Geometry-only ankle helpers. The defaults are deliberately project-tunable heuristics; they do not claim to be
    // universal anatomical limits. Callers should capture authoredRelativeToCalf before any IK pass and provide the
    // sole axes measured from their own foot geometry.
    internal static class AnkleAlignment
    {
        public const float DefaultContactWeight = 1f;
        public const float DefaultMaxAnkleAngleDegrees = 45f;
        public const float GeometryEpsilon = 1e-5f;

        internal struct AnkleSwingResult
        {
            public bool Valid;
            public Quaternion Swing;
            public Quaternion TargetFootRotation;
            public float AngleDegrees;
            public Vector3 OriginalDirection;
            public Vector3 TargetDirection;

            public Quaternion Rotation => Swing;
            public Quaternion FootRotation => TargetFootRotation;
        }

        internal struct SoleAlignmentResult
        {
            public bool Valid;
            public Quaternion Rotation;
            public Quaternion TargetRotation;
            public Quaternion DeltaRotation;
            public Vector3 SurfaceNormal;
            public Vector3 Heading;
            public float ContactWeight;
            public float NormalCorrectionDegrees;
            public bool UsedFallbackHeading;
        }

        internal struct AnkleLimitResult
        {
            public bool Valid;
            public bool Limited;
            public float LimitDegrees;
            public float RequestedAngleDegrees;
            public float AppliedAngleDegrees;
            public float AngularLimitationDegrees;
            public Quaternion RequestedLocalRotation;
            public Quaternion TargetLocalRotation;
            public Quaternion TargetRotation;

            public Quaternion Rotation => TargetRotation;
        }

        // Return the shortest swing taking the original hip-to-ankle direction onto the retargeted direction. The
        // swing is applied directly to the authored foot rotation. It must be computed before an IK solver's
        // straightening/pole rotations, because those rotations are solver aids rather than authored foot intent.
        public static AnkleSwingResult ComputeHipAnkleSwing(
            Vector3 originalHip,
            Vector3 originalAnkle,
            Vector3 targetHip,
            Vector3 targetAnkle,
            Quaternion originalFootRotation)
        {
            AnkleSwingResult result = new AnkleSwingResult
            {
                Valid = false,
                Swing = Quaternion.identity,
                TargetFootRotation = originalFootRotation,
                AngleDegrees = 0f,
                OriginalDirection = Vector3.zero,
                TargetDirection = Vector3.zero
            };

            Vector3 original = originalAnkle - originalHip;
            Vector3 target = targetAnkle - targetHip;
            Vector3 originalDirection;
            Vector3 targetDirection;
            if (!TryNormalize(original, out originalDirection) ||
                !TryNormalize(target, out targetDirection) ||
                !IsFinite(originalFootRotation))
                return result;

            Quaternion swing = ShortestSwing(originalDirection, targetDirection);
            float angle = QuaternionAngleDegrees(swing);
            Quaternion targetFoot = Normalize(swing * originalFootRotation);
            if (!IsFinite(targetFoot))
                return result;

            result.Valid = true;
            result.Swing = swing;
            result.TargetFootRotation = targetFoot;
            result.AngleDegrees = angle;
            result.OriginalDirection = originalDirection;
            result.TargetDirection = targetDirection;
            return result;
        }

        // Align a sole using its measured world heel-to-toe axis and measured authored/current sole normal. The
        // requested heading is interpreted as a world direction: its world-up yaw is retained, then it is projected
        // onto the supplied surface. Contact weight blends both the normal and heading from the authored geometry,
        // so an airborne foot (weight 0) keeps its authored rotation.
        public static SoleAlignmentResult AlignSoleToSurface(
            Quaternion authoredFootRotation,
            Vector3 heelToToe,
            Vector3 authoredSoleNormal,
            Vector3 surfaceNormal,
            Vector3 requestedHeading,
            float contactWeight = DefaultContactWeight)
        {
            SoleAlignmentResult result = new SoleAlignmentResult
            {
                Valid = false,
                Rotation = authoredFootRotation,
                TargetRotation = authoredFootRotation,
                DeltaRotation = Quaternion.identity,
                SurfaceNormal = Vector3.zero,
                Heading = Vector3.zero,
                ContactWeight = 0f,
                NormalCorrectionDegrees = 0f,
                UsedFallbackHeading = false
            };

            if (!IsFinite(authoredFootRotation))
                return result;

            Vector3 currentNormal;
            Vector3 surface;
            Vector3 sourceHeading;
            if (!TryNormalize(authoredSoleNormal, out currentNormal) ||
                !TryNormalize(surfaceNormal, out surface) ||
                !TryNormalize(ProjectOnPlane(heelToToe, currentNormal), out sourceHeading))
                return result;

            bool fallbackHeading = false;
            Vector3 worldHeading;
            if (!TryNormalize(ProjectOnPlane(requestedHeading, Vector3.up), out worldHeading))
            {
                fallbackHeading = true;
                worldHeading = ProjectOnPlane(sourceHeading, Vector3.up);
                if (!TryNormalize(worldHeading, out worldHeading))
                    worldHeading = sourceHeading;
            }

            Vector3 targetHeading;
            if (!TryNormalize(ProjectOnPlane(worldHeading, surface), out targetHeading))
            {
                fallbackHeading = true;
                targetHeading = ProjectOnPlane(sourceHeading, surface);
                if (!TryNormalize(targetHeading, out targetHeading))
                    targetHeading = AnyPerpendicular(surface);
            }

            float weight = IsFinite(contactWeight) ? Mathf.Clamp01(contactWeight) : 0f;
            Vector3 blendedNormal = SlerpDirection(currentNormal, surface, weight);
            Vector3 blendedHeading = SlerpDirection(sourceHeading, targetHeading, weight);
            blendedHeading = ProjectOnPlane(blendedHeading, blendedNormal);
            if (!TryNormalize(blendedHeading, out blendedHeading))
            {
                blendedHeading = ProjectOnPlane(targetHeading, blendedNormal);
                if (!TryNormalize(blendedHeading, out blendedHeading))
                    blendedHeading = AnyPerpendicular(blendedNormal);
            }

            Quaternion sourceBasis = Quaternion.LookRotation(sourceHeading, currentNormal);
            Quaternion targetBasis = Quaternion.LookRotation(blendedHeading, blendedNormal);
            Quaternion delta = Normalize(targetBasis * Quaternion.Inverse(sourceBasis));
            Quaternion targetRotation = Normalize(delta * authoredFootRotation);
            if (!IsFinite(delta) || !IsFinite(targetRotation))
                return result;

            result.Valid = true;
            result.Rotation = targetRotation;
            result.TargetRotation = targetRotation;
            result.DeltaRotation = delta;
            result.SurfaceNormal = surface;
            result.Heading = blendedHeading;
            result.ContactWeight = weight;
            result.NormalCorrectionDegrees = VectorAngleDegrees(currentNormal, blendedNormal);
            result.UsedFallbackHeading = fallbackHeading;
            return result;
        }

        // Convenience overload for callers whose requested heading is already a world yaw in degrees.
        public static SoleAlignmentResult AlignSoleToSurface(
            Quaternion authoredFootRotation,
            Vector3 heelToToe,
            Vector3 authoredSoleNormal,
            Vector3 surfaceNormal,
            float requestedHeadingDegrees,
            float contactWeight = DefaultContactWeight)
        {
            Vector3 requestedHeading = Quaternion.Euler(0f, requestedHeadingDegrees, 0f) * Vector3.forward;
            return AlignSoleToSurface(
                authoredFootRotation,
                heelToToe,
                authoredSoleNormal,
                surfaceNormal,
                requestedHeading,
                contactWeight);
        }

        // Limit the desired world foot rotation around the authored local ankle orientation. The reference is the
        // local rotation captured relative to the calf before IK. Only the relative ankle rotation is clamped; the
        // returned world target lets a caller recompute its sole-preserving ankle position and run IK again.
        public static AnkleLimitResult ConstrainAnkleAngle(
            Quaternion calfWorldRotation,
            Quaternion authoredRelativeToCalf,
            Quaternion requestedFootWorldRotation,
            float maxAngleDegrees = DefaultMaxAnkleAngleDegrees)
        {
            AnkleLimitResult result = new AnkleLimitResult
            {
                Valid = false,
                Limited = false,
                LimitDegrees = 0f,
                RequestedAngleDegrees = 0f,
                AppliedAngleDegrees = 0f,
                AngularLimitationDegrees = 0f,
                RequestedLocalRotation = authoredRelativeToCalf,
                TargetLocalRotation = authoredRelativeToCalf,
                TargetRotation = requestedFootWorldRotation
            };

            if (!IsFinite(calfWorldRotation) ||
                !IsFinite(authoredRelativeToCalf) ||
                !IsFinite(requestedFootWorldRotation))
                return result;

            Quaternion calf = Normalize(calfWorldRotation);
            Quaternion authoredLocal = Normalize(authoredRelativeToCalf);
            Quaternion requestedWorld = Normalize(requestedFootWorldRotation);
            Quaternion requestedLocal = Normalize(Quaternion.Inverse(calf) * requestedWorld);
            Quaternion delta = Normalize(Quaternion.Inverse(authoredLocal) * requestedLocal);
            if (!IsFinite(calf) || !IsFinite(authoredLocal) || !IsFinite(requestedWorld) ||
                !IsFinite(requestedLocal) || !IsFinite(delta))
                return result;

            // Canonicalize the delta so interpolation follows the shortest quaternion arc even on Unity versions
            // where Slerp's sign handling is not guaranteed for a custom quaternion implementation.
            if (delta.w < 0f)
                delta = Negate(delta);

            float limit = IsFinite(maxAngleDegrees) ? Mathf.Clamp(maxAngleDegrees, 0f, 180f) : DefaultMaxAnkleAngleDegrees;
            float requestedAngle = QuaternionAngleDegrees(delta);
            Quaternion limitedDelta = delta;
            bool limited = requestedAngle > limit + 1e-4f;
            if (limited && requestedAngle > GeometryEpsilon)
                limitedDelta = Slerp(Quaternion.identity, delta, limit / requestedAngle);

            Quaternion targetLocal = Normalize(authoredLocal * limitedDelta);
            Quaternion targetWorld = Normalize(calf * targetLocal);
            if (!IsFinite(targetLocal) || !IsFinite(targetWorld))
                return result;

            float appliedAngle = QuaternionAngleDegrees(limitedDelta);
            result.Valid = true;
            result.Limited = limited;
            result.LimitDegrees = limit;
            result.RequestedAngleDegrees = requestedAngle;
            result.AppliedAngleDegrees = appliedAngle;
            result.AngularLimitationDegrees = Mathf.Max(0f, requestedAngle - appliedAngle);
            result.RequestedLocalRotation = requestedLocal;
            result.TargetLocalRotation = targetLocal;
            result.TargetRotation = targetWorld;
            return result;
        }

        private static Quaternion ShortestSwing(Vector3 from, Vector3 to)
        {
            float dot = Mathf.Clamp(Vector3.Dot(from, to), -1f, 1f);
            Vector3 cross = Vector3.Cross(from, to);
            if (cross.sqrMagnitude > GeometryEpsilon * GeometryEpsilon)
                return Quaternion.AngleAxis(Mathf.Acos(dot) * Mathf.Rad2Deg, cross.normalized);
            if (dot >= 0f)
                return Quaternion.identity;

            return Quaternion.AngleAxis(180f, AnyPerpendicular(from));
        }

        private static Vector3 SlerpDirection(Vector3 from, Vector3 to, float t)
        {
            float dot = Mathf.Clamp(Vector3.Dot(from, to), -1f, 1f);
            if (dot > 0.9995f)
                return Normalize(from + (to - from) * t);
            if (dot < -0.9995f)
            {
                Vector3 axis = AnyPerpendicular(from);
                float angle = Mathf.PI * t;
                return Normalize(from * Mathf.Cos(angle) + Vector3.Cross(axis, from) * Mathf.Sin(angle));
            }

            float angleBetween = Mathf.Acos(dot);
            float sine = Mathf.Sin(angleBetween);
            if (Mathf.Abs(sine) < GeometryEpsilon)
                return from;
            float a = Mathf.Sin((1f - t) * angleBetween) / sine;
            float b = Mathf.Sin(t * angleBetween) / sine;
            return Normalize(from * a + to * b);
        }

        private static Quaternion Slerp(Quaternion from, Quaternion to, float t)
        {
            from = Normalize(from);
            to = Normalize(to);
            float dot = from.x * to.x + from.y * to.y + from.z * to.z + from.w * to.w;
            if (dot < 0f)
            {
                to = Negate(to);
                dot = -dot;
            }

            dot = Mathf.Clamp(dot, -1f, 1f);
            if (dot > 0.9995f)
                return Normalize(new Quaternion(
                    from.x + (to.x - from.x) * t,
                    from.y + (to.y - from.y) * t,
                    from.z + (to.z - from.z) * t,
                    from.w + (to.w - from.w) * t));

            float angle = Mathf.Acos(dot);
            float sine = Mathf.Sin(angle);
            float a = Mathf.Sin((1f - t) * angle) / sine;
            float b = Mathf.Sin(t * angle) / sine;
            return Normalize(new Quaternion(
                from.x * a + to.x * b,
                from.y * a + to.y * b,
                from.z * a + to.z * b,
                from.w * a + to.w * b));
        }

        private static Quaternion Normalize(Quaternion value)
        {
            float magnitude = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (!IsFinite(magnitude) || magnitude < GeometryEpsilon)
                return Quaternion.identity;
            return new Quaternion(value.x / magnitude, value.y / magnitude, value.z / magnitude, value.w / magnitude);
        }

        private static Vector3 Normalize(Vector3 value)
        {
            float magnitude = value.magnitude;
            if (!IsFinite(magnitude) || magnitude < GeometryEpsilon)
                return Vector3.zero;
            return value / magnitude;
        }

        private static bool TryNormalize(Vector3 value, out Vector3 normalized)
        {
            normalized = Normalize(value);
            return normalized.sqrMagnitude > GeometryEpsilon * GeometryEpsilon && IsFinite(normalized);
        }

        private static float QuaternionAngleDegrees(Quaternion value)
        {
            Quaternion q = Normalize(value);
            float w = Mathf.Clamp(Mathf.Abs(q.w), -1f, 1f);
            return 2f * Mathf.Acos(w) * Mathf.Rad2Deg;
        }

        private static float VectorAngleDegrees(Vector3 from, Vector3 to)
        {
            Vector3 a;
            Vector3 b;
            if (!TryNormalize(from, out a) || !TryNormalize(to, out b))
                return 0f;
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f)) * Mathf.Rad2Deg;
        }

        private static Vector3 ProjectOnPlane(Vector3 value, Vector3 normal)
        {
            return value - normal * Vector3.Dot(value, normal);
        }

        private static Vector3 AnyPerpendicular(Vector3 value)
        {
            Vector3 axis = Vector3.Cross(value, Vector3.right);
            if (axis.sqrMagnitude < GeometryEpsilon * GeometryEpsilon)
                axis = Vector3.Cross(value, Vector3.up);
            if (axis.sqrMagnitude < GeometryEpsilon * GeometryEpsilon)
                axis = Vector3.Cross(value, Vector3.forward);
            return Normalize(axis);
        }

        private static Quaternion Negate(Quaternion value)
        {
            return new Quaternion(-value.x, -value.y, -value.z, -value.w);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            float magnitudeSquared = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w) &&
                IsFinite(magnitudeSquared) && magnitudeSquared >= GeometryEpsilon * GeometryEpsilon;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
