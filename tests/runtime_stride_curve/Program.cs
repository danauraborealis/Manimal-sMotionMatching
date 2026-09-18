using System;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            EndpointsAreZero();
            MidpointUsesTheRequestedDelta();
            BellFallsAwayAndRejectsExtrapolation();
            InvalidMiddleAndNonFiniteInputsAreZero();
            CorrectionIsCapped();
            Console.WriteLine("runtime_stride_curve: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void EndpointsAreZero()
    {
        var start = new Vector3(0f, 0f, 0f);
        var end = new Vector3(10f, 0f, 0f);
        Near(StrideCurve.Correction(start, end, new Vector3(), .5f, new Vector3(5f, 1f, 0f), 0f, 10f), new Vector3(), "start endpoint");
        Near(StrideCurve.Correction(start, end, new Vector3(), .5f, new Vector3(5f, 1f, 0f), 1f, 10f), new Vector3(), "end endpoint");
    }

    private static void MidpointUsesTheRequestedDelta()
    {
        var actual = StrideCurve.Correction(new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f),
            new Vector3(.25f, 0f, 0f), .5f, new Vector3(5f, 1f, 0f), .5f, 10f);
        Near(actual, new Vector3(-.25f, 1f, 0f), "midpoint delta");
    }

    private static void BellFallsAwayAndRejectsExtrapolation()
    {
        var start = new Vector3();
        var end = new Vector3(10f, 0f, 0f);
        var source = new Vector3();
        var target = new Vector3(5f, 2f, 0f);
        Near(StrideCurve.Correction(start, end, source, .5f, target, .25f, 10f), new Vector3(0f, 1f, 0f), "left bell");
        Near(StrideCurve.Correction(start, end, source, .5f, target, .75f, 10f), new Vector3(0f, 1f, 0f), "right bell");
        Near(StrideCurve.Correction(start, end, source, .5f, target, -.01f, 10f), new Vector3(), "left extrapolation");
        Near(StrideCurve.Correction(start, end, source, .5f, target, 1.01f, 10f), new Vector3(), "right extrapolation");
    }

    private static void InvalidMiddleAndNonFiniteInputsAreZero()
    {
        var start = new Vector3();
        var end = new Vector3(1f, 0f, 0f);
        var target = new Vector3(.5f, 1f, 0f);
        Near(StrideCurve.Correction(start, end, new Vector3(), .00001f, target, .5f, 10f), new Vector3(), "near-start middle");
        Near(StrideCurve.Correction(start, end, new Vector3(), .99999f, target, .5f, 10f), new Vector3(), "near-end middle");
        Near(StrideCurve.Correction(start, end, new Vector3(), .5f, target, .5f, 0f), new Vector3(), "zero cap");
        Near(StrideCurve.Correction(start, end, new Vector3(), float.NaN, target, .5f, 10f), new Vector3(), "non-finite middle");
    }

    private static void CorrectionIsCapped()
    {
        var actual = StrideCurve.Correction(new Vector3(), new Vector3(1f, 0f, 0f), new Vector3(), .5f,
            new Vector3(.5f, 3f, 0f), .5f, 1f);
        Near(actual, new Vector3(0f, 1f, 0f), "magnitude cap");
    }

    private static void Near(Vector3 actual, Vector3 expected, string label)
    {
        if (Math.Abs(actual.x - expected.x) > .00001f || Math.Abs(actual.y - expected.y) > .00001f || Math.Abs(actual.z - expected.z) > .00001f)
            throw new InvalidOperationException(label + ": expected (" + expected.x + ", " + expected.y + ", " + expected.z + "), got (" + actual.x + ", " + actual.y + ", " + actual.z + ")");
    }
}
