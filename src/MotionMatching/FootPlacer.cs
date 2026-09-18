using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // what the placer needs from the playback each frame
    internal struct StrideState
    {
        public PoseClip Clip;
        public string SourceClip, BlendClip;
        public float BlendWeight;
        public float Frame;
        public float Weight;
        // clip root distance -> bot distance (the stride scale while a start or cut is distance-matched)
        public float DistanceScale;
        public float? IntentYaw;
        public float TravelYaw;
        public float Speed;
        // playback rate this frame; with speed it says how far the body goes before a clip frame arrives
        public float Rate;
        // body yaw rate, degrees per second: a step lands where the body will be facing (slide 51)
        public float YawRate;
        // 0 keeps ordinary placement; 1 releases placement completely during a fast non-turning start/cut spin
        public float SpinSuppression;
        // world travel heading rate, degrees per second: bends the predicted path (the body yaw only orients the foot)
        public float TravelYawRate;
        // stops scale the clip's remaining root distance instead (braking model); starts and cuts use speed x time
        public bool UseClipDistance;
        // the clip has handed off: release whatever the feet are still holding, slowly, then stop
        public bool Fading;
        // a stop clip is playing: its last step per foot lands in Tarkov's idle stance so the hand-off has nothing
        // to slide (live bots: feet drifted 13 cm median, 46 cm p90 into the idle after a stop)
        public bool Stopping;
        // the playback's own estimate of the distance still to travel before the halt (stops only)
        public float HaltRemaining;
        // a turn clip matched to the controller's own turn: the body yaw rate already is the clip's yaw delta
        public bool YawFollowing;
        // the facing the body will have at the halt, when the playback knows it (a turn's goal)
        public bool HasFinalYaw;
        public float FinalYaw;
        // Fresh native animator sole pose sampled before playback writes this frame.
        public bool HasNativeIdlePose;
        public Vector3 NativeIdleCenterL, NativeIdleCenterR;
        public float NativeIdleHeadingL, NativeIdleHeadingR;
    }

    // per-foot placer state for captures
    internal struct FootPlacerProbe
    {
        public bool Active;
        public bool Applied;
        public int Cycle;
        public float Progression;
        // temporal cycle fraction used by the authored lift/strike gates; distinct from spatial Progression
        public float CycleTime;
        public bool InAuthoredSwing;
        // the exporter grounded flag at the sampled clip frame, for separating authored swing timing from contact data
        public bool AuthoredGrounded;
        public bool Frozen;
        public float[] Prev;
        public float[] Next;
        public float[] Shown;
        public float[] Target;
        public float Residual;
        // the character frame the placer used, and where the clip's own foot sits in it this frame
        public float[] Origin;
        public float Yaw;
        public float[] ClipFoot;
        // what actually happened this frame: the weight the correction was applied with, the sole after placement,
        // the horizontal correction asked for, and why the cycle was dropped (0 none, 1 target too far, 2 clearance)
        public float Authority;
        public float[] Placed;
        public float Correction;
        public int Failure;
        // metres of a released correction still blending out toward the clip foot
        public float Release;
        // the sole is pinned at its landing point this frame
        public bool Locked;
        // heading correction applied this frame and the sole headings before and after it (degrees)
        public float Turn;
        public float ShownHeading;
        public float PlacedHeading;
        // the sole's heel and toe after placement: the blended base hops heel -> toe on a roll-off, these do not
        public float[] PlacedHeel;
        public float[] PlacedToe;
        // hip-to-ankle over thigh plus shin after placement (1.0 = locked knee), and the hips' vertical shift
        public float Straightness;
        public float HipDrop;
        public float ClearanceBefore;
        public float ClearanceRequested;
        public float ClearanceAfter;
        public float ClearanceFraction;
        public bool Refinement;
        public bool Fading;
        public bool GroundNormalAvailable;
        public float AnkleLimitDegrees;
        public float SoleTargetError;
        public float MidpointCorrection;
        public float SwingAvoidance;
        public bool SwingPathPlanned, SwingPathResolved, SwingEndpointsBlocked;
        public bool StopCorrectionStep;
        public bool StopSettlementFailed, StopSettlementReady;
        public string StrideSource, StridePartner;
        public float StrideBlend;
    }

    // Valve's stride retargeting (SIGGRAPH 2021 slides 21-29, 47-55): each foot keeps a previous step, frozen where
    // it landed, and a predicted next step; the clip only supplies the stride-relative path between them, so a
    // planted foot cannot slide and a step lands where the body is actually going. runs on the final pose
    // (VisualPass postfix) and replaces the foot lock and stride warp while a pose clip plays
    internal sealed class FootPlacer
    {
        private const float MinWeight = 0.02f;
        // a leg pulled near straight locks its knee; keep it bent
        private const float MaxReach = 0.93f;
        // corrections are applied fully up to here and only partly beyond, capped: a foot dragged decimetres to its
        // target reads as a locked leg and jerks (user), and a slide of a few centimetres reads as nothing
        private const float SoftCorrection = 0.08f;
        private const float SoftCorrectionSlope = 0.35f;
        private const float HardCorrection = 0.25f;
        // a locked plant may be held further than a steered swing; beyond this the reach clamp decides anyway
        private const float PlantedCorrection = 0.35f;
        // how fast a swing correction may change, m/s (Valve's lock damping speed is 30 units/s = 0.76 m/s)
        private const float SwingCorrectionRate = 0.8f;
        // how fast the knee's bend direction may turn, degrees per second
        private const float PoleTurnRate = 540f;
        // release pace for held corrections after a hand-off, m/s and degrees per second
        private const float FadeReleaseRate = 0.4f;
        private const float TurnReleaseRate = 180f;
        private const float SpinRebaseYawFloor = 12f;
        private const float SpinRebaseYawSlack = 6f;
        // a landing farther than this from the clip's own foot is a wrong prediction, not a correction to hold
        private const float LockTrust = 0.15f;
        private const float LockClipSpeed = 0.6f;

        // the clip foot's speed over the ground: its character-frame velocity plus the root's. character-frame speed
        // alone cannot tell a plant from a swing (a planted foot moves backward at body speed)
        private static float ClipFootGroundSpeed(PoseClip clip, int f, int side)
        {
            Vector3[] feet = side == 0 ? clip.FootL : clip.FootR;
            if (feet == null || clip.RootVelocity == null || clip.Frames < 2)
                return 0f;
            int n = clip.Frames;
            int f0 = f, f1 = clip.Loop ? (f + 1) % n : Mathf.Min(f + 1, n - 1);
            if (f1 == f0)
                f0 = Mathf.Max(f - 1, 0);
            Vector3 d = (feet[f1] - feet[f0]) * clip.Fps;
            Vector2 root = clip.RootVelocity[Mathf.Clamp(f, 0, clip.RootVelocity.Length - 1)];
            return new Vector2(d.x + root.x, d.z + root.y).magnitude;
        }
        public int LockReanchors;
        // a same-family member switch whose stride cycle index did not line up (fell back to a re-entry)
        public int FamilyCycleMismatches;
        // a planted foot keeps its heading while the body turns, up to here; past it the foot follows the leg rather
        // than twisting the ankle round (a bot turning while walking flung its legs about, user)
        private const float MaxHeadingCorrection = 30f;
        // absolute foot yaw from the character's forward (Valve grunt foot lock: 55)
        private const float MaxFootYaw = 55f;
        private const float MaxTurnLookahead = 90f;
        private const float SwingHeight = 0.08f;
        private const float SwingSteerFrom = 0.55f;
        private const float StationaryStride = 0.02f;
        // slide 43: whatever offset a transition leaves is blended out over time; capped like the lock so it never snaps
        private const float MaxResidualDecaySpeed = 0.6f;
        private const float FootbaseBlend = 0.02f;
        private const float StandingSpeed = 0.3f;
        // a shown foot slower than this is stationary for slide 42's entry rule
        private const float StationaryFootSpeed = 0.3f;
        private const float PathDraw = 2.5f;
        private const float EntryLandedProgression = 0.85f;
        // a target this far from the clip's own foot is an anchoring failure, not a correction: the foot would be
        // dragged to full reach and snap. re-enter the stride from the clip instead (the first loop bug put targets
        // a whole period away and the legs flew)
        private const float MaxCorrection = 0.6f;
        // slide 55: a prediction should barely move; the raw one jitters with speed and heading, and every jitter went
        // straight into the swing foot (choppy, user). first prediction of a stride is taken as is
        private const float PredictionSmoothing = 0.08f;

        private readonly Transform _root;
        private float _travelYaw;
        private readonly Foot[] _feet = new Foot[2];
        private readonly List<Vector3> _path = new List<Vector3>();

        // path corners from the bot's position onward; null falls back to a straight line along its travel
        public Func<IList<Vector3>> PathProvider { get; set; }
        public Func<int, Vector3?> GroundNormalProvider { get; set; }
        private bool _fading;
        private int _appliedFrame = -1;
        private bool _lastRefinement = LocomotionRefinement.Enabled;
        private string _strideSource, _stridePartner;
        private float _strideBlend;
        private readonly StopSettlement _settlement = new StopSettlement();
        private StopSettlementResult _settlementState;
        private StrideState _settlementTargetState;
        private PoseClip _settlementClip;
        private bool _stopReadyForIdle;
        private bool _spinReleasing;
        private bool _hasLastRootPose;
        private float _lastRootYaw;
        private PoseClip _spinReleaseClip;
        public bool StopReadyForIdle => _stopReadyForIdle && HasFeedback(0) && HasFeedback(1);

        public bool MatchesNativeIdle(Vector3 left, Vector3 right, float leftHeading, float rightHeading)
        {
            if (!StopReadyForIdle) return false;
            return MatchesNativeFoot(_feet[0], left, leftHeading) && MatchesNativeFoot(_feet[1], right, rightHeading);
        }

        private static bool MatchesNativeFoot(Foot foot, Vector3 desired, float heading)
        {
            Vector3 difference = (foot.PlacedHeel + foot.PlacedToe) * 0.5f - desired;
            difference.y = 0f;
            return difference.magnitude <= StopSettlement.HorizontalTolerance + 0.0001f
                && Mathf.Abs(Mathf.DeltaAngle(foot.PlacedHeading, heading)) <= StopSettlement.HeadingTolerance + 0.001f;
        }
        public bool Active = true;
        public int AppliedFrames;
        public int CycleChanges;
        public int ReachClamps;
        public float PeakResidual;
        // how far a prediction moved between frames; Valve: "ideally, shouldn't move"
        public float PeakStepShift;
        public int AnchorFailures;
        public int ClearanceLimits;
        private Vector3 _clearancePelvis;
        private float _clearanceBefore, _clearanceRequested, _clearanceAfter;
        private float _clearanceFraction = 1f;

        private readonly Transform _pelvis;
        // Valve's newer foot lock shifts the hips toward the locked feet (0.75) so the legs are not stretched into
        // reach clamps when the body has moved on; horizontal, capped and damped. moves the whole body above the
        // hips a few centimetres, which is what the Alyx soldiers do too
        public float HipShift = 0.5f;
        private const float MaxHipShift = 0.06f;
        private const float HipShiftRate = 0.5f;
        private Vector3 _hipShift;
        public float KneeBendFloor = 0f;
        private const float MaxKneeDrop = 0.06f;
        public float PeakHipShift;

        private readonly Player _player;
        private FootPlacer(Player player, Transform root, Transform pelvis)
        {
            _player = player;
            _root = root;
            _pelvis = pelvis;
        }

        public static FootPlacer Create(Player player, PoseDatabase db, out string error)
        {
            error = null;
            var references = player?.Grounder?.ik?.references;
            if (references == null || !references.pelvis || !references.leftThigh || !references.leftCalf || !references.leftFoot || !references.rightThigh || !references.rightCalf || !references.rightFoot)
            { error = "leg bone references are unavailable"; return null; }
            if (db == null || !db.HasSolePoints) { error = "database has no sole points; re-export with tools/export_posedb.py"; return null; }
            var placer = new FootPlacer(player, references.pelvis.parent, references.pelvis);
            placer._feet[0] = new Foot(0, references.leftThigh, references.leftCalf, references.leftFoot, db.SoleHeel[0], db.SoleToe[0]);
            placer._feet[1] = new Foot(1, references.rightThigh, references.rightCalf, references.rightFoot, db.SoleHeel[1], db.SoleToe[1]);
            placer.GroundNormalProvider = side =>
            {
                var grounder = player.Grounder;
                var solver = grounder?.solver;
                if (grounder == null || grounder.weight <= 0f || solver == null || !solver.initiated ||
                    solver.legs == null || side >= solver.legs.Length) return null;
                var leg = solver.legs[side];
                // Fastest quality does not fill heelHit. The solver's processed normal covers every quality.
                // Reject stale data when EFT skips an IK update for a distant/inactive character.
                if (leg == null || !leg.initiated || !leg.isGrounded || Mathf.Abs(Time.time - leg.lastTime) > 0.001f) return null;
                Vector3 normal = leg.toHitNormal * leg.up;
                return normal.sqrMagnitude > 0.5f ? (Vector3?)normal.normalized : null;
            };
            return placer;
        }

        public bool IsAnchored(int side) => _feet[side].HasCycle && _feet[side].NextFrozen;

        public FootPlacerProbe Probe(int side)
        {
            var foot = _feet[side];
            return new FootPlacerProbe
            {
                Active = foot.HasCycle,
                Applied = _appliedFrame == Time.frameCount,
                Cycle = foot.CycleIndex,
                Progression = foot.LastProgression,
                CycleTime = foot.CycleTime,
                InAuthoredSwing = foot.InAuthoredSwing,
                AuthoredGrounded = foot.AuthoredGrounded,
                Frozen = foot.NextFrozen,
                Prev = Flat(foot.Prev),
                Next = Flat(foot.Next),
                Shown = Flat(foot.ShownBase),
                Target = Flat(foot.Target),
                Residual = foot.Residual.magnitude,
                Origin = Flat(foot.Origin),
                Yaw = foot.Yaw,
                ClipFoot = Flat(foot.ClipFoot),
                Authority = foot.LastAuthority,
                Placed = Flat(foot.PlacedBase),
                Correction = Horizontal(foot.Target - foot.ShownBase),
                Failure = foot.LastFailure,
                Release = foot.Release.magnitude,
                Locked = foot.Locked,
                Turn = foot.AppliedTurn,
                ShownHeading = foot.ShownHeading,
                PlacedHeading = foot.PlacedHeading,
                PlacedHeel = Flat(foot.PlacedHeel),
                PlacedToe = Flat(foot.PlacedToe),
                Straightness = Straightness(foot),
                HipDrop = -_hipShift.y,
                ClearanceBefore = _clearanceBefore,
                ClearanceRequested = _clearanceRequested,
                ClearanceAfter = _clearanceAfter,
                ClearanceFraction = _clearanceFraction,
                Refinement = LocomotionRefinement.Enabled,
                Fading = _fading,
                GroundNormalAvailable = foot.GroundNormalAvailable,
                AnkleLimitDegrees = foot.AnkleLimitDegrees,
                SoleTargetError = foot.SoleTargetError,
                MidpointCorrection = foot.MiddleCorrection,
                SwingAvoidance = foot.SwingAvoidanceApplied,
                SwingPathPlanned = foot.SwingPlan.Valid,
                SwingPathResolved = foot.SwingPlan.Resolved,
                SwingEndpointsBlocked = foot.SwingPlan.EndpointsBlocked,
                StopCorrectionStep = foot.SettlementStep,
                StopSettlementFailed = _settlementState.Failed,
                StopSettlementReady = StopReadyForIdle,
                StrideSource = _strideSource,
                StridePartner = _stridePartner,
                StrideBlend = _strideBlend
            };
        }

        private static float[] Flat(Vector3 v) => new[] { v.x, v.y, v.z };

        private static float Straightness(Foot foot)
        {
            if (!foot.Thigh || !foot.Calf || !foot.Bone) return 0f;
            Vector3 hip = foot.Thigh.position, knee = foot.Calf.position, ankle = foot.Bone.position;
            return (ankle - hip).magnitude / Mathf.Max((knee - hip).magnitude + (ankle - knee).magnitude, 1e-6f);
        }

        private static float Horizontal(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);

        // true when this frame's feet were placed; false hands the frame to the foot lock
        // VisualPass pins the weapon in world space to its spine mount (PlayerBones.ShiftWeaponRoot) and IKs the hands
        // onto it before this runs; the hip shift here then carried the arms but not the weapon, which floated out
        // of the hands (user). the weapon follows whatever this pass did to its mount
        public Transform WeaponMount, WeaponFollower;

        public bool Apply(PosePlayback pose)
        {
            if (NativeJumpOwnership.Owns(_player)) { Reset(); return false; }
            Player.FirearmController carryController = null;
            Weapon carryWeapon = null;
            bool carry = WeaponMount && WeaponFollower
                && TryGetWeaponCarryContext(_player, out carryController, out carryWeapon);
            // the weapon's pose relative to its mount, taken BEFORE the pass and restored after it: the same result
            // whether the game pinned the weapon in world space this frame or left it riding the skeleton. adding
            // the mount's movement to the weapon's later position doubled it on bots whose arm update was skipped,
            // every frame, and the weapon flew off (user)
            Vector3 localPosition = Vector3.zero;
            Quaternion localRotation = Quaternion.identity;
            if (carry)
            {
                Quaternion inverse = Quaternion.Inverse(WeaponMount.rotation);
                localPosition = inverse * (WeaponFollower.position - WeaponMount.position);
                localRotation = inverse * WeaponFollower.rotation;
            }
            bool placed = ApplyCore(pose);
            if (carry)
            {
                // Holstering, drawing, or swapping can change hands while the pass runs. The snapshot only
                // belongs to the same active firearm and item that were present before the pass.
                if (IsSameWeaponCarryContext(_player, carryController, carryWeapon))
                {
                    Vector3 wanted = WeaponMount.position + WeaponMount.rotation * localPosition;
                    // a teleport or a swapped weapon is not this pass's doing
                    if ((wanted - WeaponFollower.position).sqrMagnitude < MaxWeaponCarry * MaxWeaponCarry)
                        WeaponFollower.SetPositionAndRotation(wanted, WeaponMount.rotation * localRotation);
                }
            }
            return placed;
        }

        internal static bool TryGetWeaponCarryContext(Player player, out Player.FirearmController controller, out Weapon weapon)
        {
            controller = player?.HandsController as Player.FirearmController;
            weapon = controller?.Item;
            if (controller == null || weapon == null)
                return false;

            // These are the real EFT hide/draw operations; the firearm controller can remain current until
            // its replacement is ready, even though the weapon is already being stowed or raised.
            var operation = controller.CurrentOperation;
            return !(operation is Player.FirearmController.Remove)
                && !(operation is Player.FirearmController.SpawnOperation);
        }

        internal static bool IsSameWeaponCarryContext(Player player, Player.FirearmController expectedController, Weapon expectedWeapon)
        {
            return TryGetWeaponCarryContext(player, out var currentController, out var currentWeapon)
                && ReferenceEquals(currentController, expectedController)
                && ReferenceEquals(currentWeapon, expectedWeapon);
        }

        private const float MaxWeaponCarry = 0.3f;

        private bool ApplyCore(PosePlayback pose)
        {
            if (_lastRefinement != LocomotionRefinement.Enabled)
            {
                Reset();
                _lastRefinement = LocomotionRefinement.Enabled;
            }
            StrideState s;
            float dt = Time.deltaTime;
            if (!Active || pose == null || dt <= 0f || !pose.TryGetStrideState(out s) || s.Clip == null || s.Clip.Stride == null)
            {
                Reset();
                return false;
            }
            BeginClearance();
            _stopReadyForIdle = false;
            _fading = s.Fading;
            _strideSource = s.SourceClip; _stridePartner = s.BlendClip; _strideBlend = s.BlendWeight;
            Vector3 origin = _root.position;
            Vector3 forward = _root.forward;
            float yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            float yawDelta = _hasLastRootPose ? Mathf.DeltaAngle(_lastRootYaw, yaw) : 0f;
            if (s.Fading)
            {
                ResetSettlement();
                _spinReleasing = false;
                bool released = Release(dt);
                RememberRootPose(yaw);
                return released;
            }
            float spinSuppression = Mathf.Clamp01(s.SpinSuppression);
            bool spinSuppressed = spinSuppression > 0f;
            if (spinSuppressed || _spinReleasing)
            {
                ResetSettlement();
                // Finish the bounded release even after the spin ends. Returning early to ordinary placement
                // would clamp a remaining large heading correction and pop the foot on re-entry.
                float releaseScale = spinSuppressed ? spinSuppression : 1f;
                bool released = ReleaseForSpin(s, dt, releaseScale, yawDelta);
                if (!spinSuppressed && SpinReleaseComplete())
                {
                    // Preserve the rendered sole pair for a fresh cycle entry on the next frame.
                    _spinReleasing = false;
                    _spinReleaseClip = null;
                }
                RememberRootPose(yaw);
                return released;
            }
            if (s.Weight <= MinWeight)
            {
                Reset();
                return false;
            }
            // Suppression ended above the ordinary weight cutoff. UpdateFoot will enter a fresh cycle from the
            // actual rendered heel/toe pair before placing resumes.
            _spinReleasing = false;
            _spinReleaseClip = null;
            var clip = s.Clip;
            clip.BuildPathLength();
            int n = clip.Frames;
            // stride data is per clip frame (30/s); the placer runs per rendered frame, so it interpolates between
            // frames of the same cycle or the feet step at 30 Hz ("looked like a low framerate", user)
            int f = Mathf.Clamp(Mathf.FloorToInt(s.Frame), 0, n - 1);
            float u = Mathf.Clamp01(s.Frame - f);
            // Root_Joint is the clip's character frame: on the ground under the pelvis, yaw only
            // prediction runs along where the body is going, else where it intends to go, else straight ahead
            float travelYaw = s.Speed >= StandingSpeed ? yaw + s.TravelYaw : s.IntentYaw.HasValue ? yaw + s.IntentYaw.Value : yaw;
            Vector3 travel = Quaternion.Euler(0f, travelYaw, 0f) * Vector3.forward;
            IList<Vector3> corners = PathProvider != null ? PathProvider() : null;
            _travelYaw = travelYaw;
            for (int i = 0; i < 2; i++)
                UpdateFoot(_feet[i], clip, f, u, n, s, origin, yaw, travel, corners, dt);
            UpdateStopSettlement(s, f, dt);
            ShiftHips(dt);
            for (int i = 0; i < 2; i++)
            {
                var foot = _feet[i];
                if (!foot.PendingPlace)
                    continue;
                Place(foot, foot.PendingTarget, foot.PendingHeading, foot.PendingWeight, foot.PendingPlanted, dt);
            }
            FinishClearance();
            FinishStopSettlement();
            PublishFeedback();
            _appliedFrame = Time.frameCount;
            AppliedFrames++;
            RememberRootPose(yaw);
            if (DebugDraw.Enabled)
                Draw(origin, travel, corners);
            return true;
        }

        private void RememberRootPose(float yaw)
        {
            _lastRootYaw = yaw;
            _hasLastRootPose = true;
        }

        public void Reset()
        {
            ResetSettlement();
            _appliedFrame = -1;
            for (int i = 0; i < 2; i++)
                _feet[i].Reset();
            _hipShift = Vector3.zero;
            _clearanceBefore = _clearanceRequested = _clearanceAfter = 0f;
            _clearanceFraction = 1f;
            _fading = false;
            _spinReleasing = false;
            _hasLastRootPose = false;
            _lastRootYaw = 0f;
            _spinReleaseClip = null;
        }

        private void ResetSettlement()
        {
            _settlement.Reset();
            _settlementClip = null;
            _settlementState = default(StopSettlementResult);
            _settlementTargetState = default(StrideState);
            _stopReadyForIdle = false;
        }

        private void UpdateStopSettlement(StrideState state, int frame, float dt)
        {
            bool eligible = LocomotionRefinement.Enabled && state.Stopping && !state.Clip.Loop
                && state.Clip.EndsInTarkovIdle && frame >= state.Clip.Frames - 1 && state.Speed < 0.15f
                && _feet[0].HasPlaced && _feet[1].HasPlaced;
            if (!eligible || _settlementClip != state.Clip)
            {
                ResetSettlement();
                _settlementClip = eligible ? state.Clip : null;
                if (!eligible) return;
            }
            var left = _feet[0]; var right = _feet[1];
            _settlementTargetState = state;
            // Fixed sole centers prevent heel/toe reference changes becoming false
            // correction requests. Desired centers come from the incoming clip's native
            // END stance if native feedback is unavailable; actual centers are the last
            // rendered pose. Handoff requires the fresh native target, including its heading.
            _settlementState = _settlement.Update(true, dt,
                (left.PlacedHeel + left.PlacedToe) * 0.5f, (right.PlacedHeel + right.PlacedToe) * 0.5f,
                state.HasNativeIdlePose ? state.NativeIdleCenterL : (left.ShownHeel + left.ShownToe) * 0.5f,
                state.HasNativeIdlePose ? state.NativeIdleCenterR : (right.ShownHeel + right.ShownToe) * 0.5f,
                left.PlacedHeading, right.PlacedHeading,
                state.HasNativeIdlePose ? state.NativeIdleHeadingL : left.ShownHeading,
                state.HasNativeIdlePose ? state.NativeIdleHeadingR : right.ShownHeading);
            for (int side = 0; side < 2; side++)
            {
                var foot = _feet[side];
                if (_settlementState.Active && side == _settlementState.Side)
                {
                    if (_settlementState.Started)
                    {
                        // Damping history must use the same fixed-center reference as
                        // the step, rather than the last frame's heel/toe base weight.
                        foot.AppliedMove = (foot.PlacedHeel + foot.PlacedToe - foot.ShownHeel - foot.ShownToe) * 0.5f;
                        foot.AppliedTurn = Mathf.DeltaAngle(foot.ShownHeading, foot.PlacedHeading);
                    }
                    foot.SettlementStep = true;
                    foot.Locked = false;
                    foot.Target = _settlementState.Position;
                    foot.Request(_settlementState.Position, _settlementState.Heading, 1f, false);
                }
                else
                    HoldRenderedFoot(foot, true);
            }
        }

        private void FinishStopSettlement()
        {
            if (_settlementState.Active && _settlementState.Completed)
                HoldRenderedFoot(_feet[_settlementState.Side], false);
            if (!_settlementState.Ready || !_settlementTargetState.HasNativeIdlePose) return;
            _stopReadyForIdle = true;
            for (int side = 0; side < 2; side++)
            {
                var foot = _feet[side];
                Vector3 desired = side == 0 ? _settlementTargetState.NativeIdleCenterL : _settlementTargetState.NativeIdleCenterR;
                float heading = side == 0 ? _settlementTargetState.NativeIdleHeadingL : _settlementTargetState.NativeIdleHeadingR;
                if (!MatchesNativeFoot(foot, desired, heading))
                    _stopReadyForIdle = false;
            }
        }

        private static void HoldRenderedFoot(Foot foot, bool request)
        {
            if (!foot.HasPlaced) return;
            foot.LockHeel = foot.PlacedHeel;
            foot.LockToe = foot.PlacedToe;
            foot.LockBase = Vector3.Lerp(foot.LockToe, foot.LockHeel, foot.BaseW);
            foot.LockHeading = foot.PlacedHeading;
            foot.LockClipHeading = Mathf.DeltaAngle(foot.Yaw, foot.ShownHeading);
            foot.Locked = foot.NextFrozen = true;
            foot.Residual = Vector3.zero;
            if (request)
            {
                foot.Target = foot.LockBase;
                foot.Request(foot.Target, foot.LockHeading, 1f, true);
            }
        }

        private float Clearance() => LegClearance.Minimum(
            _feet[0].Thigh.position, _feet[0].Calf.position, _feet[0].Bone.position,
            _feet[1].Thigh.position, _feet[1].Calf.position, _feet[1].Bone.position);

        private void BeginClearance()
        {
            _clearancePelvis = _pelvis.position;
            _clearanceBefore = Clearance();
            _clearanceFraction = 1f;
            for (int i = 0; i < 2; i++)
            {
                var foot = _feet[i];
                foot.BeforeThigh = foot.Thigh.localRotation;
                foot.BeforeCalf = foot.Calf.localRotation;
                foot.BeforeFoot = foot.Bone.localRotation;
                foot.HasSolveTarget = false;
                foot.SoleTargetError = foot.AnkleLimitDegrees = 0f;
                foot.GroundNormalAvailable = false;
            }
        }

        private void BlendCorrection(float fraction, Vector3 pelvisAfter, int preservedFoot = -1)
        {
            _pelvis.position = preservedFoot >= 0 ? pelvisAfter : Vector3.Lerp(_clearancePelvis, pelvisAfter, fraction);
            for (int i = 0; i < 2; i++)
            {
                var foot = _feet[i];
                float legFraction = i == preservedFoot ? 1f : fraction;
                foot.Thigh.localRotation = Quaternion.Slerp(foot.BeforeThigh, foot.AfterThigh, legFraction);
                foot.Calf.localRotation = Quaternion.Slerp(foot.BeforeCalf, foot.AfterCalf, legFraction);
                foot.Bone.localRotation = Quaternion.Slerp(foot.BeforeFoot, foot.AfterFoot, legFraction);
            }
        }

        private bool FindClearanceFraction(Vector3 pelvisAfter, float required, int preservedFoot, out float fraction)
        {
            BlendCorrection(0f, pelvisAfter, preservedFoot);
            fraction = 0f;
            if (Clearance() < required) return false;
            float low = 0f, high = 1f;
            for (int step = 1; step <= 8; step++)
            {
                float candidate = step / 8f;
                BlendCorrection(candidate, pelvisAfter, preservedFoot);
                if (Clearance() < required) { high = candidate; break; }
                low = candidate;
            }
            for (int iteration = 0; iteration < 7; iteration++)
            {
                float candidate = (low + high) * 0.5f;
                BlendCorrection(candidate, pelvisAfter, preservedFoot);
                if (Clearance() >= required) low = candidate;
                else high = candidate;
            }
            fraction = low;
            return true;
        }

        // A relative bone-line guard, not a mesh collision or anatomical stance solver. Preserve the source
        // animation, including its own crossings; attenuate only a correction that collapses its clearance.
        // Local rotations preserve limb lengths. Search the safe interval connected to the incoming pose.
        private void FinishClearance()
        {
            int preservedFoot = -1;
            _clearanceRequested = Clearance();
            float required = LegClearance.Required(_clearanceBefore);
            if (_clearanceRequested < required)
            {
                Vector3 pelvisAfter = _pelvis.position;
                for (int i = 0; i < 2; i++)
                {
                    var foot = _feet[i];
                    foot.AfterThigh = foot.Thigh.localRotation;
                    foot.AfterCalf = foot.Calf.localRotation;
                    foot.AfterFoot = foot.Bone.localRotation;
                }
                if (LocomotionRefinement.Enabled)
                {
                    if (_feet[0].PendingPlanted && !_feet[1].PendingPlanted) preservedFoot = 0;
                    else if (_feet[1].PendingPlanted && !_feet[0].PendingPlanted) preservedFoot = 1;
                }
                float low;
                // A solved support leg and pelvis remain unchanged if rejecting only the
                // other leg can restore clearance. Rolling both legs back caused plant pops.
                if (!FindClearanceFraction(pelvisAfter, required, preservedFoot, out low))
                {
                    preservedFoot = -1;
                    FindClearanceFraction(pelvisAfter, required, preservedFoot, out low);
                }
                _clearanceFraction = low;
                BlendCorrection(low, pelvisAfter, preservedFoot);
                Vector3 shiftRemoved = pelvisAfter - _pelvis.position;
                _hipShift -= shiftRemoved;
                ClearanceLimits++;
                for (int i = 0; i < 2; i++)
                {
                    var foot = _feet[i];
                    foot.ShownBase -= shiftRemoved;
                    foot.ShownHeel -= shiftRemoved;
                    foot.ShownToe -= shiftRemoved;
                    if (i == preservedFoot) continue;
                    // The old anchor asked for the rejected pose. Re-enter from the accepted sole with the
                    // existing residual decay instead of repeatedly pinning back into the other leg.
                    foot.HasCycle = foot.NextFrozen = foot.Locked = foot.PendingPlanted = false;
                    foot.Residual = Vector3.zero;
                    foot.HasPole = false;
                    foot.LastFailure = 2;
                }
            }
            _clearanceAfter = Clearance();
            for (int i = 0; i < 2; i++)
            {
                var foot = _feet[i];
                if (!foot.PendingPlace && !foot.HasPlaced) continue;
                foot.ReadPlaced();
                if (foot.HasSolveTarget) foot.SoleTargetError = ((foot.SettlementStep ? (foot.PlacedHeel + foot.PlacedToe) * 0.5f : foot.PlacedBase) - foot.SolvedSoleTarget).magnitude;
                if (_clearanceFraction >= 1f || i == preservedFoot) continue;
                foot.AppliedMove = foot.PlacedBase - foot.ShownBase;
                foot.AppliedTurn = Mathf.DeltaAngle(foot.ShownHeading, foot.PlacedHeading);
                foot.Release = foot.PlacedBase - foot.ClipFoot;
                foot.Release.y = 0f;
            }
        }

        // world ankle and support state as drawn by the last placer frame; null before the first
        private bool HasFeedback(int side) => _feet[side].HasLast && Time.frameCount - _feet[side].LastFrame <= 1;
        public Vector3? LastAnkle(int side) => HasFeedback(side) ? _feet[side].LastAnkle : (Vector3?)null;
        public Vector3? LastFootbase(int side) => HasFeedback(side) ? _feet[side].PlacedBase : (Vector3?)null;
        public float? LastProgression(int side) => HasFeedback(side) && _feet[side].HasCycle ? _feet[side].LastProgression : (float?)null;
        public float? CurrentPlacementCost()
        {
            if (!HasFeedback(0) || !HasFeedback(1)) return null;
            float cost = 0f;
            for (int side = 0; side < 2; side++)
                cost += Mathf.Clamp01((_feet[side].AppliedMove.magnitude + _feet[side].Residual.magnitude) / HardCorrection);
            return cost * 0.5f;
        }

        private void PublishFeedback()
        {
            for (int side = 0; side < 2; side++)
            {
                var foot = _feet[side];
                if (!foot.HasPlaced) continue;
                foot.LastAnkle = foot.Bone.position;
                foot.LastPlanted = foot.Locked || foot.PendingPlanted;
                foot.LastFrame = Time.frameCount;
                foot.HasLast = true;
            }
        }

        // the leg as it stands now (hip, knee, ankle in world), for recorders that judge the bend
        public bool LegPoints(int side, out Vector3 hip, out Vector3 knee, out Vector3 ankle)
        {
            var foot = _feet[side];
            hip = knee = ankle = Vector3.zero;
            if (!foot.Thigh || !foot.Calf || !foot.Bone) return false;
            hip = foot.Thigh.position; knee = foot.Calf.position; ankle = foot.Bone.position;
            return true;
        }
        public bool LastPlanted(int side) => HasFeedback(side) && _feet[side].LastPlanted;

        // after a hand-off: Tarkov's idle is underneath, the feet still carry their held corrections. let those bleed
        // off at walking-foot pace instead of with the 0.5 s pose blend, which slid them at 1.5-3 m/s. done when both
        // are within a couple of millimetres of the animated feet
        private bool Release(float dt)
        {
            bool releasing = false;
            // the hips ease back too; dropping the shift with the first release frame moved the body 6 cm at once
            _hipShift = Vector3.MoveTowards(_hipShift, Vector3.zero, HipShiftRate * dt);
            if (_hipShift.sqrMagnitude > 1e-8f && _pelvis)
            {
                _pelvis.position += _hipShift;
                releasing = true;
            }
            for (int i = 0; i < 2; i++)
            {
                var foot = _feet[i];
                if (!foot.HasPlaced)
                    continue;
                foot.SettlementStep = false;
                foot.ReadShown(dt);
                foot.Locked = false;
                foot.LastAuthority = 1f;
                if (foot.AppliedMove.magnitude < 0.002f && Mathf.Abs(foot.AppliedTurn) < 0.5f)
                {
                    foot.Reset();
                    continue;
                }
                releasing = true;
                Vector3 move = Vector3.MoveTowards(foot.AppliedMove, Vector3.zero, FadeReleaseRate * dt);
                float turn = Mathf.MoveTowards(foot.AppliedTurn, 0f, TurnReleaseRate * dt);
                foot.Target = foot.ShownBase + move;
                Place(foot, foot.Target, foot.ShownHeading + turn, 1f, false, dt, true);
            }
            if (!releasing)
            {
                Reset();
                return false;
            }
            FinishClearance();
            PublishFeedback();
            _appliedFrame = Time.frameCount;
            AppliedFrames++;
            return true;
        }

        // A spin suppresses placement while the animated feet turn with the body. Rebase the correction from the
        // last rendered sole against this frame's raw sole, then decay only that correction. The current raw base
        // carries ordinary root and gait motion at its authored speed.
        private bool ReleaseForSpin(StrideState state, float dt, float suppression, float yawDelta)
        {
            bool rebase = !_spinReleasing || state.Clip != _spinReleaseClip
                || Mathf.Abs(yawDelta) > Mathf.Max(SpinRebaseYawFloor, Mathf.Abs(state.YawRate) * dt * 1.5f + SpinRebaseYawSlack);
            _spinReleasing = true;
            _spinReleaseClip = state.Clip;
            float positionStep = FadeReleaseRate * dt * suppression;
            float turnStep = TurnReleaseRate * dt * suppression;

            _hipShift = Vector3.MoveTowards(_hipShift, Vector3.zero, HipShiftRate * dt * suppression);
            if (_pelvis)
                _pelvis.position += _hipShift;

            for (int i = 0; i < 2; i++)
            {
                var foot = _feet[i];
                foot.ReadShown(dt);
                foot.Locked = false;
                foot.HasCycle = false;
                foot.NextFrozen = false;
                foot.PendingPlace = foot.PendingPlanted = false;
                foot.SettlementStep = false;
                foot.Residual = Vector3.zero;
                foot.Release = Vector3.zero;
                foot.SpinReentry = true;
                foot.LastAuthority = 0f;

                Vector3 renderedBase = foot.HasPlaced
                    ? Vector3.Lerp(foot.PlacedToe, foot.PlacedHeel, foot.BaseW)
                    : foot.ShownBase;
                float renderedHeading = foot.HasPlaced ? foot.PlacedHeading : foot.ShownHeading;
                if (rebase)
                {
                    // Keep the last rendered soles through a change of raw yaw/source basis. Do not add root delta:
                    // this frame's root translation is already present in the current raw sole.
                    foot.SpinReleaseMove = renderedBase - foot.ShownBase;
                    foot.SpinReleaseTurn = Mathf.DeltaAngle(foot.ShownHeading, renderedHeading);
                }
                foot.SpinReleaseMove = Vector3.MoveTowards(foot.SpinReleaseMove, Vector3.zero, positionStep);
                foot.SpinReleaseTurn = Mathf.MoveTowards(foot.SpinReleaseTurn, 0f, turnStep);

                foot.Target = foot.ShownBase + foot.SpinReleaseMove;
                Place(foot, foot.Target, foot.ShownHeading + foot.SpinReleaseTurn, 1f, false, dt, true);
                // Place may be a no-op when the release correction is empty. Read the sole either way so resume
                // always starts from what is actually rendered after this pass.
                foot.ReadPlaced();
            }
            FinishClearance();
            PublishFeedback();
            _appliedFrame = Time.frameCount;
            AppliedFrames++;
            return true;
        }

        private bool SpinReleaseComplete()
        {
            if (_hipShift.sqrMagnitude > 1e-8f)
                return false;
            for (int i = 0; i < 2; i++)
            {
                var foot = _feet[i];
                if (foot.SpinReleaseMove.magnitude > 0.002f || Mathf.Abs(foot.SpinReleaseTurn) > 0.5f)
                    return false;
                if (foot.HasPlaced)
                {
                    Vector3 placed = Vector3.Lerp(foot.PlacedToe, foot.PlacedHeel, foot.BaseW);
                    if ((placed - foot.ShownBase).magnitude > 0.002f
                        || Mathf.Abs(Mathf.DeltaAngle(foot.ShownHeading, foot.PlacedHeading)) > 0.5f)
                        return false;
                }
            }
            return true;
        }

        private void UpdateFoot(Foot foot, PoseClip clip, int f, float u, int n, StrideState s, Vector3 origin, float yaw, Vector3 travel, IList<Vector3> corners, float dt)
        {
            StrideFoot stride = clip.Stride[foot.Side];
            int k = stride.Cycle[f];
            StrideCycle cycle = stride.Cycles[k];
            int f1 = clip.Loop ? (f + 1) % n : Mathf.Min(f + 1, n - 1);
            // the next frame only blends in while it belongs to the same stride (a stride boundary is a real event).
            // a loop with one stride per foot per period keeps the same cycle index across the boundary, so the index
            // test alone let progression lerp 1 -> 0 over three frames and rebuilt the foot 25-55 cm back between the
            // old anchors (the in-plant pops in every run capture); a progression drop marks the boundary too
            // only a wrap (1 -> 0) is a boundary: the lateral loops' progression wobbles by a few hundredths inside a
            // stance, and treating every dip as a boundary froze interpolation and stepped those feet at 30 Hz (user:
            // "jittery and floaty" strafes)
            bool sameStride = stride.Cycle[f1] == k && stride.Progression[f1] >= stride.Progression[f] - 0.5f;
            float blend = sameStride ? u : 0f;
            float t = Mathf.Lerp(stride.Progression[f], stride.Progression[f1], blend);
            // Lift/off/strike/land are normalized clip time. Progression is a spatial projection and may differ,
            // including valid negative or above-one values; use the temporal clock only for authored event gates.
            float cycleTime = cycle.CycleTime(f + blend, n, clip.Loop);
            bool inAuthoredSwing = cycle.InAuthoredSwing(cycleTime);
            Vector3 off = Vector3.Lerp(stride.Offset[f], stride.Offset[f1], blend);
            float rotOff = Mathf.Lerp(stride.RotationOffset[f], stride.RotationOffset[f1], blend);
            Vector3 footbase = Vector3.Lerp(stride.Footbase[f], stride.Footbase[f1], blend);
            float footHeading = stride.Heading[f] + Mathf.DeltaAngle(stride.Heading[f], stride.Heading[f1]) * blend;
            // clip heights are Root_Joint-relative already; offsets were measured from the clip's floor
            float groundY = origin.y + stride.Floor;
            foot.ReadShown(dt);
            foot.Origin = origin;
            foot.Yaw = yaw;
            foot.ClipFoot = origin + Quaternion.Euler(0f, yaw, 0f) * new Vector3(footbase.x, 0f, footbase.z);
            foot.ClipFoot.y = groundY + off.y;
            foot.CycleTime = cycleTime;
            foot.InAuthoredSwing = inAuthoredSwing;
            foot.AuthoredGrounded = stride.Grounded[f];
            foot.SettlementStep = false;
            bool finalStopStep = LocomotionRefinement.Enabled && s.Stopping && StopIntoTarkovStance
                && !clip.Loop && k == StopLanding.LastStep(stride);
            foot.StopLandingWeight = finalStopStep && !stride.Grounded[f]
                && cycleTime >= cycle.LiftCycle
                ? StopLanding.LandingWeight(cycle, cycleTime) : 0f;
            foot.StopLandingSeconds = finalStopStep ? Mathf.Max(0f, (cycle.StrikeFrame - (f + blend))
                / (clip.Fps * Mathf.Max(s.Rate, 0.3f))) : 0f;
            foot.LastFailure = 0;
            foot.LastAuthority = 0f;
            // Valve's step-height modulation: a stride squeezed to fit the body's travel lifts less too, so a compressed
            // start does not swing the knee as high as the authored lunge ("raise their knees up too high", user)
            foot.HeightScale = s.UseClipDistance ? 1f : Mathf.Clamp(s.DistanceScale, 0.5f, 1f);

            // a phase-aligned family member is the same cycle in another direction: keep the anchors and the index
            bool familySwitch = false;
            if (foot.HasCycle && foot.Clip != clip && clip.Family != null && foot.Clip != null && foot.Clip.Family == clip.Family)
            {
                if (foot.CycleIndex == k)
                {
                    foot.Clip = clip;
                    familySwitch = true;
                }
                else
                    FamilyCycleMismatches++;
            }
            // a loop with one stride per period wraps onto the same cycle index; the progression falling back tells
            bool wrapped = !familySwitch && foot.HasCycle && foot.Clip == clip && foot.CycleIndex == k && t < foot.LastProgression - 0.5f;
            bool newCycle = !foot.HasCycle || foot.Clip != clip || foot.CycleIndex != k || wrapped;
            foot.LastProgression = t;
            if (newCycle)
            {
                foot.HasMiddle = false;
                foot.SwingPlan = default(SwingClearanceResult);
                foot.SwingPlanAttempted = false;
                StrideCycle previous = foot.HasCycle && foot.Clip == clip ? clip.Stride[foot.Side].Cycles[foot.CycleIndex] : null;
                bool continuing = wrapped || (previous != null && (previous.EndFrame == cycle.StartFrame || previous.EndFrame - n == cycle.StartFrame));
                bool fresh = !foot.HasCycle;
                foot.AxisYaw = yaw;
                foot.Clip = clip;
                foot.CycleIndex = k;
                foot.HasCycle = true;
                foot.NextFrozen = false;
                CycleChanges++;
                if (continuing)
                {
                    // the landing becomes the previous step (slide 55)
                    // the landing becomes the previous step (slide 55): where the foot is actually held, when locked,
                    // so the next stride's offsets start from the real plant and nothing glides after the handover
                    foot.Prev = foot.Locked ? foot.LockBase : foot.Next;
                    foot.PrevYaw = foot.Locked ? foot.LockHeading : foot.NextYaw;
                    Predict(foot, clip, stride, cycle, f, n, s, origin, yaw, groundY, travel, corners, false);
                }
                else if (fresh)
                {
                    // taking over from Tarkov's legs: the playback's own blend-in carries the feet across, so no
                    // residual is owed to where Tarkov's feet were. anchors come from the clip's own foot at this
                    // frame (working back from a scaled prediction drifted 18 cm): a landed foot holds where the
                    // clip has it, a planted one starts there, a swinging one gets a predicted end
                    if (HasLanded(cycle, f, n) || t >= EntryLandedProgression)
                    {
                        Quaternion axis = Quaternion.Euler(0f, yaw + cycle.StrideYaw, 0f);
                        foot.Next = foot.ClipFoot - axis * new Vector3(off.x, 0f, off.z);
                        foot.NextHeadingOffset = stride.Heading[cycle.EndFrame % n];
                        foot.NextYaw = yaw + footHeading - rotOff;
                        foot.NextFrozen = true;
                        StartFromEnd(foot, cycle, s.DistanceScale);
                    }
                    else
                    {
                        Predict(foot, clip, stride, cycle, f, n, s, origin, yaw, groundY, travel, corners, false);
                        StartFromReference(foot, cycle, t, off, yaw, foot.ClipFoot, yaw + footHeading, rotOff);
                    }
                }
                else
                {
                    Enter(foot, clip, stride, cycle, f, n, s, t, off, rotOff, origin, yaw, groundY, travel, corners);
                }
                if (HasLanded(cycle, f, n))
                    foot.NextFrozen = true;
                if (!fresh)
                {
                    // blend from what is on screen, which is the placed foot, not the clip's: measuring against the
                    // clip foot dropped the hold for a frame at every stride handover (2-3 cm pops in the walk raids)
                    float unused;
                    Vector3 shown = foot.HasPlaced ? foot.PlacedBase : foot.ShownBase;
                    foot.Residual = shown - Reconstruct(foot, cycle, t, off, rotOff, groundY, out unused);
                    foot.Residual.y = 0f;
                }
                else if (foot.Release.sqrMagnitude > 0f)
                {
                    // re-entering after a dropped anchor: keep the sole where it was on screen and let the residual
                    // decay carry it onto the new anchors, instead of the one-frame 12-27 cm snap the captures showed
                    float unused;
                    Vector3 held = LocomotionRefinement.Enabled
                        ? Vector3.Lerp(foot.PlacedToe, foot.PlacedHeel, foot.BaseW) : foot.PlacedBase;
                    foot.Residual = held - Reconstruct(foot, cycle, t, off, rotOff, groundY, out unused);
                    if (!LocomotionRefinement.Enabled) foot.Residual.y = 0f;
                    foot.Release = Vector3.zero;
                }
                else if (foot.SpinReentry)
                {
                    // Spin suppression invalidated the old world lock. Resume from the rendered sole pair captured
                    // on the last suppressed frame, even when the residual happens to be numerically zero.
                    float unused;
                    Vector3 held = LocomotionRefinement.Enabled
                        ? Vector3.Lerp(foot.PlacedToe, foot.PlacedHeel, foot.BaseW) : foot.PlacedBase;
                    foot.Residual = held - Reconstruct(foot, cycle, t, off, rotOff, groundY, out unused);
                    if (!LocomotionRefinement.Enabled) foot.Residual.y = 0f;
                    foot.SpinReentry = false;
                }
                else if (LocomotionRefinement.Enabled && foot.HasMiddle)
                {
                    // StartFromReference preserves the incoming linear/authored-offset target. Cancel only
                    // the newly introduced curve displacement at entry, then use the existing residual decay.
                    float unused;
                    Reconstruct(foot, cycle, t, off, rotOff, groundY, out unused);
                    foot.Residual = -foot.MiddleOffsetApplied;
                }
            }
            if (!foot.NextFrozen)
            {
                if (HasLanded(cycle, f, n))
                    foot.NextFrozen = true; // slide 55: don't change once the foot lands
                else
                    Predict(foot, clip, stride, cycle, f, n, s, origin, yaw, groundY, travel, corners, true);
            }

            if (LocomotionRefinement.Enabled && inAuthoredSwing && !foot.SwingPlanAttempted)
            {
                foot.SwingPlanAttempted = true;
                var support = _feet[1 - foot.Side];
                if (support.HasLast && support.LastPlanted && Time.frameCount - support.LastFrame <= 1)
                {
                    float midClock = (cycleTime + cycle.StrikeCycle) * 0.5f;
                    int midFrame = Mathf.RoundToInt(cycle.StartFrame + midClock * (cycle.EndFrame - cycle.StartFrame));
                    int midIndex = clip.Loop ? midFrame % n : Mathf.Clamp(midFrame, 0, n - 1);
                    float midSeconds = Mathf.Max(0f, (midFrame - (f < cycle.StartFrame && clip.Loop ? f + n : f)) / (clip.Fps * Mathf.Max(s.Rate, 0.3f)));
                    Vector3 midRoot = PredictRoot(clip, f, s, origin, travel, corners,
                        ClipDistance(clip, f, midFrame, n) * s.DistanceScale, midSeconds);
                    Vector3 mid = midRoot + Quaternion.Euler(0f, yaw, 0f) * stride.Footbase[midIndex];
                    Vector3 soleToAnkle = foot.Bone.position - foot.ShownBase;
                    Vector3 hip = foot.Thigh.position;
                    Vector3 pole = foot.HasPole ? foot.Pole : foot.Calf.position - (hip + foot.Bone.position) * 0.5f;
                    Vector3 outward = Quaternion.Euler(0f, yaw, 0f) * Vector3.right * (foot.Side == 0 ? -1f : 1f);
                    Vector3 predictedHip = hip + (midRoot - origin);
                    foot.SwingPlan = SwingClearance.Choose(foot.OnScreenBase + soleToAnkle, foot.Next + soleToAnkle,
                        mid + soleToAnkle, predictedHip, predictedHip + pole,
                        (foot.Calf.position - hip).magnitude, (foot.Bone.position - foot.Calf.position).magnitude,
                        support.Thigh.position, support.Calf.position, support.LastAnkle, outward, 0.06f);
                    // Late entry has less time for a detour. The offset starts at zero at
                    // entry and returns to zero at strike, leaving both contact points alone.
                    float seconds = Mathf.Max(0f, (cycle.StrikeCycle - cycleTime) * (cycle.EndFrame - cycle.StartFrame)
                        / (clip.Fps * Mathf.Max(s.Rate, 0.3f)));
                    foot.SwingPlan.MidpointOffset = Vector3.ClampMagnitude(foot.SwingPlan.MidpointOffset, seconds * 0.3f);
                    float availableOffset = foot.SwingPlan.MidpointOffset.magnitude;
                    if (availableOffset + 1e-5f < foot.SwingPlan.OffsetDistance) foot.SwingPlan.Resolved = false;
                    foot.SwingPlan.OffsetDistance = availableOffset;
                    foot.SwingPlanStart = cycleTime;
                }
            }

            float length = foot.Residual.magnitude;
            float decay = MaxResidualDecaySpeed * dt;
            foot.Residual = length <= decay ? Vector3.zero : foot.Residual * ((length - decay) / length);
            PeakResidual = Mathf.Max(PeakResidual, length);

            float heading;
            Vector3 reconstructed = Reconstruct(foot, cycle, t, off, rotOff, groundY, out heading);
            // the anchors are judged without the residual: that part is a deliberate blend, not anchor error
            Vector3 gap = reconstructed - foot.ClipFoot;
            gap.y = 0f;
            if (gap.magnitude > MaxCorrection)
            {
                // anchors have drifted too far from the clip to trust: drop the cycle (a fresh entry next frame) but
                // do not drop the correction with it. hold the sole where it was last drawn this frame and hand the
                // offset to the re-entry as a residual, so the release is a decay, not a pop
                AnchorFailures++;
                foot.LastFailure = 1;
                foot.HasCycle = false;
                foot.Locked = false;
                foot.Residual = Vector3.zero;
                // Recover in the current sole-reference basis. Reusing yesterday's
                // heel/toe base with today's offset can move the ankle a foot-length.
                Vector3 held = foot.HasPlaced
                    ? LocomotionRefinement.Enabled ? Vector3.Lerp(foot.PlacedToe, foot.PlacedHeel, foot.BaseW) : foot.PlacedBase
                    : foot.ShownBase;
                foot.Release = held - foot.ClipFoot;
                if (!LocomotionRefinement.Enabled) foot.Release.y = 0f;
                foot.Target = LocomotionRefinement.Enabled ? held : foot.ClipFoot + foot.Release;
                foot.LastAuthority = s.Weight;
                foot.Request(foot.Target, LocomotionRefinement.Enabled && foot.HasPlaced ? foot.PlacedHeading : foot.ShownHeading, s.Weight, true);
                return;
            }
            // the animation owns the swing (the foot-lock era looked right for that reason, user): a planted foot is
            // held fully, a swinging one is left alone through its first half and steered onto the predicted step
            // over the last part, so prediction error cannot fling it mid-air
            // the hold also covers the start of the next stride, where the previous landing (now Prev) is still the
            // planted foot: only judging by the frozen landing released a 6-7 cm hold at progression 0 (two jumps per
            // steady segment after the boundary fix) until swing steering took over
            bool onGround = off.y < SwingHeight && (stride.Grounded[f] || foot.NextFrozen || t < SwingSteerFrom);
            // the authored swing releases a lock whatever its height: a turn-in-place shuffle never clears the
            // swing height, and the held foot was dragged through its own step (user, turns)
            if (!stride.Grounded[f] && inAuthoredSwing)
                onGround = false;
            if (finalStopStep && !stride.Grounded[f])
                onGround = false;
            Vector3 target;
            // a new lock needs the clip's foot to be still over the ground. at a stop entry the walk had just lifted
            // the left foot and the stop clip carried it forward low and fast; the low, early-stride test read it as
            // grounded, the lock pinned it, and the leg was dragged 35 cm in six frames (knee kicked out, user)
            bool clipStill = foot.Locked || ClipFootGroundSpeed(clip, f, foot.Side) < LockClipSpeed;
            // a foot still planted before its lift point belongs to the previous landing: without this a turn-in-
            // place clip's stance foot (cycle just begun, landing not frozen) rotated with the body (user: "slid
            // in place in a circle")
            bool preLift = stride.Grounded[f] && cycleTime < cycle.LiftCycle;
            if (onGround && clipStill && (foot.NextFrozen || foot.Locked || preLift))
            {
                // Valve's foot lock: a planted sole is pinned where it landed until the clip lifts it. following the
                // reconstructed target instead let it glide 2-3 cm (p90 8-11 cm) through every plant, because the
                // stance offsets of the ending and starting strides disagree and the handover residual settled it
                if (!foot.Locked)
                {
                    // slide 55: freeze the foot where it lands, as drawn. the swing steering withholds part of its
                    // correction (soft cap, damping), and pinning the anchor instead popped the rest in one frame:
                    // fleet batch 2 read 18 cm at the 90th percentile of lock engagements. the anchors follow the
                    // landing (Valve: the previous step is frozen where it landed); a shift past LockTrust was a
                    // wrong prediction (a 57 cm hold seen in a stop) and drops the heading too
                    // the residual is absorbed too (review: shifting by landing - reconstructed - residual left the
                    // anchors reconstructing landing - residual, and the residual keeps decaying under the lock)
                    // height as drawn too: pulling the sole to the reconstructed ground dropped Tarkov's flat plant
                    // 5 cm in one frame and left it toe-down (heel 2.7 cm up, toe 1.6 cm down through the plant)
                    // The sole reference can change from heel to toe between frames.
                    // Capture the previous rendered heel/toe pair in the CURRENT reference
                    // basis; attaching a previous heel point to a new midpoint translated
                    // the entire foot by half its length on the first locked frame.
                    Vector3 landing = LocomotionRefinement.Enabled && foot.HasPlaced
                        ? Vector3.Lerp(foot.PlacedToe, foot.PlacedHeel, foot.BaseW) : foot.OnScreenBase;
                    Vector3 shift = landing - reconstructed;
                    shift.y = 0f;
                    foot.Prev += shift;
                    foot.Next += shift;
                    foot.Residual = Vector3.zero;
                    if (shift.magnitude > LockTrust)
                    {
                        heading = foot.ShownHeading;
                        LockReanchors++;
                    }
                    foot.Locked = true;
                    foot.LockBase = landing;
                    // heel and toe are locked as a pair: the sole reference migrates heel -> toe as the clip rolls
                    // off (in one frame on Tarkov's flat plants), and holding a single point through that either
                    // pinned the lifting heel (feet dragged 0.5-1 m/s under the lock, batch 6) or popped 20 cm
                    foot.LockHeel = LocomotionRefinement.Enabled && foot.HasPlaced ? foot.PlacedHeel : landing + (foot.ShownHeel - foot.ShownBase);
                    foot.LockToe = LocomotionRefinement.Enabled && foot.HasPlaced ? foot.PlacedToe : landing + (foot.ShownToe - foot.ShownBase);
                    foot.LockHeading = heading;
                    foot.LockClipHeading = Mathf.DeltaAngle(yaw, foot.ShownHeading);
                }
                target = Vector3.Lerp(foot.LockToe, foot.LockHeel, foot.BaseW);
                target.y = foot.LockBase.y;
                // hold the heading against the body turning, not against the clip's own foot roll: pinning the landing
                // heading while the clip rotated its foot in the stance spun the sole and swung the ankle inward
                // (walk stance 11 -> 5 cm after the placer, knee bending in, user)
                heading = foot.LockHeading + Mathf.DeltaAngle(foot.LockClipHeading, Mathf.DeltaAngle(yaw, foot.ShownHeading));
                // a turn in place pivots on the planted foot: position held, heading with the body as the clip has
                // it (holding the landing heading twisted the legs at the hip while the body turned, user)
                if (s.YawFollowing)
                    heading = foot.ShownHeading;
            }
            else
            {
                foot.RebaseSwingCorrection = LocomotionRefinement.Enabled && foot.Locked && foot.HasPlaced;
                foot.Locked = false;
                target = reconstructed + foot.Residual;
            }
            foot.Target = target;
            // the release fades with lift height rather than switching off as the sole passes 8 cm, so a held
            // correction is not dropped in one frame at toe-off
            float lift = Mathf.InverseLerp(SwingHeight, SwingHeight * 2.5f, off.y);
            float steer = Mathf.SmoothStep(0f, 1f, (t - SwingSteerFrom) / (1f - SwingSteerFrom));
            float authority = onGround ? 1f : Mathf.Max(1f - lift, steer);
            foot.LastAuthority = s.Weight * authority;
            foot.Request(target, heading, s.Weight * authority, foot.Locked);
        }

        // hips toward the planted feet, by the share of their corrections the lock is about to apply, before either
        // leg is solved (shifting after one solve would undo it). the soles are re-read afterwards so the corrections
        // the placer then applies are the smaller ones
        private void ShiftHips(float dt)
        {
            Vector3 wanted = Vector3.zero;
            float total = 0f;
            for (int i = 0; i < 2; i++)
            {
                var foot = _feet[i];
                if (!foot.PendingPlace || !foot.PendingPlanted)
                    continue;
                Vector3 correction = foot.PendingTarget - foot.ShownBase;
                correction.y = 0f;
                wanted += correction * foot.PendingWeight;
                total += foot.PendingWeight;
            }
            if (total > 0f)
                wanted *= HipShift / total;
            if (wanted.magnitude > MaxHipShift)
                wanted = wanted.normalized * MaxHipShift;
            // knee bend floor: the Valve turn clips and grunt takes are authored near-straight (0.975-0.99
            // hip-ankle over thigh+shin against Tarkov's 0.94 idle; user: "legs too straight"). the hips drop so
            // the straightest planted leg comes under the floor and the leg solve bends both knees
            if (KneeBendFloor > 0f)
            {
                float drop = 0f;
                for (int i = 0; i < 2; i++)
                {
                    var foot = _feet[i];
                    if (!foot.PendingPlace || !foot.PendingPlanted) continue;
                    Vector3 hip = foot.Thigh.position, knee = foot.Calf.position, ankle = foot.Bone.position;
                    float chain = (knee - hip).magnitude + (ankle - knee).magnitude;
                    float straight = (ankle - hip).magnitude / Mathf.Max(chain, 1e-6f);
                    if (straight > KneeBendFloor)
                        drop = Mathf.Max(drop, (straight - KneeBendFloor) * chain);
                }
                wanted.y = -Mathf.Min(drop, MaxKneeDrop);
            }
            _hipShift = Vector3.MoveTowards(_hipShift, wanted, HipShiftRate * dt);
            if (_hipShift.sqrMagnitude < 1e-8f || !_pelvis)
                return;
            _pelvis.position += _hipShift;
            PeakHipShift = Mathf.Max(PeakHipShift, _hipShift.magnitude);
            for (int i = 0; i < 2; i++)
                _feet[i].RefreshShown();
        }

        // the authored start, rebuilt from the predicted end with the clip's end-to-start vector
        // the authored end-to-start vector is scaled like the predicted travel (review: a Prev rebuilt from the
        // unscaled vector while the prediction ran at DistanceScale put the reconstructed history a stride off)
        private static void StartFromEnd(Foot foot, StrideCycle cycle, float scale)
        {
            // bounded: a stop's distance ratio can reach 0 as the body arrives, which collapsed Prev onto Next and
            // failed every anchor of a run stop (55 failures in one raid)
            Vector3 back = Quaternion.Euler(0f, foot.NextYaw - foot.NextHeadingOffset, 0f) * new Vector3(cycle.ToStrideStartPos.x, 0f, cycle.ToStrideStartPos.z) * Mathf.Clamp(scale, 0.5f, 1.5f);
            foot.Prev = foot.Next + back;
            foot.Prev.y = foot.Next.y;
            foot.PrevYaw = foot.NextYaw - cycle.RotationChange;
        }

        // slide 42: a stride entered part-way from another clip keeps its end and moves its start
        private void Enter(Foot foot, PoseClip clip, StrideFoot stride, StrideCycle cycle, int f, int n, StrideState s, float t, Vector3 off, float rotOff, Vector3 origin, float yaw, float groundY, Vector3 travel, IList<Vector3> corners)
        {
            bool stationary = foot.ShownSpeed < StationaryFootSpeed && stride.Grounded[f];
            if (stationary && t < EntryLandedProgression)
            {
                // foot stationary on the ground: it starts from where it is
                foot.Prev = foot.OnScreenBase;
                foot.PrevYaw = foot.ShownHeading - rotOff;
                Predict(foot, clip, stride, cycle, f, n, s, origin, yaw, groundY, travel, corners, false);
                return;
            }
            if (stationary)
            {
                // already down at this stride's end: hold it there and put the start where the clip says it was
                Quaternion axis = Quaternion.Euler(0f, yaw + cycle.StrideYaw, 0f);
                foot.Next = foot.OnScreenBase - axis * new Vector3(off.x, 0f, off.z);
                foot.NextYaw = foot.ShownHeading - rotOff;
                foot.NextHeadingOffset = stride.Heading[cycle.EndFrame % n];
                foot.NextFrozen = true;
                StartFromEnd(foot, cycle, s.DistanceScale);
                return;
            }
            // mid-swing: predict the end, then move the start so the reference points match, clamped to the
            // authored stride length; the leftover goes into the residual and blends out
            Predict(foot, clip, stride, cycle, f, n, s, origin, yaw, groundY, travel, corners, false);
            if (t >= EntryLandedProgression)
                StartFromEnd(foot, cycle, s.DistanceScale);
            else
                StartFromReference(foot, cycle, t, off, yaw, foot.OnScreenBase, foot.ShownHeading, rotOff);
        }

        // slide 42: put the start where it must be for the stride to pass through `reference` at progression t,
        // never further from the end than the authored stride
        private static void StartFromReference(Foot foot, StrideCycle cycle, float t, Vector3 off, float yaw, Vector3 reference, float referenceHeading, float rotOff)
        {
            Quaternion strideAxis = Quaternion.Euler(0f, yaw + cycle.StrideYaw, 0f);
            Vector3 rotated = strideAxis * new Vector3(off.x, 0f, off.z);
            Vector3 next = foot.Next;
            Vector3 prev = new Vector3((reference.x - t * next.x - rotated.x) / (1f - t), next.y, (reference.z - t * next.z - rotated.z) / (1f - t));
            Vector3 d = next - prev;
            d.y = 0f;
            if (d.magnitude > cycle.StrideLength && d.magnitude > 1e-4f)
                prev = next - d.normalized * cycle.StrideLength;
            foot.Prev = prev;
            foot.PrevYaw = referenceHeading - rotOff - t * cycle.RotationChange;
        }

        private void Predict(Foot foot, PoseClip clip, StrideFoot stride, StrideCycle cycle, int f, int n, StrideState s, Vector3 origin, float yaw, float groundY, Vector3 travel, IList<Vector3> corners, bool track)
        {
            int end = cycle.EndFrame;
            int endIndex = end >= n ? end - n : end;
            // slides 47-50: sum the clip's root motion until the step frame, then march that far along the path.
            // with no path the march is a straight line along the travel direction, bent into an arc when the body
            // is turning (a bot curving at 60 deg/s while walking). (two things were tried and reverted: speed x
            // time-to-frame, whose per-frame rate jitter wandered predictions 15-28 cm a frame; and the clip's own
            // displacement vector aligned to the travel direction, which turned a 1.7 m loop stride by up to 60 deg
            // and threw steps a metre sideways whenever the velocity direction wobbled at low speed)
            float distance = ClipDistance(clip, f, end, n) * s.DistanceScale;
            float secondsToEnd = ((end >= n && f > end - n ? end : (end >= n ? end - n : end)) - f) / (clip.Fps * Mathf.Max(s.Rate, 0.3f));
            Vector3 root = PredictRoot(clip, f, s, origin, travel, corners, distance, secondsToEnd);
            // slide 51: facing at the future step, from the clip's own turn plus the body's current turning
            float yawAtEnd = yaw + (s.YawFollowing ? 0f : YawDelta(clip, f, end, n)) + Mathf.Clamp(s.YawRate * secondsToEnd, -MaxTurnLookahead, MaxTurnLookahead);
            Vector3 local = stride.Footbase[endIndex];
            float headingOffset = stride.Heading[endIndex];
            if (s.Stopping && StopIntoTarkovStance && !clip.Loop
                && (!LocomotionRefinement.Enabled || StopLanding.LastStep(stride) >= 0)
                && cycle == stride.Cycles[LocomotionRefinement.Enabled ? Mathf.Max(0, StopLanding.LastStep(stride)) : stride.Cycles.Length - 1])
            {
                // Tarkov's idle stance in the body frame (control raids, standing upright: left 24 cm forward and
                // 15 cm left, right 25 cm back and 14 cm right, tight at p10-p90), feet along the facing.
                // measured from the halt, not from the root at this foot's landing: the body still travels the
                // clip's settle after the last landing, which left both feet 10-20 cm behind the stance and
                // blending forward into Tarkov's idle (user, puppet stop)
                // the playback's halt estimate (the clip is distance-matched to it) beats the clip's remaining root
                // distance, which scattered the right foot 0.17-0.39 m back; and the slots follow the body's
                // facing, not the clip's authored turn (a diagonal hop stop put a foot 0.39 m to the side)
                float toHalt = s.HaltRemaining >= 0f ? s.HaltRemaining : ClipDistance(clip, f, n - 1, n) * s.DistanceScale;
                float secondsToHalt = (n - 1 - f) / (clip.Fps * Mathf.Max(s.Rate, 0.3f));
                root = corners != null && corners.Count > 0 ? March(origin, toHalt, travel, corners) : origin + travel * toHalt;
                yawAtEnd = s.HasFinalYaw ? s.FinalYaw : yaw + Mathf.Clamp(s.YawRate * secondsToHalt, -MaxTurnLookahead, MaxTurnLookahead);
                local = foot.Side == 0 ? TarkovIdleLeft : TarkovIdleRight;
                local.y = stride.Floor;
                headingOffset = 0f;
                if (LocomotionRefinement.Enabled && clip.EndsInTarkovIdle)
                {
                    // These clips already contain the measured native terminal stance.
                    // Replacing it with generic slots/zero toe-out created an extra
                    // position and heading mismatch when the stop finished.
                    local = stride.Footbase[n - 1];
                    headingOffset = stride.Heading[n - 1];
                }
            }
            Vector3 next = root + Quaternion.Euler(0f, yawAtEnd, 0f) * new Vector3(local.x, 0f, local.z);
            next.y = groundY + (local.y - stride.Floor);
            float nextYaw = yawAtEnd + headingOffset;
            if (track)
            {
                PeakStepShift = Mathf.Max(PeakStepShift, (next - foot.Next).magnitude);
                float blend = Mathf.Clamp01(Time.deltaTime / PredictionSmoothing);
                next = Vector3.Lerp(foot.Next, next, blend);
                nextYaw = foot.NextYaw + Mathf.DeltaAngle(foot.NextYaw, nextYaw) * blend;
            }
            foot.Next = next;
            foot.NextHeadingOffset = headingOffset;
            foot.NextYaw = nextYaw;
            if (LocomotionRefinement.Enabled && cycle.HasMidpoint)
            {
                float clock = cycle.CycleTime(f, n, clip.Loop);
                float middleClock = (cycle.MiddleFrame - cycle.StartFrame) / (float)Mathf.Max(1, cycle.EndFrame - cycle.StartFrame);
                if (clock <= middleClock)
                {
                    int mid = cycle.MiddleFrame;
                    float midDistance = ClipDistance(clip, f, mid, n) * s.DistanceScale;
                    int unwrapped = f < cycle.StartFrame && clip.Loop ? f + n : f;
                    float seconds = Mathf.Max(0f, mid - unwrapped) / (clip.Fps * Mathf.Max(s.Rate, 0.3f));
                    float midYaw = yaw + (s.YawFollowing ? 0f : YawDelta(clip, f, mid, n)) + Mathf.Clamp(s.YawRate * seconds, -MaxTurnLookahead, MaxTurnLookahead);
                    Vector3 middleRoot = PredictRoot(clip, f, s, origin, travel, corners, midDistance, seconds);
                    Vector3 middle = middleRoot + Quaternion.Euler(0f, midYaw, 0f) * cycle.MiddlePosition;
                    middle.y = groundY + cycle.MiddleOffset.y * foot.HeightScale;
                    foot.Middle = foot.HasMiddle ? Vector3.Lerp(foot.Middle, middle, Mathf.Clamp01(Time.deltaTime / PredictionSmoothing)) : middle;
                    foot.HasMiddle = true;
                }
            }
        }

        private static Vector3 PredictRoot(PoseClip clip, int frame, StrideState s, Vector3 origin, Vector3 travel,
            IList<Vector3> corners, float distance, float seconds)
        {
            if (LocomotionRefinement.Enabled && !s.Stopping && clip.RootSpeed != null)
            {
                float delta = s.Speed - clip.RootSpeed[frame] * s.Rate * s.DistanceScale;
                distance = Mathf.Max(0f, distance + LandingPrediction.VelocityCorrection(delta, seconds, 0.25f, 0.15f));
            }
            if (corners != null && corners.Count > 0) return March(origin, distance, travel, corners);
            float turned = Mathf.Clamp(s.TravelYawRate * seconds, -MaxTurnLookahead, MaxTurnLookahead);
            if (Mathf.Abs(turned) <= 5f || s.Speed < StandingSpeed) return origin + travel * distance;
            float radians = turned * Mathf.Deg2Rad;
            float chord = distance * Mathf.Sin(Mathf.Abs(radians) * 0.5f) / (Mathf.Abs(radians) * 0.5f);
            return origin + Quaternion.Euler(0f, turned * 0.5f, 0f) * travel * chord;
        }

        public bool StopIntoTarkovStance = true;
        private static readonly Vector3 TarkovIdleLeft = new Vector3(-0.17f, 0f, 0.24f);
        private static readonly Vector3 TarkovIdleRight = new Vector3(0.17f, 0f, -0.25f);

        // a loop cycle can land past the period boundary: its frames are (start, n) then [0, end - n]
        private static bool HasLanded(StrideCycle cycle, int f, int n)
        {
            if (cycle.EndFrame < n)
                return f >= cycle.StrikeFrame;
            bool afterWrap = f <= cycle.EndFrame - n && f < cycle.StartFrame;
            if (cycle.StrikeFrame >= n)
                return afterWrap && f >= cycle.StrikeFrame - n;
            return afterWrap || f >= cycle.StrikeFrame;
        }

        // character-local root displacement from frame f to the cycle's end (per-frame local velocities summed, wrapping
        // through a loop's period), so a curved or sideways root path is followed rather than its length along a line
        private static Vector2 ClipDisplacement(PoseClip clip, int f, int end, int n)
        {
            if (clip.RootVelocity == null)
                return new Vector2(0f, ClipDistance(clip, f, end, n));
            Vector2 sum = Vector2.zero;
            int stop = end >= n && f > end - n ? end : (end >= n ? end - n : end);
            for (int i = f; i < stop; i++)
                sum += clip.RootVelocity[i % n] / clip.Fps;
            return sum;
        }

        // clip root distance from frame f to the cycle's end, wrapping through a loop's period when the end lies past it
        private static float ClipDistance(PoseClip clip, int f, int end, int n)
        {
            if (clip.PathLength == null)
                return 0f;
            float to;
            if (end >= n && f > end - n)
            {
                // still before the wrap: the end lies one period on (a frame already past the wrap is just behind it)
                float period = clip.PathLength[n - 1] + clip.RootSpeed[n - 1] / clip.Fps;
                to = clip.PathLength[end - n] + period;
            }
            else
                to = clip.PathLength[end >= n ? end - n : end];
            return Mathf.Max(0f, to - clip.PathLength[f]);
        }

        private static float YawDelta(PoseClip clip, int f, int end, int n)
        {
            if (clip.YawProgress == null)
                return 0f;
            float to = end >= n && f > end - n ? clip.YawProgress[end - n] + (clip.YawProgress[n - 1] - clip.YawProgress[0]) : clip.YawProgress[end >= n ? end - n : end];
            return to - clip.YawProgress[f];
        }

        // slide 50: move along the path by the same distance as the animation; a straight line when there is no path
        private static Vector3 March(Vector3 from, float distance, Vector3 direction, IList<Vector3> corners)
        {
            if (corners == null || corners.Count == 0)
                return from + direction * distance;
            Vector3 p = from;
            Vector3 last = direction;
            for (int i = 0; i < corners.Count; i++)
            {
                Vector3 segment = corners[i] - p;
                segment.y = 0f;
                float length = segment.magnitude;
                if (length < 1e-4f)
                    continue;
                last = segment / length;
                if (length >= distance)
                    return p + last * distance;
                distance -= length;
                p = corners[i];
            }
            return p + last * distance;
        }

        // foot = start + progression * stride + R(stride) * offset; heading lerps the authored turn, then adds its offset
        private static Vector3 Reconstruct(Foot foot, StrideCycle cycle, float t, Vector3 off, float rotOff, float groundY, out float heading)
        {
            Vector3 d = foot.Next - foot.Prev;
            d.y = 0f;
            float length = d.magnitude;
            float axisYaw = cycle.Stationary || length < StationaryStride ? foot.AxisYaw : Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            Vector3 p = foot.Prev + d * t + Quaternion.Euler(0f, axisYaw, 0f) * new Vector3(off.x, 0f, off.z);
            p.y = groundY + off.y * foot.HeightScale;
            foot.MiddleCorrection = 0f;
            foot.MiddleOffsetApplied = Vector3.zero;
            foot.SwingAvoidanceApplied = 0f;
            if (LocomotionRefinement.Enabled && foot.HasMiddle && cycle.HasMidpoint)
            {
                Vector3 middleOffset = Quaternion.Euler(0f, axisYaw, 0f) * new Vector3(cycle.MiddleOffset.x, 0f, cycle.MiddleOffset.z);
                // Height remains authored. Retarget the horizontal trajectory through the predicted middle.
                Vector3 desired = foot.Middle; desired.y = foot.Prev.y;
                Vector3 end = foot.Next; end.y = foot.Prev.y;
                Vector3 correction = StrideCurve.Correction(foot.Prev, end, middleOffset, cycle.MiddleProgression, desired, t, 0.20f);
                p.x += correction.x; p.z += correction.z;
                foot.MiddleCorrection = correction.magnitude;
                foot.MiddleOffsetApplied = new Vector3(correction.x, 0f, correction.z);
            }
            if (LocomotionRefinement.Enabled && foot.SwingPlan.Valid && foot.InAuthoredSwing && !foot.Locked)
            {
                float clock = Mathf.InverseLerp(foot.SwingPlanStart, cycle.StrikeCycle, foot.CycleTime);
                Vector3 avoidance = foot.SwingPlan.OffsetAt(clock);
                p += avoidance;
                foot.SwingAvoidanceApplied = avoidance.magnitude;
            }
            // the authored rotation change picks which way round a turn past 180 degrees goes
            float raw = Mathf.DeltaAngle(foot.PrevYaw, foot.NextYaw);
            float turn = raw + 360f * Mathf.Round((cycle.RotationChange - raw) / 360f);
            heading = foot.PrevYaw + turn * t + rotOff;
            return p;
        }

        // rigid move of the foot so its FootBase lands on the target: rotate about the vertical through the sole
        // point, translate, then re-solve the leg for the new ankle. blended by the playback's weight
        private void Place(Foot foot, Vector3 target, float heading, float weight, bool planted, float dt, bool direct = false)
        {
            Vector3 shownBase = foot.SettlementStep ? (foot.ShownHeel + foot.ShownToe) * 0.5f : foot.ShownBase;
            if (!direct && !planted && !foot.SettlementStep && foot.RebaseSwingCorrection)
            {
                // A lock stores a world contact, while swing damping stores a correction
                // against the current incoming pose. Rebase after the pelvis adjustment so
                // a changing heel/toe reference cannot jump when the lock releases.
                foot.AppliedMove = Vector3.Lerp(foot.PlacedToe, foot.PlacedHeel, foot.BaseW) - shownBase;
                foot.AppliedTurn = Mathf.DeltaAngle(foot.ShownHeading, foot.PlacedHeading);
            }
            Vector3 move = target - shownBase;
            float magnitude = move.magnitude;
            if (!direct)
            {
                // a planted foot is a lock: the anchor is the truth, the correction is taken whole and it does not
                // follow the clip's blend weight (a locked sole is pinned in the world whatever clip is fading in).
                // the soft slope, the weight and the damping are for swing steering and transitions, where the
                // target is a guess (review: rate-limiting a lock's shrinking correction made it miss its anchor)
                float permitted = planted || magnitude <= SoftCorrection ? magnitude : SoftCorrection + (magnitude - SoftCorrection) * SoftCorrectionSlope;
                // A final airborne step has a destination, rather than an optional pose
                // correction. Reach that destination progressively before contact; the
                // ordinary soft slope otherwise leaves a permanent error which is locked in.
                if (!planted) permitted = Mathf.Lerp(permitted, magnitude, foot.SettlementStep ? 1f : foot.StopLandingWeight);
                permitted = Mathf.Min(permitted, foot.SettlementStep ? Mathf.Max(PlantedCorrection, foot.AppliedMove.magnitude) : planted ? PlantedCorrection : HardCorrection);
                if (magnitude > 1e-6f)
                    move *= permitted / magnitude;
                if (!planted)
                {
                    move *= Mathf.Lerp(weight, 1f, foot.SettlementStep ? 1f : foot.StopLandingWeight);
                    float correctionRate = SwingCorrectionRate;
                    if (foot.SettlementStep) correctionRate = 2f;
                    if (foot.StopLandingWeight > 0f)
                        correctionRate = Mathf.Max(correctionRate, Mathf.Min(2f,
                            (move - foot.AppliedMove).magnitude / Mathf.Max(dt, foot.StopLandingSeconds)));
                    move = Vector3.MoveTowards(foot.AppliedMove, move, correctionRate * dt);
                }
            }
            foot.AppliedMove = move;
            // Valve's foot lock limits the foot to 55 degrees from the character's forward (feet reached 80-87 in the
            // run and cut raids); the limit shapes the wanted heading before weighting so it cannot inject a turn of
            // its own. the spin is about the sole point, so it never moves the contact
            float wantedHeading = heading;
            if (!direct)
            {
                float absolute = Mathf.DeltaAngle(foot.Yaw, wantedHeading);
                if (absolute > MaxFootYaw)
                    wantedHeading -= absolute - MaxFootYaw;
                else if (absolute < -MaxFootYaw)
                    wantedHeading -= absolute + MaxFootYaw;
            }
            // Direct release has already bounded its change from the rendered heading. A clamp against
            // the new raw heading would undo that continuity after a large yaw change.
            float turn = Mathf.DeltaAngle(foot.ShownHeading, wantedHeading);
            if (!direct) turn = Mathf.Clamp(turn, -MaxHeadingCorrection, MaxHeadingCorrection);
            if (!direct && !planted)
            {
                turn *= weight;
                turn = Mathf.MoveTowards(foot.AppliedTurn, turn, TurnReleaseRate * dt);
            }
            foot.AppliedTurn = turn;
            foot.AnkleLimitDegrees = foot.SoleTargetError = 0f;
            foot.GroundNormalAvailable = false;
            if ((!LocomotionRefinement.Enabled || direct) && move.sqrMagnitude < 1e-8f && Mathf.Abs(turn) < 0.01f)
                return;
            Quaternion spin = Quaternion.Euler(0f, turn, 0f);
            Vector3 ankle = foot.Bone.position;
            Quaternion authoredRotation = foot.Bone.rotation;
            Quaternion authoredLocalAnkle = Quaternion.Inverse(foot.Calf.rotation) * authoredRotation;
            Vector3 ankleTarget = shownBase + move + spin * (ankle - shownBase);
            // never ask for more reach than the leg has; a straightened knee pops
            Vector3 hip = foot.Thigh.position;
            float chain = (foot.Calf.position - hip).magnitude + (ankle - foot.Calf.position).magnitude;
            float allowed = Mathf.Max(MaxReach * chain, (ankle - hip).magnitude);
            Vector3 fromHip = ankleTarget - hip;
            if (fromHip.magnitude > allowed)
            {
                ankleTarget = hip + fromHip.normalized * allowed;
                ReachClamps++;
            }
            // the knee keeps the direction the clip gave it (the bend is in the thigh's plane, which the animation
            // authors); only a nearly straight leg, whose bend direction is noise, takes the foot's forward instead
            Vector3 axisHipAnkle = ankle - hip;
            Vector3 kneeOffset = foot.Calf.position - (hip + ankle) * 0.5f;
            if (axisHipAnkle.sqrMagnitude > 1e-6f)
                kneeOffset -= axisHipAnkle * (Vector3.Dot(kneeOffset, axisHipAnkle) / axisHipAnkle.sqrMagnitude);
            // a nearly straight leg (the reach clamp makes many) has no bend direction of its own; instead of falling
            // back to the foot's forward and flipping the knee between frames, keep turning the last pole gently
            Vector3 wanted = kneeOffset.magnitude > 0.03f ? spin * kneeOffset.normalized : Quaternion.Euler(0f, foot.ShownHeading + turn, 0f) * Vector3.forward;
            Vector3 pole = foot.HasPole ? Vector3.RotateTowards(foot.Pole, wanted, PoleTurnRate * Mathf.Deg2Rad * dt, 0f) : wanted;
            foot.Pole = pole;
            foot.HasPole = true;
            Vector3 fallback = _root.TransformDirection(Vector3.right);
            foot.AnkleLimitDegrees = foot.SoleTargetError = 0f;
            foot.GroundNormalAvailable = false;
            if (!LocomotionRefinement.Enabled || direct)
            {
                LegIk.SolveToward(foot.Thigh, foot.Calf, foot.Bone, ankleTarget, pole, fallback);
                foot.Bone.rotation = spin * authoredRotation;
                return;
            }
            var swing = AnkleAlignment.ComputeHipAnkleSwing(hip, ankle, hip, ankleTarget, authoredRotation);
            // Moving a support ankle must not tip its shoe with the hip-to-ankle vector.
            // Keep the clip's heel/toe roll on support; only airborne steering follows the leg swing.
            Quaternion rotation = spin * (!planted && swing.Valid ? swing.TargetFootRotation : authoredRotation);
            Vector3? normal = GroundNormalProvider?.Invoke(foot.Side);
            foot.GroundNormalAvailable = normal.HasValue;
            if (normal.HasValue)
            {
                // Sole axes are derived from the exported sole geometry, not the imported bone's forward axis.
                Vector3 soleAxis = foot.LocalToe - foot.LocalHeel;
                Vector3 soleNormal = Vector3.ProjectOnPlane(-(foot.LocalHeel + foot.LocalToe) * 0.5f, soleAxis).normalized;
                float flatness = 1f - Mathf.Clamp01(Mathf.Abs(Vector3.Dot(foot.ShownToe - foot.ShownHeel, normal.Value.normalized)) / 0.06f);
                float contact = planted ? flatness : 0f;
                var aligned = AnkleAlignment.AlignSoleToSurface(rotation, rotation * soleAxis, rotation * soleNormal,
                    normal.Value, foot.ShownHeading + turn, contact);
                if (aligned.Valid) rotation = aligned.TargetRotation;
            }
            Vector3 soleTarget = shownBase + move;
            foot.HasSolveTarget = true;
            foot.SolvedSoleTarget = soleTarget;
            Vector3 localSole = Quaternion.Inverse(authoredRotation) * (shownBase - ankle);
            ankleTarget = soleTarget - rotation * localSole;
            ankleTarget = hip + Vector3.ClampMagnitude(ankleTarget - hip, allowed);
            LegIk.SolveTowardWithTargetRotation(foot.Thigh, foot.Calf, foot.Bone, ankleTarget, pole, fallback, rotation);
            var limited = AnkleAlignment.ConstrainAnkleAngle(foot.Calf.rotation, authoredLocalAnkle, rotation);
            if (limited.Valid && limited.Limited)
            {
                foot.AnkleLimitDegrees = limited.AngularLimitationDegrees;
                // Calf orientation and ankle target are coupled. A single second pass can violate the angle
                // again; iterate the sole-preserving solve with a bounded budget until both agree.
                for (int iteration = 0; iteration < 6 && limited.Valid && limited.Limited; iteration++)
                {
                    rotation = limited.TargetRotation;
                    ankleTarget = hip + Vector3.ClampMagnitude(soleTarget - rotation * localSole - hip, allowed);
                    LegIk.SolveTowardWithTargetRotation(foot.Thigh, foot.Calf, foot.Bone, ankleTarget, pole, fallback, rotation);
                    limited = AnkleAlignment.ConstrainAnkleAngle(foot.Calf.rotation, authoredLocalAnkle, rotation);
                }
                // Unreachable contacts prioritize reach and ankle bounds. Expose the remaining sole error
                // instead of claiming the lock still hits its requested target.
                if (limited.Valid && limited.Limited) foot.Bone.rotation = limited.TargetRotation;
            }
            foot.SoleTargetError = (foot.Bone.position + foot.Bone.rotation * localSole - soleTarget).magnitude;
        }

        private void Draw(Vector3 origin, Vector3 travel, IList<Vector3> corners)
        {
            _path.Clear();
            _path.Add(origin);
            if (corners != null && corners.Count > 0)
                for (int i = 0; i < corners.Count; i++) _path.Add(corners[i]);
            else
                _path.Add(origin + travel * PathDraw);
            DebugDraw.Polyline(_path, Color.cyan);
            for (int i = 0; i < 2; i++)
            {
                var foot = _feet[i];
                if (!foot.HasCycle) continue;
                DebugDraw.Box(foot.Prev, Color.red, yawDegrees: foot.PrevYaw);
                DebugDraw.Box(foot.Next, foot.NextFrozen ? new Color(0.3f, 0.3f, 1f) : Color.blue, yawDegrees: foot.NextYaw);
                DebugDraw.Box(foot.PlacedBase, Color.green, yawDegrees: foot.PlacedHeading);
                DebugDraw.Line(foot.ShownBase, foot.Target, Color.yellow);
            }
        }

        private sealed class Foot
        {
            public readonly int Side;
            public readonly Transform Thigh, Calf, Bone;
            private readonly Vector3 _heelOffset, _toeOffset;
            public Vector3 LocalHeel => _heelOffset;
            public Vector3 LocalToe => _toeOffset;
            public bool GroundNormalAvailable;
            public float AnkleLimitDegrees, SoleTargetError;
            public bool HasSolveTarget;
            public Vector3 SolvedSoleTarget;
            public bool HasCycle;
            public PoseClip Clip;
            public int CycleIndex;
            public Vector3 Prev, Next;
            public Vector3 Middle;
            public bool HasMiddle;
            public float MiddleCorrection;
            public Vector3 MiddleOffsetApplied;
            public SwingClearanceResult SwingPlan;
            public bool SwingPlanAttempted;
            public float SwingPlanStart, SwingAvoidanceApplied;
            public float PrevYaw, NextYaw;
            // the clip's own foot heading at the end stance, so the character yaw there can be recovered
            public float NextHeadingOffset;
            // facing when the stride began: frames the offsets of a stationary stride, as the root yaw did offline
            public float AxisYaw;
            public bool NextFrozen;
            public float LastProgression;
            public float CycleTime;
            public bool InAuthoredSwing;
            public bool AuthoredGrounded;
            public Vector3 Residual;
            public Vector3 ShownBase, Target, Origin, ClipFoot;
            // what the viewer saw last frame: the placed sole once the placer has run, else the clip's
            public Vector3 OnScreenBase => HasPlaced ? PlacedBase : ShownBase;
            // the sole after placement: what is actually on screen
            public Vector3 PlacedBase;
            public float PlacedHeading;
            public bool HasPlaced;
            public float Yaw;
            public float ShownHeading;
            public float ShownSpeed;
            public float LastAuthority;
            public float StopLandingWeight;
            public float StopLandingSeconds;
            public bool SettlementStep;
            public int LastFailure;
            // the plant lock: where the sole is pinned while the clip keeps it on the ground
            public bool Locked;
            public Vector3 LastAnkle;
            public bool LastPlanted, HasLast;
            public int LastFrame;
            public Vector3 LockBase, LockHeel, LockToe;
            public Vector3 PlacedHeel, PlacedToe;
            public Vector3 ShownHeel, ShownToe;
            public float BaseW => Mathf.Clamp01(_baseW);
            public float LockHeading;
            // the clip's foot heading relative to the body when the lock took, so the clip's own foot rotation passes through
            public float LockClipHeading;
            // last applied correction, heading correction and knee pole, for the damping in Place
            public Vector3 AppliedMove;
            public float AppliedTurn;
            public Vector3 Pole;
            public bool HasPole;
            public Quaternion BeforeThigh, BeforeCalf, BeforeFoot, AfterThigh, AfterCalf, AfterFoot;
            // step height factor for a compressed stride (Valve: modulate step height, decrease scale 1.0)
            public float HeightScale = 1f;
            // correction carried over a dropped anchor, consumed by the next entry as its residual
            public Vector3 Release;
            public Vector3 SpinReleaseMove;
            public float SpinReleaseTurn;
            public bool SpinReentry;
            private bool _hasShown;

            public Foot(int side, Transform thigh, Transform calf, Transform bone, Vector3 heelOffset, Vector3 toeOffset)
            {
                Side = side;
                Thigh = thigh;
                Calf = calf;
                Bone = bone;
                _heelOffset = heelOffset;
                _toeOffset = toeOffset;
            }

            public void Reset()
            {
                HasLast = LastPlanted = false;
                LastAnkle = Vector3.zero;
                LastFrame = -1;
                HasCycle = false;
                HasMiddle = false;
                MiddleCorrection = 0f;
                SwingPlan = default(SwingClearanceResult);
                SwingPlanAttempted = false;
                SwingAvoidanceApplied = 0f;
                PendingPlace = PendingPlanted = false;
                RebaseSwingCorrection = false;
                NextFrozen = false;
                CycleTime = 0f;
                InAuthoredSwing = false;
                AuthoredGrounded = false;
                Residual = Vector3.zero;
                Release = Vector3.zero;
                SpinReleaseMove = Vector3.zero;
                SpinReleaseTurn = 0f;
                SpinReentry = false;
                HasPlaced = false;
                _baseW = -1f;
                Locked = false;
                AppliedMove = Vector3.zero;
                AppliedTurn = 0f;
                HasPole = false;
                HeightScale = 1f;
                LastFailure = 0;
                LastAuthority = 0f;
                StopLandingWeight = 0f;
                StopLandingSeconds = 0f;
                SettlementStep = false;
                _hasShown = false;
            }

            // the FootBase the pose on screen has right now: the lower of the two sole points, blended near the
            // crossover exactly as the offline analysis did, so offsets mean the same thing here
            public void ReadPlaced()
            {
                Vector3 heel = Bone.position + Bone.rotation * _heelOffset;
                Vector3 toe = Bone.position + Bone.rotation * _toeOffset;
                float w = BaseWeight(heel, toe);
                PlacedBase = Vector3.Lerp(toe, heel, w);
                PlacedHeel = heel;
                PlacedToe = toe;
                PlacedHeading = Mathf.Atan2(toe.x - heel.x, toe.z - heel.z) * Mathf.Rad2Deg;
                HasPlaced = true;
            }

            public bool PendingPlace;
            public bool RebaseSwingCorrection;
            public Vector3 PendingTarget;
            public float PendingHeading, PendingWeight;
            public bool PendingPlanted;

            public void Request(Vector3 target, float heading, float weight, bool planted)
            {
                PendingPlace = true;
                PendingTarget = target;
                PendingHeading = heading;
                PendingWeight = weight;
                PendingPlanted = planted;
            }

            // the sole as the pose has it now, after the hips moved. the speed baseline stays the unshifted sole:
            // measuring next frame's unshifted sole against a shifted one read a still foot at metres per second
            public void RefreshShown()
            {
                Vector3 heel = Bone.position + Bone.rotation * _heelOffset;
                Vector3 toe = Bone.position + Bone.rotation * _toeOffset;
                float w = BaseWeight(heel, toe);
                ShownHeel = heel;
                ShownToe = toe;
                ShownBase = Vector3.Lerp(toe, heel, w);
                ShownHeading = Mathf.Atan2(toe.x - heel.x, toe.z - heel.z) * Mathf.Rad2Deg;
            }

            private Vector3 _speedBase;
            private float _baseW = -1f;

            // the lower sole point, blended across the crossover; a flat plant (Tarkov's clips land heel and toe
            // at the same height) holds the end the base came from, the same rule as tools/stride_data.py
            private float BaseWeight(Vector3 heel, Vector3 toe)
            {
                float diff = toe.y - heel.y;
                float w = Mathf.Clamp01(diff / FootbaseBlend * 0.5f + 0.5f);
                if (_baseW >= 0f && Mathf.Abs(diff) < FootbaseBlend * 0.5f && (_baseW <= 0f || _baseW >= 1f))
                    w = _baseW;
                _baseW = w;
                return w;
            }

            public void ReadShown(float dt)
            {
                PendingPlace = false;
                RebaseSwingCorrection = false;
                Vector3 heel = Bone.position + Bone.rotation * _heelOffset;
                Vector3 toe = Bone.position + Bone.rotation * _toeOffset;
                float w = BaseWeight(heel, toe);
                ShownHeel = heel;
                ShownToe = toe;
                Vector3 shown = Vector3.Lerp(toe, heel, w);
                ShownSpeed = _hasShown ? (shown - _speedBase).magnitude / dt : float.MaxValue;
                _hasShown = true;
                _speedBase = shown;
                ShownBase = shown;
                ShownHeading = Mathf.Atan2(toe.x - heel.x, toe.z - heel.z) * Mathf.Rad2Deg;
            }
        }
    }

    internal struct FootPlacerSummary
    {
        public bool Enabled;
        public int AppliedFrames;
        public int CycleChanges;
        public int ReachClamps;
        public float PeakResidual;
        public float PeakStepShift;
        public int AnchorFailures;
        public float PeakHipShift;
        public int ClearanceLimits;
    }
}
