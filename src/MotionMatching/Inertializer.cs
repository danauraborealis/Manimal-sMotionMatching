using System;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // inertialization instead of cross-fading (the technique JLPM22/MotionMatching calls "inertialize blending",
    // from Bollo's Gears of War transitions talk). cross-fading two poses that sit a metre apart reads as a snap
    // however long the fade; here the pose DIFFERENCE at the switch is captured once and decayed to zero, so the
    // legs carry on from where they already were and settle into the new pose
    internal sealed class Inertializer
    {
        private Quaternion[] _delta;
        private Vector3[] _rotationLogDelta;
        private Vector3[] _rotationLogVelocity;
        private Vector3 _pelvisDelta;
        private Vector3 _pelvisVelocity;
        private float _duration;
        private float _elapsed;
        private bool _velocityAware;

        public bool Active => _delta != null && _elapsed < _duration;

        // Preserve the original hand-off curve for existing callers. It captures only pose difference, so it
        // cannot preserve measured source velocity and uses the original cubic ease-out.
        public void Begin(Quaternion[] from, Quaternion[] to, Vector3 fromPelvis, Vector3 toPelvis, float duration)
        {
            if (from == null || to == null || from.Length != to.Length || !IsFinite(duration) || duration <= 0f)
                return;
            if (_delta == null || _delta.Length != from.Length)
                _delta = new Quaternion[from.Length];
            for (int i = 0; i < from.Length; i++)
                _delta[i] = from[i] * Quaternion.Inverse(to[i]);
            _pelvisDelta = fromPelvis - toPelvis;
            _duration = duration;
            _elapsed = 0f;
            _velocityAware = false;
        }

        // The previous pose samples provide outgoing and incoming angular/linear velocities at the switch. The
        // new curve stores rotation offsets in the parent-frame quaternion log and decays each offset with cubic
        // Hermite endpoints: captured offset/velocity at the start, zero offset/velocity at the end.
        public void Begin(
            Quaternion[] from,
            Quaternion[] to,
            Vector3 fromPelvis,
            Vector3 toPelvis,
            float duration,
            Quaternion[] previousFrom,
            Quaternion[] previousTo,
            Vector3 previousFromPelvis,
            Vector3 previousToPelvis,
            float sampleDt,
            float sourceAge = 0f)
        {
            if (from == null || to == null || from.Length != to.Length || !IsFinite(duration) || duration <= 0f)
                return;

            if (_delta == null || _delta.Length != from.Length)
                _delta = new Quaternion[from.Length];
            if (_rotationLogDelta == null || _rotationLogDelta.Length != from.Length)
                _rotationLogDelta = new Vector3[from.Length];
            if (_rotationLogVelocity == null || _rotationLogVelocity.Length != from.Length)
                _rotationLogVelocity = new Vector3[from.Length];

            bool validDt = IsFinite(sampleDt) && sampleDt > 0f;
            bool validSourceAge = IsFinite(sourceAge) && sourceAge >= 0f && sourceAge <= 0.05f;
            float acceptedSourceAge = validSourceAge ? sourceAge : 0f;
            bool validPreviousFrom = validDt && previousFrom != null && previousFrom.Length == from.Length;
            bool validPreviousTo = validDt && previousTo != null && previousTo.Length == from.Length;

            for (int i = 0; i < from.Length; i++)
            {
                Vector3 fromAngularVelocity = Vector3.zero;
                Vector3 toAngularVelocity = Vector3.zero;
                bool hasFromAngularVelocity = validPreviousFrom &&
                    TryAngularVelocity(from[i], previousFrom[i], sampleDt, out fromAngularVelocity);
                bool hasToAngularVelocity = validPreviousTo &&
                    TryAngularVelocity(to[i], previousTo[i], sampleDt, out toAngularVelocity);

                Quaternion extrapolatedFrom = from[i];
                Quaternion normalizedFrom;
                if (hasFromAngularVelocity && acceptedSourceAge > 0f && TryNormalize(from[i], out normalizedFrom))
                {
                    Quaternion extrapolation = RotationExp(fromAngularVelocity * acceptedSourceAge);
                    Quaternion candidate;
                    if (TryNormalize(extrapolation * normalizedFrom, out candidate))
                        extrapolatedFrom = candidate;
                }

                Quaternion delta;
                if (!TryRelativeRotation(extrapolatedFrom, to[i], out delta))
                {
                    _delta[i] = Quaternion.identity;
                    _rotationLogDelta[i] = Vector3.zero;
                    _rotationLogVelocity[i] = Vector3.zero;
                    continue;
                }

                _delta[i] = delta;
                Vector3 logDelta = RotationLog(delta);
                _rotationLogDelta[i] = logDelta;
                _rotationLogVelocity[i] = Vector3.zero;

                // For output = exp(offset) * incoming, the parent-frame angular velocity is
                // omegaOffset + offset * omegaIncoming. Convert that angular velocity to log-space velocity
                // with the inverse left Jacobian so the composed output starts at the outgoing velocity.
                Vector3 offsetAngularVelocity =
                    (hasFromAngularVelocity ? fromAngularVelocity : Vector3.zero) -
                    delta * (hasToAngularVelocity ? toAngularVelocity : Vector3.zero);
                Vector3 logVelocity = ApplyInverseLeftJacobian(logDelta, offsetAngularVelocity);
                if (IsFinite(logVelocity))
                    _rotationLogVelocity[i] = logVelocity;
            }

            Vector3 fromPelvisVelocity = Vector3.zero;
            Vector3 toPelvisVelocity = Vector3.zero;
            bool hasFromPelvisVelocity = validDt && IsFinite(fromPelvis) && IsFinite(previousFromPelvis);
            bool hasToPelvisVelocity = validDt && IsFinite(toPelvis) && IsFinite(previousToPelvis);
            if (hasFromPelvisVelocity)
            {
                fromPelvisVelocity = (fromPelvis - previousFromPelvis) / sampleDt;
                hasFromPelvisVelocity = IsFinite(fromPelvisVelocity);
            }
            if (hasToPelvisVelocity)
            {
                toPelvisVelocity = (toPelvis - previousToPelvis) / sampleDt;
                hasToPelvisVelocity = IsFinite(toPelvisVelocity);
            }

            Vector3 extrapolatedFromPelvis = fromPelvis;
            if (hasFromPelvisVelocity && acceptedSourceAge > 0f && IsFinite(fromPelvis))
            {
                Vector3 candidate = fromPelvis + fromPelvisVelocity * acceptedSourceAge;
                if (IsFinite(candidate))
                    extrapolatedFromPelvis = candidate;
            }

            _pelvisDelta = IsFinite(extrapolatedFromPelvis) && IsFinite(toPelvis)
                ? extrapolatedFromPelvis - toPelvis
                : Vector3.zero;
            _pelvisVelocity =
                (hasFromPelvisVelocity ? fromPelvisVelocity : Vector3.zero) -
                (hasToPelvisVelocity ? toPelvisVelocity : Vector3.zero);
            if (!IsFinite(_pelvisVelocity))
                _pelvisVelocity = Vector3.zero;

            _duration = duration;
            _elapsed = 0f;
            _velocityAware = true;
        }

        public void Cancel()
        {
            _elapsed = _duration;
        }

        public void Advance(float dt)
        {
            if (_delta != null)
                _elapsed += dt;
        }

        // ease-out: the offset leaves fastest at the start for pose-only callers
        public float Weight
        {
            get
            {
                if (!Active)
                    return 0f;
                float t = Mathf.Clamp01(_elapsed / _duration);
                float remaining = 1f - t;
                return remaining * remaining * remaining;
            }
        }

        public Quaternion Rotation(int index)
        {
            if (!Active || _delta == null || index < 0 || index >= _delta.Length)
                return Quaternion.identity;

            if (_velocityAware && _rotationLogDelta != null && index < _rotationLogDelta.Length)
            {
                float t = Mathf.Clamp01(_elapsed / _duration);
                Vector3 logOffset = Hermite(_rotationLogDelta[index], _rotationLogVelocity[index], t, _duration);
                return RotationExp(logOffset);
            }

            float weight = Weight;
            return weight <= 0f ? Quaternion.identity : Quaternion.Slerp(Quaternion.identity, _delta[index], weight);
        }

        public Vector3 PelvisOffset
        {
            get
            {
                if (!Active)
                    return Vector3.zero;
                if (_velocityAware)
                {
                    float t = Mathf.Clamp01(_elapsed / _duration);
                    return Hermite(_pelvisDelta, _pelvisVelocity, t, _duration);
                }
                return _pelvisDelta * Weight;
            }
        }

        private static Vector3 Hermite(Vector3 start, Vector3 startVelocity, float t, float duration)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            return start * h00 + startVelocity * (duration * h10);
        }

        private static bool TryAngularVelocity(Quaternion current, Quaternion previous, float dt, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (!IsFinite(dt) || dt <= 0f)
                return false;

            Quaternion currentNormalized, previousNormalized;
            if (!TryNormalize(current, out currentNormalized) || !TryNormalize(previous, out previousNormalized))
                return false;

            Quaternion change = currentNormalized * Quaternion.Inverse(previousNormalized);
            Quaternion normalizedChange;
            if (!TryNormalize(change, out normalizedChange))
                return false;

            Vector3 angularVelocity = RotationLog(Shortest(normalizedChange)) / dt;
            if (!IsFinite(angularVelocity))
                return false;
            velocity = angularVelocity;
            return true;
        }

        private static bool TryRelativeRotation(Quaternion from, Quaternion to, out Quaternion delta)
        {
            delta = Quaternion.identity;
            Quaternion normalizedFrom, normalizedTo;
            if (!TryNormalize(from, out normalizedFrom) || !TryNormalize(to, out normalizedTo))
                return false;

            Quaternion relative = normalizedFrom * Quaternion.Inverse(normalizedTo);
            Quaternion normalizedRelative;
            if (!TryNormalize(relative, out normalizedRelative))
                return false;
            delta = Shortest(normalizedRelative);
            return true;
        }

        private static Quaternion Shortest(Quaternion rotation)
        {
            return rotation.w < 0f ? Negate(rotation) : rotation;
        }

        private static Quaternion Negate(Quaternion rotation)
        {
            return new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
        }

        private static bool TryNormalize(Quaternion rotation, out Quaternion normalized)
        {
            normalized = Quaternion.identity;
            if (!IsFinite(rotation))
                return false;

            double magnitudeSquared = (double)rotation.x * rotation.x + (double)rotation.y * rotation.y +
                (double)rotation.z * rotation.z + (double)rotation.w * rotation.w;
            if (double.IsNaN(magnitudeSquared) || double.IsInfinity(magnitudeSquared) || magnitudeSquared <= 1e-20)
                return false;

            float inverseMagnitude = (float)(1.0 / Math.Sqrt(magnitudeSquared));
            normalized = new Quaternion(
                rotation.x * inverseMagnitude,
                rotation.y * inverseMagnitude,
                rotation.z * inverseMagnitude,
                rotation.w * inverseMagnitude);
            return IsFinite(normalized);
        }

        // Shortest quaternion logarithm as a parent-frame axis-angle vector in radians.
        private static Vector3 RotationLog(Quaternion rotation)
        {
            Quaternion shortest = Shortest(rotation);
            double vectorMagnitude = Math.Sqrt((double)shortest.x * shortest.x + (double)shortest.y * shortest.y +
                (double)shortest.z * shortest.z);
            if (double.IsNaN(vectorMagnitude) || double.IsInfinity(vectorMagnitude) || vectorMagnitude < 1e-8)
                return new Vector3(2f * shortest.x, 2f * shortest.y, 2f * shortest.z);

            double w = Math.Max(0.0, Math.Min(1.0, shortest.w));
            double angle = 2.0 * Math.Atan2(vectorMagnitude, w);
            float scale = (float)(angle / vectorMagnitude);
            return new Vector3(shortest.x * scale, shortest.y * scale, shortest.z * scale);
        }

        // Quaternion exponential, inverse to RotationLog for shortest parent-frame rotation vectors.
        private static Quaternion RotationExp(Vector3 rotationVector)
        {
            double angleSquared = (double)rotationVector.x * rotationVector.x +
                (double)rotationVector.y * rotationVector.y + (double)rotationVector.z * rotationVector.z;
            if (double.IsNaN(angleSquared) || double.IsInfinity(angleSquared))
                return Quaternion.identity;

            double angle = Math.Sqrt(angleSquared);
            double halfAngle = 0.5 * angle;
            double scale;
            if (angle < 1e-5)
                scale = 0.5 - angleSquared / 48.0;
            else
                scale = Math.Sin(halfAngle) / angle;

            Quaternion result = new Quaternion(
                rotationVector.x * (float)scale,
                rotationVector.y * (float)scale,
                rotationVector.z * (float)scale,
                (float)Math.Cos(halfAngle));
            Quaternion normalized;
            return TryNormalize(result, out normalized) ? Shortest(normalized) : Quaternion.identity;
        }

        // The inverse SO(3) left Jacobian maps spatial angular velocity to the derivative of its log vector.
        // The half-angle form stays finite at pi; the series avoids cancellation around zero.
        private static Vector3 ApplyInverseLeftJacobian(Vector3 rotationVector, Vector3 angularVelocity)
        {
            double angleSquared = (double)rotationVector.x * rotationVector.x +
                (double)rotationVector.y * rotationVector.y + (double)rotationVector.z * rotationVector.z;
            double coefficient;
            if (angleSquared < 1e-6)
            {
                coefficient = 1.0 / 12.0 + angleSquared / 720.0;
            }
            else
            {
                double angle = Math.Sqrt(angleSquared);
                double halfAngle = 0.5 * angle;
                double sineHalfAngle = Math.Sin(halfAngle);
                if (Math.Abs(sineHalfAngle) < 1e-12)
                    return Vector3.zero;
                coefficient = 1.0 / angleSquared - Math.Cos(halfAngle) / (2.0 * angle * sineHalfAngle);
            }

            Vector3 firstCross = Vector3.Cross(rotationVector, angularVelocity);
            Vector3 secondCross = Vector3.Cross(rotationVector, firstCross);
            Vector3 result = angularVelocity - 0.5f * firstCross + (float)coefficient * secondCross;
            return IsFinite(result) ? result : Vector3.zero;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }
    }
}
