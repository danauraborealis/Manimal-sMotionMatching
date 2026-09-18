using System;
using EFT;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            StockTwoCannotJumpSpeed();
            RepeatedWritesInOneFrameStayNative();
            HumanAndInactiveBotsAreUnaffected();
            NativeAccelerationAndExitResetAreAllowed();
            LowerRequestIsAnImmediateSafetyCap();
            NestedGuardsRemainScopedAndBalanced();
            PerContextGuardsDoNotBleed();
            ScopeAndPolicyEpochsClearLowerCaps();
            DetachAndReattachPreserveObservedSpeed();
            FinalizersReleaseExceptionScopes();
            DisabledProbeStillReportsCurrentSpeed();
            Console.WriteLine("runtime_sprint_inertia: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void StockTwoCannotJumpSpeed()
    {
        Reset();
        var player = Bot(0.5f);
        SprintInertia.Attach(player, () => true);

        ExternalSet(player, 2f);

        Near(player.MovementContext.SprintSpeed, 0.5f, "a direct stock 2 request must not jump speed");
        Equal(SprintInertia.Probe(player).SuppressedExternalWrites, 1, "first upward write telemetry");
    }

    private static void RepeatedWritesInOneFrameStayNative()
    {
        Reset();
        var player = Bot(0.5f);
        SprintInertia.Attach(player, () => true);

        ExternalSet(player, 2f);
        ExternalSet(player, 2f);
        ExternalSet(player, 2f);
        NativeAcceleration(player, 0.25f);

        Near(player.MovementContext.SprintSpeed, 0.75f, "repeated direct writes must leave native acceleration in control");
        var probe = SprintInertia.Probe(player);
        Equal(probe.SuppressedExternalWrites, 3, "repeated suppression telemetry");
        Equal(probe.NativeUpdates, 1, "native setter telemetry");
    }

    private static void HumanAndInactiveBotsAreUnaffected()
    {
        Reset();
        var human = Bot(0.5f);
        human.IsAI = false;
        SprintInertia.Attach(human, () => true);
        ExternalSet(human, 2f);
        Near(human.MovementContext.SprintSpeed, 2f, "human direct write");

        var active = false;
        var inactive = Bot(0.5f);
        SprintInertia.Attach(inactive, () => active);
        ExternalSet(inactive, 2f);
        Near(inactive.MovementContext.SprintSpeed, 2f, "inactive pose direct write");
        if (SprintInertia.Probe(inactive).Scoped)
            throw new InvalidOperationException("inactive pose must not be scoped");
    }

    private static void NativeAccelerationAndExitResetAreAllowed()
    {
        Reset();
        var player = Bot(0.5f);
        player.Physical.Sprinting = true;
        SprintInertia.Attach(player, () => true);

        NativeAcceleration(player, 0.2f);
        Near(player.MovementContext.SprintSpeed, 0.7f, "native sprint acceleration");

        Exit(player, EPlayerState.None);
        Near(player.MovementContext.SprintSpeed, 0.5f, "native sprint exit reset");
        Equal(SprintInertia.Probe(player).Resets, 1, "native reset telemetry");

        // The reset guard is separate from the acceleration guard and must also permit an upward reset.
        player.MovementContext.SprintSpeed = 0.2f;
        Exit(player, EPlayerState.None);
        Near(player.MovementContext.SprintSpeed, 0.5f, "upward native sprint exit reset");
        Equal(SprintInertia.Probe(player).Resets, 2, "second native reset telemetry");
    }

    private static void LowerRequestIsAnImmediateSafetyCap()
    {
        Reset();
        var player = Bot(1f);
        SprintInertia.Attach(player, () => true);

        ExternalSet(player, 0.5f);
        NativeAcceleration(player, 1f);
        Near(player.MovementContext.SprintSpeed, 0.5f, "native acceleration must not pass a lower request");
        Equal(SprintInertia.Probe(player).LowerRequests, 1, "lower request telemetry");

        // A brief gap expires the request cap, so a later movement order cannot inherit a stale low ceiling.
        Advance(0.21f);
        NativeAcceleration(player, 0.25f);
        Near(player.MovementContext.SprintSpeed, 0.75f, "expired request cap");
    }

    private static void NestedGuardsRemainScopedAndBalanced()
    {
        Reset();
        var player = Bot(1f);
        SprintInertia.Attach(player, () => true);

        ExternalSet(player, 0.5f);
        var context = player.MovementContext;
        SprintInertia.BeforeSprintAcceleration(context);
        SprintInertia.BeforeSprintAcceleration(context);

        // The innermost native setter must use the outer request cap while both guard depths are live.
        ExternalSet(player, 2f);
        Near(context.SprintSpeed, 0.5f, "nested native cap");
        Equal(SprintInertia.Probe(player).NativeUpdates, 1, "nested native setter telemetry");

        // Releasing one finalizer must leave the other native scope active.
        SprintInertia.AfterSprintAcceleration(context, null);
        ExternalSet(player, 2f);
        Near(context.SprintSpeed, 0.5f, "outer native guard remains active");
        Equal(SprintInertia.Probe(player).NativeUpdates, 2, "outer native guard telemetry");

        SprintInertia.AfterSprintAcceleration(context, null);
        ExternalSet(player, 2f);
        Near(context.SprintSpeed, 0.5f, "balanced native guard suppresses external write");
        Equal(SprintInertia.Probe(player).SuppressedExternalWrites, 1, "post-finalizer suppression telemetry");
    }

    private static void PerContextGuardsDoNotBleed()
    {
        Reset();
        var first = Bot(1f);
        var second = Bot(1f);
        SprintInertia.Attach(first, () => true);
        SprintInertia.Attach(second, () => true);

        ExternalSet(first, 0.25f);
        SprintInertia.BeforeSprintAcceleration(first.MovementContext);

        // A native scope on one context must not authorize upward writes on another attached bot.
        ExternalSet(second, 2f);
        Near(second.MovementContext.SprintSpeed, 1f, "cross-context upward write");
        Equal(SprintInertia.Probe(second).SuppressedExternalWrites, 1, "cross-context suppression telemetry");

        ExternalSet(first, 2f);
        Near(first.MovementContext.SprintSpeed, 0.25f, "first context native cap");
        Equal(SprintInertia.Probe(first).NativeUpdates, 1, "first context native telemetry");
        SprintInertia.AfterSprintAcceleration(first.MovementContext, null);
    }

    private static void ScopeAndPolicyEpochsClearLowerCaps()
    {
        Reset();
        var active = true;
        var player = Bot(1f);
        SprintInertia.Attach(player, () => active);
        ExternalSet(player, 0.5f);

        active = false;
        ExternalSet(player, 2f);
        Near(player.MovementContext.SprintSpeed, 2f, "inactive scope direct write");
        active = true;
        NativeAcceleration(player, 0.25f);
        Near(player.MovementContext.SprintSpeed, 2.25f, "scope reactivation must not inherit lower cap");

        ExternalSet(player, 0.5f);
        SprintInertia.Enabled = false;
        SprintInertia.Enabled = true;
        NativeAcceleration(player, 0.25f);
        Near(player.MovementContext.SprintSpeed, 0.75f, "policy epoch must clear lower cap");
    }

    private static void DetachAndReattachPreserveObservedSpeed()
    {
        Reset();
        var player = Bot(0.5f);
        SprintInertia.Attach(player, () => true);
        ExternalSet(player, 2f);
        SprintInertia.Detach(player);
        ExternalSet(player, 2f);
        Near(player.MovementContext.SprintSpeed, 2f, "detached direct write");

        SprintInertia.Attach(player, () => true);
        Near(SprintInertia.Probe(player).CurrentSprintSpeed, 2f, "reattach must preserve current speed");
        ExternalSet(player, 2f);
        Near(player.MovementContext.SprintSpeed, 2f, "reattach must not restore an old speed");
    }

    private static void FinalizersReleaseExceptionScopes()
    {
        Reset();
        var player = Bot(0.5f);
        SprintInertia.Attach(player, () => true);

        player.MovementContext.ThrowFromAcceleration = true;
        try { NativeAcceleration(player, 0.2f); }
        catch (InvalidOperationException) { }
        player.MovementContext.ThrowFromAcceleration = false;
        ExternalSet(player, 2f);
        Near(player.MovementContext.SprintSpeed, 0.7f, "acceleration finalizer must release its guard");

        // Reattach starts a fresh request epoch while keeping the observed speed.
        SprintInertia.Attach(player, () => true);
        player.MovementContext.ThrowFromExit = true;
        try { Exit(player, EPlayerState.None); }
        catch (InvalidOperationException) { }
        player.MovementContext.ThrowFromExit = false;
        ExternalSet(player, 2f);
        Near(player.MovementContext.SprintSpeed, 0.5f, "exit finalizer must release its guard");
        if (SprintInertia.Probe(player).Exceptions != 0)
            throw new InvalidOperationException("optional patch exception paths should remain telemetry-clean");
    }

    private static void DisabledProbeStillReportsCurrentSpeed()
    {
        Reset();
        var player = Bot(0.65f);
        SprintInertia.Attach(player, () => true);
        SprintInertia.Enabled = false;

        var probe = SprintInertia.Probe(player);
        if (probe.ConfiguredEnabled || !probe.Attached || !probe.HasCurrentSprintSpeed)
            throw new InvalidOperationException("disabled probe gates");
        Near(probe.CurrentSprintSpeed, 0.65f, "disabled probe current speed");
        ExternalSet(player, 2f);
        Near(player.MovementContext.SprintSpeed, 2f, "disabled policy direct write");
    }

    private static Player Bot(float speed)
    {
        var player = new Player(speed);
        player.MovementContext.WriteSpeed = value => SetSpeed(player, value);
        return player;
    }

    private static void ExternalSet(Player player, float value)
    {
        SetSpeed(player, value);
    }

    private static void SetSpeed(Player player, float value)
    {
        var context = player.MovementContext;
        float requested = value;
        if (SprintInertia.BeforeSprintSpeedSet(context, ref requested))
            context.SprintSpeed = requested;
        SprintInertia.AfterSprintSpeedSet(context);
    }

    private static void NativeAcceleration(Player player, float deltaTime)
    {
        var context = player.MovementContext;
        SprintInertia.BeforeSprintAcceleration(context);
        Exception error = null;
        try { context.SprintAcceleration(deltaTime); }
        catch (Exception exception) { error = exception; }
        SprintInertia.AfterSprintAcceleration(context, error);
        if (error != null)
            throw error;
    }

    private static void Exit(Player player, EPlayerState nextState)
    {
        var context = player.MovementContext;
        SprintInertia.BeforeSprintExit(context);
        Exception error = null;
        try { context.SprintExitDelegate(EPlayerState.Sprint, nextState); }
        catch (Exception exception) { error = exception; }
        SprintInertia.AfterSprintExit(context, error);
        if (error != null)
            throw error;
    }

    private static void Reset()
    {
        SprintInertia.DetachAll();
        SprintInertia.Enabled = true;
        Time.frameCount = 1;
        Time.realtimeSinceStartup = 0f;
    }

    private static void Advance(float seconds)
    {
        Time.realtimeSinceStartup += seconds;
        Time.frameCount++;
    }

    private static void Near(float actual, float expected, string label)
    {
        if (Math.Abs(actual - expected) > 0.00001f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }

    private static void Equal(int actual, int expected, string label)
    {
        if (actual != expected)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }
}
