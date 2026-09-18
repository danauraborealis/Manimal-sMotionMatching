using System;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            MatchesOutgoingRotationAndPelvisVelocityForStationaryTarget();
            MatchesOutgoingVelocityWithMovingTargetAndNonCollinearAxes();
            SourceAgeExtrapolatesOutgoingPoseAndPreservesVelocity();
            ZeroAndInvalidSourceAgesLeaveTheOutgoingPoseUnchanged();
            ReachesZeroOffsetAndVelocityAtDuration();
            UsesShortestRotationForAntipodalSamples();
            InvalidSampleDtFallsBackToZeroInitialVelocity();
            ExistingBeginKeepsLegacyCubicCurve();
            ReactionRecoveryPreservesMotionAndReleases();
            BoundedRecoveryNeverOvershoots();
            UpperOverlayPreservesNativeBaselineAndRebasesChest();
            LandingRecoveryNeedsOneRealMovingJump();
            Console.WriteLine("runtime_inertializer: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void MatchesOutgoingRotationAndPelvisVelocityForStationaryTarget()
    {
        const float historyDt = 0.02f;
        const float step = 0.0002f;
        Quaternion from = AxisAngle(new Vector3(0.3f, 0.8f, -0.2f), 0.7f);
        Quaternion to = AxisAngle(new Vector3(-0.4f, 0.1f, 0.9f), 0.25f);
        Vector3 outgoingAngularVelocity = new Vector3(0.8f, -0.3f, 0.45f);
        Vector3 outgoingPelvisVelocity = new Vector3(0.6f, -0.2f, 0.1f);
        Vector3 fromPelvis = new Vector3(0.24f, -0.08f, 0.05f);
        Vector3 toPelvis = new Vector3(0.03f, 0.04f, -0.02f);

        Inertializer inertializer = Begin(
            from, to, fromPelvis, toPelvis, outgoingAngularVelocity, Vector3.zero,
            outgoingPelvisVelocity, Vector3.zero, historyDt, 0.35f);

        Quaternion outputAtStart = inertializer.Rotation(0) * to;
        SameRotation(outputAtStart, from, 0.0002f, "stationary target start pose");
        NearVector(inertializer.PelvisOffset + toPelvis, fromPelvis, 0.00001f, "stationary target start pelvis");

        inertializer.Advance(step);
        Quaternion outputAfterStep = inertializer.Rotation(0) * to;
        Vector3 measuredAngularVelocity = AngularVelocityBetween(outputAfterStep, outputAtStart, step);
        NearVector(measuredAngularVelocity, outgoingAngularVelocity, 0.02f, "stationary target initial angular velocity");
        Vector3 measuredPelvisVelocity = (inertializer.PelvisOffset + toPelvis - fromPelvis) / step;
        NearVector(measuredPelvisVelocity, outgoingPelvisVelocity, 0.02f, "stationary target initial pelvis velocity");
    }

    private static void MatchesOutgoingVelocityWithMovingTargetAndNonCollinearAxes()
    {
        const float historyDt = 0.025f;
        const float step = 0.0002f;
        Quaternion from = AxisAngle(new Vector3(0.7f, -0.1f, 0.4f), 0.9f);
        Quaternion to = AxisAngle(new Vector3(-0.2f, 0.8f, 0.5f), -0.55f);
        // Deliberately non-collinear so the incoming angular velocity must be rotated by the relative offset,
        // and the log derivative must pass through the inverse left Jacobian.
        Vector3 outgoingAngularVelocity = new Vector3(0.85f, 0.2f, -0.35f);
        Vector3 incomingAngularVelocity = new Vector3(-0.25f, 0.7f, 0.5f);
        Vector3 outgoingPelvisVelocity = new Vector3(0.4f, -0.1f, 0.25f);
        Vector3 incomingPelvisVelocity = new Vector3(-0.15f, 0.3f, -0.05f);
        Vector3 fromPelvis = new Vector3(0.18f, 0.05f, -0.11f);
        Vector3 toPelvis = new Vector3(-0.03f, 0.07f, 0.02f);

        Inertializer inertializer = Begin(
            from, to, fromPelvis, toPelvis, outgoingAngularVelocity, incomingAngularVelocity,
            outgoingPelvisVelocity, incomingPelvisVelocity, historyDt, 0.3f);

        Quaternion outputAtStart = inertializer.Rotation(0) * to;
        Vector3 outputPelvisAtStart = inertializer.PelvisOffset + toPelvis;
        inertializer.Advance(step);
        Quaternion incomingAfterStep = RotationExp(incomingAngularVelocity * step) * to;
        Quaternion outputAfterStep = inertializer.Rotation(0) * incomingAfterStep;
        Vector3 measuredAngularVelocity = AngularVelocityBetween(outputAfterStep, outputAtStart, step);
        NearVector(measuredAngularVelocity, outgoingAngularVelocity, 0.025f, "moving target initial angular velocity");

        Vector3 incomingPelvisAfterStep = toPelvis + incomingPelvisVelocity * step;
        Vector3 outputPelvisAfterStep = inertializer.PelvisOffset + incomingPelvisAfterStep;
        Vector3 measuredPelvisVelocity = (outputPelvisAfterStep - outputPelvisAtStart) / step;
        NearVector(measuredPelvisVelocity, outgoingPelvisVelocity, 0.015f, "moving target initial pelvis velocity");
    }

    private static void SourceAgeExtrapolatesOutgoingPoseAndPreservesVelocity()
    {
        const float historyDt = 0.02f;
        const float sourceAge = 0.035f;
        const float step = 0.0002f;
        Quaternion fromAtLastSample = AxisAngle(new Vector3(0.7f, -0.2f, 0.5f), 0.45f);
        Quaternion toAtCurrentTime = AxisAngle(new Vector3(-0.3f, 0.8f, 0.4f), -0.6f);
        Vector3 outgoingAngularVelocity = new Vector3(0.75f, -0.25f, 0.55f);
        Vector3 incomingAngularVelocity = new Vector3(-0.2f, 0.65f, 0.35f);
        Vector3 fromPelvisAtLastSample = new Vector3(0.17f, -0.06f, 0.12f);
        Vector3 toPelvisAtCurrentTime = new Vector3(-0.04f, 0.08f, 0.01f);
        Vector3 outgoingPelvisVelocity = new Vector3(0.5f, 0.15f, -0.3f);
        Vector3 incomingPelvisVelocity = new Vector3(-0.1f, 0.25f, 0.08f);
        Quaternion expectedFromAtCurrentTime = RotationExp(outgoingAngularVelocity * sourceAge) * fromAtLastSample;
        Vector3 expectedFromPelvisAtCurrentTime = fromPelvisAtLastSample + outgoingPelvisVelocity * sourceAge;

        Inertializer inertializer = Begin(
            fromAtLastSample, toAtCurrentTime,
            fromPelvisAtLastSample, toPelvisAtCurrentTime,
            outgoingAngularVelocity, incomingAngularVelocity,
            outgoingPelvisVelocity, incomingPelvisVelocity,
            historyDt, 0.3f, sourceAge);

        Quaternion outputAtStart = inertializer.Rotation(0) * toAtCurrentTime;
        SameRotation(outputAtStart, expectedFromAtCurrentTime, 0.0003f, "aged outgoing start rotation");
        NearVector(inertializer.PelvisOffset + toPelvisAtCurrentTime,
            expectedFromPelvisAtCurrentTime, 0.00002f, "aged outgoing start pelvis");

        inertializer.Advance(step);
        Quaternion targetAfterStep = RotationExp(incomingAngularVelocity * step) * toAtCurrentTime;
        Quaternion outputAfterStep = inertializer.Rotation(0) * targetAfterStep;
        NearVector(AngularVelocityBetween(outputAfterStep, outputAtStart, step),
            outgoingAngularVelocity, 0.025f, "aged outgoing initial angular velocity");

        Vector3 targetPelvisAfterStep = toPelvisAtCurrentTime + incomingPelvisVelocity * step;
        Vector3 outputPelvisAfterStep = inertializer.PelvisOffset + targetPelvisAfterStep;
        NearVector((outputPelvisAfterStep - expectedFromPelvisAtCurrentTime) / step,
            outgoingPelvisVelocity, 0.02f, "aged outgoing initial pelvis velocity");
    }

    private static void ZeroAndInvalidSourceAgesLeaveTheOutgoingPoseUnchanged()
    {
        Quaternion from = AxisAngle(new Vector3(0.5f, 0.7f, -0.2f), 0.55f);
        Quaternion to = AxisAngle(new Vector3(-0.3f, 0.2f, 0.8f), -0.3f);
        Vector3 fromPelvis = new Vector3(0.19f, -0.02f, 0.07f);
        Vector3 toPelvis = new Vector3(-0.03f, 0.05f, 0.01f);
        Vector3 fromAngularVelocity = new Vector3(0.9f, -0.4f, 0.25f);
        Vector3 fromPelvisVelocity = new Vector3(0.6f, 0.1f, -0.2f);
        float[] ages = { 0f, float.NaN, float.PositiveInfinity, -0.001f, 0.0501f };

        for (int i = 0; i < ages.Length; i++)
        {
            Inertializer inertializer = new Inertializer();
            inertializer.Begin(
                new[] { from }, new[] { to }, fromPelvis, toPelvis, 0.3f,
                new[] { Previous(from, fromAngularVelocity, 0.02f) }, new[] { to },
                fromPelvis - fromPelvisVelocity * 0.02f, toPelvis, 0.02f, ages[i]);
            SameRotation(inertializer.Rotation(0) * to, from, 0.0002f, "invalid source age start rotation " + i);
            NearVector(inertializer.PelvisOffset + toPelvis, fromPelvis, 0.00001f, "invalid source age start pelvis " + i);
        }
    }

    private static void ReachesZeroOffsetAndVelocityAtDuration()
    {
        const float duration = 0.3f;
        const float endpointStep = 0.0005f;
        Quaternion from = AxisAngle(new Vector3(0.2f, -0.6f, 0.75f), 0.8f);
        Inertializer inertializer = Begin(
            from, Quaternion.identity,
            new Vector3(0.25f, 0.1f, -0.12f), Vector3.zero,
            new Vector3(0.9f, -0.4f, 0.3f), Vector3.zero,
            new Vector3(1.5f, -0.2f, 0.4f), Vector3.zero,
            0.02f, duration);

        inertializer.Advance(duration - endpointStep);
        Quaternion beforeEnd = inertializer.Rotation(0);
        Vector3 pelvisBeforeEnd = inertializer.PelvisOffset;
        inertializer.Advance(endpointStep);

        SameRotation(inertializer.Rotation(0), Quaternion.identity, 0.00001f, "rotation at duration");
        NearVector(inertializer.PelvisOffset, Vector3.zero, 0.000001f, "pelvis offset at duration");
        Vector3 endpointAngularVelocity = AngularVelocityBetween(inertializer.Rotation(0), beforeEnd, endpointStep);
        NearVector(endpointAngularVelocity, Vector3.zero, 0.08f, "rotation endpoint velocity");
        NearVector((inertializer.PelvisOffset - pelvisBeforeEnd) / endpointStep,
            Vector3.zero, 0.02f, "pelvis endpoint velocity");
    }

    private static void UsesShortestRotationForAntipodalSamples()
    {
        const float historyDt = 0.02f;
        const float step = 0.0002f;
        Quaternion from = AxisAngle(new Vector3(0.4f, 0.3f, 0.7f), 0.65f);
        Quaternion to = Negate(Quaternion.identity);
        Quaternion previousFrom = Negate(Previous(from, new Vector3(0.3f, -0.45f, 0.25f), historyDt));
        Quaternion previousTo = Quaternion.identity;
        Vector3 outgoingAngularVelocity = new Vector3(0.3f, -0.45f, 0.25f);

        Inertializer inertializer = new Inertializer();
        inertializer.Begin(
            new[] { from }, new[] { to },
            new Vector3(0.1f, -0.03f, 0.05f), Vector3.zero, 0.25f,
            new[] { previousFrom }, new[] { previousTo },
            Vector3.zero, Vector3.zero, historyDt);

        Quaternion outputAtStart = inertializer.Rotation(0) * to;
        SameRotation(outputAtStart, from, 0.0002f, "antipodal start pose");
        inertializer.Advance(step);
        Quaternion outputAfterStep = inertializer.Rotation(0) * to;
        NearVector(AngularVelocityBetween(outputAfterStep, outputAtStart, step),
            outgoingAngularVelocity, 0.02f, "antipodal initial angular velocity");
    }

    private static void InvalidSampleDtFallsBackToZeroInitialVelocity()
    {
        Quaternion from = AxisAngle(new Vector3(0.3f, -0.7f, 0.2f), 0.6f);
        Vector3 fromPelvis = new Vector3(0.22f, -0.04f, 0.1f);
        Inertializer inertializer = new Inertializer();
        inertializer.Begin(
            new[] { from }, new[] { Quaternion.identity }, fromPelvis, Vector3.zero, 0.3f,
            new[] { Previous(from, new Vector3(20f, -12f, 8f), 0.02f) },
            new[] { Quaternion.identity },
            new Vector3(-3f, 2f, 1f), Vector3.zero,
            float.NaN, 0.04f);

        Quaternion outputAtStart = inertializer.Rotation(0);
        inertializer.Advance(0.0001f);
        Vector3 measuredAngularVelocity = AngularVelocityBetween(inertializer.Rotation(0), outputAtStart, 0.0001f);
        NearVector(measuredAngularVelocity, Vector3.zero, 0.04f, "invalid dt angular velocity fallback");
        NearVector((inertializer.PelvisOffset - fromPelvis) / 0.0001f,
            Vector3.zero, 0.01f, "invalid dt pelvis velocity fallback");
    }

    private static void ExistingBeginKeepsLegacyCubicCurve()
    {
        Quaternion from = AxisAngle(new Vector3(0f, 1f, 0f), 0.8f);
        Vector3 pelvisDelta = new Vector3(0.4f, -0.2f, 0.1f);
        Inertializer inertializer = new Inertializer();
        inertializer.Begin(new[] { from }, new[] { Quaternion.identity }, pelvisDelta, Vector3.zero, 0.4f);
        inertializer.Advance(0.2f);

        float legacyWeight = 0.125f;
        SameRotation(inertializer.Rotation(0), Quaternion.Slerp(Quaternion.identity, from, legacyWeight),
            0.0002f, "legacy cubic rotation");
        NearVector(inertializer.PelvisOffset, pelvisDelta * legacyWeight, 0.00001f, "legacy cubic pelvis");
    }

    private static Inertializer Begin(
        Quaternion from,
        Quaternion to,
        Vector3 fromPelvis,
        Vector3 toPelvis,
        Vector3 fromAngularVelocity,
        Vector3 toAngularVelocity,
        Vector3 fromPelvisVelocity,
        Vector3 toPelvisVelocity,
        float historyDt,
        float duration,
        float sourceAge = 0f)
    {
        Inertializer inertializer = new Inertializer();
        inertializer.Begin(
            new[] { from }, new[] { to }, fromPelvis, toPelvis, duration,
            new[] { Previous(from, fromAngularVelocity, historyDt) },
            new[] { Previous(to, toAngularVelocity, historyDt) },
            fromPelvis - fromPelvisVelocity * historyDt,
            toPelvis - toPelvisVelocity * historyDt,
            historyDt,
            sourceAge);
        return inertializer;
    }

    private static Quaternion Previous(Quaternion current, Vector3 angularVelocity, float dt)
    {
        return RotationExp(angularVelocity * -dt) * current;
    }

    private static Quaternion AxisAngle(Vector3 axis, float angle)
    {
        axis = axis / axis.magnitude;
        float half = 0.5f * angle;
        float sin = (float)Math.Sin(half);
        return new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, (float)Math.Cos(half));
    }

    private static Quaternion RotationExp(Vector3 rotationVector)
    {
        float angle = rotationVector.magnitude;
        if (angle < 0.000001f)
            return new Quaternion(rotationVector.x * 0.5f, rotationVector.y * 0.5f, rotationVector.z * 0.5f, 1f);
        return AxisAngle(rotationVector / angle, angle);
    }

    private static Vector3 AngularVelocityBetween(Quaternion next, Quaternion current, float dt)
    {
        Quaternion change = next * Quaternion.Inverse(current);
        if (change.w < 0f)
            change = Negate(change);
        float vectorMagnitude = (float)Math.Sqrt(change.x * change.x + change.y * change.y + change.z * change.z);
        if (vectorMagnitude < 0.0000001f)
            return new Vector3(2f * change.x / dt, 2f * change.y / dt, 2f * change.z / dt);
        float angle = 2f * (float)Math.Atan2(vectorMagnitude, change.w);
        return new Vector3(change.x, change.y, change.z) * (angle / vectorMagnitude / dt);
    }

    private static void ReactionRecoveryPreservesMotionAndReleases()
    {
        var recovery = new ReactionTorsoRecovery();
        Quaternion before = AxisAngle(new Vector3(0f, 1f, 0f), 8f * (float)Math.PI / 180f);
        Quaternion current = AxisAngle(new Vector3(0f, 1f, 0f), 9f * (float)Math.PI / 180f);
        recovery.Track(before, .01f);
        recovery.Track(current, .01f);
        recovery.Release(.5f);
        SameRotation(recovery.Advance(.01f), current, .00001f, "recovery boundary");
        Quaternion next = recovery.Advance(.0001f);
        NearVector(AngularVelocityBetween(next, current, .0001f),
            AngularVelocityBetween(current, before, .01f), .02f, "recovery outgoing velocity");
        recovery.Release(.5f); // no new tracked pose: cannot restart recovery
        recovery.Advance(.6f);
        if (recovery.Active) throw new InvalidOperationException("recovery did not finish");
        SameRotation(recovery.Advance(.01f), Quaternion.identity, .00001f, "recovery neutral");
        recovery.Track(current, .01f);
        recovery.Release(.5f);
        recovery.Cancel();
        recovery.Release(.5f);
        if (recovery.Active) throw new InvalidOperationException("cancel retained recovery history");
        SameRotation(recovery.Advance(float.NaN), Quaternion.identity, .00001f, "cancel neutral");
    }

    private static void BoundedRecoveryNeverOvershoots()
    {
        var recovery = new BoundedRotationRecovery();
        Quaternion native = AxisAngle(new Vector3(.2f, 1f, .1f), .3f);
        Quaternion shown = AxisAngle(new Vector3(1f, .1f, .2f), 2.4f) * native;
        recovery.Begin(new[] { shown }, new[] { native }, .5f);
        SameRotation(recovery.Rotation(0) * native, shown, .00001f, "bounded initial pose");
        float previous = 3.2f;
        for (int i = 0; i < 100; i++)
        {
            recovery.Advance(.005f);
            Quaternion offset = recovery.Rotation(0);
            float angle = AngularVelocityBetween(offset, Quaternion.identity, 1f).magnitude;
            if (angle > previous + .00001f || angle > 2.4001f)
                throw new InvalidOperationException("Recovery overshot its captured rotation");
            previous = angle;
        }
        recovery.Advance(.01f);
        SameRotation(recovery.Rotation(0), Quaternion.identity, .00001f, "bounded endpoint");
        recovery.Cancel();
        if (recovery.Active) throw new InvalidOperationException("Bounded recovery did not cancel");
    }

    private static void UpperOverlayPreservesNativeBaselineAndRebasesChest()
    {
        var pelvis = AxisAngle(new Vector3(1, .2f, .4f), .6f);
        var chest = AxisAngle(new Vector3(.1f, 1, .5f), .4f);
        var sourcePelvis = AxisAngle(new Vector3(.5f, .2f, 1), .8f);
        var sourceChest = AxisAngle(new Vector3(1, .1f, .1f), .2f);
        var delta = UpperReactionMath.ChestDelta(sourcePelvis, sourceChest, sourcePelvis, sourceChest, pelvis);
        SameRotation(UpperReactionMath.Apply(chest, delta, .65f), chest, .00001f, "source reference preserves native chest");
        var impact = AxisAngle(new Vector3(.3f, 1, .5f), .5f);
        delta = UpperReactionMath.ChestDelta(impact * sourcePelvis, sourceChest, sourcePelvis, sourceChest, pelvis);
        SameRotation(pelvis * UpperReactionMath.Apply(chest, delta, 1), impact * pelvis * chest, .00001f, "chest impact remains in root space");
        SameRotation(UpperReactionMath.Apply(chest, delta, 0), chest, .00001f, "zero intensity retains native");
        SameRotation(UpperReactionMath.Apply(chest, UpperReactionMath.Delta(impact * sourceChest, sourceChest), .5f),
            Quaternion.Slerp(Quaternion.identity, impact, .5f) * chest, .00001f, "partial arm amplitude");
    }

    private static void LandingRecoveryNeedsOneRealMovingJump()
    {
        var gate = new LandingRecoveryGate();
        gate.Observe(1, true, false, false, 2); // landing alone
        gate.Observe(1.3f, false, false, false, 2);
        if (gate.Pending(1.3f)) throw new Exception("Landing without Jump armed recovery");
        gate.Observe(2, true, true, false, 2);
        gate.Observe(2.2f, true, false, false, 2); // JumpLanding still owns
        if (gate.Pending(2.2f)) throw new Exception("Recovery began during native landing");
        gate.Observe(2.4f, false, false, false, 2);
        if (!gate.Pending(2.5f) || gate.Pending(2.71f)) throw new Exception("Landing admission window incorrect");
        gate.Consume();
        gate.Observe(2.6f, false, false, false, 2);
        if (gate.Pending(2.6f)) throw new Exception("Landing fired twice");
        gate.Observe(3, true, true, false, 2);
        gate.Observe(3.1f, true, true, true, 2); // managed jump plus traversal
        gate.Observe(3.4f, false, false, false, 2);
        if (gate.Pending(3.4f)) throw new Exception("Traversal retained stale jump");
        gate.Observe(4, true, true, false, .2f);
        gate.Observe(4.4f, false, false, false, 2);
        if (gate.Pending(4.4f)) throw new Exception("Standing takeoff admitted moving recovery");
        gate.Observe(5, true, true, false, 2);
        gate.Observe(5.05f, false, false, false, 2);
        if (gate.Pending(5.05f)) throw new Exception("Transient jump admitted recovery");
        gate.Reset();
    }

    private static Quaternion Negate(Quaternion rotation)
    {
        return new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
    }

    private static void SameRotation(Quaternion actual, Quaternion expected, float tolerance, string label)
    {
        Quaternion difference = actual * Quaternion.Inverse(expected);
        if (difference.w < 0f)
            difference = Negate(difference);
        float vectorMagnitude = (float)Math.Sqrt(difference.x * difference.x + difference.y * difference.y + difference.z * difference.z);
        float angle = 2f * (float)Math.Atan2(vectorMagnitude, Math.Abs(difference.w));
        if (angle > tolerance)
            throw new InvalidOperationException(label + " differed by " + angle + " radians");
    }

    private static void NearVector(Vector3 actual, Vector3 expected, float tolerance, string label)
    {
        float error = (actual - expected).magnitude;
        if (error > tolerance)
            throw new InvalidOperationException(label + " differed by " + error + "; actual=" + actual + ", expected=" + expected);
    }
}
