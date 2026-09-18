using System;
using System.Reflection;
using EFT;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private const float DeltaTime = 0.02f;
    private static readonly MethodInfo PrefixMethod = typeof(BotInertiaPatch).GetMethod(
        "Prefix", BindingFlags.NonPublic | BindingFlags.Static);

    private static int Main()
    {
        try
        {
            AccelerationIsBounded();
            ReactionRootDriveRespectsOwnership();
            DriveDoesNotOverrideRequestedBraking();
            LateralBrakingCarryRemains();
            StopMotionIsImmediate();
            FreshCeilingSurvivesInitialization();
            CommandsExpireWhenVisualPassIsSkipped();
            UnsupportedStateResetsVelocityAndCommands();
            InvalidInputsFailOpen();
            CleanupDetachesEveryOwnerEvenWhenOneThrows();
            Console.WriteLine("runtime_bot_inertia: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void AccelerationIsBounded()
    {
        Reset();
        Player player = Bot();
        Apply(player, EPlayerState.Run, new Vector3(0f, 0f, 0f), DeltaTime);
        Advance(DeltaTime);

        Vector3 result = Apply(player, EPlayerState.Run, new Vector3(2f * DeltaTime, 0f, 0f), DeltaTime);
        Near(result.x / DeltaTime, 0.08f, "ground acceleration");
    }

    private static void DriveDoesNotOverrideRequestedBraking()
    {
        Reset();
        Player player = Bot();
        Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        BotInertiaPatch.SetDrive(player, 2f);
        Advance(DeltaTime);

        Vector3 result = Apply(player, EPlayerState.Run, new Vector3(0.5f * DeltaTime, 0f, 0f), DeltaTime);
        Near(result.x / DeltaTime, 0.5f, "Drive must preserve EFT braking request");
    }

    private static void LateralBrakingCarryRemains()
    {
        Reset();
        Player player = Bot();
        Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        Advance(DeltaTime);

        Vector3 result = Apply(player, EPlayerState.Run, new Vector3(0f, 0f, 0.5f * DeltaTime), DeltaTime);
        Near(result.x / DeltaTime, 2.8f, "lateral braking carry");
        Near(result.z / DeltaTime, 0f, "push waits for lateral braking");
    }

    private static void StopMotionIsImmediate()
    {
        Reset();
        Player player = Bot();
        Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        Advance(DeltaTime);

        Vector3 result = Apply(player, EPlayerState.Idle, new Vector3(0f, 0f, 0f), DeltaTime);
        Near(result.x, 0f, "stop clip motion");
        Near(result.z, 0f, "stop clip lateral motion");
    }

    private static void FreshCeilingSurvivesInitialization()
    {
        Reset();
        Player player = Bot();
        BotInertiaPatch.SetCeiling(player, 0.5f);

        Vector3 first = Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        Near(first.x / DeltaTime, 3f, "the first supported sample seeds native velocity");
        Advance(DeltaTime);

        Vector3 second = Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        Near(second.x / DeltaTime, 2.92f, "fresh clip ceiling after initialization");
    }

    private static void CommandsExpireWhenVisualPassIsSkipped()
    {
        Reset();
        Player player = Bot();
        Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        BotInertiaPatch.SetDrive(player, 2f);

        Vector3 result = default(Vector3);
        for (int frame = 1; frame <= 12; frame++)
        {
            Advance(DeltaTime);
            result = Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        }
        Near(result.x / DeltaTime, 2f, "active drive command");
        Advance(DeltaTime);
        result = Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        Near(result.x / DeltaTime, 2.08f, "expired drive resumes inertial acceleration");
    }

    private static void UnsupportedStateResetsVelocityAndCommands()
    {
        Reset();
        Player player = Bot();
        Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        BotInertiaPatch.SetCeiling(player, 0.5f);
        BotInertiaPatch.SetDrive(player, 2f);
        Advance(DeltaTime);

        Apply(player, EPlayerState.Jump, new Vector3(1f * DeltaTime, 0f, 0f), DeltaTime);
        Advance(DeltaTime);
        Vector3 result = Apply(player, EPlayerState.Run, new Vector3(2f * DeltaTime, 0f, 0f), DeltaTime);
        Near(result.x / DeltaTime, 1.08f, "unsupported state clears velocity and stale commands");
    }

    private static void InvalidInputsFailOpen()
    {
        Reset();
        Player player = Bot();
        Apply(player, EPlayerState.Run, new Vector3(0f, 0f, 0f), DeltaTime);
        BotInertiaPatch.SetDrive(player, float.NaN);
        Advance(DeltaTime);

        Vector3 result = Apply(player, EPlayerState.Run, new Vector3(0.5f * DeltaTime, 0f, 0f), DeltaTime);
        Near(result.x / DeltaTime, 0.08f, "invalid command is cleared");

        Advance(DeltaTime);
        Vector3 invalid = Apply(player, EPlayerState.Run, new Vector3(float.NaN, 0f, 0f), DeltaTime);
        if (!float.IsNaN(invalid.x))
            throw new InvalidOperationException("non-finite native input must pass through unchanged");
        Advance(DeltaTime);
        result = Apply(player, EPlayerState.Run, new Vector3(2f * DeltaTime, 0f, 0f), DeltaTime);
        Near(result.x / DeltaTime, 0.08f, "valid motion recovers after invalid input");

        BotInertiaPatch.Acceleration = float.PositiveInfinity;
        Vector3 native = new Vector3(2f * DeltaTime, 0f, 0f);
        Vector3 unchanged = Apply(player, EPlayerState.Run, native, DeltaTime);
        Near(unchanged.x, native.x, "invalid acceleration leaves game motion unchanged");
    }

    private static void CleanupDetachesEveryOwnerEvenWhenOneThrows()
    {
        Reset();
        Player player = Bot();
        SpeedRampPatch.Attach(player);
        BotInertiaPatch.Attach(player);
        SprintInertia.Attach(player);
        Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        BotInertiaPatch.SetCeiling(player, 0.5f);
        BotInertiaPatch.SetDrive(player, 2f);
        Advance(DeltaTime);

        Vector3 capped = Apply(player, EPlayerState.Run, new Vector3(3f * DeltaTime, 0f, 0f), DeltaTime);
        Near(capped.x / DeltaTime, 2.72f, "ceiling and drive active before cleanup");

        SpeedRampPatch.ThrowOnDetach = true;
        MovementCleanup.Detach(player);
        Equal(SpeedRampPatch.DetachCalls, 1, "speed ramp cleanup attempted");
        Equal(SprintInertia.DetachCalls, 1, "sprint cleanup attempted after another cleanup throws");

        Advance(DeltaTime);
        Vector3 requested = new Vector3(3f * DeltaTime, 0f, 0f);
        Vector3 native = Apply(player, EPlayerState.Run, requested, DeltaTime);
        Near(native.x, requested.x, "cleanup restores native motion");
    }

    private static void ReactionRootDriveRespectsOwnership()
    {
        Reset();
        Player player = Bot();
        Apply(player, EPlayerState.Run, new Vector3(2f * DeltaTime, 0f, 0f), DeltaTime);
        Advance(DeltaTime);
        BotInertiaPatch.SetRootDrive(player, new Vector2(0f, 2f));
        Vector3 driven = Apply(player, EPlayerState.Run, new Vector3(2f * DeltaTime, .01f, 0f), DeltaTime);
        if (driven.z <= 0f || driven.x >= 2f * DeltaTime) throw new Exception("root direction was not applied");
        Near(driven.y, .01f, "vertical native motion retained");
        Advance(DeltaTime);
        Vector3 stopped = Apply(player, EPlayerState.Run, new Vector3(), DeltaTime);
        Near(stopped.x, 0f, "native stop x"); Near(stopped.z, 0f, "native stop z");
        Advance(.3f);
        Apply(player, EPlayerState.Run, new Vector3(2f * DeltaTime,0,0), DeltaTime);
        Advance(DeltaTime);
        Vector3 expired = Apply(player, EPlayerState.Run, new Vector3(2f * DeltaTime,0,0), DeltaTime);
        Near(expired.z, 0f, "expired root command");
        BotInertiaPatch.SetRootDrive(player, new Vector2(float.NaN, 2f));
        Advance(DeltaTime);
        Vector3 invalid = Apply(player, EPlayerState.Run, new Vector3(2f * DeltaTime,0,0), DeltaTime);
        Near(invalid.z, 0f, "invalid root command discarded");
        BotInertiaPatch.SetRootDrive(player, new Vector2(0,2f));
        BotInertiaPatch.Detach(player);
        Vector3 detached = Apply(player, EPlayerState.Run, new Vector3(2f * DeltaTime,0,0), DeltaTime);
        Near(detached.x, 2f * DeltaTime, "detach releases root drive");
    }

    private static Vector3 Apply(Player player, EPlayerState state, Vector3 motion, float deltaTime)
    {
        var arguments = new object[]
        {
            new MovementState(player.MovementContext, state),
            motion,
            deltaTime
        };
        PrefixMethod.Invoke(null, arguments);
        return (Vector3)arguments[1];
    }

    private static Player Bot()
    {
        var player = new Player();
        BotInertiaPatch.Attach(player);
        return player;
    }

    private static void Reset()
    {
        BotInertiaPatch.Clear();
        BotInertiaPatch.Acceleration = 4f;
        SpeedRampPatch.DetachCalls = 0;
        SpeedRampPatch.ThrowOnDetach = false;
        SprintInertia.DetachCalls = 0;
        Time.time = 0f;
    }

    private static void Advance(float seconds)
    {
        Time.time += seconds;
    }

    private static void Near(float actual, float expected, string label)
    {
        if (float.IsNaN(actual) || float.IsInfinity(actual) || Math.Abs(actual - expected) > 0.0001f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }

    private static void AtMost(float actual, float maximum, string label)
    {
        if (float.IsNaN(actual) || float.IsInfinity(actual) || actual > maximum + 0.0001f)
            throw new InvalidOperationException(label + ": expected at most " + maximum + ", got " + actual);
    }

    private static void Equal(int actual, int expected, string label)
    {
        if (actual != expected)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }
}
