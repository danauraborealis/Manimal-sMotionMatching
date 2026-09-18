using UnityEngine;

namespace Manimal.MotionMatching
{
    // Applies the authored middle-of-stride shape when the two stance
    // endpoints are retargeted. This helper is intentionally stateless: the
    // caller owns the stationary-cycle temporal clock and chooses the
    // progression supplied here.
    internal static class StrideCurve
    {
        private const float EndpointEpsilon = 0.0001f;
        private const float LengthEpsilon = 0.000001f;

        /// <summary>
        /// Returns an additive world-space offset for one point on a stride.
        /// The correction is zero at both endpoints and reaches its full
        /// value at <paramref name="middleProgression"/>.
        /// </summary>
        public static Vector3 Correction(
            Vector3 start,
            Vector3 end,
            Vector3 sourceMidOffsetWorld,
            float middleProgression,
            Vector3 desiredMidpoint,
            float progression,
            float maxCorrection)
        {
            if (!Finite(start) || !Finite(end) || !Finite(sourceMidOffsetWorld) || !Finite(desiredMidpoint))
                return new Vector3();
            if (!Finite(middleProgression) || !Finite(progression) || !Finite(maxCorrection) || maxCorrection <= 0f)
                return new Vector3();
            if (middleProgression <= EndpointEpsilon || middleProgression >= 1f - EndpointEpsilon)
                return new Vector3();
            if (progression < 0f || progression > 1f)
                return new Vector3();

            Vector3 lineAtMiddle = start + (end - start) * middleProgression;
            Vector3 delta = desiredMidpoint - (lineAtMiddle + sourceMidOffsetWorld);
            float length = Length(delta);
            if (length <= LengthEpsilon)
                return new Vector3();
            if (length > maxCorrection)
                delta = delta * (maxCorrection / length);

            float bell;
            if (progression <= middleProgression)
                bell = SmoothStep(progression / middleProgression);
            else
                bell = SmoothStep((1f - progression) / (1f - middleProgression));
            return delta * bell;
        }

        private static float SmoothStep(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private static float Length(Vector3 value)
        {
            return Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }
    }
}
