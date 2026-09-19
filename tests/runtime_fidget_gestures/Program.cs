using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EFT;
using Manimal.MotionMatching;
using Newtonsoft.Json.Linq;
using RootMotion.FinalIK;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            MathMapsCanonicalRotationThroughAnArbitraryLocalBasis();
            MathBuildsFramesAndRejectsDegenerateBases();
            ClipLoadsTheEmbedded45FrameResource();
            ClipLoadsAllEmbeddedActionsAndMatchesWeaponClips();
            ClipLoadsAllWeaponSetResourcePairs();
            ClipInterpolatesHandAndFingerTracks();
            ClipRejectsMalformedCountsAndDuplicateKeys();
            ClipRejectsInvalidValuesAndEndpoints();
            HandRigAppliesResolvedLocalDeltasAndRestoresWithoutAccumulation();
            HandRigPreservesExternalWritesAndBoneLengths();
            HandRigUsesAndRestoresAResolvedSolverTargetWithoutEditingIt();
            Console.WriteLine("runtime_fidget_gestures: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void MathMapsCanonicalRotationThroughAnArbitraryLocalBasis()
    {
        Quaternion map = Quaternion.Euler(23f, -41f, 17f);
        Quaternion canonicalDelta = Quaternion.Euler(-12f, 34f, 27f);
        Quaternion localDelta = FidgetGestureMath.ToLocalDelta(map, canonicalDelta);

        Vector3 canonicalDirection = new Vector3(.23f, -.61f, .74f).normalized;
        Vector3 expectedLocalDirection = map * (canonicalDelta * canonicalDirection);
        Vector3 actualLocalDirection = localDelta * (map * canonicalDirection);
        Near(actualLocalDirection, expectedLocalDirection, 1e-4f, "canonical rotation conjugation");

        Quaternion equivalent = map * canonicalDelta * Quaternion.Inverse(map);
        NearAngle(localDelta, equivalent, .001f, "canonical rotation conjugation quaternion");
    }

    private static void MathBuildsFramesAndRejectsDegenerateBases()
    {
        Vector3 forward = new Vector3(.31f, .87f, -.22f).normalized;
        Vector3 across = new Vector3(.94f, -.11f, .26f).normalized;
        Check(FidgetGestureMath.TryPalmFrame(forward, across, out Quaternion palmFrame), "valid palm frame rejected");

        Vector3 expectedAcross = (across - forward * Vector3.Dot(across, forward)).normalized;
        Near(palmFrame * Vector3.up, forward, 1e-4f, "palm frame forward axis");
        Near(palmFrame * Vector3.right, expectedAcross, 1e-4f, "palm frame across axis");
        Near(palmFrame * Vector3.forward, Vector3.Cross(expectedAcross, forward).normalized,
            1e-4f, "palm frame normal axis");

        Vector3 palmNormal = Vector3.Cross(expectedAcross, forward).normalized;
        Check(FidgetGestureMath.TryFingerFrame(forward, across, palmNormal, false, out Quaternion fingerFrame),
            "valid non-thumb finger frame rejected");
        Near(fingerFrame * Vector3.up, forward, 1e-4f, "finger frame longitudinal axis");

        Vector3 thumbAlong = new Vector3(.42f, .74f, -.52f).normalized;
        Vector3 thumbNormal = Vector3.Cross(thumbAlong, new Vector3(.3f, -.2f, .8f)).normalized;
        Check(FidgetGestureMath.TryFingerFrame(thumbAlong, across, thumbNormal, true, out Quaternion thumbFrame),
            "valid thumb frame rejected");
        Near(thumbFrame * Vector3.up, thumbAlong, 1e-4f, "thumb frame longitudinal axis");
        Near(thumbFrame * Vector3.forward,
            (thumbNormal - thumbAlong * Vector3.Dot(thumbNormal, thumbAlong)).normalized,
            1e-4f, "thumb frame normal axis");

        Check(!FidgetGestureMath.TryPalmFrame(Vector3.zero, across, out _), "zero palm forward accepted");
        Check(!FidgetGestureMath.TryPalmFrame(forward, forward * 3f, out _), "parallel palm basis accepted");
        Check(!FidgetGestureMath.TryPalmFrame(new Vector3(float.NaN, 0f, 0f), across, out _),
            "non-finite palm basis accepted");
        Check(!FidgetGestureMath.TryFingerFrame(Vector3.zero, across, palmNormal, false, out _),
            "zero finger axis accepted");
        Check(!FidgetGestureMath.TryFingerFrame(forward, across, forward, true, out _),
            "parallel thumb normal accepted");
        Check(!FidgetGestureMath.TryFingerFrame(forward, across, new Vector3(float.PositiveInfinity, 0f, 0f), true, out _),
            "non-finite thumb basis accepted");
    }

    private static void ClipLoadsTheEmbedded45FrameResource()
    {
        FidgetGestureClip clip = FidgetGestureClip.Load();
        Near(clip.Duration, 44f / 60f, 1e-5f, "embedded gesture duration");

        clip.SampleHand(0f, out Vector3 startPosition, out Quaternion startRotation);
        clip.SampleHand(clip.Duration, out Vector3 endPosition, out Quaternion endRotation);
        Near(startPosition, Vector3.zero, 2e-5f, "embedded hand start position");
        Near(endPosition, Vector3.zero, 2e-5f, "embedded hand end position");
        NearAngle(startRotation, Quaternion.identity, .01f, "embedded hand start rotation");
        NearAngle(endRotation, Quaternion.identity, .01f, "embedded hand end rotation");

        bool hasHandMotion = false;
        bool hasFingerMotion = false;
        for (int digit = 0; digit < 5; digit++)
        {
            for (int segment = 0; segment < 3; segment++)
            {
                Quaternion first = clip.SampleFinger(digit, segment, 0f);
                Quaternion last = clip.SampleFinger(digit, segment, clip.Duration);
                NearAngle(first, Quaternion.identity, .01f, $"embedded finger {digit}/{segment} start");
                NearAngle(last, Quaternion.identity, .01f, $"embedded finger {digit}/{segment} end");
                if (Quaternion.Angle(first, clip.SampleFinger(digit, segment, .2f)) > .01f)
                    hasFingerMotion = true;
            }
        }
        clip.SampleHand(.2f, out Vector3 interiorPosition, out Quaternion interiorRotation);
        hasHandMotion = interiorPosition.sqrMagnitude > 1e-8f || Quaternion.Angle(interiorRotation, Quaternion.identity) > .01f;
        Check(hasHandMotion, "embedded hand track contains no interior motion");
        Check(hasFingerMotion, "embedded finger tracks contain no interior motion");
    }

    private static void ClipLoadsAllEmbeddedActionsAndMatchesWeaponClips()
    {
        string[] actions = { "fidget1", "fidget2", "fidget3" };
        foreach (string action in actions)
        {
            FidgetGestureClip gesture = FidgetGestureClip.Load(action);
            FidgetClip weapon = FidgetClip.Load(action);
            JObject gestureMetadata = ReadEmbeddedJson($"Manimal.MotionMatching.Data.{action}.gesture.json");
            JObject weaponMetadata = ReadEmbeddedJson($"Manimal.MotionMatching.Data.{action}.weapon.json");

            JArray handSamples = gestureMetadata["hand"]?["samples"] as JArray;
            JArray weaponSamples = weaponMetadata["samples"] as JArray;
            Check(handSamples != null && handSamples.Count >= 2, action + " gesture sample range is missing");
            Check(weaponSamples != null && weaponSamples.Count >= 2, action + " weapon sample range is missing");
            Check(handSamples.Count == weaponSamples.Count,
                action + " gesture and weapon sample counts differ");

            float gestureFps = (float?)gestureMetadata["fps"] ?? 0f;
            float weaponFps = (float?)weaponMetadata["fps"] ?? 0f;
            Check(Finite(gestureFps) && gestureFps > 0f, action + " gesture fps is invalid");
            Check(Finite(weaponFps) && weaponFps > 0f, action + " weapon fps is invalid");
            float expectedDuration = (handSamples.Count - 1) / gestureFps;
            Near(gesture.Duration, expectedDuration, 1e-5f, action + " gesture duration");
            Near(weapon.Duration, (weaponSamples.Count - 1) / weaponFps, 1e-5f,
                action + " weapon duration");
            Near(gesture.Duration, weapon.Duration, 1e-5f,
                action + " gesture/weapon duration match");

            Check(FrameRangeMatchesSamples(gestureMetadata, handSamples.Count),
                action + " gesture source frame range does not match samples");
            Check(FrameRangeMatchesSamples(weaponMetadata, weaponSamples.Count),
                action + " weapon source frame range does not match samples");
            Near((float?)gestureMetadata["durationSeconds"] ?? float.NaN, gesture.Duration,
                1e-5f, action + " gesture metadata duration");
            Near((float?)weaponMetadata["durationSeconds"] ?? float.NaN, weapon.Duration,
                1e-5f, action + " weapon metadata duration");

            gesture.SampleHand(-1f, out Vector3 startPosition, out Quaternion startRotation);
            gesture.SampleHand(gesture.Duration + 1f, out Vector3 endPosition, out Quaternion endRotation);
            Near(startPosition, Vector3.zero, 2e-5f, action + " gesture clamped start position");
            Near(endPosition, Vector3.zero, 2e-5f, action + " gesture clamped end position");
            NearAngle(startRotation, Quaternion.identity, .01f, action + " gesture clamped start rotation");
            NearAngle(endRotation, Quaternion.identity, .01f, action + " gesture clamped end rotation");

            for (int digit = 0; digit < 5; digit++)
            {
                for (int segment = 0; segment < 3; segment++)
                {
                    NearAngle(gesture.SampleFinger(digit, segment, 0f), Quaternion.identity, .01f,
                        $"{action} finger {digit}/{segment} start");
                    NearAngle(gesture.SampleFinger(digit, segment, gesture.Duration), Quaternion.identity, .01f,
                        $"{action} finger {digit}/{segment} end");
                }
            }

            bool hasHandMotion = false;
            bool hasFingerMotion = false;
            for (int frame = 1; frame < handSamples.Count - 1; frame++)
            {
                float seconds = frame / gestureFps;
                gesture.SampleHand(seconds, out Vector3 interiorPosition, out Quaternion interiorRotation);
                Check(Finite(interiorPosition) && Finite(interiorRotation),
                    action + " interior hand sample is non-finite");
                hasHandMotion |= interiorPosition.sqrMagnitude > 1e-8f ||
                    Quaternion.Angle(interiorRotation, Quaternion.identity) > .01f;

                for (int digit = 0; digit < 5; digit++)
                {
                    for (int segment = 0; segment < 3; segment++)
                        hasFingerMotion |= Quaternion.Angle(gesture.SampleFinger(digit, segment, seconds),
                            Quaternion.identity) > .01f;
                }
            }
            Check(hasFingerMotion || hasHandMotion,
                action + " contains no interior gesture motion");
        }
    }

    private static void ClipLoadsAllWeaponSetResourcePairs()
    {
        (string Group, string Action, int SampleCount)[] cases =
        {
            ("", "fidget1", 141),
            ("", "fidget2", 45),
            ("", "fidget3", 79),
            ("mp5", "fidget", 101),
            ("mp5", "fidget2", 57),
            ("mp5", "fidget3", 45),
            ("sniper", "fidget1", 141),
            ("sniper", "fidget2", 67),
            ("sniper", "fidget3", 73),
            ("tau", "fidget 1", 63),
            ("tau", "fidget 2", 73),
            ("tau", "fidget 3", 69),
        };

        foreach ((string group, string action, int sampleCount) in cases)
        {
            FidgetGestureClip gesture = FidgetGestureClip.Load(action, group);
            FidgetClip weapon = FidgetClip.Load(action, group);
            string resourcePrefix = string.IsNullOrEmpty(group) ? "" : group + ".";
            JObject gestureMetadata = ReadEmbeddedJson($"Manimal.MotionMatching.Data.{resourcePrefix}{action}.gesture.json");
            JObject weaponMetadata = ReadEmbeddedJson($"Manimal.MotionMatching.Data.{resourcePrefix}{action}.weapon.json");

            JArray handSamples = gestureMetadata["hand"]?["samples"] as JArray;
            JArray weaponSamples = weaponMetadata["samples"] as JArray;
            Check(handSamples != null && handSamples.Count == sampleCount,
                $"{group}/{action} gesture sample count");
            Check(weaponSamples != null && weaponSamples.Count == sampleCount,
                $"{group}/{action} weapon sample count");

            float gestureFps = (float?)gestureMetadata["fps"] ?? 0f;
            float weaponFps = (float?)weaponMetadata["fps"] ?? 0f;
            Near(gestureFps, 60f, 1e-5f, $"{group}/{action} gesture fps");
            Near(weaponFps, 60f, 1e-5f, $"{group}/{action} weapon fps");
            Near(gesture.Duration, (sampleCount - 1) / 60f, 1e-5f,
                $"{group}/{action} gesture duration");
            Near(weapon.Duration, (sampleCount - 1) / 60f, 1e-5f,
                $"{group}/{action} weapon duration");
            Near(gesture.Duration, weapon.Duration, 1e-5f,
                $"{group}/{action} gesture/weapon duration match");

            gesture.SampleHand(0f, out Vector3 gestureStartPosition, out Quaternion gestureStartRotation);
            gesture.SampleHand(gesture.Duration, out Vector3 gestureEndPosition, out Quaternion gestureEndRotation);
            Near(gestureStartPosition, Vector3.zero, 2e-5f, $"{group}/{action} gesture start position");
            Near(gestureEndPosition, Vector3.zero, 2e-5f, $"{group}/{action} gesture end position");
            NearAngle(gestureStartRotation, Quaternion.identity, .01f, $"{group}/{action} gesture start rotation");
            NearAngle(gestureEndRotation, Quaternion.identity, .01f, $"{group}/{action} gesture end rotation");

            weapon.Sample(0f, out Vector3 weaponStartPosition, out Quaternion weaponStartRotation);
            weapon.Sample(weapon.Duration, out Vector3 weaponEndPosition, out Quaternion weaponEndRotation);
            Near(weaponStartPosition, Vector3.zero, 2e-5f, $"{group}/{action} weapon start position");
            Near(weaponEndPosition, Vector3.zero, 2e-5f, $"{group}/{action} weapon end position");
            NearAngle(weaponStartRotation, Quaternion.identity, .01f, $"{group}/{action} weapon start rotation");
            NearAngle(weaponEndRotation, Quaternion.identity, .01f, $"{group}/{action} weapon end rotation");
        }
    }

    private static JObject ReadEmbeddedJson(string resourceName)
    {
        using Stream stream = typeof(FidgetGestureClip).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("Embedded test resource is missing: " + resourceName);
        using var reader = new StreamReader(stream);
        return JObject.Parse(reader.ReadToEnd());
    }

    private static bool FrameRangeMatchesSamples(JObject metadata, int sampleCount)
    {
        JArray frameRange = metadata["sourceFrameRange"] as JArray;
        return frameRange != null && frameRange.Count == 2 &&
            (int)frameRange[1] - (int)frameRange[0] + 1 == sampleCount;
    }

    private static void ClipInterpolatesHandAndFingerTracks()
    {
        FidgetGestureClip clip = FidgetGestureClip.Parse(BuildJson());
        Near(clip.Duration, 1f, 1e-5f, "synthetic gesture duration");

        clip.SampleHand(-1f, out Vector3 beforePosition, out Quaternion beforeRotation);
        Near(beforePosition, Vector3.zero, 1e-5f, "hand start clamp position");
        NearAngle(beforeRotation, Quaternion.identity, .001f, "hand start clamp rotation");

        clip.SampleHand(.25f, out Vector3 middlePosition, out Quaternion middleRotation);
        Near(middlePosition, new Vector3(1f, -.5f, .25f), 1e-4f, "hand interpolation position");
        NearAngle(middleRotation, Quaternion.AngleAxis(22.5f, Vector3.forward), .01f,
            "hand interpolation rotation");

        clip.SampleHand(2f, out Vector3 afterPosition, out Quaternion afterRotation);
        Near(afterPosition, Vector3.zero, 1e-5f, "hand end clamp position");
        NearAngle(afterRotation, Quaternion.identity, .001f, "hand end clamp rotation");

        Quaternion fingerStart = clip.SampleFinger(2, 1, 0f);
        Quaternion fingerMiddle = clip.SampleFinger(2, 1, .25f);
        Quaternion fingerEnd = clip.SampleFinger(2, 1, 1f);
        NearAngle(fingerStart, Quaternion.identity, .001f, "finger start sample");
        NearAngle(fingerMiddle, Quaternion.AngleAxis(22.5f, Vector3.right), .01f,
            "finger interpolation rotation");
        NearAngle(fingerEnd, Quaternion.identity, .001f, "finger end sample");

        AssertThrows<ArgumentOutOfRangeException>(() => clip.SampleFinger(-1, 0, .2f), "negative finger digit");
        AssertThrows<ArgumentOutOfRangeException>(() => clip.SampleFinger(5, 0, .2f), "excessive finger digit");
        AssertThrows<ArgumentOutOfRangeException>(() => clip.SampleFinger(0, 3, .2f), "excessive finger segment");
    }

    private static void ClipRejectsMalformedCountsAndDuplicateKeys()
    {
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(schemaVersion: 2)), "schema version");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(coordinateSystem: "unity-camera")),
            "coordinate system");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(fps: 0)), "zero fps");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(fps: -1)), "negative fps");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(fps: 1001)), "excessive fps");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(handSampleCount: 1)),
            "one hand sample");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(fingerTrackCount: 14)),
            "incomplete finger tracks");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(duplicateFingerKey: true)),
            "duplicate finger key");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(invalidFingerKey: true)),
            "out-of-range finger key");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(fingerSampleCount: 2)),
            "mismatched finger sample count");
    }

    private static void ClipRejectsInvalidValuesAndEndpoints()
    {
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(malformedPosition: true)),
            "malformed hand position");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(nonFinitePosition: true)),
            "non-finite hand position");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(nonUnitRotation: true)),
            "non-unit hand rotation");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(nonNeutralStart: true)),
            "non-neutral gesture start");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse(BuildJson(nonNeutralEnd: true)),
            "non-neutral gesture end");
        AssertThrows<InvalidDataException>(() => FidgetGestureClip.Parse("not json"), "invalid JSON");
    }

    private static void HandRigAppliesResolvedLocalDeltasAndRestoresWithoutAccumulation()
    {
        Fixture fixture = CreateFixture();
        FidgetGestureClip clip = FidgetGestureClip.Parse(BuildJson());
        FidgetHandRig rig = FidgetHandRig.Capture(fixture.Player);
        Quaternion[] initialRotations = SnapshotRotations(fixture.Fingers);
        Vector3[] initialLengths = SnapshotLengths(fixture.Fingers);

        float sampleTime = .5f;
        Quaternion palmFrame = CapturePalmFrame(fixture);
        for (int i = 0; i < fixture.Fingers.Length; i++)
        {
            int digit = i / 3, segment = i % 3;
            Quaternion frame = CaptureFingerFrame(fixture, i, palmFrame);
            Quaternion map = Quaternion.Inverse(fixture.Fingers[i].rotation) * frame;
            Quaternion source = clip.SampleFinger(digit, segment, sampleTime);
            Quaternion expectedDelta = FidgetGestureMath.ToLocalDelta(map, source);
            fixture.ExpectedFingerRotations[i] = initialRotations[i] * expectedDelta;
        }

        rig.ApplyFingers(clip, sampleTime, 1f);
        for (int i = 0; i < fixture.Fingers.Length; i++)
            NearAngle(fixture.Fingers[i].localRotation, fixture.ExpectedFingerRotations[i], .01f,
                $"resolved local finger delta {i}");
        CheckLengths(fixture.Fingers, initialLengths, "finger lengths after first apply");

        rig.Restore();
        CheckRotations(fixture.Fingers, initialRotations, "finger restore after first apply");
        CheckLengths(fixture.Fingers, initialLengths, "finger lengths after first restore");

        rig.ApplyFingers(clip, sampleTime, 1f);
        CheckRotations(fixture.Fingers, fixture.ExpectedFingerRotations, "repeated finger apply");
        rig.Restore();
        CheckRotations(fixture.Fingers, initialRotations, "repeated finger restore");
    }

    private static void HandRigPreservesExternalWritesAndBoneLengths()
    {
        Fixture fixture = CreateFixture();
        FidgetGestureClip clip = FidgetGestureClip.Parse(BuildJson());
        FidgetHandRig rig = FidgetHandRig.Capture(fixture.Player);
        Quaternion[] initialRotations = SnapshotRotations(fixture.Fingers);
        Vector3[] initialLengths = SnapshotLengths(fixture.Fingers);

        rig.ApplyFingers(clip, .5f, 1f);
        Quaternion external = Quaternion.Euler(-19f, 31f, 7f);
        fixture.Fingers[7].localRotation = external;
        rig.Restore();
        NearAngle(fixture.Fingers[7].localRotation, external, .001f, "external finger overwrite");
        for (int i = 0; i < fixture.Fingers.Length; i++)
        {
            if (i == 7) continue;
            NearAngle(fixture.Fingers[i].localRotation, initialRotations[i], .001f,
                $"restored untouched finger {i}");
        }
        CheckLengths(fixture.Fingers, initialLengths, "finger lengths after external overwrite");
    }

    private static void HandRigUsesAndRestoresAResolvedSolverTargetWithoutEditingIt()
    {
        Fixture fixture = CreateFixture();
        FidgetGestureClip clip = FidgetGestureClip.Parse(BuildJson());
        FidgetHandRig rig = FidgetHandRig.Capture(fixture.Player);
        IKSolverLimb solver = fixture.Limb.solver;

        Vector3 targetPosition = fixture.Target.position;
        Quaternion targetRotation = fixture.Target.rotation;
        // The resolved target supplies the overlay pose, while the numeric IK
        // fields remain the baseline that RestoreHandTarget must recover.
        Vector3 baselinePosition = solver.IKPosition;
        Quaternion baselineRotation = solver.IKRotation;
        Vector3[] baselineLengths = SnapshotLengths(fixture.Fingers);

        Quaternion palmFrame = CapturePalmFrame(fixture);
        Quaternion map = Quaternion.Inverse(fixture.Palm.rotation) * palmFrame;
        clip.SampleHand(.5f, out Vector3 handPosition, out Quaternion handRotation);
        Quaternion expectedRotation = targetRotation * FidgetGestureMath.ToLocalDelta(map, handRotation);
        Vector3 expectedPosition = targetPosition + targetRotation * (map * handPosition) * .05f;

        rig.ApplyHand(clip, .5f, 1f, .05f);
        Check(solver.target == null, "numeric IK target was not temporarily detached");
        Near(solver.IKPosition, expectedPosition, 1e-4f, "resolved hand target position");
        NearAngle(solver.IKRotation, expectedRotation, .01f, "resolved hand target rotation");
        Near(fixture.Target.position, targetPosition, 1e-5f, "grip target position was edited");
        NearAngle(fixture.Target.rotation, targetRotation, .001f, "grip target rotation was edited");

        rig.RestoreHandTarget();
        Check(solver.target == fixture.Target, "resolved grip target was not restored");
        Near(solver.IKPosition, baselinePosition, 1e-5f, "numeric hand target position restore");
        NearAngle(solver.IKRotation, baselineRotation, .001f, "numeric hand target rotation restore");
        Near(fixture.Target.position, targetPosition, 1e-5f, "grip target changed during restore");
        NearAngle(fixture.Target.rotation, targetRotation, .001f, "grip rotation changed during restore");
        CheckLengths(fixture.Fingers, baselineLengths, "finger lengths during hand target apply");

        rig.ApplyHand(clip, .5f, 1f, .05f);
        Vector3 externalPosition = new Vector3(-2f, 4f, .75f);
        Quaternion externalRotation = Quaternion.Euler(4f, -12f, 28f);
        Transform externalTarget = new Transform("ExternalTarget");
        externalTarget.position = new Vector3(9f, -1f, 2f);
        externalTarget.rotation = Quaternion.Euler(13f, 8f, -6f);
        solver.SetIKPosition(externalPosition);
        solver.SetIKRotation(externalRotation);
        solver.target = externalTarget;
        rig.RestoreHandTarget();
        Check(solver.target == externalTarget, "external solver target was overwritten");
        Near(solver.IKPosition, externalPosition, 1e-5f, "external IK position overwrite");
        NearAngle(solver.IKRotation, externalRotation, .001f, "external IK rotation overwrite");
    }

    private static Quaternion CapturePalmFrame(Fixture fixture)
    {
        Check(FidgetGestureMath.TryPalmFrame(
            fixture.Fingers[6].position - fixture.Palm.position,
            fixture.Fingers[12].position - fixture.Fingers[3].position,
            out Quaternion frame), "fixture palm frame invalid");
        return frame;
    }

    private static Quaternion CaptureFingerFrame(Fixture fixture, int index, Quaternion palmFrame)
    {
        int digit = index / 3, segment = index % 3;
        Vector3 across = fixture.Fingers[12].position - fixture.Fingers[3].position;
        Vector3 palmNormal = palmFrame * Vector3.forward;
        Vector3 along = segment < 2
            ? fixture.Fingers[index + 1].position - fixture.Fingers[index].position
            : fixture.Fingers[index].rotation * fixture.Fingers[index].localPosition.normalized;
        Check(FidgetGestureMath.TryFingerFrame(along, across, palmNormal, digit == 0, out Quaternion frame),
            $"fixture finger frame invalid {digit}/{segment}");
        return frame;
    }

    private static Fixture CreateFixture()
    {
        Transform root = new Transform("Armature")
        {
            localPosition = new Vector3(1.2f, -0.8f, 2.4f),
            localRotation = Quaternion.Euler(17f, -29f, 11f)
        };
        Transform palm = new Transform("LeftPalm")
        {
            parent = root,
            localPosition = new Vector3(.08f, .03f, -.14f),
            localRotation = Quaternion.Euler(-7f, 19f, -13f)
        };

        var bones = new Transform[15];
        var children = new List<Transform>(15);
        for (int digit = 0; digit < 5; digit++)
        {
            // Bases lie across the palm; the middle digit points forward from it.
            float across = digit == 0 ? -.22f : (digit - 2) * .075f;
            Vector3 basePosition = digit == 0
                ? new Vector3(across, .07f, .01f)
                : new Vector3(across, .23f, 0f);
            Transform parent = palm;
            for (int segment = 0; segment < 3; segment++)
            {
                Transform bone = new Transform($"Base HumanLDigit{digit + 1}{segment + 1}")
                {
                    parent = parent,
                    localPosition = segment == 0 ? basePosition : new Vector3(0f, .06f, 0f),
                    localRotation = Quaternion.Euler(3f * digit + segment * 4f, -5f + 6f * segment, 2f * digit - segment * 3f)
                };
                bones[digit * 3 + segment] = bone;
                children.Add(bone);
                parent = bone;
            }
        }

        var target = new Transform("ResolvedGrip")
        {
            localPosition = new Vector3(-.4f, .7f, 1.1f),
            localRotation = Quaternion.Euler(-11f, 23f, 37f)
        };
        var limb = new LimbIK();
        limb.solver.bone3.transform = palm;
        limb.solver.IKPositionWeight = .8f;
        limb.solver.IKPosition = new Vector3(2.2f, -1.4f, .6f);
        limb.solver.IKRotation = Quaternion.Euler(27f, 4f, -18f);
        limb.solver.target = target;
        var player = Player.Create(new PlayerBones { LeftPalm = palm },
            new[] { new HandPoser { Children = children.ToArray() } }, new[] { limb });
        return new Fixture
        {
            Player = player,
            Palm = palm,
            Fingers = bones,
            Limb = limb,
            Target = target,
            ExpectedFingerRotations = new Quaternion[15]
        };
    }

    private static string BuildJson(
        int schemaVersion = 1,
        string coordinateSystem = "anatomical-local",
        int fps = 2,
        int handSampleCount = 3,
        int fingerSampleCount = 3,
        int fingerTrackCount = 15,
        bool duplicateFingerKey = false,
        bool invalidFingerKey = false,
        bool malformedPosition = false,
        bool nonFinitePosition = false,
        bool nonUnitRotation = false,
        bool nonNeutralStart = false,
        bool nonNeutralEnd = false)
    {
        var json = new StringBuilder();
        json.Append("{\"schemaVersion\":").Append(schemaVersion)
            .Append(",\"action\":\"fidget2\",\"sourceSha256\":\"synthetic\"")
            .Append(",\"coordinateSystem\":\"").Append(coordinateSystem)
            .Append("\",\"fps\":").Append(fps)
            .Append(",\"hand\":{\"samples\":[");
        for (int sample = 0; sample < handSampleCount; sample++)
        {
            if (sample != 0) json.Append(',');
            bool middle = sample == 1;
            bool endpoint = sample == 0 || sample == handSampleCount - 1;
            string position = malformedPosition && sample == 1 ? "[0,0]"
                : nonFinitePosition && sample == 1 ? "[1e999,0,0]"
                : nonNeutralStart && sample == 0 ? "[0.1,0,0]"
                : nonNeutralEnd && endpoint && sample == handSampleCount - 1 ? "[0.1,0,0]"
                : middle ? "[2,-1,0.5]" : "[0,0,0]";
            string rotation = nonUnitRotation && sample == 1 ? "[0,0,0,2]"
                : (nonNeutralStart && sample == 0) || (nonNeutralEnd && endpoint && sample == handSampleCount - 1)
                    ? "[0,0,0.2,0.98]"
                    : middle ? "[0,0,0.38268343,0.92387953]" : "[0,0,0,1]";
            json.Append("{\"position\":").Append(position).Append(",\"rotation\":").Append(rotation).Append('}');
        }
        json.Append("]},\"fingers\":[");

        var keys = new List<(int Digit, int Segment)>();
        for (int digit = 0; digit < 5 && keys.Count < fingerTrackCount; digit++)
            for (int segment = 0; segment < 3 && keys.Count < fingerTrackCount; segment++)
                keys.Add((digit, segment));
        if (duplicateFingerKey && keys.Count == 15)
            keys[14] = (0, 0);
        if (invalidFingerKey && keys.Count > 0)
            keys[0] = (5, 0);

        for (int track = 0; track < keys.Count; track++)
        {
            if (track != 0) json.Append(',');
            json.Append("{\"digit\":").Append(keys[track].Digit)
                .Append(",\"segment\":").Append(keys[track].Segment)
                .Append(",\"samples\":[");
            for (int sample = 0; sample < fingerSampleCount; sample++)
            {
                if (sample != 0) json.Append(',');
                bool middle = sample == 1;
                string rotation = middle && keys[track].Digit == 2 && keys[track].Segment == 1
                    ? "[0.38268343,0,0,0.92387953]" : "[0,0,0,1]";
                json.Append("{\"rotation\":").Append(rotation).Append('}');
            }
            json.Append("]}");
        }
        json.Append("]}");
        return json.ToString();
    }

    private static Quaternion[] SnapshotRotations(Transform[] bones)
    {
        var result = new Quaternion[bones.Length];
        for (int i = 0; i < bones.Length; i++) result[i] = bones[i].localRotation;
        return result;
    }

    private static Vector3[] SnapshotLengths(Transform[] bones)
    {
        var result = new Vector3[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            result[i] = i % 3 == 2 ? Vector3.zero : new Vector3(
                (bones[i + 1].position - bones[i].position).magnitude, 0f, 0f);
        }
        return result;
    }

    private static void CheckLengths(Transform[] bones, Vector3[] expected, string label)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            if (i % 3 == 2) continue;
            float actual = (bones[i + 1].position - bones[i].position).magnitude;
            Near(actual, expected[i].x, 1e-4f, $"{label} {i}");
        }
    }

    private static void CheckRotations(Transform[] bones, Quaternion[] expected, string label)
    {
        for (int i = 0; i < bones.Length; i++)
            NearAngle(bones[i].localRotation, expected[i], .001f, $"{label} {i}");
    }

    private static void AssertThrows<T>(Action action, string label) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(label + ": expected " + typeof(T).Name + ", got " + error.GetType().Name, error);
        }
        throw new InvalidOperationException(label + ": expected " + typeof(T).Name + ".");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool Finite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool Finite(Vector3 value)
        => Finite(value.x) && Finite(value.y) && Finite(value.z);

    private static bool Finite(Quaternion value)
        => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);

    private static void Near(float actual, float expected, float tolerance, string label)
    {
        if (MathF.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }

    private static void Near(Vector3 actual, Vector3 expected, float tolerance, string label)
    {
        Near(actual.x, expected.x, tolerance, label + ".x");
        Near(actual.y, expected.y, tolerance, label + ".y");
        Near(actual.z, expected.z, tolerance, label + ".z");
    }

    private static void NearAngle(Quaternion actual, Quaternion expected, float tolerance, string label)
    {
        if (Quaternion.Angle(actual, expected) > tolerance)
            throw new InvalidOperationException($"{label}: angular distance exceeded {tolerance} degrees");
    }

    private sealed class Fixture
    {
        internal Player Player;
        internal Transform Palm;
        internal Transform[] Fingers;
        internal LimbIK Limb;
        internal Transform Target;
        internal Quaternion[] ExpectedFingerRotations;
    }
}

