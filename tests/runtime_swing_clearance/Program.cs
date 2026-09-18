using System;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private const float Required = .06f;

    private static int Main()
    {
        try
        {
            SafePathRemainsUnmodified();
            CrossingPathImprovesForBothSides();
            EndpointDisplacementIsAlwaysZero();
            BlockedEndpointsRemainUnresolved();
            UnreachableTargetUsesResolvedAnkle();
            MalformedInputsAreRejectedWithFiniteBounds();
            Console.WriteLine("runtime_swing_clearance: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void SafePathRemainsUnmodified()
    {
        SwingClearanceResult plan = SwingClearance.Choose(
            P(.75f, 0f, 0f), P(.75f, 0f, 0f), P(.75f, 0f, 0f),
            P(.75f, 1f, 0f), P(.75f, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f),
            P(1f, 0f, 0f), Required);

        if (!plan.Valid || !plan.Resolved || plan.EndpointsBlocked)
            throw new InvalidOperationException("a separated path must be resolved without endpoint blocking");
        Near(plan.OffsetDistance, 0f, "safe path offset");
        Near(plan.ClearanceAfter, plan.ClearanceBefore, "safe path clearance");
        Near(plan.MidpointOffset, new Vector3(), "safe path midpoint offset");

        // This path is already above the lower floor, although moving its interior
        // outward would improve the sampled clearance. The lower-floor plan must still
        // remain unmodified.
        SwingClearanceResult safeButImprovable = SwingClearance.Choose(
            P(.75f, 0f, 0f), P(.75f, 0f, 0f), P(.70f, 0f, 0f),
            P(.75f, 1f, 0f), P(.75f, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f),
            P(1f, 0f, 0f), .69f);
        if (!safeButImprovable.Valid || !safeButImprovable.Resolved || safeButImprovable.OffsetDistance != 0f
            || !(safeButImprovable.ClearanceBefore > .69f))
            throw new InvalidOperationException("an already-safe but improvable path must keep a zero offset");

        SwingClearanceResult forcedImprovement = SwingClearance.Choose(
            P(.75f, 0f, 0f), P(.75f, 0f, 0f), P(.70f, 0f, 0f),
            P(.75f, 1f, 0f), P(.75f, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f),
            P(1f, 0f, 0f), .74f);
        if (!(forcedImprovement.OffsetDistance > .0001f)
            || !(forcedImprovement.ClearanceAfter > forcedImprovement.ClearanceBefore + .0001f))
            throw new InvalidOperationException("the safe fixture must be demonstrably improvable");
    }

    private static void CrossingPathImprovesForBothSides()
    {
        AssertCrossingImproves(false);
        AssertCrossingImproves(true);
    }

    private static void AssertCrossingImproves(bool mirror)
    {
        float side = mirror ? -1f : 1f;
        SwingClearanceResult plan = SwingClearance.Choose(
            P(side * .75f, 0f, 0f), P(side * .75f, 0f, 0f), P(side * -.02f, 0f, 0f),
            P(side * .75f, 1f, 0f), P(side * .75f, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f),
            P(side, 0f, 0f), Required);

        if (!plan.Valid || plan.EndpointsBlocked)
            throw new InvalidOperationException("crossing path must have usable preserved endpoints");
        if (!(plan.OffsetDistance > .0001f && plan.OffsetDistance <= .20f + .000001f))
            throw new InvalidOperationException("crossing path must choose a bounded outward offset");
        if (!(plan.ClearanceAfter > plan.ClearanceBefore + .0001f))
            throw new InvalidOperationException("outward offset must improve crossing clearance");
        if (!plan.Resolved || plan.ClearanceAfter + .000001f < Required)
            throw new InvalidOperationException("crossing path should reach the requested clearance");
    }

    private static void EndpointDisplacementIsAlwaysZero()
    {
        SwingClearanceResult plan = SwingClearance.Choose(
            P(.75f, 0f, 0f), P(.75f, 0f, 0f), P(-.02f, 0f, 0f),
            P(.75f, 1f, 0f), P(.75f, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f),
            P(1f, 0f, 0f), Required);
        if (!plan.Valid || plan.OffsetDistance <= .0001f)
            throw new InvalidOperationException("endpoint test requires a nonzero plan");
        Near(plan.OffsetAt(0f), new Vector3(), "start endpoint offset");
        Near(plan.OffsetAt(1f), new Vector3(), "end endpoint offset");
        Near(plan.OffsetAt(-.1f), new Vector3(), "left extrapolation offset");
        Near(plan.OffsetAt(1.1f), new Vector3(), "right extrapolation offset");
        Near(plan.OffsetAt(.5f), plan.MidpointOffset, "midpoint offset");
        Near(plan.OffsetAt(.25f), plan.MidpointOffset * .5f, "left bell offset");
        Near(plan.OffsetAt(.75f), plan.MidpointOffset * .5f, "right bell offset");
    }

    private static void BlockedEndpointsRemainUnresolved()
    {
        SwingClearanceResult plan = SwingClearance.Choose(
            P(.02f, 0f, 0f), P(.02f, 0f, 0f), P(-.02f, 0f, 0f),
            P(.75f, 1f, 0f), P(.75f, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f),
            P(1f, 0f, 0f), Required);
        if (!plan.Valid || !plan.EndpointsBlocked || plan.Resolved)
            throw new InvalidOperationException("a blocked preserved endpoint must report unresolved");
        Near(plan.OffsetAt(0f), new Vector3(), "blocked start endpoint offset");
        Near(plan.OffsetAt(1f), new Vector3(), "blocked end endpoint offset");
    }

    private static void MalformedInputsAreRejectedWithFiniteBounds()
    {
        SwingClearanceResult nanPoint = SwingClearance.Choose(
            P(float.NaN, 0f, 0f), P(1f, 0f, 0f), P(.5f, 0f, 0f),
            P(.5f, 1f, 0f), P(.5f, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f), P(1f, 0f, 0f), Required);
        AssertInvalidAndFinite(nanPoint, "NaN point");

        SwingClearanceResult zeroLength = SwingClearance.Choose(
            P(1f, 0f, 0f), P(1f, 0f, 0f), P(1f, 0f, 0f),
            P(1f, 1f, 0f), P(1f, 1f, -1f), 0f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f), P(1f, 0f, 0f), Required);
        AssertInvalidAndFinite(zeroLength, "zero upper length");

        SwingClearanceResult zeroOutward = SwingClearance.Choose(
            P(1f, 0f, 0f), P(1f, 0f, 0f), P(1f, 0f, 0f),
            P(1f, 1f, 0f), P(1f, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f), new Vector3(), Required);
        AssertInvalidAndFinite(zeroOutward, "zero outward direction");

        SwingClearanceResult nanRequired = SwingClearance.Choose(
            P(1f, 0f, 0f), P(1f, 0f, 0f), P(1f, 0f, 0f),
            P(1f, 1f, 0f), P(1f, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f), P(1f, 0f, 0f), float.NaN);
        AssertInvalidAndFinite(nanRequired, "NaN required clearance");

        SwingClearanceResult finiteBounds = SwingClearance.Choose(
            P(float.MaxValue, 0f, 0f), P(float.MaxValue, 0f, 0f), P(float.MaxValue, 0f, 0f),
            P(float.MaxValue, 1f, 0f), P(float.MaxValue, 1f, -1f), .6f, .6f,
            P(0f, 1f, 0f), P(0f, .5f, 0f), P(0f, 0f, 0f), P(1f, 0f, 0f), Required);
        if (!Finite(finiteBounds.OffsetDistance) || !Finite(finiteBounds.ClearanceBefore)
            || !Finite(finiteBounds.ClearanceAfter) || finiteBounds.OffsetDistance < 0f
            || finiteBounds.OffsetDistance > .20f + .000001f)
            throw new InvalidOperationException("finite-bound inputs must not produce nonfinite or unbounded output");
    }

    private static void UnreachableTargetUsesResolvedAnkle()
    {
        SwingClearanceResult plan = SwingClearance.Choose(
            P(-3f, 0f, 0f), P(-3f, 0f, 0f), P(-3f, 0f, 0f),
            P(1f, 1f, 0f), P(1f, 1f, -1f), .6f, .6f,
            P(-.3f, 1f, 0f), P(-.3f, .5f, 0f), P(-.3f, 0f, 0f),
            P(-1f, 0f, 0f), Required);
        if (!plan.Valid || !plan.Resolved || plan.EndpointsBlocked || plan.OffsetDistance != 0f
            || !(plan.ClearanceBefore > Required) || !(plan.ClearanceAfter > Required))
            throw new InvalidOperationException("unreachable authored targets must be measured at the clamped ankle");
    }

    private static void AssertInvalidAndFinite(SwingClearanceResult result, string label)
    {
        if (result.Valid || result.Resolved || result.EndpointsBlocked || result.OffsetDistance != 0f
            || !Finite(result.OffsetDistance) || !Finite(result.ClearanceBefore)
            || !Finite(result.ClearanceAfter) || !Finite(result.RequiredClearance))
            throw new InvalidOperationException(label + " must be rejected with finite zero output");
    }

    private static Vector3 P(float x, float y, float z) => new Vector3(x, y, z);

    private static bool Finite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void Near(float actual, float expected, string label)
    {
        if (Math.Abs(actual - expected) > .00001f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }

    private static void Near(Vector3 actual, Vector3 expected, string label)
    {
        if (Math.Abs(actual.x - expected.x) > .00001f || Math.Abs(actual.y - expected.y) > .00001f
            || Math.Abs(actual.z - expected.z) > .00001f)
            throw new InvalidOperationException(label + ": expected (" + expected.x + ", " + expected.y + ", " + expected.z
                + "), got (" + actual.x + ", " + actual.y + ", " + actual.z + ")");
    }
}
