using System;
using Manimal.MotionMatching;

internal static class Program
{
    private static int Main()
    {
        try
        {
            WrappedCycleUsesUnwrappedClipFrame();
            SameStrideBoundaryDoesNotAdvanceTemporalClock();
            SpatialProgressionDoesNotChangeSwingGate();
            StationaryCycleHasNoSwingWindow();
            Console.WriteLine("runtime_stride_timing: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void WrappedCycleUsesUnwrappedClipFrame()
    {
        var cycle = Cycle(8, 12, .25f, .75f);
        Near(cycle.CycleTime(9f, 10, true), .25f, "wrapped cycle before boundary");
        Near(cycle.CycleTime(0f, 10, true), .5f, "wrapped cycle after boundary");
        Near(cycle.CycleTime(.5f, 10, true), .625f, "wrapped cycle interpolated frame");
    }

    private static void SameStrideBoundaryDoesNotAdvanceTemporalClock()
    {
        var cycle = Cycle(8, 12, .25f, .75f);
        // UpdateFoot passes f + blend, with blend forced to zero when the next sample crosses a stride boundary.
        Near(cycle.CycleTime(9f + 0f, 10, true), .25f, "same-stride boundary frame");
        Near(cycle.CycleTime(9f + .5f, 10, true), .375f, "same-stride interpolated frame");
    }

    private static void SpatialProgressionDoesNotChangeSwingGate()
    {
        var cycle = Cycle(8, 12, .25f, .75f);
        float spatialProgression = -.35f; // a valid spatial projection can be behind the stride start
        if (spatialProgression >= cycle.LiftCycle && spatialProgression < cycle.StrikeCycle)
            throw new InvalidOperationException("fixture must disagree with the temporal clock");
        if (!cycle.InAuthoredSwing(cycle.CycleTime(0f, 10, true)))
            throw new InvalidOperationException("temporal swing gate should ignore spatial progression");
    }

    private static void StationaryCycleHasNoSwingWindow()
    {
        var cycle = Cycle(0, 10, 1f, 1f);
        if (cycle.InAuthoredSwing(cycle.CycleTime(5f, 10, false)))
            throw new InvalidOperationException("stationary cycle must not enter an empty swing window");
        if (cycle.InAuthoredSwing(cycle.CycleTime(10f, 10, false)))
            throw new InvalidOperationException("strike edge must be exclusive");
    }

    private static StrideCycle Cycle(int start, int end, float lift, float strike)
    {
        return new StrideCycle
        {
            StartFrame = start,
            EndFrame = end,
            LiftCycle = lift,
            StrikeCycle = strike
        };
    }

    private static void Near(float actual, float expected, string label)
    {
        if (Math.Abs(actual - expected) > 0.00001f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }
}
