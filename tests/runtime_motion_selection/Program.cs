using System;
using System.Collections.Generic;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            PhaseWrapUsesTheShortestCyclicDistance();
            MissingFeaturesAreSkippedPerFoot();
            StopFallbackHonorsRemainingSteps();
            FuturePathUsesExporterCoordinateConvention();
            FuturePathIsDistanceMatchedAndBounded();
            Console.WriteLine("runtime_motion_selection: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void PhaseWrapUsesTheShortestCyclicDistance()
    {
        Near(MotionSelection.CircularPhaseDistance(.99f, .01f), .02f, "phase wrap");
        float[] left = { .99f, .1f };
        float[] right = { .25f, .1f };
        Near(MotionSelection.ProgressionCost(left, right, 0, .01f, null), .0004f, "wrapped progression cost");
    }

    private static void MissingFeaturesAreSkippedPerFoot()
    {
        var leftBase = new[] { new Vector3(1f, 0f, 0f) };
        var rightBase = new[] { new Vector3(4f, 0f, 0f) };
        Near(MotionSelection.FootbaseCost(leftBase, rightBase, 0, null, new Vector3(5f, 0f, 0f)), 1f, "right-only footbase cost");
        Near(MotionSelection.ProgressionCost(new[] { .8f }, null, 0, null, null), 0f, "missing progression callback");
        Near(MotionSelection.ProgressionCost(new[] { float.NaN }, null, 0, .2f, null), 0f, "invalid authored progression");
    }

    private static void StopFallbackHonorsRemainingSteps()
    {
        int[] remaining = { 2, 1, 0, 0 };
        if (!MotionSelection.IsStopEntryEligible(remaining, 1, 3) || MotionSelection.IsStopEntryEligible(remaining, 2, 3))
            throw new InvalidOperationException("stop eligibility did not filter terminal entries");
        Equal(MotionSelection.FindStopEntryFallback(remaining, 3, 3), 1, "filtered stop fallback");
        Equal(MotionSelection.FindStopEntryFallback(new[] { 0, 0 }, 1, 1), -1, "no moving stop fallback");
        Equal(MotionSelection.FindStopEntryFallback(null, 3, 8), 3, "legacy stop fallback");
    }

    private static void FuturePathUsesExporterCoordinateConvention()
    {
        var forward = new List<Vector3> { new Vector3(0f, 0f, 2f) };
        float straight = MotionSelection.FuturePathCost(forward, Vector3.zero, 0f,
            ConstantVelocity(0f, 2f), new[] { 0f, 0f, 0f }, 30f, 0, true, 2f);
        if (straight > .0005f)
            throw new InvalidOperationException("local forward velocity must follow +z at yaw zero");

        var yawed = new List<Vector3> { new Vector3(2f, 0f, 0f) };
        float rotated = MotionSelection.FuturePathCost(yawed, Vector3.zero, 90f,
            ConstantVelocity(0f, 2f), new[] { 0f, 0f, 0f }, 30f, 0, true, 2f);
        if (rotated > .0005f)
            throw new InvalidOperationException("local forward velocity must follow +x at +90 yaw");

        float wrong = MotionSelection.FuturePathCost(forward, Vector3.zero, 0f,
            ConstantVelocity(2f, 0f), new[] { 0f, 0f, 0f }, 30f, 0, true, 2f);
        if (!(wrong > straight + .05f))
            throw new InvalidOperationException("lateral trajectory must cost more than forward trajectory");
    }

    private static void FuturePathIsDistanceMatchedAndBounded()
    {
        var path = new List<Vector3> { new Vector3(0f, 0f, 2f) };
        float matching = MotionSelection.FuturePathCost(path, Vector3.zero, 0f,
            ConstantVelocity(0f, 2f), null, 30f, 0, true, 2f);
        float slow = MotionSelection.FuturePathCost(path, Vector3.zero, 0f,
            ConstantVelocity(0f, 1f), null, 30f, 0, true, 2f);
        if (Math.Abs(slow - matching) > .01f)
            throw new InvalidOperationException("same-direction clips must be compared at equal traveled distances");

        float bounded = MotionSelection.FuturePathCost(new List<Vector3> { new Vector3(100f, 0f, 100f) }, Vector3.zero, 0f,
            ConstantVelocity(0f, 2f), null, 30f, 0, true, 2f);
        if (float.IsNaN(bounded) || float.IsInfinity(bounded) || bounded > 4.00001f)
            throw new InvalidOperationException("future path cost must remain bounded");
    }

    private static Vector2[] ConstantVelocity(float x, float z)
    {
        var velocity = new Vector2[30];
        for (int i = 0; i < velocity.Length; i++) velocity[i] = new Vector2(x, z);
        return velocity;
    }

    private static void Near(float actual, float expected, string label)
    {
        if (Math.Abs(actual - expected) > .0005f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }

    private static void Equal(int actual, int expected, string label)
    {
        if (actual != expected)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }
}
