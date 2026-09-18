using System;
using Manimal.MotionMatching;

internal static class Program
{
    private static int Main()
    {
        try
        {
            WrapsPhasesAndOffsets();
            FloatRoundingKeepsWrappedRanges();
            ConvergesPositiveAndNegativeOffsetsMonotonically();
            ReportsTheActualBoundedCadence();
            PreservesChosenPhaseOnEntry();
            RebasesWithoutJumps();
            LocksExactlyWhenAligned();
            FreezesOnInvalidInputs();
            Console.WriteLine("runtime_native_walk_phase: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void WrapsPhasesAndOffsets()
    {
        var phase = new NativeWalkPhase();
        phase.Reset(.98f, .98f, 1);
        Near(.02f, phase.Step(.02f, 1f, .04f, 1, false), .000001f,
            "leg phase wraps through one");
        Near(0f, phase.OffsetCycles, .000001f, "wrapped phase stays aligned");
        InUnitInterval(phase.OffsetCycles, "wrapped signed offset");

        phase.Reset(.5f, 0f, 2);
        Near(-.5f, phase.OffsetCycles, 0f, "positive half-cycle maps to negative half-cycle");

        phase.Reset(1.25f, .25f, 3);
        Near(0f, phase.OffsetCycles, .000001f, "finite phase inputs wrap into one cycle");
    }

    private static void FloatRoundingKeepsWrappedRanges()
    {
        float[] deltas = { .00000001f, .00000002f, .00000003f, .00000004f, .00000005f };
        for (int i = 0; i < deltas.Length; i++)
        {
            var phase = new NativeWalkPhase();
            phase.Reset(.99999994f, .99999994f, 31);
            float wrapped = phase.Step(0f, 1f, deltas[i], 31, false);
            InUnitInterval(wrapped, "near-one float conversion");
            if (i == 3)
                Near(0f, wrapped, 0f, "a value rounded to one wraps back to zero");
        }

        var signed = new NativeWalkPhase();
        signed.Reset(.49999997f, 0f, 32);
        float legPhase = signed.Step(0f, 1f, .00000002f, 32, false);
        InUnitInterval(legPhase, "signed-boundary leg phase");
        Near(-.5f, signed.OffsetCycles, 0f,
            "an offset rounded to positive half-cycle normalizes to negative half-cycle");
    }

    private static void ConvergesPositiveAndNegativeOffsetsMonotonically()
    {
        AssertOffsetConverges(.2f, .85f, "positive offset");
        AssertOffsetConverges(-.2f, 1.15f, "negative offset");
    }

    private static void AssertOffsetConverges(float initialOffset, float expectedScale, string label)
    {
        var phase = new NativeWalkPhase();
        float legStart = initialOffset >= 0f ? initialOffset : 1f + initialOffset;
        phase.Reset(legStart, 0f, 4);
        phase.Step(.01f, 1f, .01f, 4, false); // first sample preserves the reset trajectory

        float previousMagnitude = Math.Abs(phase.OffsetCycles);
        for (int i = 2; i <= 170; i++)
        {
            float native = i * .01f;
            float leg = phase.Step(native, 1f, .01f, 4, false);
            float magnitude = Math.Abs(phase.OffsetCycles);
            if (magnitude > previousMagnitude + .00001f)
                throw new InvalidOperationException(label + " must move monotonically toward zero");
            if (magnitude > .00001f)
                Near(expectedScale, phase.CadenceScale, .0001f, label + " cadence scale bound");
            InUnitInterval(leg, label + " returned phase");
            previousMagnitude = magnitude;
        }

        Near(0f, phase.OffsetCycles, .00001f, label + " converges to exact lock");
        Near(1f, phase.CadenceScale, .0001f, label + " returns nominal cadence at lock");
    }

    private static void ReportsTheActualBoundedCadence()
    {
        AssertCadenceForNativeSample(.515f, .85f, "jittered native delta");
        AssertCadenceForNativeSample(.51f, .85f, "stalled native delta");
        AssertCadenceForNativeSample(.525f, 1.15f, "fast native sample");
    }

    private static void AssertCadenceForNativeSample(float nextNative, float expectedScale, string label)
    {
        var phase = new NativeWalkPhase();
        phase.Reset(.5f, .5f, 41);
        float previousLeg = phase.Step(.51f, 1f, .01f, 41, false);
        float nextLeg = phase.Step(nextNative, 1f, .01f, 41, false);
        float actualAdvance = ShortestDelta(nextLeg, previousLeg);
        float actualScale = actualAdvance / .01f;

        Near(expectedScale, actualScale, .0001f, label + " measured cadence");
        Near(actualScale, phase.CadenceScale, .0001f, label + " reported cadence");
        if (phase.CadenceScale < NativeWalkPhase.MinimumCadenceScale
            || phase.CadenceScale > NativeWalkPhase.MaximumCadenceScale)
            throw new InvalidOperationException(label + " cadence must stay within [0.85, 1.15]");
        if (phase.Rebased)
            throw new InvalidOperationException(label + " should use bounded normal convergence");
    }

    private static void PreservesChosenPhaseOnEntry()
    {
        var phase = new NativeWalkPhase();
        phase.Reset(.73f, .12f, 5);
        float first = phase.Step(.92f, 1f, .02f, 5, false);
        Near(.75f, first, .000001f, "first step advances chosen phase at nominal cadence");
        if (Math.Abs(first - .92f) < .05f)
            throw new InvalidOperationException("entry must not snap to the native phase");
        Near(-.17f, phase.OffsetCycles, .00001f, "entry retains its phase offset");
        Near(1f, phase.CadenceScale, 0f, "entry uses nominal cadence");
        if (!phase.Rebased)
            throw new InvalidOperationException("first post-reset sample should report a rebase");
    }

    private static void RebasesWithoutJumps()
    {
        var phase = new NativeWalkPhase();
        phase.Reset(.25f, .25f, 6);
        Near(.26f, phase.Step(.26f, 1f, .01f, 6, false), .000001f,
            "phase is initialized before state change");

        float changedState = phase.Step(.95f, 1f, .01f, 7, false);
        Near(.27f, changedState, .000001f, "state change advances old leg trajectory");
        AssertRebased(phase, "state change");

        float transition = phase.Step(.40f, 1f, .01f, 7, true);
        Near(.28f, transition, .000001f, "transition advances old leg trajectory");
        AssertRebased(phase, "transition");

        float jump = phase.Step(.80f, 1f, .01f, 7, false);
        Near(.29f, jump, .000001f, "discontinuous native phase advances old leg trajectory");
        AssertRebased(phase, "phase jump");
    }

    private static void LocksExactlyWhenAligned()
    {
        var phase = new NativeWalkPhase();
        phase.Reset(.5f, .5f, 8);
        Near(.52f, phase.Step(.52f, 1f, .02f, 8, false), .000001f, "first aligned advance");
        Near(.54f, phase.Step(.54f, 1f, .02f, 8, false), .000001f, "second aligned advance");
        Near(0f, phase.OffsetCycles, .000001f, "exact phase lock has zero offset");
        Near(1f, phase.CadenceScale, .000001f, "exact phase lock uses nominal cadence");
        if (phase.Rebased)
            throw new InvalidOperationException("aligned continuous samples should not rebase");
    }

    private static void FreezesOnInvalidInputs()
    {
        var phase = new NativeWalkPhase();
        phase.Reset(.4f, .4f, 9);
        float baseline = phase.Step(.42f, 1f, .02f, 9, false);
        Near(.42f, baseline, .000001f, "valid baseline");

        Near(baseline, phase.Step(.8f, 1f, 0f, 10, false), 0f, "zero dt freezes phase");
        Near(baseline, phase.Step(.8f, 1f, .051f, 10, false), 0f, "large dt freezes phase");
        Near(baseline, phase.Step(float.NaN, 1f, .02f, 10, false), 0f, "NaN native phase freezes");
        Near(baseline, phase.Step(.8f, 0f, .02f, 10, false), 0f, "zero length freezes phase");
        Near(baseline, phase.Step(.8f, float.PositiveInfinity, .02f, 10, false), 0f,
            "infinite length freezes phase");
        Near(baseline, phase.Step(.8f, 1f, float.NaN, 10, false), 0f, "NaN dt freezes phase");
        if (phase.CadenceScale != 1f || phase.Rebased)
            throw new InvalidOperationException("invalid samples must report neutral cadence without rebasing");

        // A skipped bad sample leaves the last valid native clock untouched.
        Near(.44f, phase.Step(.44f, 1f, .02f, 9, false), .000001f,
            "valid samples resume from the previous safe clock");
        InUnitInterval(phase.Step(float.PositiveInfinity, 1f, .02f, 9, false),
            "invalid inputs never return a non-finite phase");

        phase.Reset(float.NaN, .5f, 11);
        Near(0f, phase.Step(.7f, 1f, .02f, 11, false), 0f,
            "invalid reset phase leaves a finite frozen output");
    }

    private static void AssertRebased(NativeWalkPhase phase, string label)
    {
        if (!phase.Rebased)
            throw new InvalidOperationException(label + " should report a rebase");
        Near(1f, phase.CadenceScale, 0f, label + " uses nominal cadence");
        if (phase.OffsetCycles < -.5f || phase.OffsetCycles >= .5f)
            throw new InvalidOperationException(label + " offset must use [-0.5, 0.5)");
    }

    private static void InUnitInterval(float value, string label)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value >= 1f)
            throw new InvalidOperationException(label + " must be finite and wrapped to [0, 1)");
    }

    private static void Near(float expected, float actual, float tolerance, string label)
    {
        if (float.IsNaN(actual) || float.IsInfinity(actual)
            || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException(label + " expected " + expected + " but got " + actual);
    }

    private static float ShortestDelta(float to, float from)
    {
        float delta = (to - from) % 1f;
        if (delta < -.5f) delta += 1f;
        else if (delta >= .5f) delta -= 1f;
        return delta;
    }
}
