using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private const float Dt = 0.02f;

    private static int Main()
    {
        try
        {
            LandingPredictionHasFiniteIntegralAndCaps();
            LandingPredictionRejectsInvalidInputs();
            PredictRootUsesArcAndIntegralAtBothHorizons();
            NormalApplyPublishesFeedbackAndResetClearsIt();
            WeaponCarryTracksActiveFirearmHands();
            NativeJumpReleasesGroundConstraints();
            ReleasePublishesTheCurrentSolvedAnkleUntilComplete();
            SpinReleasePreservesContinuityAndMovingRoot();
            GroundNormalAlignmentRunsWithZeroTranslationAndYaw();
            PlantedTranslationPreservesAuthoredFootRoll();
            AirborneTranslationStillSteersFootRotation();
            UnreachableContactReportsAnkleLimitAndSoleError();
            BoneLengthsAndClearanceRemainValidAfterPlacement();
            ClearanceGuardRejectsCrossingCorrection();
            ClearanceGuardPreservesOppositePlantedAnchor();
            SwingClearanceIsTranslationInvariantAtMapCoordinates();
            ReferenceWeightChangeReanchorsFromRenderedSolePair();
            LockedSwingRebasesCorrectionAcrossSoleBasis();
            AnchorFailureReentryPreservesRenderedSolePairAndHeight();
            FreshMidStrideEntryCancelsOnlyCurveDisplacement();
            StopEntryLeavesARealLandingForBothFeet();
            StopFinalAirborneCorrectionUsesDeadlineAndTerminalTarget();
            AlreadyGroundedStopFootKeepsItsAnchor();
            StopSettlementMovesOneFootHoldsSupportAndClearsOnCancel();
            Console.WriteLine("runtime_foot_placer: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void LandingPredictionHasFiniteIntegralAndCaps()
    {
        Near(LandingPrediction.VelocityCorrection(1f, .1f, .25f, 1f), .08f, "ramp integral");
        Near(LandingPrediction.VelocityCorrection(-1f, .1f, .25f, 1f), -.08f, "negative ramp integral");
        Near(LandingPrediction.VelocityCorrection(1f, .5f, .25f, 1f), .125f, "horizon clamped to duration");
        Near(LandingPrediction.VelocityCorrection(2f, .1f, .25f, .15f), .15f, "positive correction cap");
        Near(LandingPrediction.VelocityCorrection(-2f, .1f, .25f, .15f), -.15f, "negative correction cap");
        float huge = LandingPrediction.VelocityCorrection(float.MaxValue, .25f, .25f, .15f);
        if (float.IsNaN(huge) || float.IsInfinity(huge) || Math.Abs(huge - .15f) > .0001f)
            throw new InvalidOperationException("finite input with a finite cap must produce a finite capped result");
    }

    private static void StopEntryLeavesARealLandingForBothFeet()
    {
        var left = new StrideFoot { Cycles = new[] {
            new StrideCycle { StartFrame = 13, EndFrame = 71, StrikeFrame = 32, LiftCycle = 0f, StrikeCycle = .3276f },
            new StrideCycle { StartFrame = 71, EndFrame = 80, StrikeFrame = 80, LiftCycle = 1f, StrikeCycle = 1f, VirtualEnd = true }
        }};
        var right = new StrideFoot { Cycles = new[] {
            new StrideCycle { StartFrame = 0, EndFrame = 71, StrikeFrame = 12, LiftCycle = 0f, StrikeCycle = .169f }
        }};
        var strides = new[] { left, right };
        if (StopLanding.LastStep(left) != 0 || StopLanding.CanEnter(strides, 12, 30f)
            || !StopLanding.CanEnter(strides, 6, 30f) || StopLanding.CanEnter(strides, 8, 30f))
            throw new InvalidOperationException("stop entry must retain a real landing and approach time on each foot");
        Near(StopLanding.LandingWeight(right.Cycles[0], 0f), 0f, "no final landing authority at lift");
        Near(StopLanding.LandingWeight(right.Cycles[0], .169f), 1f, "full final landing authority at strike");
        var held = new StrideFoot { Cycles = new[] { new StrideCycle { StartFrame = 0, EndFrame = 71,
            StrikeFrame = 71, LiftCycle = 1f, StrikeCycle = 1f } } };
        if (StopLanding.LastStep(held) != -1 || StopLanding.CanEnter(new[] { left, held }, 0, 30f))
            throw new InvalidOperationException("an all-grounded tail cannot count as a corrective step");
    }

    private static void LandingPredictionRejectsInvalidInputs()
    {
        Near(LandingPrediction.VelocityCorrection(0f, .2f, .25f, .15f), 0f, "zero delta");
        Near(LandingPrediction.VelocityCorrection(float.NaN, .2f, .25f, .15f), 0f, "NaN delta");
        Near(LandingPrediction.VelocityCorrection(float.PositiveInfinity, .2f, .25f, .15f), 0f, "infinite delta");
        Near(LandingPrediction.VelocityCorrection(1f, 0f, .25f, .15f), 0f, "zero horizon");
        Near(LandingPrediction.VelocityCorrection(1f, .2f, 0f, .15f), 0f, "zero duration");
        Near(LandingPrediction.VelocityCorrection(1f, .2f, .25f, 0f), 0f, "zero maximum");
        Near(LandingPrediction.VelocityCorrection(1f, .2f, float.NaN, .15f), 0f, "NaN duration");
        Near(LandingPrediction.VelocityCorrection(1f, .2f, float.PositiveInfinity, .15f), 0f, "infinite duration");
        Near(LandingPrediction.VelocityCorrection(1f, .2f, .25f, float.NaN), 0f, "NaN maximum");
        Near(LandingPrediction.VelocityCorrection(1f, .2f, .25f, float.PositiveInfinity), 0f, "infinite maximum");
    }

    private static void PredictRootUsesArcAndIntegralAtBothHorizons()
    {
        PoseClip clip = Clip();
        clip.RootSpeed[1] = 1f;
        var state = new StrideState
        {
            Speed = 1.4f,
            Rate = 1f,
            DistanceScale = 1f,
            TravelYawRate = 90f,
            Stopping = false
        };
        Vector3 origin = new Vector3(2f, 0f, -1f);
        Vector3 travel = new Vector3(0f, 0f, 1f);
        Vector3 end = InvokePredictRoot(clip, 1, state, origin, travel, .4f, .5f);
        Vector3 middle = InvokePredictRoot(clip, 1, state, origin, travel, .4f, .125f);
        NearVector(end, ExpectedPredictedRoot(origin, travel, .4f, .5f, 1.4f - 1f, 90f), "end horizon prediction", .0001f);
        NearVector(middle, ExpectedPredictedRoot(origin, travel, .4f, .125f, 1.4f - 1f, 90f), "middle horizon prediction", .0001f);
        if ((end - middle).magnitude < .001f)
            throw new InvalidOperationException("different turn horizons must produce different arc positions");
    }

    private static Vector3 InvokePredictRoot(PoseClip clip, int frame, StrideState state, Vector3 origin, Vector3 travel, float distance, float seconds)
    {
        MethodInfo method = typeof(FootPlacer).GetMethod("PredictRoot", BindingFlags.Static | BindingFlags.NonPublic);
        return (Vector3)method.Invoke(null, new object[] { clip, frame, state, origin, travel, null, distance, seconds });
    }

    private static Vector3 ExpectedPredictedRoot(Vector3 origin, Vector3 travel, float distance, float seconds, float delta, float yawRate)
    {
        float corrected = Math.Max(0f, distance + LandingPrediction.VelocityCorrection(delta, seconds, .25f, .15f));
        float turned = Math.Max(-90f, Math.Min(90f, yawRate * seconds));
        if (Math.Abs(turned) <= 5f)
            return origin + travel * corrected;
        float radians = turned * Mathf.Deg2Rad;
        float half = Math.Abs(radians) * .5f;
        float chord = corrected * Mathf.Sin(half) / half;
        return origin + Quaternion.Euler(0f, turned * .5f, 0f) * travel * chord;
    }

    private static void FreshMidStrideEntryCancelsOnlyCurveDisplacement()
    {
        Rig rig = Rig.Create();
        PoseClip clip = MidpointClip();
        PosePlayback playback = Playback(clip, 2f);
        rig.Placer.GroundNormalProvider = side => null;
        Step(500);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("fresh mid-stride fixture apply failed");
        FootPlacerProbe first = rig.Placer.Probe(0);
        if (!first.Applied || !(first.MidpointCorrection > .1f))
            throw new InvalidOperationException("mid-stride entry must compute the shifted midpoint curve");
        if (!(first.Residual > .05f && first.Residual < .2f))
            throw new InvalidOperationException("mid-stride entry must carry the curve cancellation as a decaying residual: " + first.Residual);
        if (!(first.Correction < first.MidpointCorrection - .05f))
            throw new InvalidOperationException("fresh entry applied the full curve correction in its first frame: curve=" + first.MidpointCorrection + ", correction=" + first.Correction);

        Step(501);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("second mid-stride residual frame failed");
        FootPlacerProbe second = rig.Placer.Probe(0);
        if (!(second.Residual < first.Residual - .005f))
            throw new InvalidOperationException("curve cancellation residual must continue at the existing decay rate: first=" + first.Residual + ", second=" + second.Residual);
        if (!(second.Correction < first.MidpointCorrection - .03f))
            throw new InvalidOperationException("second frame still applied an unbounded midpoint jump: curve=" + first.MidpointCorrection + ", correction=" + second.Correction);
    }

    private static void NormalApplyPublishesFeedbackAndResetClearsIt()
    {
        Rig rig = Rig.Create();
        PoseClip clip = Clip();
        PosePlayback playback = Playback(clip, 0f);
        Step(1);

        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("normal stride apply should succeed");
        if (!rig.Placer.Probe(0).Applied)
            throw new InvalidOperationException("normal apply must mark feedback as applied on the current frame");
        Vector3? ankle = rig.Placer.LastAnkle(0);
        Vector3? basePoint = rig.Placer.LastFootbase(0);
        if (!ankle.HasValue || !basePoint.HasValue || !rig.Placer.LastProgression(0).HasValue)
            throw new InvalidOperationException("normal apply must publish ankle, sole, and progression feedback");
        if (!rig.Placer.CurrentPlacementCost().HasValue)
            throw new InvalidOperationException("normal apply must publish placement cost after both feet solve");
        if ((ankle.Value - rig.LeftFoot.position).magnitude > .0001f)
            throw new InvalidOperationException("published ankle must be the current solved ankle");
        Step(2);
        if (rig.Placer.Probe(0).Applied)
            throw new InvalidOperationException("applied marker must expire when the frame advances");

        rig.Placer.Reset();
        if (rig.Placer.LastAnkle(0).HasValue || rig.Placer.LastFootbase(0).HasValue || rig.Placer.LastProgression(0).HasValue || rig.Placer.CurrentPlacementCost().HasValue)
            throw new InvalidOperationException("Reset must clear all stale placer feedback");
    }

    private static void NativeJumpReleasesGroundConstraints()
    {
        Rig rig = Rig.Create();
        var playback = Playback(Clip(), 0f);
        Step(2100);
        if (!rig.Placer.Apply(playback)) throw new InvalidOperationException("jump fixture setup failed");
        rig.Root.position += new Vector3(0f, 0f, .2f);
        Step(2101);
        rig.Placer.Apply(playback);
        var before = rig.LeftFoot.position;
        rig.Player.MovementContext.IsGrounded = false;
        rig.Player.MovementContext.CurrentState.Name = EPlayerState.Jump;
        Step(2102);
        if (rig.Placer.Apply(playback)) throw new InvalidOperationException("airborne placement must yield");
        NearVector(rig.LeftFoot.position, before, "airborne guard must not write feet");
        if (rig.Placer.LastAnkle(0).HasValue) throw new InvalidOperationException("stale airborne ankle feedback");
        rig.Player.MovementContext.IsGrounded = true;
        rig.Player.MovementContext.CurrentState.Name = EPlayerState.JumpLanding;
        Step(2103);
        if (rig.Placer.Apply(playback)) throw new InvalidOperationException("grounded native landing must still own feet");
        NearVector(rig.LeftFoot.position, before, "landing guard must not write feet");
        rig.Player.MovementContext.CurrentState.Name = EPlayerState.Run;
        rig.Player.CurrentManagedState = new MovementState { Name = EPlayerState.Jump };
        if (!NativeJumpOwnership.Owns(rig.Player)) throw new InvalidOperationException("managed jump transition lost");
        rig.Player.CurrentManagedState = null;
        rig.Player.MovementContext.IsGrounded = false;
        if (NativeJumpOwnership.Owns(rig.Player)) throw new InvalidOperationException("ground-contact chatter must not reset ordinary locomotion");
        rig.Player.MovementContext.IsGrounded = true;
        Step(2104);
        if (!rig.Placer.Apply(playback)) throw new InvalidOperationException("grounded running must resume placement");
        foreach (EPlayerState state in new[] { EPlayerState.Jump, EPlayerState.JumpLanding, EPlayerState.FallDown,
            EPlayerState.ClimbOver, EPlayerState.ClimbUp, EPlayerState.VaultingFallDown, EPlayerState.VaultingLanding })
            if (!NativeJumpOwnership.OwnsState(state)) throw new InvalidOperationException("missing native state: " + state);
        if (NativeJumpOwnership.OwnsState(EPlayerState.Run) || NativeJumpOwnership.OwnsState(EPlayerState.Sprint))
            throw new InvalidOperationException("normal locomotion must remain available");
    }

    private static void WeaponCarryTracksActiveFirearmHands()
    {
        Rig rig = Rig.Create();
        var mount = new Transform { position = new Vector3(2f, 1f, -1f) };
        var follower = new Transform { position = new Vector3(2.2f, 1.1f, -1f) };
        rig.Placer.WeaponMount = mount;
        rig.Placer.WeaponFollower = follower;
        var playback = Playback(Clip(), 0f);
        var rifle = new Player.FirearmController { Item = new Weapon() };
        var secondRifle = new Player.FirearmController { Item = new Weapon() };
        var grenade = new Player.GrenadeHandsController();

        rig.Player.HandsController = rifle;
        AssertWeaponCarry(rig, playback, mount, follower, 2000, null, true, "active rifle must follow placer movement");

        rig.Player.HandsController = grenade;
        AssertWeaponCarry(rig, playback, mount, follower, 2001, null, false, "grenade hands must leave the stowed rifle follower alone");

        rig.Player.HandsController = rifle;
        rifle.CurrentOperation = new Player.FirearmController.Remove();
        AssertWeaponCarry(rig, playback, mount, follower, 2002, null, false, "firearm hide operation must not carry a stowing weapon");

        rifle.CurrentOperation = new Player.FirearmController.SpawnOperation();
        AssertWeaponCarry(rig, playback, mount, follower, 2003, null, false, "firearm spawn operation must not carry a drawing weapon");

        rifle.CurrentOperation = null;
        rig.Player.HandsController = rifle;
        AssertWeaponCarry(rig, playback, mount, follower, 2004, null, true, "rifle carry must resume after a hands transition");

        rig.Player.HandsController = rifle;
        AssertWeaponCarry(rig, playback, mount, follower, 2005, () => rig.Player.HandsController = grenade, false,
            "a grenade switch during ApplyCore must invalidate that frame's rifle capture");

        rig.Player.HandsController = rifle;
        AssertWeaponCarry(rig, playback, mount, follower, 2006, () => rig.Player.HandsController = secondRifle, false,
            "a firearm swap during ApplyCore must invalidate the old weapon capture");

        rig.Player.HandsController = secondRifle;
        AssertWeaponCarry(rig, playback, mount, follower, 2007, null, true, "the new active firearm must carry on the next frame");
    }

    private static void AssertWeaponCarry(Rig rig, PosePlayback playback, Transform mount, Transform follower, int frame,
        Action duringApplyCore, bool expectCarry, string label)
    {
        Vector3 mountBefore = mount.position;
        Vector3 followerBefore = follower.position;
        playback.OnTryGetStrideState = () =>
        {
            mount.position += new Vector3(.1f, 0f, 0f);
            duringApplyCore?.Invoke();
        };
        Step(frame);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException(label + ": fixture placer did not apply");

        Vector3 expected = expectCarry ? followerBefore + (mount.position - mountBefore) : followerBefore;
        NearVector(follower.position, expected, label);
    }

    private static void ReleasePublishesTheCurrentSolvedAnkleUntilComplete()
    {
        Rig rig = Rig.Create();
        PoseClip clip = Clip();
        PosePlayback playback = Playback(clip, 0f);
        Step(10);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("release fixture setup apply failed");

        // Move the body while the pre-lift plant is locked. This creates a correction that Release must bleed out.
        rig.Root.position += new Vector3(0f, 0f, .2f);
        Step(11);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("translated planted apply should succeed");
        Vector3 beforeFade = rig.Placer.LastAnkle(0).Value;
        if (rig.Placer.Probe(0).Correction < .01f)
            throw new InvalidOperationException("translated plant should carry a release correction");

        playback.State.Fading = true;
        Step(12);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("first fading frame should publish a release solve");
        Vector3 afterFade = rig.Placer.LastAnkle(0).Value;
        if ((afterFade - rig.LeftFoot.position).magnitude > .0001f)
            throw new InvalidOperationException("release feedback must report the ankle after the current release solve");
        if ((afterFade - beforeFade).magnitude < .00001f)
            throw new InvalidOperationException("release frame should advance the held correction");

        bool completed = false;
        for (int frame = 13; frame < 160; frame++)
        {
            Step(frame);
            if (!rig.Placer.Apply(playback))
            {
                completed = true;
                break;
            }
            Vector3? current = rig.Placer.LastAnkle(0);
            if (!current.HasValue || (current.Value - rig.LeftFoot.position).magnitude > .0001f)
                throw new InvalidOperationException("every fading frame must publish current ankle feedback");
        }
        if (!completed)
            throw new InvalidOperationException("release should eventually finish and return false");
        if (rig.Placer.LastAnkle(0).HasValue || rig.Placer.CurrentPlacementCost().HasValue)
            throw new InvalidOperationException("completed release must clear feedback instead of leaving stale values");
    }

    private static void SpinReleasePreservesContinuityAndMovingRoot()
    {
        Rig rig = Rig.Create();
        // Give the straight fixture physical bend room for the translated plant and 45-degree body turn.
        rig.Pelvis.localPosition = new Vector3(0f, .1f + .9f * Mathf.Cos(30f * Mathf.Deg2Rad), 0f);
        rig.LeftThigh.localRotation = rig.RightThigh.localRotation = Quaternion.Euler(-30f, 0f, 0f);
        rig.LeftCalf.localRotation = rig.RightCalf.localRotation = Quaternion.Euler(60f, 0f, 0f);
        rig.LeftFoot.localRotation = rig.RightFoot.localRotation = Quaternion.Euler(-30f, 0f, 0f);
        PoseSnapshot rawPose = PoseSnapshot.Capture(rig);
        PosePlayback playback = Playback(Clip(), 0f);
        int frame = 300;
        float rootZ = 0f;
        Action<float> prepare = yaw =>
        {
            rawPose.Restore();
            rig.Root.position = new Vector3(0f, 0f, rootZ);
            rig.Root.rotation = Quaternion.Euler(0f, yaw, 0f);
            Step(frame++);
        };
        prepare(0f);
        if (!rig.Placer.Apply(playback)) throw new InvalidOperationException("spin setup apply failed");
        for (int i = 0; i < 6; i++)
        {
            rootZ += .02f;
            prepare(0f);
            rig.Placer.Apply(playback);
        }
        if (((Vector3)GetField(rig.Placer, "_hipShift")).magnitude < .02f
            || !rig.Placer.Probe(0).Locked || rig.Placer.Probe(0).Correction < .03f)
            throw new InvalidOperationException("spin fixture requires a displaced plant and pelvis correction");

        foreach (float yawRate in new[] { 197f, 198f })
        {
            Vector3 soleBefore = SoleCenter(rig.LeftFoot), pelvisBefore = rig.Pelvis.position;
            float headingBefore = SoleHeading(rig.LeftFoot);
            playback.State.SpinSuppression = Mathf.InverseLerp(90f, 200f, yawRate);
            playback.State.Weight = 1f - playback.State.SpinSuppression;
            playback.State.YawRate = yawRate;
            prepare(yawRate == 197f ? 0f : 45f);
            if (!rig.Placer.Apply(playback) || !rig.Placer.Probe(0).Applied)
                throw new InvalidOperationException("spin cutoff must retain the release solve");
            if ((SoleCenter(rig.LeftFoot) - soleBefore).magnitude > .010f)
                throw new InvalidOperationException("spin cutoff/yaw jump exceeded sole release budget: " + (SoleCenter(rig.LeftFoot) - soleBefore).magnitude + " yaw=" + yawRate + " clearance=" + rig.Placer.Probe(0).ClearanceFraction + " before=" + Format(soleBefore) + " after=" + Format(SoleCenter(rig.LeftFoot)) + " heading=" + SoleHeading(rig.LeftFoot));
            if ((rig.Pelvis.position - pelvisBefore).magnitude > .0101f)
                throw new InvalidOperationException("spin cutoff discarded the pelvis correction");
            if (Math.Abs(Mathf.DeltaAngle(headingBefore, SoleHeading(rig.LeftFoot))) > 180f * Dt + .05f)
                throw new InvalidOperationException("spin raw-yaw rebase exceeded rendered heading release budget");
        }
        Vector3 movingStart = SoleCenter(rig.LeftFoot);
        prepare(45f);
        float initialOffset = (movingStart - SoleCenter(rig.LeftFoot)).magnitude;
        for (int i = 0; i < 10; i++)
        {
            rootZ += 2f * Dt;
            prepare(45f);
            rig.Placer.Apply(playback);
        }
        Vector3 movingEnd = SoleCenter(rig.LeftFoot);
        prepare(45f);
        float finalOffset = (movingEnd - SoleCenter(rig.LeftFoot)).magnitude;
        if (finalOffset >= initialOffset - .02f || movingEnd.z - movingStart.z < .3f)
            throw new InvalidOperationException("spin release stranded the feet behind a moving root");
        // Restore the last actual solve before continuing; prepare above sampled the raw target only.
        rig.Placer.Apply(playback);
        playback.State.SpinSuppression = 0f;
        playback.State.Weight = 1f;
        playback.State.YawRate = 0f;
        bool completed = false;
        for (int i = 0; i < 120; i++)
        {
            float previousHeading = SoleHeading(rig.LeftFoot);
            rootZ += 2f * Dt;
            prepare(45f);
            rig.Placer.Apply(playback);
            if (Math.Abs(Mathf.DeltaAngle(previousHeading, SoleHeading(rig.LeftFoot))) > 180f * Dt + .05f)
                throw new InvalidOperationException("clearing suppression must finish the large heading release");
            if (!(bool)GetField(rig.Placer, "_spinReleasing")) { completed = true; break; }
        }
        if (!completed) throw new InvalidOperationException("spin release failed to finish after suppression cleared");
        Vector3 resumeSole = SoleCenter(rig.LeftFoot);
        float resumeHeading = SoleHeading(rig.LeftFoot);
        prepare(45f);
        if (!rig.Placer.Apply(playback)) throw new InvalidOperationException("normal placement did not resume");
        if ((SoleCenter(rig.LeftFoot) - resumeSole).magnitude > .02f
            || Math.Abs(Mathf.DeltaAngle(resumeHeading, SoleHeading(rig.LeftFoot))) > 180f * Dt + .05f)
            throw new InvalidOperationException("normal placement popped on spin release re-entry");
    }

    private static void GroundNormalAlignmentRunsWithZeroTranslationAndYaw()
    {
        Rig rig = Rig.Create(footTiltDegrees: 4f);
        PoseClip clip = Clip();
        PosePlayback playback = Playback(clip, 0f);
        rig.Placer.GroundNormalProvider = side => new Vector3(.15f, .98f, .04f).normalized;
        Quaternion before = rig.LeftFoot.rotation;
        Step(200);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("zero-motion tilted sole apply should succeed");
        FootPlacerProbe probe = rig.Placer.Probe(0);
        if (!probe.GroundNormalAvailable)
            throw new InvalidOperationException("ground normal must be available for the contacting foot");
        if (Quaternion.Angle(before, rig.LeftFoot.rotation) < .01f)
            throw new InvalidOperationException("ground normal alignment should change a tilted sole");
    }

    private static void PlantedTranslationPreservesAuthoredFootRoll()
    {
        foreach (float tilt in new[] { 0f, 25f })
        {
            Rig rig = Rig.Create();
            rig.LeftCalf.localPosition = new Vector3(0f, -.4f, .15f);
            rig.LeftFoot.localPosition = new Vector3(0f, -.4f, -.15f);
            rig.LeftFoot.rotation = Quaternion.Euler(tilt, 0f, 0f);
            // No terrain normal isolates placement pitch; a steep authored toe-off also
            // remains intact with a normal because its sole is deliberately not flat.
            rig.Placer.GroundNormalProvider = side => tilt == 0f ? (Vector3?)null : Vector3.up;
            object foot = GetPrivateFeet(rig.Placer).GetValue(0);
            Invoke(foot, "ReadShown", Dt);
            Quaternion authored = rig.LeftFoot.rotation;
            Vector3 shown = (Vector3)GetField(foot, "ShownBase");
            float heading = (float)GetField(foot, "ShownHeading");
            Invoke(rig.Placer, "Place", foot, shown + new Vector3(0f, 0f, -.1f), heading, 1f, true, Dt, false);
            Near(Quaternion.Angle(authored, rig.LeftFoot.rotation), 0f, "planted backward correction must preserve foot roll", .05f);
        }
    }

    private static void AirborneTranslationStillSteersFootRotation()
    {
        Rig rig = Rig.Create();
        rig.LeftCalf.localPosition = new Vector3(0f, -.4f, .15f);
        rig.LeftFoot.localPosition = new Vector3(0f, -.4f, -.15f);
        rig.Placer.GroundNormalProvider = side => null;
        object foot = GetPrivateFeet(rig.Placer).GetValue(0);
        Invoke(foot, "ReadShown", Dt);
        Quaternion authored = rig.LeftFoot.rotation;
        Vector3 ankle = rig.LeftFoot.position;
        Vector3 shown = (Vector3)GetField(foot, "ShownBase");
        float heading = (float)GetField(foot, "ShownHeading");
        Invoke(rig.Placer, "Place", foot, shown + new Vector3(0f, 0f, -.1f), heading, 1f, false, Dt, false);
        Vector3 move = (Vector3)GetField(foot, "AppliedMove");
        var expected = AnkleAlignment.ComputeHipAnkleSwing(rig.LeftThigh.position, ankle,
            rig.LeftThigh.position, ankle + move, authored);
        if (!expected.Valid || Quaternion.Angle(authored, expected.TargetFootRotation) < .1f)
            throw new InvalidOperationException("airborne test must exercise nonzero ankle swing");
        Near(Quaternion.Angle(expected.TargetFootRotation, rig.LeftFoot.rotation), 0f, "airborne steering keeps hip-ankle swing", .05f);
    }

    private static void UnreachableContactReportsAnkleLimitAndSoleError()
    {
        Rig rig = Rig.Create();
        PoseClip clip = Clip();
        PosePlayback playback = Playback(clip, 0f);
        Step(300);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("unreachable fixture setup apply failed");

        object foot = GetPrivateFeet(rig.Placer).GetValue(0);
        Invoke(foot, "ReadShown", Dt);
        SetField(foot, "Yaw", 0f);
        SetField(foot, "ShownHeading", 0f);
        rig.Placer.GroundNormalProvider = side => new Vector3(1f, .04f, 0f).normalized;
        Quaternion authoredRelative = Quaternion.Inverse(rig.LeftCalf.rotation) * rig.LeftFoot.rotation;
        Vector3 shown = (Vector3)GetField(foot, "ShownBase");
        Invoke(rig.Placer, "Place", foot, shown + new Vector3(.75f, 0f, 0f), 0f, 1f, true, Dt, false);
        Invoke(foot, "ReadPlaced");

        FootPlacerProbe probe = rig.Placer.Probe(0);
        if (!probe.GroundNormalAvailable)
            throw new InvalidOperationException("unreachable solve must still record the available ground normal");
        if (!(probe.AnkleLimitDegrees > .01f))
            throw new InvalidOperationException("unreachable steep contact must report ankle limitation");
        if (!(probe.SoleTargetError > .001f))
            throw new InvalidOperationException("unreachable contact must expose remaining sole target error");

        Quaternion finalRelative = Quaternion.Inverse(rig.LeftCalf.rotation) * rig.LeftFoot.rotation;
        float finalAnkleDelta = Quaternion.Angle(authoredRelative, finalRelative);
        if (finalAnkleDelta > 45.05f)
            throw new InvalidOperationException("final relative ankle rotation exceeded 45 degrees: " + finalAnkleDelta);
    }

    private static void BoneLengthsAndClearanceRemainValidAfterPlacement()
    {
        Rig rig = Rig.Create();
        PoseClip clip = Clip();
        PosePlayback playback = Playback(clip, 0f);
        float leftThighCalf = (rig.LeftCalf.position - rig.LeftThigh.position).magnitude;
        float leftCalfFoot = (rig.LeftFoot.position - rig.LeftCalf.position).magnitude;
        float rightThighCalf = (rig.RightCalf.position - rig.RightThigh.position).magnitude;
        float rightCalfFoot = (rig.RightFoot.position - rig.RightCalf.position).magnitude;
        Step(400);
        rig.Root.position += new Vector3(.18f, 0f, .08f);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("clearance fixture apply failed");

        Near((rig.LeftCalf.position - rig.LeftThigh.position).magnitude, leftThighCalf, "left thigh length", .0001f);
        Near((rig.LeftFoot.position - rig.LeftCalf.position).magnitude, leftCalfFoot, "left shin length", .0001f);
        Near((rig.RightCalf.position - rig.RightThigh.position).magnitude, rightThighCalf, "right thigh length", .0001f);
        Near((rig.RightFoot.position - rig.RightCalf.position).magnitude, rightCalfFoot, "right shin length", .0001f);

        FootPlacerProbe probe = rig.Placer.Probe(0);
        float actualClearance = LegClearance.Minimum(rig.LeftThigh.position, rig.LeftCalf.position, rig.LeftFoot.position,
            rig.RightThigh.position, rig.RightCalf.position, rig.RightFoot.position);
        if (float.IsNaN(actualClearance) || float.IsInfinity(actualClearance) || actualClearance < .06f - .0001f)
            throw new InvalidOperationException("placed leg geometry violates the clearance floor: " + actualClearance);
        if (probe.ClearanceAfter + .0001f < LegClearance.Required(probe.ClearanceBefore))
            throw new InvalidOperationException("clearance guard metrics report a floor violation");
        if (probe.ClearanceFraction < 0f || probe.ClearanceFraction > 1f)
            throw new InvalidOperationException("clearance guard fraction must stay in [0,1]");
    }

    private static void ClearanceGuardRejectsCrossingCorrection()
    {
        Rig rig = Rig.Create();
        object foot = GetPrivateFeet(rig.Placer).GetValue(0);
        Invoke(foot, "ReadShown", Dt);
        Invoke(rig.Placer, "BeginClearance");
        Vector3 shown = (Vector3)GetField(foot, "ShownBase");
        Invoke(rig.Placer, "Place", foot, shown + new Vector3(.7f, 0f, 0f), 0f, 1f, true, Dt, false);
        Invoke(foot, "ReadPlaced");
        Invoke(rig.Placer, "FinishClearance");

        FootPlacerProbe probe = rig.Placer.Probe(0);
        if (probe.ClearanceFraction >= 1f || rig.Placer.ClearanceLimits <= 0)
            throw new InvalidOperationException("crossing correction should activate the clearance guard");
        if (probe.ClearanceAfter + .0001f < LegClearance.Required(probe.ClearanceBefore))
            throw new InvalidOperationException("clearance guard accepted a below-floor pose");
    }

    private static void ClearanceGuardPreservesOppositePlantedAnchor()
    {
        Rig rig = Rig.Create();
        Array feet = GetPrivateFeet(rig.Placer);
        object left = feet.GetValue(0);
        object right = feet.GetValue(1);
        Invoke(left, "ReadShown", Dt);
        Invoke(right, "ReadShown", Dt);

        // The public Apply path marks this state while the right support leg is locked.  Set up the
        // same per-foot state here so the test can isolate FinishClearance's hierarchical rollback.
        Vector3 rightBeforeThigh = rig.RightThigh.position;
        Vector3 rightBeforeCalf = rig.RightCalf.position;
        Vector3 rightBeforeFoot = rig.RightFoot.position;
        Vector3 lockBase = (Vector3)GetField(right, "ShownBase");
        Vector3 lockHeel = (Vector3)GetField(right, "ShownHeel");
        Vector3 lockToe = (Vector3)GetField(right, "ShownToe");
        float lockHeading = (float)GetField(right, "ShownHeading");
        SetField(right, "HasCycle", true);
        SetField(right, "NextFrozen", true);
        SetField(right, "Locked", true);
        SetField(right, "LockBase", lockBase);
        SetField(right, "LockHeel", lockHeel);
        SetField(right, "LockToe", lockToe);
        SetField(right, "LockHeading", lockHeading);
        SetField(right, "PendingPlanted", true);

        Invoke(rig.Placer, "BeginClearance");
        Vector3 shown = (Vector3)GetField(left, "ShownBase");
        Vector3 crossingTarget = shown + new Vector3(.7f, 0f, 0f);
        for (int frame = 0; frame < 32; frame++)
        {
            Invoke(left, "ReadShown", Dt);
            Invoke(rig.Placer, "Place", left, crossingTarget, 0f, 1f, false, Dt, false);
        }
        Invoke(left, "ReadPlaced");
        Invoke(rig.Placer, "FinishClearance");

        FootPlacerProbe probe = rig.Placer.Probe(1);
        if (rig.Placer.ClearanceLimits <= 0 || probe.ClearanceFraction >= 1f)
            throw new InvalidOperationException("hierarchical clearance rollback was not exercised");
        if ((rig.RightThigh.position - rightBeforeThigh).magnitude > .00001f
            || (rig.RightCalf.position - rightBeforeCalf).magnitude > .00001f
            || (rig.RightFoot.position - rightBeforeFoot).magnitude > .00001f)
            throw new InvalidOperationException("clearance rollback moved the planted opposite leg in world space");
        if (!rig.Placer.IsAnchored(1) || !probe.Locked || !(bool)GetField(right, "Locked"))
            throw new InvalidOperationException("clearance rollback dropped the planted opposite leg's anchor");
        NearVector((Vector3)GetField(right, "LockBase"), lockBase, "preserved lock base", .00001f);
        NearVector((Vector3)GetField(right, "LockHeel"), lockHeel, "preserved lock heel", .00001f);
        NearVector((Vector3)GetField(right, "LockToe"), lockToe, "preserved lock toe", .00001f);
        if (probe.ClearanceAfter + .0001f < LegClearance.Required(probe.ClearanceBefore))
            throw new InvalidOperationException("hierarchical clearance rollback accepted a below-floor pose");
    }

    private static void SwingClearanceIsTranslationInvariantAtMapCoordinates()
    {
        Vector3 shift = new Vector3(-500f, 0f, -500f);
        SwingClearanceResult local = SwingClearance.Choose(
            new Vector3(.75f, 0f, 0f), new Vector3(.75f, 0f, 0f), new Vector3(-.02f, 0f, 0f),
            new Vector3(.75f, 1f, 0f), new Vector3(.75f, 1f, -1f), .6f, .6f,
            new Vector3(0f, 1f, 0f), new Vector3(0f, .5f, 0f), new Vector3(0f, 0f, 0f),
            Vector3.right, .06f);
        SwingClearanceResult world = SwingClearance.Choose(
            new Vector3(.75f, 0f, 0f) + shift, new Vector3(.75f, 0f, 0f) + shift, new Vector3(-.02f, 0f, 0f) + shift,
            new Vector3(.75f, 1f, 0f) + shift, new Vector3(.75f, 1f, -1f) + shift, .6f, .6f,
            new Vector3(0f, 1f, 0f) + shift, new Vector3(0f, .5f, 0f) + shift, new Vector3(0f, 0f, 0f) + shift,
            Vector3.right, .06f);
        if (!local.Valid || !local.Resolved || !world.Valid || !world.Resolved
            || local.EndpointsBlocked != world.EndpointsBlocked)
            throw new InvalidOperationException("translated swing-clearance fixture changed validity");
        Near(local.OffsetDistance, world.OffsetDistance, "translated swing offset", .0001f);
        Near(local.ClearanceBefore, world.ClearanceBefore, "translated swing clearance before", .0001f);
        Near(local.ClearanceAfter, world.ClearanceAfter, "translated swing clearance after", .0001f);
        NearVector(local.MidpointOffset, world.MidpointOffset, "translated swing midpoint offset", .0001f);
    }

    private static void ReferenceWeightChangeReanchorsFromRenderedSolePair()
    {
        Rig rig = Rig.Create(footTiltDegrees: 8f);
        rig.Placer.GroundNormalProvider = side => null;
        PoseClip clip = Clip();
        PosePlayback playback = Playback(clip, 0f);
        Step(550);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("reference-weight lock setup failed");

        object foot = GetPrivateFeet(rig.Placer).GetValue(0);
        FootPlacerProbe before = rig.Placer.Probe(0);
        Vector3 previousAnkle = rig.LeftFoot.position;
        Vector3 previousHeel = Unflat(before.PlacedHeel);
        Vector3 previousToe = Unflat(before.PlacedToe);
        float previousWeight = (float)GetField(foot, "_baseW");
        if (!(previousWeight <= .01f))
            throw new InvalidOperationException("reference-weight fixture did not start from the heel endpoint: " + previousWeight);
        SetField(foot, "Locked", false);
        SetField(foot, "NextFrozen", true);

        // Move through a non-endpoint base weight, then flatten the sole.  The hysteresis in BaseWeight
        // makes the final frame's reference exactly the midpoint while HasPlaced still contains the prior
        // rendered heel/toe pair.
        rig.LeftFoot.rotation = Quaternion.Euler(-3f, 0f, 0f);
        Invoke(foot, "ReadShown", Dt);
        float middleWeight = (float)GetField(foot, "_baseW");
        rig.LeftFoot.rotation = Quaternion.identity;
        playback.State.Frame = 2f;
        Step(551);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("reference-weight relock failed");

        FootPlacerProbe after = rig.Placer.Probe(0);
        float currentWeight = (float)GetField(foot, "_baseW");
        Vector3 expectedReference = Vector3.Lerp(previousToe, previousHeel, currentWeight);
        Vector3 lockHeel = (Vector3)GetField(foot, "LockHeel");
        Vector3 lockToe = (Vector3)GetField(foot, "LockToe");
        if (!(middleWeight > .01f && middleWeight < .99f))
            throw new InvalidOperationException("reference-weight fixture did not pass through an interior weight: " + middleWeight);
        if (!(currentWeight > .35f && currentWeight < .65f))
            throw new InvalidOperationException("reference-weight fixture did not move to the midpoint: middle=" + middleWeight + " current=" + currentWeight);
        NearVector(lockHeel, previousHeel, "relock heel endpoint", .002f);
        NearVector(lockToe, previousToe, "relock toe endpoint", .002f);
        if (HorizontalDelta(Unflat(after.Placed), expectedReference) > .02f)
            throw new InvalidOperationException("reference-weight relock shifted the sole reference: expected=" + Format(expectedReference) + " actual=" + Format(Unflat(after.Placed)));
        if ((rig.LeftFoot.position - previousAnkle).magnitude > .02f)
            throw new InvalidOperationException("reference-weight relock moved the ankle: " + (rig.LeftFoot.position - previousAnkle).magnitude);
    }

    private static void LockedSwingRebasesCorrectionAcrossSoleBasis()
    {
        Rig rig = Rig.Create(footTiltDegrees: 4f);
        rig.Placer.GroundNormalProvider = side => null;
        PoseClip clip = Clip();
        clip.Stride[0].Cycles[0].LiftCycle = .2f;
        clip.Stride[0].Cycles[0].OffCycle = .25f;
        clip.Stride[0].Grounded[1] = false;
        // Let the opposite foot enter its pre-lift plant on the same frame. Its prior
        // unplanted prediction gives ShiftHips a real pending support correction while
        // the left foot releases into authored swing.
        clip.Stride[1].Cycles = new[]
        {
            new StrideCycle
            {
                StartFrame = 0, EndFrame = 3, StrikeFrame = 2, StrideLength = .36f,
                ToStrideStartPos = Vector3.zero, RotationChange = 0f, StrideYaw = 0f,
                HasMidpoint = false, LiftCycle = .5f, OffCycle = .55f,
                StrikeCycle = .85f, LandCycle = .85f, Stationary = true
            }
        };
        clip.Stride[1].Grounded[0] = false;
        clip.Stride[1].Grounded[1] = true;
        PosePlayback playback = Playback(clip, 0f);

        Step(570);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("lock-to-swing setup failed");
        FootPlacerProbe locked = rig.Placer.Probe(0);
        object foot = GetPrivateFeet(rig.Placer).GetValue(0);
        if (!locked.Locked || locked.InAuthoredSwing || !locked.AuthoredGrounded)
            throw new InvalidOperationException("lock-to-swing setup did not establish a grounded lock");

        Vector3 previousCenter = (Unflat(locked.PlacedHeel) + Unflat(locked.PlacedToe)) * .5f;
        Vector3 previousAnkle = rig.LeftFoot.position;
        float previousW = ReferenceWeight(
            (Vector3)GetField(foot, "ShownBase"),
            (Vector3)GetField(foot, "ShownHeel"),
            (Vector3)GetField(foot, "ShownToe"));

        // A small pitch crosses the heel/toe blend threshold while the lock releases.  The
        // source pose is deliberately changed before the swing frame is sampled, matching the
        // live animator's incoming sole basis rather than mutating the cached placement.
        rig.LeftFoot.rotation = Quaternion.Euler(2f, 0f, 0f);
        rig.RightFoot.rotation = Quaternion.Euler(2f, 0f, 0f);
        playback.State.Frame = 1f;
        Step(571);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("first lock-to-swing frame failed");
        FootPlacerProbe first = rig.Placer.Probe(0);
        float firstW = ReferenceWeight(
            (Vector3)GetField(foot, "ShownBase"),
            (Vector3)GetField(foot, "ShownHeel"),
            (Vector3)GetField(foot, "ShownToe"));
        Vector3 firstCenter = (Unflat(first.PlacedHeel) + Unflat(first.PlacedToe)) * .5f;
        Vector3 firstAppliedMove = (Vector3)GetField(foot, "AppliedMove");
        Vector3 firstHipShift = (Vector3)GetField(rig.Placer, "_hipShift");
        if (firstHipShift.magnitude <= .0001f)
            throw new InvalidOperationException("lock-to-swing fixture did not exercise a nonzero hip shift: " + firstHipShift.magnitude);
        if (!first.InAuthoredSwing || first.AuthoredGrounded || first.Locked)
            throw new InvalidOperationException("lock-to-swing frame did not enter authored swing: swing=" + first.InAuthoredSwing
                + " grounded=" + first.AuthoredGrounded + " locked=" + first.Locked + " cycleTime=" + first.CycleTime
                + " failure=" + first.Failure + " progression=" + first.Progression);
        if (!(previousW > .01f && previousW < .2f && firstW > previousW + .1f && firstW < .5f))
            throw new InvalidOperationException("lock-to-swing fixture did not cross the sole basis threshold: before=" + previousW + " after=" + firstW);
        // With the rendered basis rebased after ShiftHips, the first release stays below
        // the one-frame swing rate cap. The old correction history starts from zero here
        // and saturates that cap, moving the rendered sole over 16 mm.
        if ((firstCenter - previousCenter).magnitude >= .0162f)
            throw new InvalidOperationException("unlock moved the rendered sole discontinuously: "
                + (firstCenter - previousCenter).magnitude + " previous=" + Format(previousCenter) + " actual=" + Format(firstCenter));
        if ((rig.LeftFoot.position - previousAnkle).magnitude > .04f)
            throw new InvalidOperationException("unlock moved the ankle discontinuously: "
                + (rig.LeftFoot.position - previousAnkle).magnitude);
        if (firstAppliedMove.magnitude >= .0158f)
            throw new InvalidOperationException("rebased swing correction saturated the release rate: " + firstAppliedMove.magnitude);

        // A second authored swing sample must keep advancing the stride after the rebase;
        // the correction history must not leave the foot pinned at the old lock basis.
        playback.State.Frame = 2f;
        Step(572);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("second lock-to-swing frame failed");
        FootPlacerProbe second = rig.Placer.Probe(0);
        if (!second.InAuthoredSwing || second.Failure == 1)
            throw new InvalidOperationException("swing did not remain active after the rebase");
        if (!(second.Progression > first.Progression + .01f))
            throw new InvalidOperationException("swing progression stalled after the rebase: first=" + first.Progression + " second=" + second.Progression);
    }

    private static void AnchorFailureReentryPreservesRenderedSolePairAndHeight()
    {
        Rig rig = Rig.Create(footTiltDegrees: 8f);
        rig.Placer.GroundNormalProvider = side => null;
        rig.Root.position = new Vector3(0f, .8f, 0f);
        PoseClip clip = Clip();
        PosePlayback playback = Playback(clip, 0f);
        Step(560);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("anchor-failure setup failed");

        object foot = GetPrivateFeet(rig.Placer).GetValue(0);
        FootPlacerProbe before = rig.Placer.Probe(0);
        Vector3 previousAnkle = rig.LeftFoot.position;
        Vector3 previousHeel = Unflat(before.PlacedHeel);
        Vector3 previousToe = Unflat(before.PlacedToe);
        float previousHeading = before.PlacedHeading;
        float previousWeight = (float)GetField(foot, "_baseW");
        if (!(previousWeight <= .01f))
            throw new InvalidOperationException("anchor-failure fixture did not start from the heel/toe endpoint: " + previousWeight);

        // Move the incoming character frame down while the previously rendered sole stays
        // elevated. This reproduces the live stop jump: the held sole is about 25 cm above
        // the new ClipFoot, so clearing Release.y would drop it on the failure frame.
        rig.Root.position -= new Vector3(0f, .25f, 0f);
        clip.Stride[0].Cycles[0].LiftCycle = .2f;
        clip.Stride[0].Cycles[0].OffCycle = .25f;
        clip.Stride[0].Grounded[1] = false;
        rig.LeftFoot.rotation = Quaternion.Euler(-8f, 0f, 0f);
        PoseSnapshot failurePose = PoseSnapshot.Capture(rig);

        // Keep the animation's anchors deliberately untrustworthy while retaining the
        // rendered sole. The opposite pitch flips the current base reference to the
        // other endpoint, which exposed the old PlacedBase-basis jump.
        Vector3 previous = (Vector3)GetField(foot, "Prev");
        SetField(foot, "Prev", previous + new Vector3(.75f, 0f, 0f));
        SetField(foot, "NextFrozen", true);
        failurePose.Restore();
        Step(561);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("anchor-failure frame failed");

        FootPlacerProbe failure = rig.Placer.Probe(0);
        float currentWeight = (float)GetField(foot, "_baseW");
        Vector3 expectedReference = Vector3.Lerp(previousToe, previousHeel, currentWeight);
        Vector3 pendingTarget = (Vector3)GetField(foot, "PendingTarget");
        float pendingHeading = (float)GetField(foot, "PendingHeading");
        Vector3 failureCenter = (Unflat(failure.PlacedHeel) + Unflat(failure.PlacedToe)) * .5f;
        Vector3 failureClipFoot = (Vector3)GetField(foot, "ClipFoot");
        if (failure.Failure != 1)
            throw new InvalidOperationException("anchor-failure fixture did not report failure 1: " + failure.Failure);
        if (!(currentWeight > .99f))
            throw new InvalidOperationException("anchor-failure fixture did not flip to the opposite sole endpoint: " + currentWeight);
        NearVector(pendingTarget, expectedReference, "anchor-failure pending target", .001f);
        NearVector(Unflat(failure.Target), expectedReference, "anchor-failure probe target", .001f);
        Near(pendingHeading, previousHeading, "anchor-failure pending heading", .001f);
        if ((rig.LeftFoot.position - previousAnkle).magnitude > .05f)
            throw new InvalidOperationException("anchor-failure frame moved the ankle discontinuously: "
                + (rig.LeftFoot.position - previousAnkle).magnitude);
        if (failureCenter.y - failureClipFoot.y < .20f)
            throw new InvalidOperationException("anchor-failure fixture did not retain its raised rendered sole: rendered="
                + failureCenter.y + " clip=" + failureClipFoot.y);
        if (Math.Abs(failureCenter.y - expectedReference.y) > .02f)
            throw new InvalidOperationException("anchor-failure frame changed the rendered sole height: expected="
                + Format(expectedReference) + " actual=" + Format(failureCenter));

        // The next Apply is a fresh authored swing re-entry. It must consume the held
        // full-height reference without dropping the vertical residual or producing a
        // delayed jump after the failure frame.
        playback.State.Frame = 1f;
        failurePose.Restore();
        Step(562);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("anchor-failure re-entry failed");
        FootPlacerProbe reentry = rig.Placer.Probe(0);
        Vector3 reentryCenter = (Unflat(reentry.PlacedHeel) + Unflat(reentry.PlacedToe)) * .5f;
        if (reentry.Failure == 1)
            throw new InvalidOperationException("anchor-failure swing re-entry immediately failed again");
        if ((rig.LeftFoot.position - previousAnkle).magnitude > .04f)
            throw new InvalidOperationException("anchor-failure re-entry moved the ankle discontinuously: "
                + (rig.LeftFoot.position - previousAnkle).magnitude);
        if (Math.Abs(reentryCenter.y - expectedReference.y) > .025f)
            throw new InvalidOperationException("anchor-failure re-entry dropped the held sole height: expected="
                + Format(expectedReference) + " actual=" + Format(reentryCenter));
    }

    private static void StopFinalAirborneCorrectionUsesDeadlineAndTerminalTarget()
    {
        PoseClip clip = StopClip();
        Rig ordinaryRig = CreateStopRig();
        PosePlayback ordinary = Playback(clip, 4f);
        ordinary.State.Stopping = false;
        Step(600);
        if (!ordinaryRig.Placer.Apply(ordinary))
            throw new InvalidOperationException("ordinary pre-contact stop fixture apply failed");
        FootPlacerProbe ordinaryPreContact = ordinaryRig.Placer.Probe(0);

        Rig stoppingRig = CreateStopRig();
        PosePlayback stopping = Playback(clip, 4f);
        stopping.State.Stopping = true;
        stopping.State.HaltRemaining = .35f;
        stopping.State.HasFinalYaw = true;
        stopping.State.FinalYaw = 0f;
        Step(601);
        if (!stoppingRig.Placer.Apply(stopping))
            throw new InvalidOperationException("stopping pre-contact fixture apply failed");
        FootPlacerProbe stoppingPreContact = stoppingRig.Placer.Probe(0);
        Vector3 stoppingPreContactTarget = Unflat(stoppingPreContact.Target);
        Vector3 ordinaryPreContactTarget = Unflat(ordinaryPreContact.Target);
        Vector3 stoppingPreApplied = (Vector3)GetField(GetPrivateFeet(stoppingRig.Placer).GetValue(0), "AppliedMove");
        if ((stoppingPreContactTarget - ordinaryPreContactTarget).magnitude < .05f)
            throw new InvalidOperationException("pre-contact stop target did not aim toward the terminal halt pose");
        if (stoppingPreApplied.magnitude > .8f * Dt + .0001f)
            throw new InvalidOperationException("pre-contact stop correction bypassed the ordinary soft cap");

        stopping.State.Frame = 10f;
        Step(602);
        if (!stoppingRig.Placer.Apply(stopping))
            throw new InvalidOperationException("airborne stop fixture apply failed");
        object stoppingFoot = GetPrivateFeet(stoppingRig.Placer).GetValue(0);
        FootPlacerProbe airborne = stoppingRig.Placer.Probe(0);
        float stopWeight = (float)GetField(stoppingFoot, "StopLandingWeight");
        Vector3 applied = (Vector3)GetField(stoppingFoot, "AppliedMove");
        if (!(stopWeight > .01f && stopWeight < 1f))
            throw new InvalidOperationException("airborne final stop did not expose progressive landing authority: " + stopWeight);
        if (!(Unflat(airborne.Target).z > stoppingPreContactTarget.z + .05f))
            throw new InvalidOperationException("airborne final stop did not target the terminal halt pose");
        if (!(applied.magnitude > .016f))
            throw new InvalidOperationException("deadline landing did not increase correction authority over the ordinary soft cap");
        if ((applied - stoppingPreApplied).magnitude > 2f * Dt + .0001f)
            throw new InvalidOperationException("deadline landing exceeded its 2 m/s correction rate bound: " + (applied - stoppingPreApplied).magnitude);

        for (int frame = 11; frame <= 19; frame++)
        {
            stopping.State.Frame = frame;
            Step(602 + frame);
            if (!stoppingRig.Placer.Apply(stopping))
                throw new InvalidOperationException("airborne stop continuation failed at frame " + frame);
        }
        FootPlacerProbe landed = stoppingRig.Placer.Probe(0);
        Vector3 terminal = (Vector3)GetField(GetPrivateFeet(stoppingRig.Placer).GetValue(0), "Next");
        Vector3 landedPlaced = Unflat(landed.Placed);
        if (HorizontalDelta(landedPlaced, terminal) > .08f)
            throw new InvalidOperationException("airborne final stop did not fulfill its terminal target before clip end: error=" + HorizontalDelta(landedPlaced, terminal) + " placed=" + Format(landedPlaced) + " terminal=" + Format(terminal));

        PoseClip nativeClip = StopClip(endsInIdle: true);
        Rig nativeRig = CreateStopRig();
        PosePlayback native = Playback(nativeClip, 4f);
        native.State.Stopping = true;
        native.State.HaltRemaining = .35f;
        native.State.HasFinalYaw = true;
        native.State.FinalYaw = 0f;
        Step(630);
        if (!nativeRig.Placer.Apply(native))
            throw new InvalidOperationException("native terminal stop fixture apply failed");
        object nativeFoot = GetPrivateFeet(nativeRig.Placer).GetValue(0);
        Vector3 nativeNext = (Vector3)GetField(nativeFoot, "Next");
        float nativeNextYaw = (float)GetField(nativeFoot, "NextYaw");
        NearVector(nativeNext, new Vector3(.05f, 0f, .39f), "native terminal sole", .0001f);
        Near(nativeNextYaw, 22f, "native terminal heading", .0001f);
    }

    private static void AlreadyGroundedStopFootKeepsItsAnchor()
    {
        Rig rig = CreateStopRig();
        PoseClip clip = StopClip();
        PosePlayback playback = Playback(clip, 0f);
        playback.State.Stopping = true;
        playback.State.HaltRemaining = .35f;
        playback.State.HasFinalYaw = true;
        playback.State.FinalYaw = 0f;
        Step(700);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("grounded stop setup apply failed");
        Vector3 anchored = rig.RightFoot.position;
        FootPlacerProbe setup = rig.Placer.Probe(1);
        if (!setup.Locked)
            throw new InvalidOperationException("grounded stop foot did not establish a plant lock: locked=" + setup.Locked + " anchored=" + rig.Placer.IsAnchored(1) + " cycle=" + setup.Cycle + " t=" + setup.CycleTime + " grounded=" + setup.AuthoredGrounded + " swing=" + setup.InAuthoredSwing + " target=" + Format(Unflat(setup.Target)) + " shown=" + Format(Unflat(setup.Shown)));

        for (int frame = 1; frame <= 19; frame++)
        {
            playback.State.Frame = frame;
            Step(700 + frame);
            if (!rig.Placer.Apply(playback))
                throw new InvalidOperationException("grounded stop continuation failed at frame " + frame);
            FootPlacerProbe probe = rig.Placer.Probe(1);
            if (!probe.Locked)
                throw new InvalidOperationException("grounded stop foot lost its lock at frame " + frame);
            if ((rig.RightFoot.position - anchored).magnitude > .0001f)
                throw new InvalidOperationException("already-grounded stop foot was retargeted at frame " + frame);
        }
    }

    private static void StopSettlementMovesOneFootHoldsSupportAndClearsOnCancel()
    {
        PoseClip clip = StopClip(endsInIdle: true);
        // Keep the support foot's authored terminal slot at its already-rendered stance. The
        // left foot is intentionally left as the only bad hand-off pose below.
        clip.Stride[1].Footbase[clip.Frames - 1] = new Vector3(.18f, 0f, 0f);
        clip.Stride[1].Heading[clip.Frames - 1] = 0f;

        Rig rig = CreateStopRig();
        PosePlayback playback = Playback(clip, 0f);
        playback.State.Stopping = true;
        playback.State.HaltRemaining = .35f;
        playback.State.HasFinalYaw = true;
        playback.State.FinalYaw = 0f;
        PoseSnapshot authoredPose = PoseSnapshot.Capture(rig);

        // Replay the incoming animation pose every frame. FootPlacer mutates the bones in
        // Apply, while the real animation overwrites those mutations before the next pass.
        for (int frame = 0; frame < clip.Frames - 1; frame++)
        {
            playback.State.Frame = frame;
            authoredPose.Restore();
            Step(900 + frame);
            if (!rig.Placer.Apply(playback))
                throw new InvalidOperationException("stop settlement warm-up failed at frame " + frame);
        }

        FootPlacerProbe before = rig.Placer.Probe(1);
        Vector3 supportHeel = Unflat(before.PlacedHeel);
        Vector3 supportToe = Unflat(before.PlacedToe);
        Vector3 leftBefore = (Unflat(rig.Placer.Probe(0).PlacedHeel) + Unflat(rig.Placer.Probe(0).PlacedToe)) * .5f;
        Vector3 rightBefore = (supportHeel + supportToe) * .5f;

        // Construct a reachable source terminal pose near the rendered left anchor, then
        // make the fresh native sample disagree with that authored endpoint by about 7 cm.
        // The right sole center remains exactly at its rendered anchor so the helper has one
        // clear foot to lift. Native sampling and source playback are restored separately on
        // every Apply, matching the live hand-off order.
        authoredPose.Restore();
        SetFootSoleCenter(rig.LeftFoot, leftBefore + new Vector3(-.02f, 0f, .01f));
        SetFootSoleCenter(rig.RightFoot, rightBefore);
        PoseSnapshot sourceTerminalPose = PoseSnapshot.Capture(rig);
        sourceTerminalPose.Restore();
        rig.LeftFoot.rotation = Quaternion.Euler(0f, 12f, 0f);
        SetFootSoleCenter(rig.LeftFoot, SoleCenter(rig.LeftFoot) + new Vector3(-.06f, 0f, .04f));
        PoseSnapshot nativeTerminalPose = PoseSnapshot.Capture(rig);
        nativeTerminalPose.Restore();
        Vector3 leftDesired = SoleCenter(rig.LeftFoot);
        Vector3 rightDesired = SoleCenter(rig.RightFoot);
        float leftDesiredHeading = SoleHeading(rig.LeftFoot);
        float rightDesiredHeading = SoleHeading(rig.RightFoot);
        sourceTerminalPose.Restore();
        Vector3 authoredLeftEndpoint = SoleCenter(rig.LeftFoot);

        if (HorizontalDelta(leftDesired, authoredLeftEndpoint) < .05f
            || HorizontalDelta(leftDesired, authoredLeftEndpoint) > .10f
            || HorizontalDelta(leftDesired, leftBefore) < .025f)
            throw new InvalidOperationException("stop settlement fixture did not create a reachable 5-10 cm native mismatch");
        if (HorizontalDelta(rightDesired, rightBefore) > .002f)
            throw new InvalidOperationException("stop settlement fixture moved the support native pose");

        playback.State.Frame = clip.Frames - 1;
        SetNativeIdleFeedback(playback, nativeTerminalPose, sourceTerminalPose, rig);
        Step(950);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("stop settlement did not start at the terminal frame");
        FootPlacerProbe first = rig.Placer.Probe(0);
        if (!first.StopCorrectionStep)
            throw new InvalidOperationException("bad native terminal pose did not start a stop settlement step");
        if (first.Locked)
            throw new InvalidOperationException("settling foot must be lifted out of its plant lock");
        if (!rig.Placer.Probe(1).Locked)
            throw new InvalidOperationException("support foot lost its rendered anchor when settlement started");

        float peakLeftLift = Unflat(first.Placed).y;
        for (int frame = 1; frame <= 48 && !rig.Placer.StopReadyForIdle; frame++)
        {
            playback.State.Frame = clip.Frames - 1;
            SetNativeIdleFeedback(playback, nativeTerminalPose, sourceTerminalPose, rig);
            Step(950 + frame);
            if (!rig.Placer.Apply(playback))
                throw new InvalidOperationException("stop settlement continuation failed at step " + frame);
            FootPlacerProbe left = rig.Placer.Probe(0);
            FootPlacerProbe right = rig.Placer.Probe(1);
            peakLeftLift = Mathf.Max(peakLeftLift, Unflat(left.Placed).y);
            if (HorizontalDelta(Unflat(right.PlacedHeel), supportHeel) > .004f
                || HorizontalDelta(Unflat(right.PlacedToe), supportToe) > .004f
                || Math.Abs(Unflat(right.PlacedHeel).y - supportHeel.y) > .004f
                || Math.Abs(Unflat(right.PlacedToe).y - supportToe.y) > .004f)
                throw new InvalidOperationException("support foot slid while the other foot settled at step " + frame);
        }

        if (!rig.Placer.StopReadyForIdle)
        {
            FootPlacerProbe debugLeft = rig.Placer.Probe(0);
            FootPlacerProbe debugRight = rig.Placer.Probe(1);
            object debugSettlement = GetField(rig.Placer, "_settlement");
            throw new InvalidOperationException("stop settlement did not report readiness after bounded completion: left="
                + Format((Unflat(debugLeft.PlacedHeel) + Unflat(debugLeft.PlacedToe)) * .5f)
                + " target=" + Format(leftDesired) + " leftStep=" + debugLeft.StopCorrectionStep
                + " failed=" + debugLeft.StopSettlementFailed + " right="
                + Format((Unflat(debugRight.PlacedHeel) + Unflat(debugRight.PlacedToe)) * .5f)
                + " rightStep=" + debugRight.StopCorrectionStep + " stop=" + rig.Placer.StopReadyForIdle
                + " active=" + GetField(debugSettlement, "_active") + " side=" + GetField(debugSettlement, "_side")
                + " elapsed=" + GetField(debugSettlement, "_elapsed") + " steps=" + GetField(debugSettlement, "_steps"));
        }
        FootPlacerProbe settledLeft = rig.Placer.Probe(0);
        FootPlacerProbe settledRight = rig.Placer.Probe(1);
        Vector3 settledLeftCenter = (Unflat(settledLeft.PlacedHeel) + Unflat(settledLeft.PlacedToe)) * .5f;
        Vector3 settledRightCenter = (Unflat(settledRight.PlacedHeel) + Unflat(settledRight.PlacedToe)) * .5f;
        if (HorizontalDelta(settledLeftCenter, leftDesired) > StopSettlement.HorizontalTolerance + .005f)
            throw new InvalidOperationException("settled foot did not reach its native shown sole center: expected="
                + Format(leftDesired) + " actual=" + Format(settledLeftCenter));
        if (Math.Abs(settledLeftCenter.y - leftDesired.y) > .02f
            || Math.Abs(Mathf.DeltaAngle(settledLeft.PlacedHeading, leftDesiredHeading)) > StopSettlement.HeadingTolerance + .5f)
            throw new InvalidOperationException("settled foot did not reach its native shown height/heading");
        if (HorizontalDelta(settledRightCenter, rightDesired) > .004f)
            throw new InvalidOperationException("support foot did not remain at its native shown center");
        if (Math.Abs(Mathf.DeltaAngle(settledRight.PlacedHeading, rightDesiredHeading)) > .5f)
            throw new InvalidOperationException("support foot heading changed during settlement");
        if (peakLeftLift <= leftBefore.y + .001f)
            throw new InvalidOperationException("stop settlement never lifted the moving foot");
        if (settledLeft.StopCorrectionStep || settledRight.StopCorrectionStep)
            throw new InvalidOperationException("completed settlement leaked its active step flag");
        if (!rig.Placer.MatchesNativeIdle(leftDesired, rightDesired, leftDesiredHeading, rightDesiredHeading))
            throw new InvalidOperationException("ready stop did not match the current native idle target");
        if (rig.Placer.MatchesNativeIdle(leftDesired + new Vector3(.08f, 0f, 0f), rightDesired,
            leftDesiredHeading, rightDesiredHeading))
            throw new InvalidOperationException("readiness accepted a left native target moved by 8 cm");
        if (rig.Placer.MatchesNativeIdle(leftDesired, rightDesired, leftDesiredHeading + 15f, rightDesiredHeading))
            throw new InvalidOperationException("readiness accepted a left native heading changed by 15 degrees");

        // An ineligible moving/new-clip sample must cancel the episode, including the path
        // into the release hand-off, so the next clip cannot inherit a stale step request.
        playback.State.Stopping = false;
        playback.State.HasNativeIdlePose = false;
        playback.State.Fading = true;
        playback.State.Frame = clip.Frames - 1;
        authoredPose.Restore();
        Step(990);
        rig.Placer.Apply(playback);
        if (rig.Placer.StopReadyForIdle || rig.Placer.Probe(0).StopCorrectionStep || rig.Placer.Probe(1).StopCorrectionStep)
            throw new InvalidOperationException("release hand-off retained stale settlement readiness or an active step");
        playback.State.Fading = false;
        playback.State.Clip = Clip();
        playback.State.SourceClip = playback.State.Clip.Name;
        playback.State.Stopping = false;
        playback.State.Frame = 0f;
        authoredPose.Restore();
        Step(991);
        if (!rig.Placer.Apply(playback))
            throw new InvalidOperationException("new clip after settlement cancellation failed");
        if (rig.Placer.StopReadyForIdle || rig.Placer.Probe(0).StopCorrectionStep || rig.Placer.Probe(1).StopCorrectionStep)
            throw new InvalidOperationException("cancelled settlement leaked readiness or an active step into the next clip");

        // Without the fresh native sample the helper may still hold/render a bounded
        // correction, but it must never announce that the idle hand-off is ready.
        Rig noFeedbackRig = CreateStopRig();
        PoseClip noFeedbackClip = StopClip(endsInIdle: true);
        PosePlayback noFeedback = Playback(noFeedbackClip, 0f);
        noFeedback.State.Stopping = true;
        noFeedback.State.HaltRemaining = .35f;
        noFeedback.State.HasFinalYaw = true;
        noFeedback.State.FinalYaw = 0f;
        PoseSnapshot noFeedbackPose = PoseSnapshot.Capture(noFeedbackRig);
        for (int frame = 0; frame < noFeedbackClip.Frames - 1; frame++)
        {
            noFeedback.State.Frame = frame;
            noFeedbackPose.Restore();
            Step(1000 + frame);
            if (!noFeedbackRig.Placer.Apply(noFeedback))
                throw new InvalidOperationException("no-feedback stop warm-up failed at frame " + frame);
        }
        for (int frame = 0; frame < 24; frame++)
        {
            noFeedback.State.Frame = noFeedbackClip.Frames - 1;
            noFeedbackPose.Restore();
            Step(1040 + frame);
            if (!noFeedbackRig.Placer.Apply(noFeedback))
                throw new InvalidOperationException("no-feedback stop continuation failed at frame " + frame);
            if (noFeedbackRig.Placer.StopReadyForIdle)
                throw new InvalidOperationException("stop reported idle readiness without fresh native feedback at frame " + frame);
        }
    }

    private static Vector3 SoleCenter(Transform foot)
    {
        Vector3 heel = foot.position + foot.rotation * new Vector3(0f, -.1f, -.12f);
        Vector3 toe = foot.position + foot.rotation * new Vector3(0f, -.1f, .12f);
        return (heel + toe) * .5f;
    }

    private static float SoleHeading(Transform foot)
    {
        Vector3 heel = foot.position + foot.rotation * new Vector3(0f, -.1f, -.12f);
        Vector3 toe = foot.position + foot.rotation * new Vector3(0f, -.1f, .12f);
        return Mathf.Atan2(toe.x - heel.x, toe.z - heel.z) * Mathf.Rad2Deg;
    }

    private static void SetNativeIdleFeedback(PosePlayback playback, PoseSnapshot nativePose, PoseSnapshot sourcePose, Rig rig)
    {
        nativePose.Restore();
        playback.State.HasNativeIdlePose = true;
        playback.State.NativeIdleCenterL = SoleCenter(rig.LeftFoot);
        playback.State.NativeIdleCenterR = SoleCenter(rig.RightFoot);
        playback.State.NativeIdleHeadingL = SoleHeading(rig.LeftFoot);
        playback.State.NativeIdleHeadingR = SoleHeading(rig.RightFoot);
        // Simulate playback overwriting the sampled native animator pose before the
        // placer reads its shown pose for this frame.
        sourcePose.Restore();
    }

    private static void SetFootSoleCenter(Transform foot, Vector3 center)
    {
        Vector3 offset = SoleCenter(foot) - foot.position;
        foot.position = center - offset;
    }

    private sealed class PoseSnapshot
    {
        private readonly Transform[] _bones;
        private readonly Vector3[] _positions;
        private readonly Quaternion[] _rotations;

        private PoseSnapshot(Transform[] bones)
        {
            _bones = bones;
            _positions = new Vector3[bones.Length];
            _rotations = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                _positions[i] = bones[i].localPosition;
                _rotations[i] = bones[i].localRotation;
            }
        }

        public static PoseSnapshot Capture(Rig rig)
        {
            return new PoseSnapshot(new[]
            {
                rig.Root, rig.Pelvis,
                rig.LeftThigh, rig.LeftCalf, rig.LeftFoot,
                rig.RightThigh, rig.RightCalf, rig.RightFoot
            });
        }

        public void Restore()
        {
            for (int i = 0; i < _bones.Length; i++)
            {
                _bones[i].localPosition = _positions[i];
                _bones[i].localRotation = _rotations[i];
            }
        }
    }

    private static PosePlayback Playback(PoseClip clip, float frame)
    {
        return new PosePlayback
        {
            State = new StrideState
            {
                Clip = clip,
                SourceClip = clip.Name,
                Frame = frame,
                Weight = 1f,
                DistanceScale = 1f,
                IntentYaw = 0f,
                TravelYaw = 0f,
                Speed = 0f,
                Rate = 1f,
                YawRate = 0f,
                TravelYawRate = 0f,
                UseClipDistance = true,
                Fading = false
            }
        };
    }

    private static Rig CreateStopRig()
    {
        Rig rig = Rig.Create();
        // The generic terminal slots are 24-25 cm fore/aft of the root.  Give this fixture enough
        // leg length to reach those slots while keeping both soles on the same y=0 floor.
        rig.Pelvis.localPosition = new Vector3(0f, 1.25f, 0f);
        rig.LeftCalf.localPosition = new Vector3(0f, -.575f, 0f);
        rig.LeftFoot.localPosition = new Vector3(0f, -.575f, 0f);
        rig.RightCalf.localPosition = new Vector3(0f, -.575f, 0f);
        rig.RightFoot.localPosition = new Vector3(0f, -.575f, 0f);
        return rig;
    }

    private static PoseClip Clip()
    {
        const int frames = 4;
        var clip = new PoseClip
        {
            Name = "fixture",
            Fps = 30f,
            Frames = frames,
            Loop = false,
            SpeedMetersPerSecond = 0f,
            RootSpeed = new float[frames],
            RootVelocity = new Vector2[frames],
            FootL = new Vector3[frames],
            FootR = new Vector3[frames]
        };
        StrideCycle cycle = new StrideCycle
        {
            StartFrame = 0,
            EndFrame = frames - 1,
            StrikeFrame = 2,
            StrideLength = .36f,
            ToStrideStartPos = Vector3.zero,
            RotationChange = 0f,
            StrideYaw = 0f,
            HasMidpoint = false,
            LiftCycle = .6f,
            OffCycle = .65f,
            StrikeCycle = .85f,
            LandCycle = .85f,
            Stationary = true
        };
        clip.Stride = new[] { Stride(0, -.18f, cycle), Stride(1, .18f, cycle) };
        for (int frame = 0; frame < frames; frame++)
        {
            clip.FootL[frame] = new Vector3(-.18f, 0f, 0f);
            clip.FootR[frame] = new Vector3(.18f, 0f, 0f);
        }
        return clip;
    }

    private static PoseClip MidpointClip()
    {
        const int frames = 6;
        var clip = new PoseClip
        {
            Name = "midpoint-fixture",
            Fps = 30f,
            Frames = frames,
            Loop = false,
            SpeedMetersPerSecond = 1f,
            RootSpeed = new[] { 1f, 1f, 1f, 1f, 1f, 1f },
            RootVelocity = new Vector2[frames],
            FootL = new Vector3[frames],
            FootR = new Vector3[frames]
        };
        StrideCycle leftCycle = MidpointCycle(new Vector3(.02f, 0f, 0f));
        StrideCycle rightCycle = MidpointCycle(new Vector3(.18f, 0f, 0f));
        clip.Stride = new[] { MidpointStride(-.18f, leftCycle), MidpointStride(.18f, rightCycle) };
        for (int frame = 0; frame < frames; frame++)
        {
            clip.FootL[frame] = new Vector3(-.18f, 0f, 0f);
            clip.FootR[frame] = new Vector3(.18f, 0f, 0f);
        }
        return clip;
    }

    private static PoseClip StopClip(bool endsInIdle = false)
    {
        const int frames = 20;
        var clip = new PoseClip
        {
            Name = "stop-fixture",
            Fps = 30f,
            Frames = frames,
            Loop = false,
            SpeedMetersPerSecond = 0f,
            RootSpeed = new float[frames],
            RootVelocity = new Vector2[frames],
            FootL = new Vector3[frames],
            FootR = new Vector3[frames],
            EndsInTarkovIdle = endsInIdle
        };
        var cycle = new StrideCycle
        {
            StartFrame = 0,
            EndFrame = frames - 1,
            StrikeFrame = 15,
            StrideLength = .5f,
            ToStrideStartPos = Vector3.zero,
            RotationChange = 0f,
            StrideYaw = 0f,
            HasMidpoint = false,
            LiftCycle = .25f,
            OffCycle = .3f,
            StrikeCycle = .75f,
            LandCycle = .75f,
            Stationary = false
        };
        clip.Stride = new[]
        {
            StopStride(-.18f, cycle, false),
            StopStride(.18f, cycle, true)
        };
        for (int frame = 0; frame < frames; frame++)
        {
            clip.FootL[frame] = new Vector3(-.18f, 0f, 0f);
            clip.FootR[frame] = new Vector3(.18f, 0f, 0f);
        }
        if (endsInIdle)
        {
            clip.Stride[0].Footbase[frames - 1] = new Vector3(.05f, 0f, .04f);
            clip.Stride[0].Heading[frames - 1] = 22f;
            clip.Stride[1].Footbase[frames - 1] = new Vector3(-.05f, 0f, -.04f);
            clip.Stride[1].Heading[frames - 1] = -18f;
        }
        return clip;
    }

    private static StrideFoot StopStride(float x, StrideCycle cycle, bool grounded)
    {
        const int frames = 20;
        var stride = new StrideFoot
        {
            Cycles = new[] { cycle },
            Cycle = new int[frames],
            Progression = new float[frames],
            Offset = new Vector3[frames],
            RotationOffset = new float[frames],
            Footbase = new Vector3[frames],
            Heading = new float[frames],
            Grounded = new bool[frames],
            Floor = 0f
        };
        for (int frame = 0; frame < frames; frame++)
        {
            stride.Progression[frame] = frame / (float)(frames - 1);
            stride.Footbase[frame] = new Vector3(x, 0f, 0f);
            stride.Grounded[frame] = grounded;
        }
        return stride;
    }

    private static StrideCycle MidpointCycle(Vector3 middlePosition)
    {
        return new StrideCycle
        {
            StartFrame = 0,
            EndFrame = 5,
            StrikeFrame = 5,
            StrideLength = .4f,
            ToStrideStartPos = Vector3.zero,
            RotationChange = 0f,
            StrideYaw = 0f,
            HasMidpoint = true,
            MiddleFrame = 3,
            MiddlePosition = middlePosition,
            MiddleOffset = Vector3.zero,
            MiddleProgression = .6f,
            LiftCycle = .2f,
            OffCycle = .25f,
            StrikeCycle = .8f,
            LandCycle = .8f,
            Stationary = false
        };
    }

    private static StrideFoot MidpointStride(float x, StrideCycle cycle)
    {
        const int frames = 6;
        var stride = new StrideFoot
        {
            Cycles = new[] { cycle },
            Cycle = new[] { 0, 0, 0, 0, 0, 0 },
            Progression = new[] { 0f, .2f, .4f, .6f, .8f, 1f },
            Offset = new Vector3[frames],
            RotationOffset = new float[frames],
            Footbase = new Vector3[frames],
            Heading = new float[frames],
            Grounded = new[] { false, false, false, false, false, false },
            Floor = 0f
        };
        for (int frame = 0; frame < frames; frame++)
            stride.Footbase[frame] = new Vector3(x, 0f, 0f);
        return stride;
    }

    private static StrideFoot Stride(int side, float x, StrideCycle cycle)
    {
        const int frames = 4;
        var stride = new StrideFoot
        {
            Cycles = new[] { cycle },
            Cycle = new[] { 0, 0, 0, 0 },
            Progression = new[] { 0f, .33f, .67f, 1f },
            Offset = new Vector3[frames],
            RotationOffset = new float[frames],
            Footbase = new Vector3[frames],
            Heading = new float[frames],
            Grounded = new[] { true, true, true, true },
            Floor = 0f
        };
        for (int frame = 0; frame < frames; frame++)
            stride.Footbase[frame] = new Vector3(x, 0f, 0f);
        return stride;
    }

    private static object GetField(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value);
    private static Array GetPrivateFeet(FootPlacer placer) => (Array)typeof(FootPlacer).GetField("_feet", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(placer);
    private static object Invoke(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(target, arguments);

    private static void Step(int frame)
    {
        Time.deltaTime = Dt;
        Time.frameCount = frame;
        Time.time = frame * Dt;
    }

    private sealed class Rig
    {
        public Transform Root, Pelvis, LeftThigh, LeftCalf, LeftFoot, RightThigh, RightCalf, RightFoot;
        public Player Player;
        public FootPlacer Placer;

        public static Rig Create(float footTiltDegrees = 0f)
        {
            var rig = new Rig
            {
                Root = new Transform { position = Vector3.zero },
                Pelvis = Child(new Transform(), null, new Vector3(0f, 1f, 0f)),
                LeftThigh = new Transform(),
                LeftCalf = new Transform(),
                LeftFoot = new Transform(),
                RightThigh = new Transform(),
                RightCalf = new Transform(),
                RightFoot = new Transform()
            };
            rig.Pelvis.parent = rig.Root;
            rig.Pelvis.localPosition = new Vector3(0f, 1f, 0f);
            rig.LeftThigh.parent = rig.Pelvis;
            rig.LeftThigh.localPosition = new Vector3(-.18f, 0f, 0f);
            rig.LeftCalf.parent = rig.LeftThigh;
            rig.LeftCalf.localPosition = new Vector3(0f, -.45f, 0f);
            rig.LeftFoot.parent = rig.LeftCalf;
            rig.LeftFoot.localPosition = new Vector3(0f, -.45f, 0f);
            rig.RightThigh.parent = rig.Pelvis;
            rig.RightThigh.localPosition = new Vector3(.18f, 0f, 0f);
            rig.RightCalf.parent = rig.RightThigh;
            rig.RightCalf.localPosition = new Vector3(0f, -.45f, 0f);
            rig.RightFoot.parent = rig.RightCalf;
            rig.RightFoot.localPosition = new Vector3(0f, -.45f, 0f);
            rig.LeftFoot.localRotation = Quaternion.Euler(footTiltDegrees, 0f, 0f);
            rig.RightFoot.localRotation = Quaternion.Euler(footTiltDegrees, 0f, 0f);

            var player = new Player();
            rig.Player = player;
            player.Grounder.ik.references.pelvis = rig.Pelvis;
            player.Grounder.ik.references.leftThigh = rig.LeftThigh;
            player.Grounder.ik.references.leftCalf = rig.LeftCalf;
            player.Grounder.ik.references.leftFoot = rig.LeftFoot;
            player.Grounder.ik.references.rightThigh = rig.RightThigh;
            player.Grounder.ik.references.rightCalf = rig.RightCalf;
            player.Grounder.ik.references.rightFoot = rig.RightFoot;
            player.Grounder.solver.legs[0] = new GrounderLeg { lastTime = 0f, toHitNormal = Vector3.up, up = 1f };
            player.Grounder.solver.legs[1] = new GrounderLeg { lastTime = 0f, toHitNormal = Vector3.up, up = 1f };
            var db = new PoseDatabase
            {
                HasSolePoints = true,
                SoleHeel = new[] { new Vector3(0f, -.1f, -.12f), new Vector3(0f, -.1f, -.12f) },
                SoleToe = new[] { new Vector3(0f, -.1f, .12f), new Vector3(0f, -.1f, .12f) }
            };
            rig.Placer = FootPlacer.Create(player, db, out string error);
            if (rig.Placer == null)
                throw new InvalidOperationException("fixture placer creation failed: " + error);
            rig.Placer.GroundNormalProvider = side => Vector3.up;
            return rig;
        }

        private static Transform Child(Transform value, Transform parent, Vector3 position)
        {
            value.parent = parent;
            value.localPosition = position;
            return value;
        }
    }

    private static void Near(float actual, float expected, string label, float tolerance = .00001f)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }

    private static void NearVector(Vector3 actual, Vector3 expected, string label, float tolerance = .00001f)
    {
        if ((actual - expected).magnitude > tolerance)
            throw new InvalidOperationException(label + ": expected " + Format(expected) + ", got " + Format(actual));
    }

    private static string Format(Vector3 value) => "(" + value.x + "," + value.y + "," + value.z + ")";

    private static Vector3 Unflat(float[] value) => new Vector3(value[0], value[1], value[2]);

    private static float ReferenceWeight(Vector3 value, Vector3 heel, Vector3 toe)
    {
        Vector3 axis = heel - toe;
        return Mathf.Clamp01(Vector3.Dot(value - toe, axis) / Mathf.Max(axis.sqrMagnitude, 1e-8f));
    }

    private static float HorizontalDelta(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x;
        float z = a.z - b.z;
        return Mathf.Sqrt(x * x + z * z);
    }
}
