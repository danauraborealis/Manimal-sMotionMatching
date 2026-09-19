using System;
using System.IO;
using Manimal.MotionMatching;
using Newtonsoft.Json.Linq;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            ScheduleWaitsForAvailabilityWithoutResettingOrCatchingUp();
            ScheduleRestartsOnlyForPlaybackOrNewSession();
            ScheduleSamplesOnlyWhenArmed();
            PlaybackUsesAbsoluteTimeAndRepeatedSamplesAreStable();
            NaturalCompletionStopsAfterTheFinalSample();
            CustomNaturalFadeWindowBlendsSmoothlyAtOnsetAndEnd();
            CancellationDuringCustomFadePreservesWeightAndUsesDefaultDuration();
            RestartResetsCustomNaturalFadeWindowToDefault();
            CancellationPreservesTheCurrentSampleAndFadesSmoothly();
            RepeatedCancellationDoesNotExtendTheFade();
            RestartResetsTheTimelineAndFadeState();
            InvalidPlaybackTimesStopSafely();
            InvalidNaturalFadeWindowsAreRejected();
            ClipInterpolatesAndClampsPositionAndRotation();
            ClipLoadsEmbeddedResourceAndHasNeutralEndpoints();
            ClipLoadsAllEmbeddedActionsAndMatchesMetadata();
            FidgetPoolSupportsOnlyEligibleWidths();
            FidgetPoolLoadsOrderedPairedEntriesWithExpectedTiming();
            ClipRejectsUnsupportedSchemaAndCoordinateSystem();
            ClipRejectsInvalidRateAndSampleCounts();
            ClipRejectsMalformedAndNonFiniteTransforms();
            ClipRejectsNonNeutralEndpoints();
            ClipRejectsNonUnitRotations();
            PivotCompensationPreservesThePivotWorldPosition();
            PivotCompensationUsesTheExpectedPositiveAxisSign();
            IdentityRotationLeavesTranslationUnchangedForNonzeroPivot();
            ZeroPivotLeavesTranslationUnchanged();
            PivotCompensationUsesTheAlreadyBlendedRotationOnce();
            Console.WriteLine("runtime_fidget_playback: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void ScheduleWaitsForAvailabilityWithoutResettingOrCatchingUp()
    {
        var schedule = new FidgetSchedule(() => 9f);
        Check(!schedule.IsDue(100f), "new session should wait nine seconds");
        Check(!schedule.IsDue(108.99f), "cooldown fired early");
        Check(schedule.IsDue(109f), "deadline should be due");
        // Busy weapon / ADS / weapon changes only poll; none consumes the deadline.
        Check(schedule.IsDue(130f), "busy interval must retain due request");
        schedule.PlaybackStarted(130f);
        Check(!schedule.IsDue(132.34f), "clip completion must not retrigger");
        Check(!schedule.IsDue(138.99f), "no catch-up playback after a busy interval");
        Check(schedule.IsDue(139f), "next deadline is nine seconds from actual start");
    }

    private static void ScheduleRestartsOnlyForPlaybackOrNewSession()
    {
        var schedule = new FidgetSchedule(() => 9f);
        schedule.IsDue(10f);
        schedule.PlaybackStarted(12f); // Manual preview shares the cooldown.
        Check(!schedule.IsDue(20.99f), "manual playback must reset shared deadline");
        Check(schedule.IsDue(21f), "manual playback deadline");
        schedule.Reset();
        Check(!schedule.IsDue(50f), "new raid must not inherit overdue deadline");
        Check(schedule.IsDue(59f), "new raid cooldown");
        Check(!schedule.IsDue(1f), "clock rollback must reinitialize");
        Check(schedule.IsDue(10f), "clock rollback deadline");
    }

    private static void ScheduleSamplesOnlyWhenArmed()
    {
        float[] delays = { 9f, 15f, 12f };
        int sampleCount = 0;
        var schedule = new FidgetSchedule(() =>
        {
            Check(sampleCount < delays.Length, "schedule sampled more delays than expected");
            return delays[sampleCount++];
        });

        Check(!schedule.IsDue(100f), "first sequence delay should wait nine seconds");
        Check(sampleCount == 1, "first IsDue should sample once");
        Check(!schedule.IsDue(100.1f), "repeated poll should remain before first deadline");
        Check(!schedule.IsDue(108.99f), "first sequence delay fired early");
        Check(sampleCount == 1, "polling must not reroll the first delay");
        Check(schedule.IsDue(109f), "first sequence delay deadline");

        Check(schedule.IsDue(120f), "busy poll must retain the due request");
        Check(sampleCount == 1, "busy polling must not reroll the due request");
        schedule.PlaybackStarted(120f);
        Check(sampleCount == 2, "playback start should sample the next delay once");
        Check(!schedule.IsDue(134.99f), "second sequence delay fired early");
        Check(schedule.IsDue(135f), "second sequence delay deadline from playback start");

        Check(schedule.IsDue(160f), "overdue poll must retain the second due request");
        Check(!schedule.IsDue(50f), "clock rollback should arm the third delay");
        Check(sampleCount == 3, "clock rollback should sample exactly once");
        Check(!schedule.IsDue(61.99f), "third sequence delay fired early");
        Check(schedule.IsDue(62f), "third sequence delay deadline after rollback");
        Check(schedule.IsDue(100f), "overdue rollback poll must retain the due request");
        Check(sampleCount == 3, "post-rollback polling must not reroll the delay");

        int resetSamples = 0;
        var resetSchedule = new FidgetSchedule(() =>
        {
            resetSamples++;
            return 12f;
        });
        Check(!resetSchedule.IsDue(200f), "reset schedule should arm its first delay");
        Check(resetSamples == 1, "reset schedule first arm should sample once");
        resetSchedule.Reset();
        Check(!resetSchedule.IsDue(300f), "Reset should arm a new delay on the next poll");
        Check(resetSamples == 2, "Reset should sample exactly once");
        Check(!resetSchedule.IsDue(311.99f), "reset delay fired early");
        Check(resetSchedule.IsDue(312f), "reset delay deadline");
        Check(resetSamples == 2, "reset polling must not reroll the delay");

        int directSamples = 0;
        var directSchedule = new FidgetSchedule(() =>
        {
            directSamples++;
            return 15f;
        });
        directSchedule.PlaybackStarted(400f);
        Check(directSamples == 1, "first PlaybackStarted should draw one delay");
        Check(!directSchedule.IsDue(414.99f), "direct playback delay fired early");
        Check(directSchedule.IsDue(415f), "direct playback deadline should use actual start");
        Check(directSamples == 1, "direct playback polling must not reroll the delay");
    }

    private static void PlaybackUsesAbsoluteTimeAndRepeatedSamplesAreStable()
    {
        var playback = new FidgetPlayback();
        playback.Start(100f, 2f);

        playback.Evaluate(100.25f, out float firstSample, out float firstWeight);
        playback.Evaluate(100.25f, out float repeatedSample, out float repeatedWeight);

        Near(firstSample, .25f, "absolute sample time");
        Near(repeatedSample, firstSample, "repeated sample time");
        Near(repeatedWeight, firstWeight, "repeated weight");
        Near(firstWeight, 1f, "interior playback weight");
        Check(playback.Active, "playback should remain active before its duration");
    }

    private static void NaturalCompletionStopsAfterTheFinalSample()
    {
        var playback = new FidgetPlayback();
        playback.Start(5f, .4f);
        playback.Evaluate(5.4f, out float finalSample, out float finalWeight);

        Near(finalSample, .4f, "final sample time");
        Near(finalWeight, 0f, "final sample weight");
        Check(!playback.Active, "natural completion should stop playback");

        playback.Evaluate(5.5f, out float afterSample, out float afterWeight);
        Near(afterSample, 0f, "sample after natural completion");
        Near(afterWeight, 0f, "weight after natural completion");
    }

    private static void CustomNaturalFadeWindowBlendsSmoothlyAtOnsetAndEnd()
    {
        var playback = new FidgetPlayback();
        playback.Start(0f, 1f, .25f);

        playback.Evaluate(0f, out float startSample, out float startWeight);
        Near(startSample, 0f, "custom fade start sample");
        Near(startWeight, 0f, "custom fade-in onset");

        playback.Evaluate(.05f, out float fadeInSample, out float fadeInWeight);
        Near(fadeInSample, .05f, "custom fade-in sample");
        Near(fadeInWeight, .5f, "custom fade-in midpoint");

        playback.Evaluate(.74f, out _, out float beforeEndFadeWeight);
        Near(beforeEndFadeWeight, 1f, "custom end fade before onset");
        playback.Evaluate(.875f, out float midpointSample, out float midpointWeight);
        Near(midpointSample, .875f, "custom end fade midpoint sample");
        Near(midpointWeight, .5f, "custom end fade midpoint");
        Check(playback.Active, "custom natural fade should remain active before duration");

        playback.Evaluate(1f, out float finalSample, out float finalWeight);
        Near(finalSample, 1f, "custom final sample time");
        Near(finalWeight, 0f, "custom final sample weight");
        Check(!playback.Active, "custom natural fade should stop at duration");
    }

    private static void CancellationDuringCustomFadePreservesWeightAndUsesDefaultDuration()
    {
        var playback = new FidgetPlayback();
        playback.Start(10f, 1f, .25f);
        playback.Evaluate(10.875f, out float beforeSample, out float beforeWeight);
        Check(beforeWeight > 0f && beforeWeight < 1f, "custom fade should be in progress before cancellation");

        playback.Cancel(10.875f);
        playback.Evaluate(10.875f, out float cancelSample, out float cancelWeight);
        Near(cancelSample, beforeSample, "custom cancellation sample continuity");
        Near(cancelWeight, beforeWeight, "custom cancellation weight continuity");

        playback.Evaluate(10.925f, out float halfSample, out float halfWeight);
        Near(halfSample, beforeSample, "sample should freeze during custom cancellation");
        Near(halfWeight, beforeWeight * .5f, "custom cancellation halfway weight");
        Check(halfWeight < cancelWeight, "custom cancellation weight should decrease");

        playback.Evaluate(10.976f, out float endSample, out float endWeight);
        Near(endSample, beforeSample, "sample at custom cancellation fade end");
        Near(endWeight, 0f, "custom cancellation fade end weight");
        Check(!playback.Active, "custom cancellation should use the .1 second fade");
    }

    private static void RestartResetsCustomNaturalFadeWindowToDefault()
    {
        var playback = new FidgetPlayback();
        playback.Start(20f, 1f, .25f);
        playback.Evaluate(20.8f, out _, out _);

        playback.Start(30f, 1f);
        playback.Evaluate(30.95f, out float sample, out float weight);
        Near(sample, .95f, "default restart sample time");
        Near(weight, .5f, "restart should restore the default .1 second end fade");
        Check(playback.Active, "default restart should remain active before duration");
    }

    private static void CancellationPreservesTheCurrentSampleAndFadesSmoothly()
    {
        var playback = new FidgetPlayback();
        playback.Start(10f, 2f);
        playback.Evaluate(10.05f, out float beforeSample, out float beforeWeight);
        playback.Cancel(10.05f);

        playback.Evaluate(10.05f, out float cancelSample, out float cancelWeight);
        Near(cancelSample, beforeSample, "cancel sample continuity");
        Near(cancelWeight, beforeWeight, "cancel weight continuity");

        playback.Evaluate(10.10f, out float halfSample, out float halfWeight);
        Near(halfSample, beforeSample, "sample should freeze during cancellation");
        Near(halfWeight, .25f, "halfway cancellation fade");
        Check(halfWeight < cancelWeight, "cancellation weight should decrease");

        // Use a timestamp just beyond the nominal deadline; decimal float literals can
        // round 10.15f - 10.05f just below FadeSeconds.
        playback.Evaluate(10.151f, out float endSample, out float endWeight);
        Near(endSample, beforeSample, "sample at cancellation fade end");
        Near(endWeight, 0f, "weight at cancellation fade end");
        Check(!playback.Active, "cancellation fade should stop playback");
    }

    private static void RepeatedCancellationDoesNotExtendTheFade()
    {
        var playback = new FidgetPlayback();
        playback.Start(20f, 2f);
        playback.Evaluate(20.20f, out float sample, out _);
        playback.Cancel(20.20f);
        playback.Cancel(20.25f);

        playback.Evaluate(20.301f, out float fadedSample, out float fadedWeight);
        Near(fadedSample, sample, "repeated cancellation sample");
        Near(fadedWeight, 0f, "repeated cancellation must not extend fade");
        Check(!playback.Active, "repeated cancellation should still finish at first fade deadline");
    }

    private static void RestartResetsTheTimelineAndFadeState()
    {
        var playback = new FidgetPlayback();
        playback.Start(30f, 2f);
        playback.Evaluate(30.20f, out _, out _);
        playback.Cancel(30.20f);

        playback.Start(100f, .4f);
        playback.Evaluate(100f, out float startSample, out float startWeight);
        Near(startSample, 0f, "restarted sample time");
        Near(startWeight, 0f, "restarted fade-in");
        Check(playback.Active, "restart should reactivate playback");

        playback.Evaluate(100.05f, out float restartedSample, out float restartedWeight);
        Near(restartedSample, .05f, "restarted absolute sample time");
        Near(restartedWeight, .5f, "restarted fade-in weight");
    }

    private static void InvalidPlaybackTimesStopSafely()
    {
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(float.NaN, 1f), "NaN start time");
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(float.PositiveInfinity, 1f), "infinite start time");
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(0f, float.NaN), "NaN duration");
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(0f, float.PositiveInfinity), "infinite duration");
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(0f, 0f), "zero duration");
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(0f, -1f), "negative duration");

        var playback = new FidgetPlayback();
        playback.Start(40f, 1f);
        playback.Evaluate(float.NaN, out float nanSample, out float nanWeight);
        Near(nanSample, 0f, "NaN evaluation sample");
        Near(nanWeight, 0f, "NaN evaluation weight");
        Check(!playback.Active, "NaN evaluation should stop playback");

        playback.Start(50f, 1f);
        playback.Evaluate(49.99f, out float beforeStartSample, out float beforeStartWeight);
        Near(beforeStartSample, 0f, "pre-start sample");
        Near(beforeStartWeight, 0f, "pre-start weight");
        Check(!playback.Active, "pre-start evaluation should stop playback");
    }

    private static void InvalidNaturalFadeWindowsAreRejected()
    {
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(0f, 1f, float.NaN), "NaN natural fade window");
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(0f, 1f, float.PositiveInfinity), "infinite natural fade window");
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(0f, 1f, float.NegativeInfinity), "negative infinite natural fade window");
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(0f, 1f, 0f), "zero natural fade window");
        AssertThrows<ArgumentOutOfRangeException>(() => new FidgetPlayback().Start(0f, 1f, -0.01f), "negative natural fade window");
    }

    private static void ClipInterpolatesAndClampsPositionAndRotation()
    {
        FidgetClip clip = FidgetClip.Parse("{" +
            "\"schemaVersion\":1," +
            "\"coordinateSystem\":\"unity-camera\",\"fps\":2," +
            "\"samples\":[" +
                "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}," +
                "{\"position\":[2,0,0],\"rotation\":[0,0,0.70710678,0.70710678]}," +
                "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}" +
            "]}");

        Near(clip.Duration, 1f, "clip duration");
        clip.Sample(-1f, out Vector3 before, out Quaternion beforeRotation);
        Near(before, new Vector3(0f, 0f, 0f), "clamped start position");
        NearAngle(beforeRotation, Quaternion.identity, "clamped start rotation");

        clip.Sample(.25f, out Vector3 middle, out Quaternion middleRotation);
        Near(middle, new Vector3(1f, 0f, 0f), "position interpolation");
        NearQuaternion(middleRotation, new Quaternion(0f, 0f, .38268343f, .9238795f), "rotation interpolation");

        clip.Sample(.75f, out Vector3 otherMiddle, out Quaternion otherMiddleRotation);
        Near(otherMiddle, new Vector3(1f, 0f, 0f), "second position interpolation");
        NearQuaternion(otherMiddleRotation, new Quaternion(0f, 0f, .38268343f, .9238795f), "second rotation interpolation");

        clip.Sample(2f, out Vector3 after, out Quaternion afterRotation);
        Near(after, new Vector3(0f, 0f, 0f), "clamped end position");
        NearAngle(afterRotation, Quaternion.identity, "clamped end rotation");
    }

    private static void ClipLoadsEmbeddedResourceAndHasNeutralEndpoints()
    {
        FidgetClip clip = FidgetClip.Load();
        Check(clip.Duration > 0f && Finite(clip.Duration), "embedded fidget clip duration is invalid");

        clip.Sample(0f, out Vector3 startPosition, out Quaternion startRotation);
        clip.Sample(clip.Duration, out Vector3 endPosition, out Quaternion endRotation);
        Near(startPosition, new Vector3(0f, 0f, 0f), "embedded clip start position");
        Near(endPosition, new Vector3(0f, 0f, 0f), "embedded clip end position");
        NearAngle(startRotation, Quaternion.identity, "embedded clip start rotation");
        NearAngle(endRotation, Quaternion.identity, "embedded clip end rotation");
    }

    private static void ClipLoadsAllEmbeddedActionsAndMatchesMetadata()
    {
        string[] actions = { "fidget1", "fidget2", "fidget3" };
        foreach (string action in actions)
        {
            JObject metadata = ReadEmbeddedJson($"Manimal.MotionMatching.Data.{action}.weapon.json");
            JArray samples = metadata["samples"] as JArray;
            Check(samples != null && samples.Count >= 2, action + " weapon sample range is missing");

            float fps = (float?)metadata["fps"] ?? 0f;
            Check(Finite(fps) && fps > 0f, action + " weapon fps is invalid");
            float expectedDuration = (samples.Count - 1) / fps;
            FidgetClip clip = FidgetClip.Load(action);
            Near(clip.Duration, expectedDuration, "embedded " + action + " duration");

            JArray frameRange = metadata["sourceFrameRange"] as JArray;
            Check(frameRange != null && frameRange.Count == 2, action + " source frame range is missing");
            Check((int)frameRange[1] - (int)frameRange[0] + 1 == samples.Count,
                action + " source frame range does not match samples");
            Near((float?)metadata["durationSeconds"] ?? float.NaN, expectedDuration,
                "embedded " + action + " metadata duration");

            clip.Sample(-1f, out Vector3 startPosition, out Quaternion startRotation);
            clip.Sample(clip.Duration + 1f, out Vector3 endPosition, out Quaternion endRotation);
            Near(startPosition, Vector3Zero, action + " clamped start position");
            Near(endPosition, Vector3Zero, action + " clamped end position");
            NearAngle(startRotation, Quaternion.identity, action + " clamped start rotation");
            NearAngle(endRotation, Quaternion.identity, action + " clamped end rotation");

            clip.Sample(clip.Duration * .5f, out Vector3 interiorPosition, out Quaternion interiorRotation);
            Check(Finite(interiorPosition.x) && Finite(interiorPosition.y) && Finite(interiorPosition.z),
                action + " interior position is non-finite");
            Check(Finite(interiorRotation.x) && Finite(interiorRotation.y) &&
                Finite(interiorRotation.z) && Finite(interiorRotation.w),
                action + " interior rotation is non-finite");
        }
    }

    private static void FidgetPoolSupportsOnlyEligibleWidths()
    {
        Check(FidgetPool.SupportsWidth(1), "one-cell fidget pool width should be supported");
        Check(FidgetPool.SupportsWidth(2), "two-cell fidget pool width should be supported");
        Check(FidgetPool.SupportsWidth(3), "three-cell fidget pool width should be supported");
        Check(FidgetPool.SupportsWidth(4), "four-cell fidget pool width should be supported");
        Check(FidgetPool.SupportsWidth(5), "five-cell fidget pool width should be supported");
        Check(FidgetPool.SupportsWidth(6), "six-cell fidget pool width should be supported");
        Check(FidgetPool.SupportsWidth(7), "seven-cell weapons must use the long gun pool");
        Check(FidgetPool.SupportsWidth(8), "larger weapons must use the long gun pool");
        foreach (int width in new[] { -1, 0 })
        {
            Check(!FidgetPool.SupportsWidth(width), $"unexpected fidget pool width support: {width}");
            AssertThrows<ArgumentOutOfRangeException>(() => FidgetPool.Load(width), "unsupported width load");
        }
    }

    private static void FidgetPoolLoadsOrderedPairedEntriesWithExpectedTiming()
    {
        foreach (int width in new[] { 3, 4, 5, 6, 7, 8 }) ValidateFidgetPool(width);
        foreach (int width in new[] { 1, 2 })
        {
            var entries = FidgetPool.Load(width);
            Check(entries.Length == 3, "revolver pool count");
            string[] names = { "Revolver/fidget", "Revolver/fidget2", "Revolver/fidget3" };
            int[] samples = { 149, 55, 95 };
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                Check(entry.Name == names[i], "revolver pool membership");
                Check(entry.Gesture == null, "revolver must never apply hand or finger gesture tracks");
                Near(entry.EndFadeSeconds, .25f, "revolver end blend");
                Near(entry.Weapon.Duration, (samples[i] - 1) / 60f, "revolver duration");
                entry.Weapon.Sample(0, out var p0, out var q0);
                entry.Weapon.Sample(entry.Weapon.Duration, out var p1, out var q1);
                Near(p0, new Vector3(0, 0, 0), "revolver neutral position");
                Near(p1, new Vector3(0, 0, 0), "revolver ending position");
                NearAngle(q0, Quaternion.identity, "revolver neutral rotation");
                NearAngle(q1, Quaternion.identity, "revolver ending rotation");
            }
        }
    }

    private static void ValidateFidgetPool(int width)
    {
        FidgetPoolEntry[] entries = FidgetPool.Load(width);
        Check(entries != null && entries.Length == 6, "fidget pool should contain six entries");

        string[] expectedNames =
        {
            "Shotgun/fidget1", "Shotgun/fidget2", "Shotgun/fidget3",
            "MP5/fidget", "MP5/fidget2", "MP5/fidget3",
        };
        int[] expectedSampleCounts = { 141, 45, 79, 101, 57, 45 };
        if (width >= 5)
        {
            expectedNames = new[] { "Sniper/fidget1", "Sniper/fidget2", "Sniper/fidget3",
                "Tau/fidget 1", "Tau/fidget 2", "Tau/fidget 3" };
            expectedSampleCounts = new[] { 141, 67, 73, 63, 73, 69 };
        }
        float[] expectedFadeSeconds = { .25f, .25f, .25f, .25f, .25f, .25f };

        for (int i = 0; i < entries.Length; i++)
        {
            FidgetPoolEntry entry = entries[i];
            Check(entry != null, $"fidget pool entry {i} is null");
            Check(entry.Weapon != null, $"fidget pool weapon {i} is null");
            Check(entry.Gesture != null, $"fidget pool gesture {i} is null");
            Check(entry.Name == expectedNames[i], $"fidget pool entry {i} name");
            for (int previous = 0; previous < i; previous++)
                Check(entry.Name != entries[previous].Name, $"duplicate fidget pool name: {entry.Name}");

            float expectedDuration = (expectedSampleCounts[i] - 1) / 60f;
            Near(entry.Weapon.Duration, expectedDuration, "fidget pool weapon duration " + i);
            Near(entry.Gesture.Duration, expectedDuration, "fidget pool gesture duration " + i);
            Near(entry.EndFadeSeconds, expectedFadeSeconds[i], "fidget pool end fade " + i);

            entry.Weapon.Sample(0f, out Vector3 weaponStartPosition, out Quaternion weaponStartRotation);
            entry.Weapon.Sample(entry.Weapon.Duration, out Vector3 weaponEndPosition, out Quaternion weaponEndRotation);
            Near(weaponStartPosition, new Vector3(0f, 0f, 0f), "fidget pool weapon start position " + i);
            Near(weaponEndPosition, new Vector3(0f, 0f, 0f), "fidget pool weapon end position " + i);
            NearAngle(weaponStartRotation, Quaternion.identity, "fidget pool weapon start rotation " + i);
            NearAngle(weaponEndRotation, Quaternion.identity, "fidget pool weapon end rotation " + i);

            entry.Gesture.SampleHand(0f, out Vector3 gestureStartPosition, out Quaternion gestureStartRotation);
            entry.Gesture.SampleHand(entry.Gesture.Duration, out Vector3 gestureEndPosition, out Quaternion gestureEndRotation);
            Near(gestureStartPosition, new Vector3(0f, 0f, 0f), "fidget pool gesture start position " + i);
            Near(gestureEndPosition, new Vector3(0f, 0f, 0f), "fidget pool gesture end position " + i);
            NearAngle(gestureStartRotation, Quaternion.identity, "fidget pool gesture start rotation " + i);
            NearAngle(gestureEndRotation, Quaternion.identity, "fidget pool gesture end rotation " + i);
        }
    }

    private static JObject ReadEmbeddedJson(string resourceName)
    {
        using Stream stream = typeof(FidgetClip).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("Embedded test resource is missing: " + resourceName);
        using var reader = new StreamReader(stream);
        return JObject.Parse(reader.ReadToEnd());
    }

    private static void ClipRejectsUnsupportedSchemaAndCoordinateSystem()
    {
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: TwoNeutralSamples, schemaVersion: 2)), "schema version");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: TwoNeutralSamples, coordinateSystem: "world")), "coordinate system");
    }

    private static void ClipRejectsInvalidRateAndSampleCounts()
    {
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: TwoNeutralSamples, fps: 0f)), "zero fps");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: TwoNeutralSamples, fps: -1f)), "negative fps");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: TwoNeutralSamples, fps: 1001f)), "excessive fps");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}]")), "one sample");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[]")), "empty samples");
    }

    private static void ClipRejectsMalformedAndNonFiniteTransforms()
    {
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[" +
            "{\"position\":[NaN,0,0],\"rotation\":[0,0,0,1]}," +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}]")), "non-finite position");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[" +
            "{\"position\":[0,0],\"rotation\":[0,0,0,1]}," +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}]")), "malformed position");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[" +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0]}," +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}]")), "malformed rotation");
    }

    private static void ClipRejectsNonNeutralEndpoints()
    {
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[" +
            "{\"position\":[0.01,0,0],\"rotation\":[0,0,0,1]}," +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}]")), "non-neutral start position");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[" +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}," +
            "{\"position\":[0.01,0,0],\"rotation\":[0,0,0,1]}]")), "non-neutral end position");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[" +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0.01,1]}," +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}]")), "non-neutral start rotation");
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[" +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}," +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0.01,1]}]")), "non-neutral end rotation");
    }

    private static void ClipRejectsNonUnitRotations()
    {
        AssertThrows<InvalidDataException>(() => FidgetClip.Parse(Json(samples: "[" +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}," +
            "{\"position\":[1,0,0],\"rotation\":[0,0,0,2]}," +
            "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}]")), "non-unit rotation");
    }

    private static void PivotCompensationPreservesThePivotWorldPosition()
    {
        Vector3 translation = new Vector3(3f, -2f, .5f);
        Vector3 pivot = new Vector3(.2f, .1f, 1.5f);
        Quaternion rotation = new Quaternion(0f, 0f, .70710678f, .70710678f);

        Vector3 compensated = FidgetPivot.CompensatedTranslation(translation, rotation, pivot);
        Near(compensated + rotation * pivot, translation + pivot, "compensated pivot world position");
    }

    private static void PivotCompensationUsesTheExpectedPositiveAxisSign()
    {
        Vector3 pivot = new Vector3(1f, 0f, 0f);
        Quaternion positiveNinetyAroundZ = new Quaternion(0f, 0f, .70710678f, .70710678f);

        Vector3 compensated = FidgetPivot.CompensatedTranslation(Vector3Zero, positiveNinetyAroundZ, pivot);
        Near(compensated, new Vector3(1f, -1f, 0f), "positive Z 90-degree pivot compensation");
    }

    private static void IdentityRotationLeavesTranslationUnchangedForNonzeroPivot()
    {
        Vector3 translation = new Vector3(-1.5f, 2f, .25f);
        Vector3 pivot = new Vector3(.4f, -.7f, 1.1f);

        Vector3 compensated = FidgetPivot.CompensatedTranslation(translation, Quaternion.identity, pivot);
        Near(compensated, translation, "identity rotation pivot compensation");
    }

    private static void ZeroPivotLeavesTranslationUnchanged()
    {
        Vector3 translation = new Vector3(-1.5f, 2f, .25f);
        Quaternion rotation = new Quaternion(0f, .38268343f, 0f, .9238795f);

        Vector3 compensated = FidgetPivot.CompensatedTranslation(translation, rotation, Vector3Zero);
        Near(compensated, translation, "zero pivot compensation");
    }

    private static void PivotCompensationUsesTheAlreadyBlendedRotationOnce()
    {
        Vector3 translation = new Vector3(2f, -1f, .5f);
        Vector3 pivot = new Vector3(1f, 0f, 0f);
        Quaternion blendedFortyFiveAroundZ = new Quaternion(0f, 0f, .38268343f, .9238795f);

        Vector3 compensated = FidgetPivot.CompensatedTranslation(translation, blendedFortyFiveAroundZ, pivot);
        Vector3 expected = translation + pivot - blendedFortyFiveAroundZ * pivot;
        Near(compensated, expected, "already blended rotation compensation");
        Near(compensated + blendedFortyFiveAroundZ * pivot, translation + pivot,
            "already blended rotation pivot world position");
    }

    private static readonly Vector3 Vector3Zero = new Vector3(0f, 0f, 0f);

    private const string TwoNeutralSamples = "[" +
        "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}," +
        "{\"position\":[0,0,0],\"rotation\":[0,0,0,1]}]";

    private static string Json(string samples, float fps = 2f, int schemaVersion = 1, string coordinateSystem = "unity-camera")
    {
        return "{\"schemaVersion\":" + schemaVersion +
            ",\"coordinateSystem\":\"" + coordinateSystem +
            "\",\"fps\":" + fps.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"samples\":" + samples + "}";
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

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Near(float actual, float expected, string label)
    {
        if (Math.Abs(actual - expected) > .0001f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }

    private static void Near(Vector3 actual, Vector3 expected, string label)
    {
        Near(actual.x, expected.x, label + ".x");
        Near(actual.y, expected.y, label + ".y");
        Near(actual.z, expected.z, label + ".z");
    }

    private static void NearQuaternion(Quaternion actual, Quaternion expected, string label)
    {
        float direct = Math.Abs(actual.x - expected.x) + Math.Abs(actual.y - expected.y) +
            Math.Abs(actual.z - expected.z) + Math.Abs(actual.w - expected.w);
        float negated = Math.Abs(actual.x + expected.x) + Math.Abs(actual.y + expected.y) +
            Math.Abs(actual.z + expected.z) + Math.Abs(actual.w + expected.w);
        if (Math.Min(direct, negated) > .002f)
            throw new InvalidOperationException(label + ": quaternion did not interpolate as expected");
    }

    private static void NearAngle(Quaternion actual, Quaternion expected, string label)
    {
        if (Quaternion.Angle(actual, expected) > .01f)
            throw new InvalidOperationException(label + ": expected angular distance <= .01 degrees");
    }
}
