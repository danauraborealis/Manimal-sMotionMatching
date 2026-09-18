using System;
using System.Collections.Generic;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // Small, allocation-free feature calculations used by PosePlayback's selectors.  The
    // database stores root velocity in character space (x right, z forward); path points
    // arrive in world space and are projected into the character frame by the caller's yaw.
    internal static class MotionSelection
    {
        // Keep the trajectory query short enough that an old corner cannot dominate a local
        // choice.  Distances are targetSpeed * these horizons, not samples at fixed seconds.
        private const float FutureHorizon0 = 0.25f;
        private const float FutureHorizon1 = 0.5f;
        private const float FutureHorizon2 = 0.75f;
        private const float MaxFutureIntegrationSeconds = 1.5f;
        private const float MaxFuturePathCost = 4f;
        private const float MinFeatureSpeed = 0.01f;

        public static float CircularPhaseDistance(float a, float b)
        {
            if (float.IsNaN(a) || float.IsInfinity(a) || float.IsNaN(b) || float.IsInfinity(b))
                return 0f;
            float delta = a - b;
            delta -= (float)Math.Floor(delta + 0.5f);
            return Math.Abs(delta);
        }

        // Missing observed or authored data contributes no term.  In particular, a missing
        // callback is not treated as progression zero, which would bias every candidate near
        // the beginning of its cycle.
        public static float ProgressionCost(float[] left, float[] right, int frame, float? observedLeft, float? observedRight)
        {
            float cost = 0f;
            if (observedLeft.HasValue && HasValue(left, frame))
                cost += CircularPhaseDistance(left[frame], observedLeft.Value) * CircularPhaseDistance(left[frame], observedLeft.Value);
            if (observedRight.HasValue && HasValue(right, frame))
                cost += CircularPhaseDistance(right[frame], observedRight.Value) * CircularPhaseDistance(right[frame], observedRight.Value);
            return cost;
        }

        public static float FootbaseCost(Vector3[] left, Vector3[] right, int frame, Vector3? observedLeft, Vector3? observedRight)
        {
            float cost = 0f;
            if (observedLeft.HasValue && HasValue(left, frame))
            {
                Vector3 d = left[frame] - observedLeft.Value;
                cost += d.x * d.x + d.y * d.y + d.z * d.z;
            }
            if (observedRight.HasValue && HasValue(right, frame))
            {
                Vector3 d = right[frame] - observedRight.Value;
                cost += d.x * d.x + d.y * d.y + d.z * d.z;
            }
            return cost;
        }

        private static bool HasValue(float[] values, int frame) => values != null && frame >= 0 && frame < values.Length
            && !float.IsNaN(values[frame]) && !float.IsInfinity(values[frame]);

        private static bool HasValue(Vector3[] values, int frame) => values != null && frame >= 0 && frame < values.Length
            && IsFinite(values[frame]);

        private static bool IsFinite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        // StepsRemaining is exported for one-shot clips. A moving stop entry must leave at
        // least one real foot strike; terminal/virtual-only frames are not valid entry points.
        // A null or malformed array is treated as unavailable for compatibility with older
        // databases, while a complete array is enforced strictly.
        public static bool IsStopEntryEligible(int[] stepsRemaining, int frame, int motionEnd)
        {
            if (frame < 0 || frame > motionEnd)
                return false;
            if (stepsRemaining == null || frame >= stepsRemaining.Length)
                return true;
            return stepsRemaining[frame] > 0;
        }

        // Used when the distance/pose window has no candidate. The fallback is subject to the
        // same moving-step filter, so a zero-cost terminal frame cannot sneak through this path.
        public static int FindStopEntryFallback(int[] stepsRemaining, int motionEnd, int desiredFrame)
        {
            if (motionEnd < 0)
                return -1;
            int desired = Math.Max(0, Math.Min(desiredFrame, motionEnd));
            if (stepsRemaining == null)
                return desired;

            for (int radius = 0; radius <= motionEnd; radius++)
            {
                int before = desired - radius;
                if (before >= 0 && IsStopEntryEligible(stepsRemaining, before, motionEnd))
                    return before;
                int after = desired + radius;
                if (after <= motionEnd && after != before && IsStopEntryEligible(stepsRemaining, after, motionEnd))
                    return after;
            }
            return -1;
        }

        // Compare a candidate's future root trajectory against the traversable path. Both
        // current and candidate clips call this exact method, so continuation cost has the
        // same horizons, distance samples, caps, and coordinate conversion.
        public static float FuturePathCost(
            IList<Vector3> corners,
            Vector3 origin,
            float bodyYaw,
            Vector2[] rootVelocity,
            float[] yawProgress,
            float fps,
            int startFrame,
            bool loop,
            float targetSpeed)
        {
            if (corners == null || corners.Count == 0 || rootVelocity == null || rootVelocity.Length == 0
                || fps <= 0f || targetSpeed <= MinFeatureSpeed || float.IsNaN(targetSpeed) || float.IsInfinity(targetSpeed))
                return 0f;

            float total = 0f;
            float targetDistance = 0f;
            float travelled = 0f;
            float elapsed = 0f;
            Vector3 predicted = Vector3.zero;
            int sample = 0;
            float previousYaw = SampleYaw(yawProgress, startFrame, loop);
            const float epsilon = 1e-5f;
            float maxDistance = targetSpeed * FutureHorizon2;
            float integrationStep = 1f / Math.Max(fps, 1f);

            while (sample < 3)
            {
                float horizon = sample == 0 ? FutureHorizon0 : sample == 1 ? FutureHorizon1 : FutureHorizon2;
                targetDistance = targetSpeed * horizon;
                if (targetDistance > maxDistance)
                    targetDistance = maxDistance;

                // March one authored frame at a time until the clip has covered the same
                // distance represented by this path sample. A low-speed clip is bounded by
                // MaxFutureIntegrationSeconds and simply contributes the finite position it
                // reached within that bound.
                while (travelled + epsilon < targetDistance && elapsed < MaxFutureIntegrationSeconds)
                {
                    float frame = startFrame + elapsed * fps;
                    Vector2 velocity = SampleVelocity(rootVelocity, frame, loop);
                    float speed = (float)Math.Sqrt(velocity.x * velocity.x + velocity.y * velocity.y);
                    float dt = integrationStep;
                    float distanceStep = speed * dt;
                    if (distanceStep > targetDistance - travelled && speed > MinFeatureSpeed)
                        dt = (targetDistance - travelled) / speed;

                    float yaw = SampleYaw(yawProgress, frame, loop);
                    float yawDelta = yaw - previousYaw;
                    Vector2 rotated = RotateToInitialFrame(velocity, yawDelta);
                    predicted.x += rotated.x * dt;
                    predicted.z += rotated.y * dt;
                    travelled += speed * dt;
                    elapsed += dt;
                    if (dt <= epsilon)
                        break;
                }

                Vector3 pathPoint;
                bool hasPath = PathSample(corners, origin, bodyYaw, targetDistance, out pathPoint);
                if (hasPath)
                {
                    Vector3 difference = predicted - pathPoint;
                    float position = difference.x * difference.x + difference.z * difference.z;
                    // Keep one bad/short path bounded so it cannot outweigh direction, speed,
                    // or support features in a selector.
                    if (position > MaxFuturePathCost)
                        position = MaxFuturePathCost;
                    total += position;
                }
                sample++;
            }
            return Math.Min(total / 3f, MaxFuturePathCost);
        }

        private static Vector2 RotateToInitialFrame(Vector2 local, float yawDegrees)
        {
            float radians = yawDegrees * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            // Unity's +Y yaw maps local forward toward +X. This is the inverse of the
            // exporter root-velocity conversion and preserves its x-right/z-forward basis.
            return new Vector2(local.x * cos + local.y * sin, -local.x * sin + local.y * cos);
        }

        private static Vector2 SampleVelocity(Vector2[] values, float frame, bool loop)
        {
            int count = values.Length;
            if (count == 0)
                return Vector2.zero;
            float index = frame;
            if (loop)
            {
                index %= count;
                if (index < 0f) index += count;
            }
            else
                index = Math.Max(0f, Math.Min(index, count - 1));
            int a = (int)Math.Floor(index);
            int b = loop ? (a + 1) % count : Math.Min(a + 1, count - 1);
            float u = index - a;
            return new Vector2(values[a].x + (values[b].x - values[a].x) * u, values[a].y + (values[b].y - values[a].y) * u);
        }

        private static float SampleYaw(float[] values, float frame, bool loop)
        {
            if (values == null || values.Length == 0)
                return 0f;
            int count = values.Length;
            float index = frame;
            float cycleChange = values[count - 1] - values[0];
            float cycles = 0f;
            if (loop)
            {
                cycles = (float)Math.Floor(index / count);
                index -= cycles * count;
                if (index < 0f) { index += count; cycles -= 1f; }
            }
            else
                index = Math.Max(0f, Math.Min(index, count - 1));
            int a = (int)Math.Floor(index);
            int b = loop ? (a + 1) % count : Math.Min(a + 1, count - 1);
            float u = index - a;
            float from = values[a];
            float to = values[b] + (loop && b == 0 ? cycleChange : 0f);
            return from + (to - from) * u + cycles * cycleChange;
        }

        private static bool PathSample(IList<Vector3> corners, Vector3 origin, float bodyYaw, float distance, out Vector3 point)
        {
            point = Vector3.zero;
            Vector3 previous = origin;
            float left = Math.Max(0f, distance);
            float radians = bodyYaw * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            bool hasSegment = false;
            for (int i = 0; i < corners.Count; i++)
            {
                Vector3 next = corners[i];
                float dx = next.x - previous.x;
                float dz = next.z - previous.z;
                float length = (float)Math.Sqrt(dx * dx + dz * dz);
                if (length <= 1e-5f)
                {
                    previous = next;
                    continue;
                }
                hasSegment = true;
                if (left <= length)
                {
                    float u = left / length;
                    Vector3 world = new Vector3(previous.x + dx * u, 0f, previous.z + dz * u);
                    float localX = (world.x - origin.x) * cos - (world.z - origin.z) * sin;
                    float localZ = (world.x - origin.x) * sin + (world.z - origin.z) * cos;
                    point = new Vector3(localX, 0f, localZ);
                    return true;
                }
                left -= length;
                previous = next;
            }

            if (!hasSegment)
                return false;
            Vector3 last = corners[corners.Count - 1];
            float lastX = (last.x - origin.x) * cos - (last.z - origin.z) * sin;
            float lastZ = (last.x - origin.x) * sin + (last.z - origin.z) * cos;
            point = new Vector3(lastX, 0f, lastZ);
            return true;
        }
    }
}
