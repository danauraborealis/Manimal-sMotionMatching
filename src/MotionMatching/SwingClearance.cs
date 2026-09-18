using System;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // A bounded, centerline-only plan for moving a swing foot around the planted leg.
    // This helper deliberately does not claim to solve mesh collision: it only measures
    // the four opposite-leg segment pairs through LegClearance.Minimum.
    internal struct SwingClearanceResult
    {
        public bool Valid;
        public bool Resolved;
        public bool EndpointsBlocked;
        public float OffsetDistance;
        public Vector3 MidpointOffset;
        public float ClearanceBefore;
        public float ClearanceAfter;
        public float RequiredClearance;

        // Returns the correction to add to the authored swing-foot path at a normalized
        // progression. The correction is exactly zero at both preserved endpoints.
        public Vector3 OffsetAt(float progression)
        {
            if (!Valid || !Finite(progression) || progression <= 0f || progression >= 1f)
                return new Vector3();

            float bell = progression <= .5f
                ? SmoothStep(progression * 2f)
                : SmoothStep((1f - progression) * 2f);
            return new Vector3(MidpointOffset.x * bell, MidpointOffset.y * bell, MidpointOffset.z * bell);
        }

        private static float SmoothStep(float value)
        {
            value = value < 0f ? 0f : value > 1f ? 1f : value;
            return value * value * (3f - 2f * value);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    internal static class SwingClearance
    {
        private const float MaxOffset = .20f;
        private const float LengthEpsilon = .0001f;
        private const float DistanceEpsilon = .000001f;
        private const int PathSampleCount = 9;
        private const int OffsetSampleCount = 40;
        private const int RefinementCount = 10;
        private const float ComparisonEpsilon = .000001f;

        /// <summary>
        /// Chooses the smallest outward midpoint offset that gives the swing path the
        /// requested centerline clearance from the supporting leg. If no bounded offset
        /// reaches the request, the candidate with the best sampled improvement is used.
        /// The start and end points are never displaced; inspect EndpointsBlocked and
        /// Resolved when a preserved endpoint already lies below the requested floor.
        /// </summary>
        public static SwingClearanceResult Choose(
            Vector3 start,
            Vector3 end,
            Vector3 mid,
            Vector3 swingHip,
            Vector3 swingKneePole,
            float upperLength,
            float lowerLength,
            Vector3 supportingHip,
            Vector3 supportingKnee,
            Vector3 supportingAnkle,
            Vector3 outwardDirection,
            float requiredClearance)
        {
            SwingClearanceResult result = new SwingClearanceResult();
            if (!Finite(start) || !Finite(end) || !Finite(mid)
                || !Finite(swingHip) || !Finite(swingKneePole)
                || !Finite(supportingHip) || !Finite(supportingKnee) || !Finite(supportingAnkle)
                || !Finite(upperLength) || !Finite(lowerLength)
                || upperLength <= LengthEpsilon || lowerLength <= LengthEpsilon
                || !Finite(requiredClearance) || requiredClearance < 0f
                || !TryNormalize(outwardDirection, out Vector3 outward))
                return result;

            float before = EvaluatePath(start, end, mid, swingHip, swingKneePole,
                upperLength, lowerLength, supportingHip, supportingKnee, supportingAnkle, outward, 0f);
            if (!Finite(before))
                return result;

            float startClearance = EvaluateSample(start, end, mid, 0f, swingHip, swingKneePole,
                upperLength, lowerLength, supportingHip, supportingKnee, supportingAnkle, outward, 0f);
            float endClearance = EvaluateSample(start, end, mid, 1f, swingHip, swingKneePole,
                upperLength, lowerLength, supportingHip, supportingKnee, supportingAnkle, outward, 0f);
            if (!Finite(startClearance) || !Finite(endClearance))
                return result;

            bool endpointsBlocked = startClearance + ComparisonEpsilon < requiredClearance
                || endClearance + ComparisonEpsilon < requiredClearance;
            if (before + ComparisonEpsilon >= requiredClearance)
            {
                // A path that already satisfies the requested floor must remain
                // untouched, even when a larger offset could improve its score.
                result.Valid = true;
                result.Resolved = true;
                result.EndpointsBlocked = endpointsBlocked;
                result.OffsetDistance = 0f;
                result.MidpointOffset = new Vector3();
                result.ClearanceBefore = before;
                result.ClearanceAfter = before;
                result.RequiredClearance = requiredClearance;
                return result;
            }

            float chosenOffset = 0f;
            float chosenClearance = before;
            bool foundMeetingOffset = false;
            float meetingLow = 0f;
            float meetingHigh = 0f;

            // Fixed offset sampling keeps the search allocation-free and gives us a
            // bracket for the first threshold crossing even when the clearance curve
            // is not perfectly monotonic.
            float previousOffset = 0f;
            for (int index = 1; index <= OffsetSampleCount; index++)
            {
                float candidateOffset = MaxOffset * index / OffsetSampleCount;
                float candidateClearance = EvaluatePath(start, end, mid, swingHip, swingKneePole,
                    upperLength, lowerLength, supportingHip, supportingKnee, supportingAnkle, outward, candidateOffset);
                if (!Finite(candidateClearance))
                    return result;

                if (candidateClearance > chosenClearance + ComparisonEpsilon)
                {
                    chosenOffset = candidateOffset;
                    chosenClearance = candidateClearance;
                }

                if (!foundMeetingOffset && candidateClearance + ComparisonEpsilon >= requiredClearance)
                {
                    foundMeetingOffset = true;
                    meetingLow = previousOffset;
                    meetingHigh = candidateOffset;
                }

                previousOffset = candidateOffset;
            }

            if (foundMeetingOffset && meetingHigh > meetingLow)
            {
                // Refine only the first sampled crossing. This preserves the smallest
                // threshold-satisfying offset while the fixed scan above handles the
                // no-solution/best-improvement case.
                float low = meetingLow;
                float high = meetingHigh;
                for (int iteration = 0; iteration < RefinementCount; iteration++)
                {
                    float candidate = (low + high) * .5f;
                    float clearance = EvaluatePath(start, end, mid, swingHip, swingKneePole,
                        upperLength, lowerLength, supportingHip, supportingKnee, supportingAnkle, outward, candidate);
                    if (clearance + ComparisonEpsilon >= requiredClearance)
                        high = candidate;
                    else
                        low = candidate;
                }
                chosenOffset = high;
                chosenClearance = EvaluatePath(start, end, mid, swingHip, swingKneePole,
                    upperLength, lowerLength, supportingHip, supportingKnee, supportingAnkle, outward, chosenOffset);
            }

            result.Valid = true;
            result.Resolved = !endpointsBlocked && chosenClearance + ComparisonEpsilon >= requiredClearance;
            result.EndpointsBlocked = endpointsBlocked;
            result.OffsetDistance = chosenOffset;
            result.MidpointOffset = new Vector3(
                outward.x * chosenOffset,
                outward.y * chosenOffset,
                outward.z * chosenOffset);
            result.ClearanceBefore = before;
            result.ClearanceAfter = chosenClearance;
            result.RequiredClearance = requiredClearance;
            return result;
        }

        private static float EvaluatePath(
            Vector3 start,
            Vector3 end,
            Vector3 mid,
            Vector3 swingHip,
            Vector3 swingKneePole,
            float upperLength,
            float lowerLength,
            Vector3 supportingHip,
            Vector3 supportingKnee,
            Vector3 supportingAnkle,
            Vector3 outward,
            float offset)
        {
            float minimum = float.MaxValue;
            for (int index = 0; index < PathSampleCount; index++)
            {
                float progression = index / (float)(PathSampleCount - 1);
                float clearance = EvaluateSample(start, end, mid, progression, swingHip, swingKneePole,
                    upperLength, lowerLength, supportingHip, supportingKnee, supportingAnkle, outward, offset);
                if (!Finite(clearance))
                    return float.NaN;
                if (clearance < minimum)
                    minimum = clearance;
            }
            return minimum;
        }

        private static float EvaluateSample(
            Vector3 start,
            Vector3 end,
            Vector3 mid,
            float progression,
            Vector3 swingHip,
            Vector3 swingKneePole,
            float upperLength,
            float lowerLength,
            Vector3 supportingHip,
            Vector3 supportingKnee,
            Vector3 supportingAnkle,
            Vector3 outward,
            float offset)
        {
            if (!TryPathPoint(start, end, mid, progression, outward, offset, out Vector3 foot)
                || !TryPredictKnee(swingHip, foot, swingKneePole, upperLength, lowerLength, outward,
                    out Vector3 knee, out Vector3 resolvedAnkle))
                return float.NaN;

            float clearance = LegClearance.Minimum(
                swingHip, knee, resolvedAnkle,
                supportingHip, supportingKnee, supportingAnkle);
            return Finite(clearance) ? clearance : float.NaN;
        }

        private static bool TryPathPoint(
            Vector3 start,
            Vector3 end,
            Vector3 mid,
            float progression,
            Vector3 outward,
            float offset,
            out Vector3 point)
        {
            point = new Vector3();
            double x, y, z;
            if (progression <= 0f)
            {
                x = start.x;
                y = start.y;
                z = start.z;
            }
            else if (progression >= 1f)
            {
                x = end.x;
                y = end.y;
                z = end.z;
            }
            else if (progression <= .5f)
            {
                double t = progression * 2d;
                x = start.x + (mid.x - start.x) * t;
                y = start.y + (mid.y - start.y) * t;
                z = start.z + (mid.z - start.z) * t;
            }
            else
            {
                double t = (progression - .5d) * 2d;
                x = mid.x + (end.x - mid.x) * t;
                y = mid.y + (end.y - mid.y) * t;
                z = mid.z + (end.z - mid.z) * t;
            }

            float bell = progression <= 0f || progression >= 1f ? 0f
                : progression <= .5f ? SmoothStep(progression * 2f) : SmoothStep((1f - progression) * 2f);
            x += outward.x * offset * bell;
            y += outward.y * offset * bell;
            z += outward.z * offset * bell;
            return TryVector(x, y, z, out point);
        }

        // Computes the knee from the two segment lengths, target ankle and pole. The
        // target direction is retained while an unreachable target is clamped to the
        // two-bone annulus, so a long authored stride still produces a finite estimate.
        private static bool TryPredictKnee(
            Vector3 hip,
            Vector3 target,
            Vector3 pole,
            float upperLength,
            float lowerLength,
            Vector3 preferredBend,
            out Vector3 knee,
            out Vector3 resolvedAnkle)
        {
            knee = new Vector3();
            resolvedAnkle = new Vector3();
            double dx = (double)target.x - hip.x;
            double dy = (double)target.y - hip.y;
            double dz = (double)target.z - hip.z;
            double distanceSquared = dx * dx + dy * dy + dz * dz;
            if (!Finite(distanceSquared) || distanceSquared <= DistanceEpsilon * DistanceEpsilon)
                return false;

            double distance = Math.Sqrt(distanceSquared);
            double upper = upperLength;
            double lower = lowerLength;
            double minimumReach = Math.Abs(upper - lower);
            double maximumReach = upper + lower;
            double reach = distance < minimumReach ? minimumReach : distance > maximumReach ? maximumReach : distance;
            if (!(reach > DistanceEpsilon) || !Finite(reach))
                return false;

            double dirX = dx / distance;
            double dirY = dy / distance;
            double dirZ = dz / distance;

            double along = (upper * upper + reach * reach - lower * lower) / (2d * reach);
            double heightSquared = upper * upper - along * along;
            // The clamped reach makes this non-negative in exact arithmetic. Clamp a
            // tiny floating-point residue as well, keeping the estimate deterministic.
            if (heightSquared < 0d)
                heightSquared = 0d;
            double height = Math.Sqrt(heightSquared);

            double poleX = (double)pole.x - hip.x;
            double poleY = (double)pole.y - hip.y;
            double poleZ = (double)pole.z - hip.z;
            double poleAlong = poleX * dirX + poleY * dirY + poleZ * dirZ;
            double bendX = poleX - dirX * poleAlong;
            double bendY = poleY - dirY * poleAlong;
            double bendZ = poleZ - dirZ * poleAlong;
            double bendSquared = bendX * bendX + bendY * bendY + bendZ * bendZ;
            if (!(bendSquared > DistanceEpsilon * DistanceEpsilon) || !Finite(bendSquared))
            {
                // A pole on the target line has no unique bend plane. Prefer the
                // requested outward side, then use a least-aligned world axis so the
                // result remains deterministic for either leg.
                bendX = preferredBend.x;
                bendY = preferredBend.y;
                bendZ = preferredBend.z;
                double preferredAlong = bendX * dirX + bendY * dirY + bendZ * dirZ;
                bendX -= dirX * preferredAlong;
                bendY -= dirY * preferredAlong;
                bendZ -= dirZ * preferredAlong;
                bendSquared = bendX * bendX + bendY * bendY + bendZ * bendZ;
                if (!(bendSquared > DistanceEpsilon * DistanceEpsilon) || !Finite(bendSquared))
                {
                    if (Math.Abs(dirX) <= Math.Abs(dirY) && Math.Abs(dirX) <= Math.Abs(dirZ))
                    {
                        bendX = 1d - dirX * dirX;
                        bendY = -dirX * dirY;
                        bendZ = -dirX * dirZ;
                    }
                    else if (Math.Abs(dirY) <= Math.Abs(dirZ))
                    {
                        bendX = -dirY * dirX;
                        bendY = 1d - dirY * dirY;
                        bendZ = -dirY * dirZ;
                    }
                    else
                    {
                        bendX = -dirZ * dirX;
                        bendY = -dirZ * dirY;
                        bendZ = 1d - dirZ * dirZ;
                    }
                    bendSquared = bendX * bendX + bendY * bendY + bendZ * bendZ;
                }
            }

            if (!(bendSquared > 0d) || !Finite(bendSquared))
                return false;
            double bendLength = Math.Sqrt(bendSquared);
            bendX /= bendLength;
            bendY /= bendLength;
            bendZ /= bendLength;

            double kneeX = (double)hip.x + dirX * along + bendX * height;
            double kneeY = (double)hip.y + dirY * along + bendY * height;
            double kneeZ = (double)hip.z + dirZ * along + bendZ * height;
            double ankleX = (double)hip.x + dirX * reach;
            double ankleY = (double)hip.y + dirY * reach;
            double ankleZ = (double)hip.z + dirZ * reach;
            if (!TryVector(kneeX, kneeY, kneeZ, out knee)
                || !TryVector(ankleX, ankleY, ankleZ, out resolvedAnkle))
            {
                knee = new Vector3();
                resolvedAnkle = new Vector3();
                return false;
            }
            return true;
        }

        private static bool TryNormalize(Vector3 value, out Vector3 normalized)
        {
            normalized = new Vector3();
            double squared = (double)value.x * value.x + (double)value.y * value.y + (double)value.z * value.z;
            if (!(squared > DistanceEpsilon * DistanceEpsilon) || !Finite(squared))
                return false;
            double length = Math.Sqrt(squared);
            return TryVector(value.x / length, value.y / length, value.z / length, out normalized);
        }

        private static bool TryVector(double x, double y, double z, out Vector3 value)
        {
            value = new Vector3();
            if (!Finite(x) || !Finite(y) || !Finite(z)
                || Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue || Math.Abs(z) > float.MaxValue)
                return false;
            float fx = (float)x;
            float fy = (float)y;
            float fz = (float)z;
            if (!Finite(fx) || !Finite(fy) || !Finite(fz))
                return false;
            value = new Vector3(fx, fy, fz);
            return true;
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static float SmoothStep(float value)
        {
            value = value < 0f ? 0f : value > 1f ? 1f : value;
            return value * value * (3f - 2f * value);
        }
    }
}
