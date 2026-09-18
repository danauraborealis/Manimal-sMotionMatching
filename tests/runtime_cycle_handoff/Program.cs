using System;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            AcceptsEqualDisplacementBoundary();
            RejectsOutgoingSupportLoss();
            AllowsSwingToPlantAndStationarySwing();
            AllowsCompatibleDoubleSupport();
            RejectsOpposedSwingVelocity();
            RejectsMissingOrInvalidRequiredData();
            Console.WriteLine("runtime_cycle_handoff: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void AcceptsEqualDisplacementBoundary()
    {
        AssertAllowed(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(.20f, 0f, 0f), P(0f, .20f, 0f),
            false, false, false, false,
            null, null, null, null,
            "displacement at the 0.20 m boundary");

        AssertRejected(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(0f, 0f, 0f), P(0f, .2001f, 0f),
            false, false, false, false,
            null, null, null, null,
            "right foot displacement above the limit", "right footbase displacement exceeds 0.20 m");
    }

    private static void RejectsOutgoingSupportLoss()
    {
        AssertRejected(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(.01f, 0f, 0f), P(0f, 0f, 0f),
            true, false, false, false,
            null, null, null, null,
            "left planted foot becoming swing", "left planted foot would become swing");

        // The reverse support transition is a plausible landing and remains eligible.
        AssertAllowed(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(.01f, 0f, 0f), P(0f, 0f, 0f),
            false, false, true, false,
            null, null, null, null,
            "outgoing swing becoming planted");
    }

    private static void AllowsSwingToPlantAndStationarySwing()
    {
        AssertAllowed(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(0f, 0f, 0f), P(0f, 0f, 0f),
            false, false, false, false,
            P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(0f, 0f, 0f), P(0f, 0f, 0f),
            "stationary swing feet");
    }

    private static void AllowsCompatibleDoubleSupport()
    {
        AssertAllowed(P(-.1f, 0f, 0f), P(.1f, 0f, 0f),
            P(-.1f, 0f, .05f), P(.1f, 0f, -.05f),
            true, true, true, true,
            P(2f, 0f, 0f), P(-2f, 0f, 0f),
            P(-2f, 0f, 0f), P(2f, 0f, 0f),
            "compatible double support ignores swing velocity");
    }

    private static void RejectsOpposedSwingVelocity()
    {
        AssertRejected(P(-.1f, 0f, 0f), P(.1f, 0f, 0f),
            P(-.1f, 0f, .05f), P(.1f, 0f, -.05f),
            false, false, false, false,
            P(1f, 2f, 0f), P(0f, 0f, 0f),
            P(-1f, -2f, 0f), P(0f, 0f, 0f),
            "opposed left swing velocity", "left swing velocity reverses");

        // Vertical velocity does not affect the horizontal direction check.
        AssertAllowed(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(0f, 0f, 0f), P(0f, 0f, 0f),
            false, false, false, false,
            P(.25f, 99f, 0f), P(0f, 0f, 0f),
            P(-.25f, -99f, 0f), P(0f, 0f, 0f),
            "sub-threshold planar velocity");

        // Missing or malformed optional velocity simply omits this policy check.
        AssertAllowed(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(0f, 0f, 0f), P(0f, 0f, 0f),
            false, false, false, false,
            P(1f, 0f, 0f), P(0f, 0f, 0f),
            null, null,
            "unavailable velocity");

        AssertAllowed(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(0f, 0f, 0f), P(0f, 0f, 0f),
            false, false, false, false,
            P(float.NaN, 0f, 0f), P(0f, 0f, 0f),
            P(-1f, 0f, 0f), P(0f, 0f, 0f),
            "non-finite optional velocity");
    }

    private static void RejectsMissingOrInvalidRequiredData()
    {
        AssertRejected(null, P(0f, 0f, 0f),
            P(0f, 0f, 0f), P(0f, 0f, 0f),
            false, false, false, false,
            null, null, null, null,
            "missing left position", "left footbase position is missing");

        AssertRejected(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(float.NaN, 0f, 0f), P(0f, 0f, 0f),
            false, false, false, false,
            null, null, null, null,
            "NaN incoming position", "left footbase position is non-finite");

        AssertRejected(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(0f, 0f, 0f), P(0f, 0f, 0f),
            null, false, false, false,
            null, null, null, null,
            "missing outgoing contact", "left support contact is missing");

        AssertRejected(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(0f, 0f, 0f), P(0f, 0f, 0f),
            false, false, null, false,
            null, null, null, null,
            "missing incoming contact", "left support contact is missing");

        AssertRejected(P(0f, 0f, 0f), P(0f, 0f, 0f),
            P(0f, 0f, 0f), null,
            false, false, false, false,
            null, null, null, null,
            "missing right position", "right footbase position is missing");
    }

    private static void AssertAllowed(
        Vector3? fromL, Vector3? fromR, Vector3? toL, Vector3? toR,
        bool? plantedL, bool? plantedR, bool? nextPlantedL, bool? nextPlantedR,
        Vector3? velocityL, Vector3? velocityR,
        Vector3? nextVelocityL, Vector3? nextVelocityR,
        string label)
    {
        string reason;
        if (!CycleHandoff.Allow(fromL, fromR, toL, toR,
                plantedL, plantedR, nextPlantedL, nextPlantedR,
                velocityL, velocityR, nextVelocityL, nextVelocityR, out reason))
            throw new InvalidOperationException(label + " should be allowed, got: " + reason);
        if (reason != null)
            throw new InvalidOperationException(label + " should clear the reason on success");
    }

    private static void AssertRejected(
        Vector3? fromL, Vector3? fromR, Vector3? toL, Vector3? toR,
        bool? plantedL, bool? plantedR, bool? nextPlantedL, bool? nextPlantedR,
        Vector3? velocityL, Vector3? velocityR,
        Vector3? nextVelocityL, Vector3? nextVelocityR,
        string label, string expectedReason)
    {
        string reason;
        if (CycleHandoff.Allow(fromL, fromR, toL, toR,
                plantedL, plantedR, nextPlantedL, nextPlantedR,
                velocityL, velocityR, nextVelocityL, nextVelocityR, out reason))
            throw new InvalidOperationException(label + " should be rejected");
        if (!string.Equals(reason, expectedReason, StringComparison.Ordinal))
            throw new InvalidOperationException(label + " returned unexpected reason: " + reason);
    }

    private static Vector3 P(float x, float y, float z)
    {
        return new Vector3(x, y, z);
    }
}
