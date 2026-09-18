using System;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            HipAnkleIdentityKeepsAuthoredRotation();
            HipAnkleSwingUsesOriginalFootRotation();
            ContactSoleFollowsNearFlatSurface();
            AirborneSoleKeepsAuthoredTiltAndPartialContactBlends();
            BadSurfaceNormalsReturnAuthoredRotation();
            AnkleAngleClampUsesAuthoredLocalReference();
            AnkleAngleWithinLimitIsUnchanged();
            TargetRotationSolverIsExplicitAndPreservesRequest();
            Console.WriteLine("runtime_ankle_alignment: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void HipAnkleIdentityKeepsAuthoredRotation()
    {
        Quaternion authored = Quaternion.Euler(20f, 35f, -12f);
        var result = AnkleAlignment.ComputeHipAnkleSwing(
            P(0f, 1f, 0f), P(0f, 0f, 1f),
            P(0f, 1f, 0f), P(0f, 0f, 1f), authored);

        if (!result.Valid)
            throw new InvalidOperationException("identity hip-to-ankle input should be valid");
        Near(result.AngleDegrees, 0f, "identity swing angle");
        SameRotation(result.TargetFootRotation, authored, "identity authored foot rotation");
    }

    private static void HipAnkleSwingUsesOriginalFootRotation()
    {
        Quaternion authored = Quaternion.Euler(20f, 35f, -12f);
        var result = AnkleAlignment.ComputeHipAnkleSwing(
            P(0f, 0f, 0f), P(0f, 0f, 1f),
            P(2f, 1f, 3f), P(3f, 1f, 3f), authored);

        if (!result.Valid)
            throw new InvalidOperationException("rotated hip-to-ankle input should be valid");
        Near(result.AngleDegrees, 90f, "rotated chain swing angle");
        SameRotation(result.TargetFootRotation, result.Swing * authored, "swing must use original authored foot rotation");
        NearVector(result.Swing * result.OriginalDirection, result.TargetDirection, "swing retarget direction");
    }

    private static void ContactSoleFollowsNearFlatSurface()
    {
        Quaternion authored = Quaternion.Euler(22f, 30f, -8f);
        Vector3 authoredNormal = authored * Vector3.up;
        Vector3 heelToToe = authored * Vector3.forward;
        Vector3 surface = new Vector3(0.04f, 0.9992f, 0.01f).normalized;
        var result = AnkleAlignment.AlignSoleToSurface(
            authored, heelToToe, authoredNormal, surface, P(1f, 0f, 0f), 1f);

        if (!result.Valid || result.UsedFallbackHeading)
            throw new InvalidOperationException("valid contacting sole alignment should use supplied heading");
        NearVector(result.TargetRotation * Vector3.up, surface, "contact surface normal");
        Vector3 horizontalHeading = Project(result.TargetRotation * Vector3.forward, Vector3.up).normalized;
        NearVector(horizontalHeading, Vector3.right, "contact requested world heading", 0.001f);
    }

    private static void AirborneSoleKeepsAuthoredTiltAndPartialContactBlends()
    {
        Quaternion authored = Quaternion.Euler(28f, -20f, 11f);
        Vector3 authoredNormal = authored * Vector3.up;
        Vector3 heelToToe = authored * Vector3.forward;
        Vector3 surface = Vector3.up;

        var airborne = AnkleAlignment.AlignSoleToSurface(
            authored, heelToToe, authoredNormal, surface, Vector3.forward, 0f);
        SameRotation(airborne.TargetRotation, authored, "airborne authored sole tilt");

        var partial = AnkleAlignment.AlignSoleToSurface(
            authored, heelToToe, authoredNormal, surface, Vector3.forward, 0.5f);
        float fullCorrection = Quaternion.Angle(authored, AnkleAlignment.AlignSoleToSurface(
            authored, heelToToe, authoredNormal, surface, Vector3.forward, 1f).TargetRotation);
        float partialCorrection = Quaternion.Angle(authored, partial.TargetRotation);
        if (!(partialCorrection > 0.01f && partialCorrection < fullCorrection - 0.01f))
            throw new InvalidOperationException("contact weight should blend authored tilt toward flatness");
    }

    private static void BadSurfaceNormalsReturnAuthoredRotation()
    {
        Quaternion authored = Quaternion.Euler(15f, 4f, 9f);
        Vector3 heading = authored * Vector3.forward;
        Vector3 normal = authored * Vector3.up;
        var zero = AnkleAlignment.AlignSoleToSurface(authored, heading, normal, Vector3.zero, Vector3.forward, 1f);
        if (zero.Valid)
            throw new InvalidOperationException("zero surface normal must be rejected");
        SameRotation(zero.TargetRotation, authored, "zero normal fallback rotation");

        var nan = AnkleAlignment.AlignSoleToSurface(authored, heading, normal, new Vector3(float.NaN, 1f, 0f), Vector3.forward, 1f);
        if (nan.Valid)
            throw new InvalidOperationException("NaN surface normal must be rejected");
        SameRotation(nan.TargetRotation, authored, "NaN normal fallback rotation");
    }

    private static void AnkleAngleClampUsesAuthoredLocalReference()
    {
        Quaternion calf = Quaternion.Euler(0f, 30f, 0f);
        Quaternion authoredLocal = Quaternion.Euler(8f, -4f, 3f);
        Quaternion requestedLocal = authoredLocal * Quaternion.Euler(0f, 70f, 0f);
        Quaternion requestedWorld = calf * requestedLocal;
        var result = AnkleAlignment.ConstrainAnkleAngle(calf, authoredLocal, requestedWorld, 35f);

        if (!result.Valid || !result.Limited)
            throw new InvalidOperationException("over-limit ankle angle should be clamped");
        if (!(result.RequestedAngleDegrees > 60f && result.AppliedAngleDegrees <= 35.01f))
            throw new InvalidOperationException("ankle angle clamp amount");
        Quaternion targetLocalDelta = Quaternion.Inverse(authoredLocal) * result.TargetLocalRotation;
        if (Quaternion.Angle(Quaternion.identity, targetLocalDelta) > 35.01f)
            throw new InvalidOperationException("target local rotation exceeds ankle limit");
        SameRotation(result.TargetRotation, calf * result.TargetLocalRotation, "world target recomposition");
    }

    private static void AnkleAngleWithinLimitIsUnchanged()
    {
        Quaternion calf = Quaternion.Euler(-10f, 15f, 2f);
        Quaternion authoredLocal = Quaternion.Euler(4f, 2f, -5f);
        Quaternion requestedWorld = calf * (authoredLocal * Quaternion.Euler(0f, 12f, 0f));
        var result = AnkleAlignment.ConstrainAnkleAngle(calf, authoredLocal, requestedWorld, 35f);

        if (!result.Valid || result.Limited)
            throw new InvalidOperationException("within-limit ankle angle should remain unchanged");
        SameRotation(result.TargetRotation, requestedWorld, "within-limit world target");
    }

    private static void TargetRotationSolverIsExplicitAndPreservesRequest()
    {
        Transform thigh = new Transform { position = P(0f, 1f, 0f), rotation = Quaternion.identity };
        Transform calf = new Transform { position = P(0f, 0.5f, 0.2f), rotation = Quaternion.identity };
        Transform foot = new Transform { position = P(0f, 0f, 0.4f), rotation = Quaternion.identity };
        Quaternion requested = Quaternion.Euler(10f, 20f, -5f);
        var result = LegIk.SolveTowardWithTargetRotation(
            thigh, calf, foot, P(0.1f, 0.1f, 0.35f), P(1f, 0f, 0f), Vector3.right, requested);

        if (!result.Solved)
            throw new InvalidOperationException("valid second-pass leg solve should report solved");
        SameRotation(foot.rotation, requested, "opt-in solver target rotation");
    }

    private static Vector3 P(float x, float y, float z) => new Vector3(x, y, z);

    private static Vector3 Project(Vector3 value, Vector3 normal)
    {
        Vector3 unit = normal.normalized;
        return value - unit * Vector3.Dot(value, unit);
    }

    private static void NearVector(Vector3 actual, Vector3 expected, string label, float tolerance = 0.0002f)
    {
        if ((actual - expected).magnitude > tolerance)
            throw new InvalidOperationException(label + ": expected " + Format(expected) + ", got " + Format(actual));
    }

    private static void SameRotation(Quaternion actual, Quaternion expected, string label)
    {
        if (Quaternion.Angle(actual, expected) > 0.02f)
            throw new InvalidOperationException(label + ": rotation mismatch");
    }

    private static void Near(float actual, float expected, string label)
    {
        if (Math.Abs(actual - expected) > 0.02f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }

    private static string Format(Vector3 value) => "(" + value.x + "," + value.y + "," + value.z + ")";
}
