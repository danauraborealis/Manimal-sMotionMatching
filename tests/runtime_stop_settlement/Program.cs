using System;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private static readonly Vector3 RightFoot = new Vector3(.2f, 0f, 0f);
    private static readonly Vector3 Zero = new Vector3();

    private static int Main()
    {
        try
        {
            EndpointHeightIsZero();
            TrajectoryIsContinuousAndBounded();
            OnlyOneFootIsActive();
            ResetAndMovementCancelClearState();
            InvalidInputsFailClosed();
            FeedbackConvergesWithinFourSteps();
            UnreachableTargetFailsBoundedly();
            Console.WriteLine("runtime_stop_settlement: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void EndpointHeightIsZero()
    {
        var settlement = new StopSettlement();
        StopSettlementResult first = settlement.Update(true, .016f,
            Zero, RightFoot, new Vector3(.2f, .02f, 0f), RightFoot,
            0f, 0f, 0f, 0f);
        if (!first.Active || !first.Started || first.Side != 0)
            throw new InvalidOperationException("a correction must start on the largest-error foot");
        Near(first.Position.y, 0f, "start lift");

        StopSettlementResult result = first;
        for (int i = 0; i < 10 && !result.Completed; i++)
            result = settlement.Update(true, .1f,
                Zero, RightFoot, new Vector3(.2f, .02f, 0f), RightFoot,
                0f, 0f, 0f, 0f);
        if (!result.Completed)
            throw new InvalidOperationException("correction did not complete");
        Near(result.Position.x, .2f, "landing x");
        Near(result.Position.y, .02f, "landing height");
    }

    private static void TrajectoryIsContinuousAndBounded()
    {
        var settlement = new StopSettlement();
        Vector3 actual = Zero;
        Vector3 desired = new Vector3(.25f, .03f, 0f);
        StopSettlementResult previous = settlement.Update(true, .016f,
            actual, RightFoot, desired, RightFoot, 0f, 0f, 0f, 0f);
        if (!previous.Active)
            throw new InvalidOperationException("bounded trajectory did not start");

        while (true)
        {
            StopSettlementResult current = previous;
            if (!Finite(current.Position) || !Finite(current.Heading))
                throw new InvalidOperationException("trajectory produced a non-finite sample");
            if (current.Position.x < -.00001f || current.Position.x > .25001f
                || current.Position.z < -.00001f || current.Position.z > .00001f)
                throw new InvalidOperationException("horizontal correction exceeded its target");
            float baseline = .03f * (current.Position.x / .25f);
            if (current.Position.y < baseline - .00001f || current.Position.y > baseline + .0551f)
                throw new InvalidOperationException("lift arc escaped its bounded envelope");
            if (current.Completed)
                break;
            previous = settlement.Update(true, .05f,
                actual, RightFoot, desired, RightFoot, 0f, 0f, 0f, 0f);
        }
    }

    private static void OnlyOneFootIsActive()
    {
        var settlement = new StopSettlement();
        Vector3 leftDesired = new Vector3(.2f, 0f, 0f);
        Vector3 rightDesired = new Vector3(.2f, 0f, 0f);
        Vector3 leftActual = Zero;
        Vector3 rightActual = Zero;
        StopSettlementResult result = settlement.Update(true, 0f,
            leftActual, rightActual, leftDesired, rightDesired, 0f, 0f, 0f, 0f);
        if (!result.Active || result.Side != 0)
            throw new InvalidOperationException("equal errors must choose the left foot deterministically");
        int firstSide = result.Side;
        while (!result.Completed)
        {
            result = settlement.Update(true, .1f,
                leftActual, rightActual, leftDesired, rightDesired, 0f, 0f, 0f, 0f);
            if (result.Active && result.Side != firstSide)
                throw new InvalidOperationException("both feet cannot step during one swing");
        }

        leftActual = result.Position;
        result = settlement.Update(true, 0f,
            leftActual, rightActual, leftDesired, rightDesired, 0f, 0f, 0f, 0f);
        if (!result.Active || result.Side != 1 || !result.Started)
            throw new InvalidOperationException("the other foot must start only after completion is observed");
    }

    private static void ResetAndMovementCancelClearState()
    {
        var settlement = new StopSettlement();
        StopSettlementResult active = settlement.Update(true, 0f,
            Zero, RightFoot, new Vector3(.2f, 0f, 0f), RightFoot, 0f, 0f, 0f, 0f);
        if (!active.Active)
            throw new InvalidOperationException("reset fixture did not start");

        StopSettlementResult cancelled = settlement.Update(false, .1f,
            Zero, RightFoot, new Vector3(.2f, 0f, 0f), RightFoot, 0f, 0f, 0f, 0f);
        if (cancelled.Active || cancelled.Side != -1 || cancelled.Started || cancelled.Completed)
            throw new InvalidOperationException("ineligible movement must cancel the swing");

        StopSettlementResult restarted = settlement.Update(true, 0f,
            Zero, RightFoot, new Vector3(.2f, 0f, 0f), RightFoot, 0f, 0f, 0f, 0f);
        if (!restarted.Active || !restarted.Started || restarted.Side != 0)
            throw new InvalidOperationException("a cancelled episode must be restartable");

        settlement.Reset();
        StopSettlementResult reset = settlement.Update(true, 0f,
            Zero, RightFoot, Zero, RightFoot, 0f, 0f, 0f, 0f);
        if (!reset.Ready || reset.Active)
            throw new InvalidOperationException("explicit reset must clear the active correction");
    }

    private static void InvalidInputsFailClosed()
    {
        var settlement = new StopSettlement();
        StopSettlementResult invalid = settlement.Update(true, 0f,
            new Vector3(float.NaN, 0f, 0f), RightFoot, Zero, RightFoot,
            0f, 0f, 0f, 0f);
        if (invalid.Active || !invalid.Failed || !Finite(invalid.Position) || !Finite(invalid.Heading))
            throw new InvalidOperationException("invalid pose data must fail closed with finite output");

        StopSettlementResult started = settlement.Update(true, -1f,
            Zero, RightFoot, new Vector3(.2f, 0f, 0f), RightFoot,
            0f, 0f, 0f, 0f);
        if (!started.Active || !started.Started)
            throw new InvalidOperationException("a negative clock delta must not suppress a valid start");
        StopSettlementResult held = settlement.Update(true, float.NaN,
            Zero, RightFoot, new Vector3(.2f, 0f, 0f), RightFoot,
            0f, 0f, 0f, 0f);
        Near(held.Position.x, started.Position.x, "invalid clock hold");
    }

    private static void FeedbackConvergesWithinFourSteps()
    {
        var settlement = new StopSettlement();
        Vector3 leftActual = Zero;
        Vector3 desired = new Vector3(.9f, 0f, 0f);
        int starts = 0;
        for (int step = 0; step < 4; step++)
        {
            StopSettlementResult result = settlement.Update(true, 0f,
                leftActual, RightFoot, desired, RightFoot, 0f, 0f, 0f, 0f);
            if (!result.Started || result.Side != 0)
                throw new InvalidOperationException("feedback correction did not select the left foot");
            starts++;
            do
            {
                result = settlement.Update(true, 1f,
                    leftActual, RightFoot, desired, RightFoot, 0f, 0f, 0f, 0f);
            } while (!result.Completed);
            leftActual = result.Position;
        }

        StopSettlementResult ready = settlement.Update(true, 0f,
            leftActual, RightFoot, desired, RightFoot, 0f, 0f, 0f, 0f);
        if (starts != 4 || !ready.Ready || ready.Failed)
            throw new InvalidOperationException("rendered landing feedback must converge after four bounded steps");
    }

    private static void UnreachableTargetFailsBoundedly()
    {
        var settlement = new StopSettlement();
        Vector3 desired = new Vector3(2f, 0f, 0f);
        int starts = 0;
        StopSettlementResult result;
        do
        {
            result = settlement.Update(true, 0f,
                Zero, RightFoot, desired, RightFoot, 0f, 0f, 0f, 0f);
            if (result.Started)
                starts++;
            while (result.Active && !result.Completed)
                result = settlement.Update(true, 1f,
                    Zero, RightFoot, desired, RightFoot, 0f, 0f, 0f, 0f);
        } while (!result.Failed && starts < 10);

        if (!result.Failed || starts != 4)
            throw new InvalidOperationException("an unreachable correction must fail after four attempts");
    }

    private static bool Finite(Vector3 value)
    {
        return Finite(value.x) && Finite(value.y) && Finite(value.z);
    }

    private static bool Finite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void Near(float actual, float expected, string label)
    {
        if (Math.Abs(actual - expected) > .00001f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }
}
