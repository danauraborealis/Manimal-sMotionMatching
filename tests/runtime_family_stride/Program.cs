using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            WeightZeroAndOneUseExactSourcePair();
            CanonicalClockAndMidpointRemainReferenceOwned();
            RecompositionMatchesUnwrappedSourceAcrossBoundary();
            TurningLoopUsesRotatedRootContinuation();
            ExporterBoundaryOwnershipUsesEndingCycleGeometry();
            WarmSamplingDoesNotAllocate();
            CompatibilityRejectsIncompleteBothFootData();
            CompatibilityMatchesNearStanceOffsetsUniquely();
            if (args.Length > 1)
                throw new InvalidOperationException("usage: runtime_family_stride [path-to-family-json]");
            if (args.Length == 1)
                VerifyRealFamily(args[0]);
            Console.WriteLine("runtime_family_stride: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void WeightZeroAndOneUseExactSourcePair()
    {
        PoseClip reference = Clip("reference", 0, 0f, new float[] { 0f, .2f, .45f, .7f, 1f, 1.2f, 1.4f, 1.6f }, 0f, 0f);
        PoseClip first = Clip("first", 0, 0f, new float[] { 0f, .1f, .2f, .3f, .4f, .5f, .6f, .7f }, 0f, 10f);
        PoseClip second = Clip("second", 0, 0f, new float[] { 0f, .3f, .6f, .9f, 1.2f, 1.5f, 1.8f, 2.1f }, 0f, -10f);
        SetTrajectory(reference, 0f, 0f, 0f, 0f);
        SetTrajectory(first, 0f, 4f, 0f, 12f);
        SetTrajectory(second, 0f, 4f, 0f, -18f);

        var sampler = new FamilyStride();
        PoseClip zero = sampler.Sample(reference, first, second, 0f, true);
        if (!ReferenceEquals(zero.Stride[0].Progression, reference.Stride[0].Progression))
            throw new InvalidOperationException("weight zero must retain canonical reference progression");
        for (int f = 0; f < reference.Frames; f++)
        {
            NearVector(zero.Stride[0].Footbase[f], first.Stride[0].Footbase[f], "weight zero footbase");
            Near(zero.Stride[0].Heading[f], first.Stride[0].Heading[f], "weight zero heading");
            Near(zero.RootVelocity[f].x, first.RootVelocity[f].x, "weight zero root velocity x");
            Near(zero.RootVelocity[f].y, first.RootVelocity[f].y, "weight zero root velocity y");
            Near(zero.YawProgress[f], first.YawProgress[f], "weight zero yaw");
        }

        PoseClip one = sampler.Sample(reference, first, second, 1f, true);
        for (int f = 0; f < reference.Frames; f++)
        {
            NearVector(one.Stride[0].Footbase[f], second.Stride[0].Footbase[f], "weight one footbase");
            Near(one.Stride[0].Heading[f], second.Stride[0].Heading[f], "weight one heading");
            Near(one.RootVelocity[f].x, second.RootVelocity[f].x, "weight one root velocity x");
            Near(one.RootVelocity[f].y, second.RootVelocity[f].y, "weight one root velocity y");
            Near(one.YawProgress[f], second.YawProgress[f], "weight one yaw");
        }
    }

    private static void CanonicalClockAndMidpointRemainReferenceOwned()
    {
        PoseClip reference = Clip("reference", 0, 0f, new float[] { 0f, .25f, .5f, .75f, 1f, 1.25f, 1.5f, 1.75f }, 0f, 0f);
        PoseClip first = Clip("first", 0, 0f, new float[] { 0f, .15f, .3f, .45f, .6f, .75f, .9f, 1.05f }, 0f, 0f);
        PoseClip second = Clip("second", 0, 0f, new float[] { 0f, .35f, .7f, 1.05f, 1.4f, 1.75f, 2.1f, 2.45f }, 0f, 0f);
        var sampler = new FamilyStride();
        PoseClip sample = sampler.Sample(reference, first, second, .5f, true);
        if (!ReferenceEquals(sample, sampler.Sample(reference, first, second, .5f, true)))
            throw new InvalidOperationException("unchanged family sample should reuse its result object");

        if (!ReferenceEquals(sample.Stride[0].Cycle, reference.Stride[0].Cycle) ||
            !ReferenceEquals(sample.Stride[0].Progression, reference.Stride[0].Progression))
            throw new InvalidOperationException("sample must retain reference cycle and progression arrays");
        StrideCycle cycle = sample.Stride[0].Cycles[0];
        if (cycle.MiddleFrame != reference.Stride[0].Cycles[0].MiddleFrame)
            throw new InvalidOperationException("midpoint frame must stay canonical");
        Near(cycle.MiddleProgression, reference.Stride[0].Progression[cycle.MiddleFrame], "canonical midpoint progression");
        NearVector(cycle.MiddlePosition, sample.Stride[0].Footbase[cycle.MiddleFrame], "recomputed midpoint position");
        NearVector(cycle.MiddleOffset, sample.Stride[0].Offset[cycle.MiddleFrame], "recomputed midpoint offset");
    }

    private static void RecompositionMatchesUnwrappedSourceAcrossBoundary()
    {
        PoseClip reference = Clip("reference", 0, 0f, new float[] { 0f, .2f, .4f, .6f, .8f, 1f, 1.2f, 1.4f }, 0f, 0f);
        PoseClip source = Clip("source", 0, 0f, new float[] { 0f, .2f, .4f, .6f, .8f, 1f, 1.2f, 1.4f }, 0f, 0f);
        // A single cycle crosses the loop image at frame 8. Its midpoint remains at frame 4, and its endpoint is
        // read from the cached n+1 root position rather than wrapping to the first root sample.
        SetTrajectory(reference, 0f, 4f, 0f, 0f);
        SetTrajectory(source, 0f, 4f, 0f, 0f);
        UseSingleCycle(reference);
        UseSingleCycle(source);
        for (int f = 0; f < 8; f++)
            source.Stride[0].Footbase[f] = new Vector3(0f, 0f, f * 0.1f);
        var sampler = new FamilyStride();
        PoseClip sample = sampler.Sample(reference, source, source, 0f, true);
        Vector3[] world = sampler.GetWorldSole(0);
        if (world == null || world.Length != 17)
            throw new InvalidOperationException("unwrapped world sole cache must contain two periods plus endpoint");
        Vector3 start = world[0];
        Vector3 end = world[8];
        Vector3 sourceMiddle = world[4];
        StrideCycle cycle = sample.Stride[0].Cycles[0];
        Vector3 expectedMiddle = start + (end - start) * sample.Stride[0].Progression[4] +
            RotateStride(sample.Stride[0].Offset[4], end - start);
        NearVector(sourceMiddle, expectedMiddle, "midpoint reconstruction across loop");
        Near(cycle.StrideLength, (end - start).magnitude, "source endpoint stride length");
        NearVector(sampler.RootPositions[8], P(0f, 0f, 8f), "integrated root endpoint");
    }

    private static void TurningLoopUsesRotatedRootContinuation()
    {
        PoseClip reference = Clip("reference", 0, 0f, new float[] { 0f, .25f, .5f, .75f, 1f, 1.25f, 1.5f, 1.75f }, 0f, 0f);
        PoseClip source = Clip("source", 0, 0f, new float[] { 0f, .25f, .5f, .75f, 1f, 1.25f, 1.5f, 1.75f }, 0f, 0f);
        UseSingleCycle(reference);
        UseSingleCycle(source);
        SetTrajectory(reference, 0f, 4f, 0f, 30f);
        SetTrajectory(source, 0f, 4f, 0f, 30f);
        for (int f = 0; f < source.Frames; f++)
            source.Stride[0].Footbase[f] = Vector3.zero;

        var sampler = new FamilyStride();
        sampler.Sample(reference, source, source, 0f, true);
        Vector3[] world = sampler.GetWorldSole(0);
        float loopYaw = source.YawProgress[source.Frames - 1] - source.YawProgress[0];
        Vector3 expected = sampler.RootPositions[source.Frames] + RotateYaw(sampler.RootPositions[1], loopYaw);
        NearVector(world[source.Frames + 1], expected, "turning loop root continuation");
        Vector3 straightTranslation = sampler.RootPositions[source.Frames] + sampler.RootPositions[1];
        if ((world[source.Frames + 1] - straightTranslation).magnitude < .01f)
            throw new InvalidOperationException("turning loop must rotate the repeated root trajectory");
    }

    private static void ExporterBoundaryOwnershipUsesEndingCycleGeometry()
    {
        PoseClip reference = Clip("reference", 0, 0f, new float[] { 0f, .25f, .5f, .75f, 1f, .25f, .5f, .75f }, 0f, 0f);
        PoseClip source = Clip("source", 0, 0f, new float[] { 0f, .25f, .5f, .75f, 1f, .25f, .5f, .75f }, 0f, 0f);
        SetTrajectory(reference, 0f, 4f, 0f, 0f);
        SetTrajectory(source, 0f, 4f, 0f, 0f);
        // The exporter assigns a shared stance boundary to the cycle that ends there: frame 4 remains cycle 0,
        // while the next cycle owns frames 5..7 and the wrapped frame 0 image.
        var sampler = new FamilyStride();
        PoseClip sample = sampler.Sample(reference, source, source, 0f, true);
        if (sample.Stride[0].Cycle[4] != 0)
            throw new InvalidOperationException("exporter boundary frame must remain owned by ending cycle");
        Vector3[] world = sampler.GetWorldSole(0);
        NearVector(sample.Stride[0].Offset[4], Vector3.zero, "ending-cycle boundary offset");

        StrideCycle next = sample.Stride[0].Cycles[1];
        int nextStart = source.Frames + next.StartFrame;
        int nextEnd = source.Frames + next.EndFrame;
        Vector3 nextStartWorld = world[nextStart];
        Vector3 nextEndWorld = world[nextEnd];
        Vector3 nextStride = nextEndWorld - nextStartWorld;
        Vector3 expectedBoundary = nextStartWorld + nextStride * sample.Stride[0].Progression[5] +
            RotateStride(sample.Stride[0].Offset[5], nextStride);
        NearVector(world[source.Frames + 5], expectedBoundary, "next-cycle owned frame reconstruction");
    }

    private static void WarmSamplingDoesNotAllocate()
    {
        PoseClip reference = Clip("reference", 0, 0f, new float[] { 0f, .25f, .5f, .75f, 1f, .25f, .5f, .75f }, 0f, 0f);
        PoseClip first = Clip("first", 0, 0f, new float[] { 0f, .2f, .4f, .6f, .8f, .2f, .4f, .6f }, 0f, 0f);
        PoseClip second = Clip("second", 0, 0f, new float[] { 0f, .3f, .6f, .9f, 1.2f, .3f, .6f, .9f }, 0f, 0f);
        var sampler = new FamilyStride();
        sampler.Sample(reference, first, second, 0f, true);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 12; i++)
            sampler.Sample(reference, first, second, (i % 11) / 10f, true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated != 0)
            throw new InvalidOperationException("warm family samples allocated " + allocated + " bytes");
    }

    private static void CompatibilityRejectsIncompleteBothFootData()
    {
        PoseClip reference = Clip("reference", 0, 0f, new float[] { 0f, .2f, .4f, .6f, .8f, 1f, 1.2f, 1.4f }, 0f, 0f);
        PoseClip member = Clip("member", 0, 0f, new float[] { 0f, .2f, .4f, .6f, .8f, 1f, 1.2f, 1.4f }, 0f, 0f);
        member.Stride[1].Footbase = new Vector3[reference.Frames - 1];
        if (FamilyStride.Compatible(reference, member, true))
            throw new InvalidOperationException("truncated right foot data must reject compatibility");

        member = Clip("member", 0, 0f, new float[] { 0f, .2f, .4f, .6f, .8f, 1f, 1.2f, 1.4f }, 0f, 0f);
        member.Contact = new bool[2][];
        member.Contact[0] = new bool[reference.Frames];
        member.Contact[1] = new bool[reference.Frames - 1];
        if (FamilyStride.Compatible(reference, member, true))
            throw new InvalidOperationException("truncated contact data must reject compatibility");
    }

    private static void CompatibilityMatchesNearStanceOffsetsUniquely()
    {
        PoseClip reference = Clip("reference", 0, 0f, new float[] { 0f, .2f, .4f, .6f, .8f, 1f, 1.2f, 1.4f }, 0f, 0f);
        PoseClip member = Clip("member", 2, 0f, new float[] { 0f, .2f, .4f, .6f, .8f, 1f, 1.2f, 1.4f }, 0f, 0f);
        for (int side = 0; side < 2; side++)
        {
            member.Stride[side].Cycles[0].StartFrame = 2;
            member.Stride[side].Cycles[0].EndFrame = 2;
            member.Stride[side].Cycles[1].StartFrame = 6;
            member.Stride[side].Cycles[1].EndFrame = 6;
            for (int f = 0; f < member.Frames; f++)
                member.Stride[side].Cycle[f] = f < 6 ? 0 : 1;
        }
        if (!FamilyStride.Compatible(reference, member, false))
            throw new InvalidOperationException("nearby member stance offsets should match by nearest unique starts");
    }

    private static void VerifyRealFamily(string path)
    {
        List<PoseClip> clips = LoadRealFamily(path);
        PoseClip reference = FindClip(clips, "tarkov_walk_aim_0");
        PoseClip first = FindClip(clips, "tarkov_walk_aim_225");
        PoseClip second = FindClip(clips, "tarkov_walk_aim_270");
        if (reference.Frames != 48 || first.Frames != reference.Frames || second.Frames != reference.Frames)
            throw new InvalidOperationException("real family fixture must contain three matching 48-frame clips");

        reference.PhaseOffset = 0;
        first.PhaseOffset = FamilyStride.FindPhaseOffset(reference, first);
        second.PhaseOffset = FamilyStride.FindPhaseOffset(reference, second);
        if (first.PhaseOffset != 0 || second.PhaseOffset != 0)
            throw new InvalidOperationException("both-foot alignment should preserve the native phase for the actual 225/270 walk pair");
        if (!FamilyStride.Compatible(reference, first, false) || !FamilyStride.Compatible(reference, second, false))
            throw new InvalidOperationException("real family derived phase offset rejected the 225/270 walk pair");
        // The captures share the same authored clock. Their small one-to-two-frame capture offsets are within the
        // quarter-period contact gate and should be accepted by the production compatibility check.
        if (!FamilyStride.Compatible(reference, first, true))
            throw new InvalidOperationException("real family phase compatibility rejected tarkov_walk_aim_225");
        if (!FamilyStride.Compatible(reference, second, true))
            throw new InvalidOperationException("real family phase compatibility rejected tarkov_walk_aim_270");

        var sampler = new FamilyStride();
        float[] weights = { 0f, 0.5f, 1f };
        foreach (float weight in weights)
        {
            PoseClip sample = sampler.Sample(reference, first, second, weight, true);
            if (ReferenceEquals(sample, reference))
                throw new InvalidOperationException("real family sample fell back to the reference clip at weight " + weight);

            for (int side = 0; side < 2; side++)
            {
                StrideFoot canonical = reference.Stride[side];
                StrideFoot output = sample.Stride[side];
                if (!ReferenceEquals(output.Cycle, canonical.Cycle) || !ReferenceEquals(output.Progression, canonical.Progression))
                    throw new InvalidOperationException("real family sample replaced canonical clock for side " + side + " at weight " + weight);

                for (int frame = 0; frame < reference.Frames; frame++)
                {
                    if (output.Cycle[frame] != canonical.Cycle[frame] || output.Progression[frame] != canonical.Progression[frame])
                        throw new InvalidOperationException("real family canonical clock changed at side " + side + ", frame " + frame + ", weight " + weight);
                }

                int unwrappedFrames = 0;
                for (int frame = 0; frame < reference.Frames; frame++)
                    unwrappedFrames += VerifyRealFrame(sample, sampler, side, frame, weight);
                if (unwrappedFrames == 0)
                    throw new InvalidOperationException("real family verification did not exercise an unwrapped owned endpoint for side " + side);

                for (int cycleIndex = 0; cycleIndex < canonical.Cycles.Length; cycleIndex++)
                    VerifyRealMidpoint(sample, sampler, side, cycleIndex, weight);
            }
        }

        Console.WriteLine("runtime_family_stride: real family fixture verified (weights 0, 0.5, 1; 48 frames; both feet)");
    }

    private static int VerifyRealFrame(PoseClip sample, FamilyStride sampler, int side, int frame, float weight)
    {
        int n = sample.Frames;
        StrideFoot foot = sample.Stride[side];
        int cycleIndex = foot.Cycle[frame];
        if (cycleIndex < 0 || cycleIndex >= foot.Cycles.Length)
            throw new InvalidOperationException("real family produced an invalid cycle index at side " + side + ", frame " + frame + ", weight " + weight);

        StrideCycle cycle = foot.Cycles[cycleIndex];
        int start = ExtendedIndex(cycle.StartFrame, n);
        int end = ExtendedIndex(cycle.EndFrame, n);
        if (end <= start)
            end += n;
        if (end > 2 * n)
            end = 2 * n;

        int extended = frame;
        if (extended < start)
            extended += n;
        if (extended > end)
            throw new InvalidOperationException("real family frame ownership falls outside its cycle at side " + side + ", frame " + frame + ", cycle " + cycleIndex + ", weight " + weight);

        float progression = foot.Progression[frame];
        if (!IsFinite(progression))
            progression = 0f;
        if (frame == Wrap(cycle.StartFrame, n) && extended == start && progression > 0.5f)
            extended = end;

        Vector3[] world = sampler.GetWorldSole(side);
        Vector3 sourceStart = world[start];
        Vector3 sourceEnd = world[end];
        Vector3 stride = sourceEnd - sourceStart;
        stride.y = 0f;
        Vector3 baseline = sourceStart + (sourceEnd - sourceStart) * progression;
        Vector3 offset = foot.Offset[frame];
        Vector3 expected = baseline + RotateStride(offset, stride);
        // Stride offsets store world height above Floor; their planar components are in the stride axis.
        expected.y = foot.Floor + offset.y;
        NearVectorWithin(world[extended], expected, "real family sole reconstruction side " + side + ", frame " + frame + ", weight " + weight, 0.0015f);
        return extended >= n ? 1 : 0;
    }

    private static void VerifyRealMidpoint(PoseClip sample, FamilyStride sampler, int side, int cycleIndex, float weight)
    {
        StrideFoot foot = sample.Stride[side];
        StrideCycle cycle = foot.Cycles[cycleIndex];
        if (!cycle.HasMidpoint)
            return;

        int n = sample.Frames;
        int start = ExtendedIndex(cycle.StartFrame, n);
        int end = ExtendedIndex(cycle.EndFrame, n);
        if (end <= start)
            end += n;
        if (end > 2 * n)
            end = 2 * n;
        int middle = MiddleIndex(cycle.MiddleFrame, start, end, n);
        int middleFrame = Wrap(middle, n);
        float progression = foot.Progression[middleFrame];
        if (!IsFinite(progression))
            progression = 0f;

        Vector3[] world = sampler.GetWorldSole(side);
        Vector3 sourceStart = world[start];
        Vector3 sourceEnd = world[end];
        Vector3 stride = sourceEnd - sourceStart;
        stride.y = 0f;
        Vector3 baseline = sourceStart + (sourceEnd - sourceStart) * progression;
        Vector3 expected = baseline + RotateStride(cycle.MiddleOffset, stride);
        expected.y = foot.Floor + cycle.MiddleOffset.y;
        NearVectorWithin(world[middle], expected, "real family midpoint reconstruction side " + side + ", cycle " + cycleIndex + ", weight " + weight, 0.0015f);
        NearVectorWithin(cycle.MiddlePosition, foot.Footbase[middleFrame], "real family midpoint position side " + side + ", cycle " + cycleIndex + ", weight " + weight, 0.0015f);
        Near(cycle.MiddleProgression, foot.Progression[middleFrame], "real family midpoint progression");
    }

    private static List<PoseClip> LoadRealFamily(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("real family fixture not found: " + path);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("real family fixture root must be a JSON array");

        var clips = new List<PoseClip>(document.RootElement.GetArrayLength());
        foreach (JsonElement item in document.RootElement.EnumerateArray())
            clips.Add(ParseRealClip(item));
        return clips;
    }

    private static PoseClip FindClip(List<PoseClip> clips, string name)
    {
        for (int i = 0; i < clips.Count; i++)
        {
            if (string.Equals(clips[i].Name, name, StringComparison.Ordinal))
                return clips[i];
        }
        throw new InvalidOperationException("real family fixture is missing " + name);
    }

    private static PoseClip ParseRealClip(JsonElement item)
    {
        int frames = item.GetProperty("frames").GetInt32();
        JsonElement feet = item.GetProperty("feet");
        if (feet.ValueKind != JsonValueKind.Array || feet.GetArrayLength() < frames)
            throw new InvalidOperationException("real family fixture has incomplete feet data for " + item.GetProperty("name").GetString());

        var clip = new PoseClip
        {
            Name = item.GetProperty("name").GetString(),
            Fps = item.GetProperty("fps").GetSingle(),
            Frames = frames,
            Loop = item.GetProperty("loop").GetBoolean(),
            SpeedMetersPerSecond = item.GetProperty("speedMetersPerSecond").GetSingle(),
            Family = "real-family",
            PhaseOffset = 0,
            MoveYaw = item.GetProperty("moveYaw").GetSingle(),
            RootSpeed = ReadFloatArray(item.GetProperty("rootSpeed"), frames),
            RootVelocity = ReadVector2Array(item.GetProperty("rootVelocity"), frames),
            YawProgress = ReadFloatArray(item.GetProperty("yawProgress"), frames),
            PelvisPosition = new Vector3[frames],
            FootL = new Vector3[frames],
            FootR = new Vector3[frames],
            Stride = new StrideFoot[2],
            Contact = new bool[2][]
        };

        for (int frame = 0; frame < frames; frame++)
        {
            JsonElement row = feet[frame];
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6)
                throw new InvalidOperationException("real family fixture has an invalid feet row at frame " + frame + " for " + clip.Name);
            clip.FootL[frame] = new Vector3(row[0].GetSingle(), row[1].GetSingle(), row[2].GetSingle());
            clip.FootR[frame] = new Vector3(row[3].GetSingle(), row[4].GetSingle(), row[5].GetSingle());
        }

        JsonElement contacts = item.GetProperty("contacts");
        clip.Contact[0] = ReadBoolArray(contacts.GetProperty("L"), frames);
        clip.Contact[1] = ReadBoolArray(contacts.GetProperty("R"), frames);
        JsonElement stride = item.GetProperty("stride");
        clip.Stride[0] = ParseRealStride(stride.GetProperty("L"), frames, clip.Name + " L");
        clip.Stride[1] = ParseRealStride(stride.GetProperty("R"), frames, clip.Name + " R");
        return clip;
    }

    private static StrideFoot ParseRealStride(JsonElement item, int frames, string label)
    {
        JsonElement frameData = item.GetProperty("frames");
        var foot = new StrideFoot
        {
            Cycles = new StrideCycle[item.GetProperty("cycles").GetArrayLength()],
            Cycle = ReadIntArray(frameData.GetProperty("cycle"), frames),
            Progression = ReadFloatArray(frameData.GetProperty("progression"), frames),
            Offset = ReadVector3Array(frameData.GetProperty("translationOffset"), frames),
            RotationOffset = ReadFloatArray(frameData.GetProperty("rotationOffset"), frames),
            Footbase = ReadFootbaseArray(frameData.GetProperty("footbase"), frames),
            Heading = new float[frames],
            Grounded = ReadBoolArray(frameData.GetProperty("grounded"), frames),
            Floor = item.GetProperty("floor").GetSingle()
        };

        JsonElement footbase = frameData.GetProperty("footbase");
        for (int frame = 0; frame < frames; frame++)
            foot.Heading[frame] = footbase[frame][3].GetSingle();

        JsonElement cycles = item.GetProperty("cycles");
        for (int cycle = 0; cycle < foot.Cycles.Length; cycle++)
            foot.Cycles[cycle] = ParseRealCycle(cycles[cycle], label + " cycle " + cycle);
        return foot;
    }

    private static StrideCycle ParseRealCycle(JsonElement item, string label)
    {
        int start = item.GetProperty("startFrame").GetInt32();
        int end = item.GetProperty("endFrame").GetInt32();
        return new StrideCycle
        {
            StartFrame = start,
            EndFrame = end,
            StrikeFrame = item.GetProperty("strikeFrame").GetInt32(),
            StancePosition = ReadVector3(item.GetProperty("stancePosition")),
            StanceDirection = item.GetProperty("stanceDirection").GetSingle(),
            StrideLength = item.GetProperty("strideLength").GetSingle(),
            StrideYaw = item.GetProperty("strideYaw").GetSingle(),
            RotationChange = item.GetProperty("rotationChange").GetSingle(),
            ToStrideStartPos = ReadVector3(item.GetProperty("toStrideStartPos")),
            HasMidpoint = true,
            MiddleFrame = start + (end - start) / 2,
            MiddleOffset = ReadVector3(item.GetProperty("middleOffset")),
            MiddleProgression = item.GetProperty("middleProgression").GetSingle(),
            LiftCycle = item.GetProperty("footLiftCycle").GetSingle(),
            OffCycle = item.GetProperty("footOffCycle").GetSingle(),
            StrikeCycle = item.GetProperty("footStrikeCycle").GetSingle(),
            LandCycle = item.GetProperty("footLandCycle").GetSingle(),
            Stationary = item.GetProperty("stationary").GetBoolean(),
            VirtualStart = item.GetProperty("virtualStart").GetBoolean(),
            VirtualEnd = item.GetProperty("virtualEnd").GetBoolean()
        };
    }

    private static float[] ReadFloatArray(JsonElement array, int count)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < count)
            throw new InvalidOperationException("real family fixture has an incomplete float array");
        var result = new float[count];
        for (int i = 0; i < count; i++)
            result[i] = array[i].GetSingle();
        return result;
    }

    private static int[] ReadIntArray(JsonElement array, int count)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < count)
            throw new InvalidOperationException("real family fixture has an incomplete integer array");
        var result = new int[count];
        for (int i = 0; i < count; i++)
            result[i] = array[i].GetInt32();
        return result;
    }

    private static bool[] ReadBoolArray(JsonElement array, int count)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < count)
            throw new InvalidOperationException("real family fixture has an incomplete boolean array");
        var result = new bool[count];
        for (int i = 0; i < count; i++)
        {
            JsonElement value = array[i];
            result[i] = value.ValueKind == JsonValueKind.True ||
                (value.ValueKind != JsonValueKind.False && value.GetInt32() != 0);
        }
        return result;
    }

    private static Vector2[] ReadVector2Array(JsonElement array, int count)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < count)
            throw new InvalidOperationException("real family fixture has an incomplete Vector2 array");
        var result = new Vector2[count];
        for (int i = 0; i < count; i++)
            result[i] = new Vector2(array[i][0].GetSingle(), array[i][1].GetSingle());
        return result;
    }

    private static Vector3[] ReadVector3Array(JsonElement array, int count)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < count)
            throw new InvalidOperationException("real family fixture has an incomplete Vector3 array");
        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
            result[i] = ReadVector3(array[i]);
        return result;
    }

    private static Vector3[] ReadFootbaseArray(JsonElement array, int count)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() < count)
            throw new InvalidOperationException("real family fixture has an incomplete footbase array");
        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
            result[i] = new Vector3(array[i][0].GetSingle(), array[i][1].GetSingle(), array[i][2].GetSingle());
        return result;
    }

    private static Vector3 ReadVector3(JsonElement value)
    {
        return new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
    }

    private static int ExtendedIndex(int frame, int n)
    {
        if (n <= 0)
            return 0;
        int result = frame;
        while (result < 0)
            result += n;
        while (result > 2 * n)
            result -= n;
        return Math.Max(0, Math.Min(result, 2 * n));
    }

    private static int MiddleIndex(int frame, int start, int end, int n)
    {
        int result = ExtendedIndex(frame, n);
        while (result < start && result + n <= 2 * n)
            result += n;
        while (result > end && result - n >= 0)
            result -= n;
        return Math.Max(start, Math.Min(result, end));
    }

    private static int Wrap(int frame, int n)
    {
        if (n <= 0)
            return 0;
        int result = frame % n;
        return result < 0 ? result + n : result;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static PoseClip Clip(string name, int phaseOffset, float progressionBase, float[] progression, float moveYaw, float headingOffset)
    {
        const int n = 8;
        var clip = new PoseClip
        {
            Name = name,
            Fps = 4f,
            Frames = n,
            Loop = true,
            Family = "test",
            PhaseOffset = phaseOffset,
            MoveYaw = moveYaw,
            SpeedMetersPerSecond = 4f,
            RootSpeed = new float[n],
            RootVelocity = new Vector2[n],
            YawProgress = new float[n],
            PelvisPosition = new Vector3[n],
            FootL = new Vector3[n],
            FootR = new Vector3[n],
            Stride = new StrideFoot[2],
            Contact = new bool[2][]
        };
        clip.Contact[0] = new bool[n];
        clip.Contact[1] = new bool[n];
        for (int side = 0; side < 2; side++)
        {
            clip.Stride[side] = Stride(n, progression);
            for (int f = 0; f < n; f++)
            {
                clip.Stride[side].Footbase[f] = new Vector3(side == 0 ? -.2f : .2f, 0f, f * .05f);
                clip.Stride[side].Heading[f] = headingOffset + f * 6f;
                clip.Stride[side].Grounded[f] = true;
                clip.Contact[side][f] = true;
            }
        }
        return clip;
    }

    private static StrideFoot Stride(int n, float[] progression)
    {
        var foot = new StrideFoot
        {
            Cycles = new[]
            {
                new StrideCycle { StartFrame = 0, EndFrame = n / 2, MiddleFrame = n / 4, HasMidpoint = true },
                new StrideCycle { StartFrame = n / 2, EndFrame = n, MiddleFrame = n / 2 + n / 4, HasMidpoint = true }
            },
            Cycle = new int[n],
            Progression = (float[])progression.Clone(),
            Offset = new Vector3[n],
            RotationOffset = new float[n],
            Footbase = new Vector3[n],
            Heading = new float[n],
            Grounded = new bool[n]
        };
        for (int f = 0; f < n; f++)
            foot.Cycle[f] = f <= n / 2 ? 0 : 1;
        return foot;
    }

    private static void UseSingleCycle(PoseClip clip)
    {
        const int n = 8;
        for (int side = 0; side < 2; side++)
        {
            StrideFoot foot = clip.Stride[side];
            foot.Cycles = new[] { new StrideCycle { StartFrame = 0, EndFrame = n, MiddleFrame = n / 2, HasMidpoint = true } };
            for (int f = 0; f < n; f++)
                foot.Cycle[f] = 0;
        }
    }

    private static void SetTrajectory(PoseClip clip, float vx, float vz, float yawStart, float yawStep)
    {
        for (int f = 0; f < clip.Frames; f++)
        {
            clip.RootVelocity[f] = new Vector2(vx, vz);
            clip.RootSpeed[f] = (float)Math.Sqrt(vx * vx + vz * vz);
            clip.YawProgress[f] = yawStart + f * yawStep;
        }
    }

    private static Vector3 RotateStride(Vector3 local, Vector3 worldStride)
    {
        float yaw = (float)Math.Atan2(worldStride.x, worldStride.z);
        float sin = (float)Math.Sin(yaw);
        float cos = (float)Math.Cos(yaw);
        return new Vector3(local.x * cos + local.z * sin, local.y, -local.x * sin + local.z * cos);
    }

    private static Vector3 RotateYaw(Vector3 local, float yawDegrees)
    {
        float radians = yawDegrees * (float)Math.PI / 180f;
        float sin = (float)Math.Sin(radians);
        float cos = (float)Math.Cos(radians);
        return new Vector3(local.x * cos + local.z * sin, local.y, -local.x * sin + local.z * cos);
    }

    private static Vector3 P(float x, float y, float z) => new Vector3(x, y, z);

    private static void NearVector(Vector3 actual, Vector3 expected, string label)
    {
        if ((actual - expected).magnitude > 0.0002f)
            throw new InvalidOperationException(label + ": expected (" + expected.x + "," + expected.y + "," + expected.z + ") got (" + actual.x + "," + actual.y + "," + actual.z + ")");
    }

    private static void NearVectorWithin(Vector3 actual, Vector3 expected, string label, float tolerance)
    {
        if ((actual - expected).magnitude > tolerance)
            throw new InvalidOperationException(label + ": expected (" + expected.x + "," + expected.y + "," + expected.z + ") got (" + actual.x + "," + actual.y + "," + actual.z + ")");
    }

    private static void Near(float actual, float expected, string label)
    {
        if (Math.Abs(actual - expected) > 0.0002f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }
}
