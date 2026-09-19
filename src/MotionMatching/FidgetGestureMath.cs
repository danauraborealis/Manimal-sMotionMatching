using UnityEngine;

namespace Manimal.MotionMatching
{
    internal static class FidgetGestureMath
    {
        private const float AxisEpsilonSquared = 1e-12f;

        internal static bool TryPalmFrame(Vector3 forward, Vector3 across, out Quaternion frame)
        {
            frame = Quaternion.identity;
            if (!Finite(forward) || !Finite(across) || !TryNormalize(forward, out var y))
                return false;

            Vector3 x = across - y * Vector3.Dot(across, y);
            if (!TryNormalize(x, out x))
                return false;

            Vector3 z = Vector3.Cross(x, y);
            if (!TryNormalize(z, out z))
                return false;

            frame = Quaternion.LookRotation(z, y);
            return Finite(frame);
        }

        internal static bool TryFingerFrame(
            Vector3 along,
            Vector3 across,
            Vector3 palmNormal,
            bool thumb,
            out Quaternion frame)
        {
            if (!thumb)
                return TryPalmFrame(along, across, out frame);

            frame = Quaternion.identity;
            if (!Finite(along) || !Finite(palmNormal) || !TryNormalize(along, out var y))
                return false;

            Vector3 z = palmNormal - y * Vector3.Dot(palmNormal, y);
            if (!TryNormalize(z, out z))
                return false;

            Vector3 x = Vector3.Cross(y, z);
            if (!TryNormalize(x, out x))
                return false;

            frame = Quaternion.LookRotation(z, y);
            return Finite(frame);
        }

        internal static Quaternion ToLocalDelta(Quaternion canonicalToLocal, Quaternion canonicalDelta)
            => canonicalToLocal * canonicalDelta * Quaternion.Inverse(canonicalToLocal);

        private static bool TryNormalize(Vector3 value, out Vector3 normalized)
        {
            normalized = default;
            float lengthSquared = Vector3.Dot(value, value);
            if (!Finite(lengthSquared) || lengthSquared <= AxisEpsilonSquared)
                return false;

            float inverseLength = 1f / Mathf.Sqrt(lengthSquared);
            normalized = value * inverseLength;
            return Finite(normalized);
        }

        private static bool Finite(Vector3 value)
            => Finite(value.x) && Finite(value.y) && Finite(value.z);

        private static bool Finite(Quaternion value)
            => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);

        private static bool Finite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
