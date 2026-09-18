using UnityEngine;

namespace Manimal.MotionMatching
{
    // Admission policy for switching between cyclic poses. Positions and support
    // contacts are required because missing values cannot establish compatibility.
    // Velocity is a best-effort check and is skipped when either sample is absent
    // or its horizontal components are non-finite.
    internal static class CycleHandoff
    {
        internal const float MaximumFootbaseDisplacement = .20f;
        internal const float MinimumPlanarSpeed = .25f;

        internal static bool Allow(
            Vector3? fromL,
            Vector3? fromR,
            Vector3? toL,
            Vector3? toR,
            bool? plantedL,
            bool? plantedR,
            bool? nextPlantedL,
            bool? nextPlantedR,
            Vector3? velocityL,
            Vector3? velocityR,
            Vector3? nextVelocityL,
            Vector3? nextVelocityR,
            out string reason)
        {
            reason = null;
            if (!FootCompatible(fromL, toL, plantedL, nextPlantedL,
                    velocityL, nextVelocityL, "left", out reason))
                return false;

            if (!FootCompatible(fromR, toR, plantedR, nextPlantedR,
                    velocityR, nextVelocityR, "right", out reason))
                return false;

            return true;
        }

        private static bool FootCompatible(
            Vector3? from,
            Vector3? to,
            bool? planted,
            bool? nextPlanted,
            Vector3? velocity,
            Vector3? nextVelocity,
            string side,
            out string reason)
        {
            reason = null;
            if (!from.HasValue || !to.HasValue)
            {
                reason = side + " footbase position is missing";
                return false;
            }

            Vector3 fromPosition = from.Value;
            Vector3 toPosition = to.Value;
            if (!Finite(fromPosition) || !Finite(toPosition))
            {
                reason = side + " footbase position is non-finite";
                return false;
            }

            if (!planted.HasValue || !nextPlanted.HasValue)
            {
                reason = side + " support contact is missing";
                return false;
            }

            if (planted.Value && !nextPlanted.Value)
            {
                reason = side + " planted foot would become swing";
                return false;
            }

            if (ExceedsMaximumDisplacement(fromPosition, toPosition))
            {
                reason = side + " footbase displacement exceeds 0.20 m";
                return false;
            }

            if (planted.Value || nextPlanted.Value
                || !velocity.HasValue || !nextVelocity.HasValue)
                return true;

            Vector3 outgoingVelocity = velocity.Value;
            Vector3 incomingVelocity = nextVelocity.Value;
            if (!FiniteHorizontal(outgoingVelocity) || !FiniteHorizontal(incomingVelocity))
                return true;

            double outgoingSpeedSquared = (double)outgoingVelocity.x * outgoingVelocity.x
                + (double)outgoingVelocity.z * outgoingVelocity.z;
            double incomingSpeedSquared = (double)incomingVelocity.x * incomingVelocity.x
                + (double)incomingVelocity.z * incomingVelocity.z;
            double minimumSpeedSquared = (double)MinimumPlanarSpeed * MinimumPlanarSpeed;
            if (outgoingSpeedSquared <= minimumSpeedSquared
                || incomingSpeedSquared <= minimumSpeedSquared)
                return true;

            double velocityDot = (double)outgoingVelocity.x * incomingVelocity.x
                + (double)outgoingVelocity.z * incomingVelocity.z;
            if (velocityDot < 0d)
            {
                reason = side + " swing velocity reverses";
                return false;
            }

            return true;
        }

        private static bool ExceedsMaximumDisplacement(Vector3 from, Vector3 to)
        {
            double dx = (double)to.x - from.x;
            double dy = (double)to.y - from.y;
            double dz = (double)to.z - from.z;
            double distanceSquared = dx * dx + dy * dy + dz * dz;
            double maximumDistanceSquared = (double)MaximumFootbaseDisplacement
                * MaximumFootbaseDisplacement;
            return distanceSquared > maximumDistanceSquared;
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool FiniteHorizontal(Vector3 value)
        {
            return Finite(value.x) && Finite(value.z);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
