using System;
using System.Collections.Generic;
using System.Globalization;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.MotionMatching
{
    // drives one bot's body from a fixed script through the same Player calls BotMover uses
    // (Move/ChangeSpeed/Rotate/EnableSprint) while its AI update is skipped, so runs are repeatable
    internal sealed class PuppetSession : IDisposable
    {
        private const float StuckSeconds = 2f;
        private const float ProgressMeters = 0.05f;
        private const float WallMargin = 0.6f;
        // 540 deg/s swung the whole lower body ~0.5 m between two frames when a strafe followed a sprint,
        // which reads as the character snapping; bots turn far slower than that
        private const float BodyYawRate = 240f;
        // bots swing onto a sprint heading rather than snapping to it; 540 deg/s left the transition footwork behind
        private const float SprintYawRate = 180f;
        // sprints need far more room than walks: 7 m plus a 1.8 m slide
        private const float MaxLaneMeters = 20f;
        private const float MinLaneMeters = 4f;
        private const float PreferredAreaSearchRadiusMeters = 12f;
        private const float PreferredAreaSnapMeters = 1.5f;
        private const float JumpGroundEligibilityTimeoutSeconds = 1.5f;
        private const float JumpAirborneTimeoutSeconds = 1.25f;
        private const float JumpLandingTimeoutSeconds = 4f;

        private struct PreferredTestArea
        {
            public string LocationId;
            public Vector3 Position;
            public float Yaw;
        }

        private static PuppetSession _active;
        private static PreferredTestArea? _preferredTestArea;
        private static Vector3? _openTestCenter;
        private static float _openTestRadius;
        internal static void SetOpenTestArea(string location, Vector3 center, float radius)
        {
            _preferredTestArea = new PreferredTestArea { LocationId = location, Position = center, Yaw = 0f };
            _openTestCenter = center; _openTestRadius = radius;
        }

        private readonly Player _player;
        private readonly BrainLease _brain;
        private readonly PuppetStep[] _steps;
        private readonly List<PuppetStepReport> _reports = new List<PuppetStepReport>();
        private bool _compatibilityEnabled;
        private bool _disposed;

        private int _stepIndex = -1;
        private bool _stepClosed;
        private float _headingYaw;
        private float _stepStartTime;
        private int _stepStartFrame;
        private Vector3 _stepStartPosition;
        private float _plannedMeters;
        private float _turned;
        private Vector3 _lastProgressPosition;
        private float _lastProgressTime;
        private int _lastDriveFrame = -1;
        private Vector3 _laneStart;
        private Vector3 _laneEnd;
        private Player _viewer;
        // strafes keep the body on the viewer and travel along a world direction fixed when the step starts
        private bool _faceViewer;
        private float _moveYaw;
        private float _jumpDistanceReachedTime = -1f;
        private bool _jumpTriggered;
        private bool _jumpGroundedAtTrigger;
        private bool _jumpSawAirborne;
        private bool _jumpLanded;
        private int _jumpTriggerFrame = -1;
        private float _jumpTriggerTime = -1f;
        private int _jumpLandingFrame = -1;
        private float _jumpLandingTime = -1f;

        internal static void ClearPreferredTestArea()
        {
            _openTestCenter = null;
            _preferredTestArea = null;
        }

        internal static bool TrySetPreferredTestArea(string locationId, Vector3 position, float yaw, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(locationId))
                error = "Preferred test area requires a map id.";
            else if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) || !IsFinite(yaw))
                error = "Preferred test area coordinates and yaw must be finite numbers.";
            if (error != null)
                return false;

            _openTestCenter = null;
            _preferredTestArea = new PreferredTestArea { LocationId = locationId, Position = position, Yaw = yaw };
            return true;
        }

        internal static bool TryGetActiveReport(out PuppetReport report)
        {
            var active = _active;
            if (active == null || active._disposed)
            {
                report = default(PuppetReport);
                return false;
            }
            report = active.Report;
            return true;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private PuppetSession(Player player, string scenario, bool freezeOthers)
        {
            if (player == null || !player.IsAI || player.HealthController == null || !player.HealthController.IsAlive)
                throw new InvalidOperationException("Puppet rejected: selected player is not a living AI bot.");
            var owner = player.AIData?.BotOwner;
            if (owner == null || owner.BotState != EBotState.Active)
                throw new InvalidOperationException("Puppet rejected: bot is not active.");
            if (player.IsInPronePose)
                throw new InvalidOperationException("Puppet rejected: bot is prone.");

            _steps = PuppetScript.Parse(scenario);
            _brain = new BrainLease(owner, "Puppet rejected: ");
            _player = player;
            Owner = owner;
            Scenario = scenario;
            FreezeOthers = freezeOthers;
        }

        public BotOwner Owner { get; }
        public string Scenario { get; }
        public bool FreezeOthers { get; }
        public bool? SprintRequested => _disposed || IsComplete || _stepIndex < 0 || _stepIndex >= _steps.Length
            ? (bool?)null : IsSprintStep(_steps[_stepIndex].Kind);
        public bool IsComplete { get; private set; }
        public string Failure { get; private set; }
        public bool BroughtToPlayer { get; private set; }
        public bool MovedViewer { get; private set; }
        public float LaneYaw { get; private set; }
        // the heading the puppet is turning toward, relative to its facing; null once it faces it
        public float? GoalYaw
        {
            get
            {
                if (_disposed || _player == null) return null;
                // a turn step reports what is left of its full turn (a 180 is not a 90 read at its midpoint)
                if (_stepIndex >= 0 && _stepIndex < _steps.Length && _steps[_stepIndex].Kind == PuppetStepKind.Turn)
                {
                    float left = _steps[_stepIndex].Degrees - _turned;
                    return Mathf.Abs(left) > 5f ? (float?)left : null;
                }
                float delta = Mathf.DeltaAngle(_player.Rotation.x, _headingYaw);
                return Mathf.Abs(delta) > 5f ? (float?)delta : null;
            }
        }
        public float LaneClearMeters { get; private set; }
        public int StepIndex => _stepIndex;
        public int StepCount => _steps.Length;
        public string CurrentStep => _stepIndex >= 0 && _stepIndex < _steps.Length ? _steps[_stepIndex].ToString() : null;

        // the intent pose playback keys starts and stops off
        public bool WantsMove => MoveIntentYaw.HasValue;

        // travel direction relative to the body in degrees, null while the script wants the bot standing
        public float? MoveIntentYaw
        {
            get
            {
                if (_disposed || IsComplete || _stepIndex < 0 || _stepIndex >= _steps.Length) return null;
                var kind = _steps[_stepIndex].Kind;
                if (kind == PuppetStepKind.Strafe) return Mathf.DeltaAngle(_player.Rotation.x, _moveYaw);
                // a direction step travels along its heading while the body is still swinging onto it: the travel
                // relative to the facing is what a stock bot's mover reports in the same situation
                if (kind == PuppetStepKind.SprintTo || kind == PuppetStepKind.MoveTo) return Mathf.DeltaAngle(_player.Rotation.x, _headingYaw);
                return IsMoving(kind) ? 0f : (float?)null;
            }
        }

        // metres left in the current move step; the playback anticipates the stop from it like a real driver's goal.
        // curves have no planned distance and the standing steps have nothing left
        public float? RemainingMeters
        {
            get
            {
                if (_disposed || IsComplete || _stepIndex < 0 || _stepIndex >= _steps.Length) return null;
                var kind = _steps[_stepIndex].Kind;
                if (!IsMoving(kind) || kind == PuppetStepKind.Curve) return null;
                return Mathf.Max(0f, _plannedMeters - Horizontal(_player.Position - _stepStartPosition));
            }
        }

        // the step's commanded Player.Speed (0-1); sprints are full speed, standing steps have no command
        public float? CommandSpeed
        {
            get
            {
                if (_disposed || IsComplete || _stepIndex < 0 || _stepIndex >= _steps.Length) return null;
                var step = _steps[_stepIndex];
                if (!IsMoving(step.Kind)) return null;
                return IsSprintStep(step.Kind) ? 1f : step.Speed;
            }
        }

        private static bool IsMoving(PuppetStepKind kind) => kind == PuppetStepKind.Move || kind == PuppetStepKind.Sprint || kind == PuppetStepKind.Curve || kind == PuppetStepKind.Strafe || kind == PuppetStepKind.SprintTo || kind == PuppetStepKind.MoveTo || kind == PuppetStepKind.JumpMove || kind == PuppetStepKind.JumpSprint;

        private static bool IsSprintStep(PuppetStepKind kind) => kind == PuppetStepKind.Sprint || kind == PuppetStepKind.SprintTo || kind == PuppetStepKind.JumpSprint;

        private static bool IsJumpStep(PuppetStepKind kind) => kind == PuppetStepKind.JumpMove || kind == PuppetStepKind.JumpSprint;

        public static PuppetSession Start(Player player, Player viewer, string scenario, bool freezeOthers, bool bringToViewer)
        {
            if (_active != null && !_active._disposed)
            {
                ClearPreferredTestArea();
                throw new InvalidOperationException("Puppet rejected: another puppet is already active.");
            }
            var session = new PuppetSession(player, scenario, freezeOthers);
            _active = session;
            try
            {
                session.Begin(viewer, bringToViewer);
                ClearPreferredTestArea();
                Debug.Log("[" + ModInfo.Name + "] Puppet started: " + scenario);
                return session;
            }
            catch
            {
                ClearPreferredTestArea();
                session.Dispose();
                throw;
            }
        }

        // true for the puppet, and for every other bot while freezing is on
        internal static bool ShouldSkipAi(BotOwner owner)
        {
            var active = _active;
            return active != null && !active._disposed && (active.FreezeOthers || ReferenceEquals(owner, active.Owner));
        }

        internal static bool IsPuppetContext(MovementContext context)
        {
            var active = _active;
            return active != null && !active._disposed && active._player != null && ReferenceEquals(active._player.MovementContext, context);
        }

        internal static void DriveFromAiUpdate(BotOwner owner)
        {
            var active = _active;
            if (active != null && !active._disposed && ReferenceEquals(owner, active.Owner))
                active.DriveSafely();
        }

        public PuppetReport Report
        {
            get
            {
                return new PuppetReport
                {
                    Scenario = Scenario,
                    FreezeOthers = FreezeOthers,
                    BroughtToPlayer = BroughtToPlayer,
                    MovedViewer = MovedViewer,
                    LaneYaw = LaneYaw,
                    LaneClearMeters = LaneClearMeters,
                    LaneStart = new[] { _laneStart.x, _laneStart.y, _laneStart.z },
                    LaneEnd = new[] { _laneEnd.x, _laneEnd.y, _laneEnd.z },
                    Reseats = Reseats,
                    Steps = _reports.ToArray()
                };
            }
        }

        // fallback for frames where BotsList didnt tick this bot; Drive dedupes per frame
        public void Tick()
        {
            if (_disposed || IsComplete)
                return;
            if (Owner.GetPlayer == null || Owner.BotState != EBotState.Active || !_player.HealthController.IsAlive)
                throw new InvalidOperationException("Puppet aborted: bot is no longer active and alive.");
            if (!_brain.IsIntact)
                throw new InvalidOperationException("Puppet aborted: bot brain changed.");
            if (_brain.IsReRegistered)
                throw new InvalidOperationException("Puppet aborted: vanilla brain agent was re-registered.");
            Drive();
        }

        // keeps the viewer's camera on the puppet so animation isnt culled
        public void FaceViewer(Player viewer)
        {
            if (_disposed || viewer == null)
                return;
            Vector3 to = _player.Position - viewer.Position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.25f)
                return;
            float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            float delta = Mathf.DeltaAngle(viewer.Rotation.x, yaw);
            if (Mathf.Abs(delta) > 0.5f)
                viewer.Rotate(new Vector2(delta, 0f), false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                Cleanup(() =>
                {
                    if (!IsUsable()) return;
                    _player.Move(Vector2.zero);
                    if (_player.IsSprintEnabled) _player.EnableSprint(false);
                });
                if (_stepIndex >= 0 && _stepIndex < _steps.Length)
                    CloseStep(Failure ?? "session ended");
                Cleanup(() => _brain.Release(IsUsable()));
            }
            finally
            {
                try { if (_compatibilityEnabled) Cleanup(RouteCompatibility.Disable); }
                finally
                {
                    _compatibilityEnabled = false;
                    if (ReferenceEquals(_active, this)) _active = null;
                }
            }
            Debug.Log("[" + ModInfo.Name + "] Puppet released; AI updates resumed.");
        }

        private void Begin(Player viewer, bool bringToViewer)
        {
            PreferredTestArea? preferredArea = _preferredTestArea;
            if (preferredArea.HasValue)
            {
                VerifyPreferredAreaMap(preferredArea.Value);
                if (!bringToViewer || viewer == null)
                    throw new InvalidOperationException("Puppet rejected: preferred test area requires the viewer lane placement path.");
            }
            if (!RouteCompatibility.TryEnable())
                throw new InvalidOperationException("Puppet rejected: SAIN compatibility could not be established safely.");
            _compatibilityEnabled = RouteCompatibility.IsEnabled;
            _brain.Acquire("Puppet rejected: ");
            ClearMoverPath();
            _viewer = viewer;

            Vector3 lanePosition;
            float laneYaw;
            float clear;
            if (bringToViewer && viewer != null)
            {
                var stats = new LaneSearchStats();
                LaneChoice choice;
                if (!FindViewerLane(viewer, _player.Position, out choice, stats))
                {
                    if (preferredArea.HasValue)
                    {
                        var area = preferredArea.Value;
                        string position = string.Format(CultureInfo.InvariantCulture, "({0:F1}, {1:F1}, {2:F1})", area.Position.x, area.Position.y, area.Position.z);
                        throw new InvalidOperationException("Puppet rejected: no usable lane in the local 6-12 m search around preferred test area " + position + " on map '" + area.LocationId + "' (" + stats + ").");
                    }
                    throw new InvalidOperationException("Puppet rejected: no clear, reachable lane found (" + stats + ").");
                }
                lanePosition = choice.Position;
                laneYaw = choice.Yaw;
                clear = choice.Clear;
                if (choice.ViewerSpot.HasValue)
                {
                    viewer.Teleport(Grounded(choice.ViewerSpot.Value));
                    MovedViewer = true;
                }
                _player.Teleport(Grounded(lanePosition));
                BroughtToPlayer = true;
            }
            else
            {
                lanePosition = OnMesh(_player.Position);
                if (!FindLane(lanePosition, Vector3.zero, out laneYaw, out clear))
                    throw new InvalidOperationException("Puppet rejected: no clear lane around the bot.");
            }

            LaneYaw = laneYaw;
            LaneClearMeters = clear;
            _headingYaw = laneYaw;
            Vector3 laneDirection = Quaternion.Euler(0f, laneYaw, 0f) * Vector3.forward;
            _laneStart = lanePosition;
            _laneEnd = OnMesh(lanePosition + laneDirection * Mathf.Max(0f, ClearDistance(lanePosition, laneDirection) - WallMargin));
            // a strafe script should already be facing the viewer when its first strafe begins
            foreach (PuppetStep first in _steps)
            {
                if (first.Kind == PuppetStepKind.Stop) continue;
                if (first.Kind == PuppetStepKind.Strafe && viewer != null)
                {
                    // lanes run across the view, so side strafes need room both ways: start mid-lane, not at one end
                    // a factory lane midpoint once snapped onto a raised surface 0.7 m up, leaving no room anywhere
                    Vector3 middle;
                    NavMeshHit blocked;
                    // the lane is a straight line between two mesh points: on woods terrain its midpoint sat below the
                    // ground and the bot fell out of the world, so an unsnapped midpoint is never teleported to
                    if (!_openTestCenter.HasValue && TryOnMesh(Vector3.Lerp(_laneStart, _laneEnd, 0.5f), 0.4f, out middle)
                        && Horizontal(middle - lanePosition) > 0.5f && Mathf.Abs(middle.y - lanePosition.y) < 0.3f
                        && !NavMesh.Raycast(lanePosition, middle, out blocked, NavMesh.AllAreas))
                    {
                        _player.Teleport(Grounded(middle));
                        lanePosition = middle;
                    }
                    // facing the viewer only lines sidesteps up with the lane when the viewer stands beside it
                    Vector3 viewerSpot = viewer.Position;
                    Vector3 besideSpot;
                    if (!_openTestCenter.HasValue && TryBesideLane(lanePosition, out besideSpot))
                    {
                        viewer.Teleport(Grounded(besideSpot));
                        viewerSpot = besideSpot;
                        MovedViewer = true;
                    }
                    Vector3 to = viewerSpot - lanePosition;
                    to.y = 0f;
                    if (to.sqrMagnitude > 1f)
                    {
                        laneYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                        _headingYaw = laneYaw;
                        _faceViewer = true;
                    }
                }
                break;
            }
            _player.Rotate(new Vector2(Mathf.DeltaAngle(_player.Rotation.x, laneYaw), 0f), true);
            _player.Move(Vector2.zero);
            AdvanceStep();
            // Player.Position can still report the pre-teleport spot this frame
            _stepStartPosition = _lastProgressPosition = lanePosition;
        }

        // BotMoverImpostor.OnMotionApplied snaps the body back onto the mover's path point after every
        // character-controller move while a path exists; with MovePlayer skipped that point never advances
        private void ClearMoverPath()
        {
            var mover = Owner.Mover;
            if (mover != null && mover.ActualPathController != null && mover.ActualPathController.HavePath)
                mover.Stop();
        }

        private void DriveSafely()
        {
            try { Drive(); }
            catch (Exception ex)
            {
                // never let a puppet fault escape into BotsList's loop and stall every other bot
                Debug.LogError("[" + ModInfo.Name + "] Puppet drive failed: " + ex);
                Failure = "drive failed: " + ex.Message;
                IsComplete = true;
            }
        }

        private void Drive()
        {
            if (_disposed || IsComplete || Time.frameCount == _lastDriveFrame)
                return;
            _lastDriveFrame = Time.frameCount;
            ClearMoverPath();
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            PuppetStep step = _steps[_stepIndex];
            bool moving = IsMoving(step.Kind);
            bool sprint = IsSprintStep(step.Kind);
            float viewerYaw;
            if (_faceViewer && TryViewerYaw(out viewerYaw))
                _headingYaw = viewerYaw;
            if (_player.IsSprintEnabled != sprint)
                _player.EnableSprint(sprint);
            // a bot that sees the viewer aims and EFT caps its speed at 0.33 of max (a run leg steadied at 1.64 m/s
            // and the summary flagged it); the puppet's legs are the experiment, not the bot's aim
            try { _player.MovementContext.SetAimingSlowdown(false, 0f); } catch { }
            if (_player.PoseLevel < 0.99f)
                _player.ChangePose(1f - _player.PoseLevel);
            if (moving)
            {
                float target = sprint ? 1f : step.Speed;
                if (Mathf.Abs(target - _player.Speed) > 1e-4f)
                    _player.ChangeSpeed(target - _player.Speed);
            }

            if (step.Kind == PuppetStepKind.Turn || step.Kind == PuppetStepKind.Curve)
            {
                float remaining = step.Degrees - _turned;
                float delta = Mathf.Sign(remaining) * Mathf.Min(Mathf.Abs(remaining), step.Rate * dt);
                _headingYaw += delta;
                _turned += delta;
            }

            float bodyDelta = Mathf.DeltaAngle(_player.Rotation.x, _headingYaw);
            float yawRate = step.Kind == PuppetStepKind.SprintTo || step.Kind == PuppetStepKind.MoveTo ? SprintYawRate : BodyYawRate;
            bodyDelta = Mathf.Clamp(bodyDelta, -yawRate * dt, yawRate * dt);
            if (Mathf.Abs(bodyDelta) > 1e-3f)
                _player.Rotate(new Vector2(bodyDelta, 0f), true);

            Vector3 position = _player.Position;
            if (moving)
            {
                float radians = TravelYaw(step) * Mathf.Deg2Rad;
                Vector2 world = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
                // same world->local conversion as BotMover.TurnMove
                Vector3 local = Quaternion.Euler(0f, 0f, _player.Rotation.x) * world;
                _player.Move(new Vector2(local.x, local.y).normalized);
            }
            else
            {
                _player.Move(Vector2.zero);
            }

            string jumpFailure = UpdateJump(step, position);
            if (jumpFailure != null)
            {
                FailCurrentStep(jumpFailure);
                return;
            }

            string endReason = StepEndReason(step, position, moving);
            if (endReason != null)
            {
                if (endReason.StartsWith("jump failed:", StringComparison.Ordinal) || IsJumpStep(step.Kind) && endReason != "completed")
                {
                    FailCurrentStep(endReason.StartsWith("jump failed:", StringComparison.Ordinal) ? endReason : "jump failed: " + endReason);
                    return;
                }
                CloseStep(endReason);
                AdvanceStep();
            }
        }

        private string UpdateJump(PuppetStep step, Vector3 position)
        {
            if (!IsJumpStep(step.Kind))
                return null;

            float traveled = Horizontal(position - _stepStartPosition);
            if (!_jumpTriggered)
            {
                if (_jumpDistanceReachedTime < 0f && traveled >= step.JumpAfterMeters)
                    _jumpDistanceReachedTime = Time.time;
                if (_jumpDistanceReachedTime < 0f)
                    return null;

                MovementContext context = _player.MovementContext;
                if (context == null)
                    return "jump failed: movement context unavailable at jump distance";
                if (!context.IsGrounded)
                {
                    if (Time.time - _jumpDistanceReachedTime > JumpGroundEligibilityTimeoutSeconds)
                        return "jump failed: no grounded jump opportunity after jump distance";
                    return null;
                }

                if (!context.CanJump)
                    return "jump failed: EFT CanJump is false at the requested distance";
                if (step.Kind == PuppetStepKind.JumpSprint && !_player.IsSprintEnabled)
                    return "jump failed: physical sprint not active at the requested distance (check stamina)";

                // Record the single request before invoking EFT so an exception cannot result in a retry.
                _jumpTriggered = true;
                _jumpGroundedAtTrigger = context.IsGrounded;
                _jumpTriggerFrame = Time.frameCount;
                _jumpTriggerTime = Time.time;
                try { _player.Jump(); }
                catch (Exception ex) { return "jump failed: Player.Jump threw " + ex.Message; }

                if (!context.IsGrounded)
                    _jumpSawAirborne = true;
                return null;
            }

            MovementContext movement = _player.MovementContext;
            if (movement == null)
                return "jump failed: movement context unavailable after jump request";

            if (!_jumpSawAirborne && !movement.IsGrounded)
                _jumpSawAirborne = true;
            else if (_jumpSawAirborne && !_jumpLanded && movement.IsGrounded)
            {
                _jumpLanded = true;
                _jumpLandingFrame = Time.frameCount;
                _jumpLandingTime = Time.time;
            }

            float sinceTrigger = Time.time - _jumpTriggerTime;
            if (!_jumpSawAirborne && sinceTrigger > JumpAirborneTimeoutSeconds)
                return "jump failed: Player.Jump was refused (no airborne state observed)";
            if (_jumpSawAirborne && !_jumpLanded && sinceTrigger > JumpLandingTimeoutSeconds)
                return "jump failed: landing timeout (airborne state did not return to grounded)";
            return null;
        }

        private string StepEndReason(PuppetStep step, Vector3 position, bool moving)
        {
            float elapsed = Time.time - _stepStartTime;
            switch (step.Kind)
            {
                case PuppetStepKind.Stop:
                    return elapsed >= step.Seconds ? "completed" : null;
                case PuppetStepKind.Turn:
                    if (Mathf.Abs(_turned) >= Mathf.Abs(step.Degrees) - 0.01f && Mathf.Abs(Mathf.DeltaAngle(_player.Rotation.x, _headingYaw)) < 3f)
                        return "completed";
                    return elapsed > Mathf.Abs(step.Degrees) / step.Rate + 3f ? "turn timeout" : null;
            }

            if (step.Kind == PuppetStepKind.Curve && Mathf.Abs(_turned) >= Mathf.Abs(step.Degrees) - 0.01f)
                return "completed";
            if (IsJumpStep(step.Kind))
            {
                if (!_jumpTriggered && Horizontal(position - _stepStartPosition) >= _plannedMeters)
                    return "jump failed: no grounded jump opportunity before lane end";
                // Distance completion, NavMesh wall checks, and the ordinary stuck timer are suspended in flight.
                // UpdateJump owns the bounded airborne and landing timeouts for this interval.
                if (_jumpTriggered && !_jumpLanded)
                    return null;
            }
            if (step.Kind != PuppetStepKind.Curve && Horizontal(position - _stepStartPosition) >= _plannedMeters)
                return "completed";

            float radians = TravelYaw(step) * Mathf.Deg2Rad;
            Vector3 ahead = position + new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians)) * WallMargin;
            NavMeshHit hit;
            if (NavMesh.Raycast(OnMesh(position), ahead, out hit, NavMesh.AllAreas))
                return "blocked";

            if (Horizontal(position - _lastProgressPosition) >= ProgressMeters)
            {
                _lastProgressPosition = position;
                _lastProgressTime = Time.time;
            }
            else if (Time.time - _lastProgressTime > StuckSeconds)
            {
                return "stuck";
            }
            return null;
        }

        private void AdvanceStep()
        {
            _stepIndex++;
            if (_stepIndex >= _steps.Length)
            {
                IsComplete = true;
                _player.Move(Vector2.zero);
                return;
            }

            PuppetStep step = _steps[_stepIndex];
            _stepStartTime = _lastProgressTime = Time.time;
            _stepStartFrame = Time.frameCount;
            _stepStartPosition = _lastProgressPosition = _player.Position;
            _turned = 0f;
            _plannedMeters = step.Meters;
            _jumpDistanceReachedTime = -1f;
            _stepClosed = false;
            _jumpTriggered = false;
            _jumpGroundedAtTrigger = false;
            _jumpSawAirborne = false;
            _jumpLanded = false;
            _jumpTriggerFrame = -1;
            _jumpTriggerTime = -1f;
            _jumpLandingFrame = -1;
            _jumpLandingTime = -1f;
            if (step.Kind != PuppetStepKind.Stop)
                _faceViewer = step.Kind == PuppetStepKind.Strafe;
            if (step.Kind == PuppetStepKind.SprintTo || step.Kind == PuppetStepKind.MoveTo)
            {
                // bots turn to face where they sprint, so the body swings onto the new heading and runs down it
                _headingYaw = _player.Rotation.x + step.Degrees;
                Vector3 from = OnMesh(_stepStartPosition);
                float clear = ClearDistance(from, YawDirection(_headingYaw));
                // the mirrored direction exercises the same transition, so take it when this side is walled in
                float mirrored = ClearDistance(from, YawDirection(_player.Rotation.x - step.Degrees));
                if (mirrored > clear + 2f)
                {
                    _headingYaw = _player.Rotation.x - step.Degrees;
                    clear = mirrored;
                }
                _plannedMeters = Mathf.Max(0f, Mathf.Min(step.Meters, clear - WallMargin - StoppingMargin(step)));
                if (_plannedMeters < 0.8f)
                {
                    CloseStep(string.Format(CultureInfo.InvariantCulture, "no room (clear {0:F1} m)", clear));
                    AdvanceStep();
                }
            }
            if (step.Kind == PuppetStepKind.Strafe)
            {
                float viewerYaw;
                if (TryViewerYaw(out viewerYaw))
                    _headingYaw = viewerYaw;
                // travel relative to where the bot will face, not its current body yaw, which may still be turning
                _moveYaw = _headingYaw + step.Degrees;
                Vector3 from = OnMesh(_stepStartPosition);
                float clear = ClearDistance(from, YawDirection(_moveYaw));
                if (clear - WallMargin - StoppingMargin(step) < 0.8f)
                {
                    // facing the viewer puts forward/back strafes across the lane, where a wall can sit a metre
                    // away (strafe:180 failed three raids running): travel down the lane instead, facing so that
                    // the same relative direction is exercised, and let the camera follow rather than be faced
                    Vector3 end = Horizontal(_laneEnd - from) >= Horizontal(_laneStart - from) ? _laneEnd : _laneStart;
                    Vector3 along = end - from;
                    along.y = 0f;
                    if (along.sqrMagnitude > 1f)
                    {
                        float alongYaw = Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg;
                        float alongClear = ClearDistance(from, YawDirection(alongYaw));
                        if (alongClear > clear + 1f)
                        {
                            _moveYaw = alongYaw;
                            _headingYaw = alongYaw - step.Degrees;
                            _faceViewer = false;
                            clear = alongClear;
                        }
                    }
                }
                _plannedMeters = Mathf.Max(0f, Mathf.Min(step.Meters, clear - WallMargin - StoppingMargin(step)));
                if (_plannedMeters < 0.8f)
                {
                    CloseStep(string.Format(CultureInfo.InvariantCulture, "no room (clear {0:F1} m)", clear));
                    AdvanceStep();
                }
            }
            else if (step.Kind == PuppetStepKind.Move || step.Kind == PuppetStepKind.Sprint || step.Kind == PuppetStepKind.JumpMove || step.Kind == PuppetStepKind.JumpSprint)
            {
                Vector3 from = OnMesh(_stepStartPosition);
                // a startstop run once found both lane ends within 2 m of the bot with 10 m of lane reported,
                // i.e. the bot wasn't on its lane; put it back instead of refusing every leg
                float offLane = DistanceToSegment(from, _laneStart, _laneEnd);
                if (offLane > LaneReseatMeters)
                {
                    Vector3 seat;
                    if (!TryOnMesh(ClosestOnSegment(from, _laneStart, _laneEnd), 1f, out seat))
                        seat = from;
                    _player.Teleport(Grounded(seat));
                    from = seat;
                    _stepStartPosition = _lastProgressPosition = seat;
                    Reseats++;
                }
                // shuttle toward whichever lane end is farther; stop slides (up to ~1.8 m after a run)
                // would otherwise walk the legs out of the lane one by one. check real clearance both ways
                Vector3 end = Horizontal(_laneEnd - from) >= Horizontal(_laneStart - from) ? _laneEnd : _laneStart;
                Vector3 toEnd = end - from;
                toEnd.y = 0f;
                float towardYaw = toEnd.sqrMagnitude > 0.01f ? Mathf.Atan2(toEnd.x, toEnd.z) * Mathf.Rad2Deg : _headingYaw;
                float towardClear = Mathf.Min(ClearDistance(from, YawDirection(towardYaw)), Horizontal(toEnd) + WallMargin);
                float awayClear = ClearDistance(from, YawDirection(towardYaw + 180f));
                float chosenYaw = towardClear >= awayClear ? towardYaw : towardYaw + 180f;
                float clear = Mathf.Max(towardClear, awayClear);
                _headingYaw = _player.Rotation.x + Mathf.DeltaAngle(_player.Rotation.x, chosenYaw);
                _plannedMeters = Mathf.Max(0f, Mathf.Min(step.Meters, clear - WallMargin - StoppingMargin(step)));
                if (IsJumpStep(step.Kind) && (_plannedMeters < 0.8f || _plannedMeters + 0.05f < step.Meters))
                {
                    FailCurrentStep(string.Format(CultureInfo.InvariantCulture,
                        "jump failed: insufficient room (requested {0:F1} m, available {1:F1} m)", step.Meters, _plannedMeters));
                    return;
                }
                if (_plannedMeters < 0.8f)
                {
                    CloseStep(string.Format(CultureInfo.InvariantCulture, "no room (clear toward {0:F1} m, away {1:F1} m, off lane {2:F1} m)", towardClear, awayClear, offLane));
                    AdvanceStep();
                }
            }
        }

        private void CloseStep(string reason)
        {
            if (_stepClosed) return;
            _stepClosed = true;
            PuppetStep step = _steps[_stepIndex];
            float seconds = Time.time - _stepStartTime;
            float meters = Horizontal(_player.Position - _stepStartPosition);
            _reports.Add(new PuppetStepReport
            {
                Index = _stepIndex,
                Step = step.ToString(),
                Kind = step.Kind.ToString(),
                Speed = step.Speed,
                Degrees = step.Degrees,
                RequestedMeters = step.Meters,
                PlannedMeters = _plannedMeters,
                StartTime = _stepStartTime,
                StartFrame = _stepStartFrame,
                EndTime = Time.time,
                EndFrame = Time.frameCount,
                Meters = meters,
                MeanSpeedMetersPerSecond = seconds > 0f ? meters / seconds : 0f,
                EndReason = reason,
                JumpExpected = IsJumpStep(step.Kind),
                JumpGroundedAtTrigger = _jumpGroundedAtTrigger,
                JumpTriggerFrame = _jumpTriggerFrame,
                JumpTriggerTime = _jumpTriggerTime,
                JumpSawAirborne = _jumpSawAirborne,
                JumpLandingFrame = _jumpLandingFrame,
                JumpLandingTime = _jumpLandingTime
            });
        }

        private void FailCurrentStep(string reason)
        {
            Failure = reason;
            CloseStep(reason);
            IsComplete = true;
            _player.Move(Vector2.zero);
            if (_player.IsSprintEnabled)
                _player.EnableSprint(false);
        }

        private static void VerifyPreferredAreaMap(PreferredTestArea area)
        {
            var world = Singleton<GameWorld>.Instance;
            string currentLocationId = world == null ? null : world.LocationId;
            if (world == null || world.MainPlayer == null || !string.Equals(currentLocationId, area.LocationId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Puppet rejected: preferred test area map mismatch (area '" + area.LocationId + "', current '" + (currentLocationId ?? "unknown") + "'); no teleport was attempted.");
        }

        private struct LaneChoice
        {
            public Vector3 Position;
            public float Yaw;
            public float Clear;
            public Vector3? ViewerSpot;
        }

        private sealed class LaneSearchStats
        {
            public int Sampled, OffMesh, WrongFloor, Unreachable, NoSight, NoLane, NoViewerSpot, PreferredAreaUnavailable;
            public override string ToString() => $"sampled {Sampled}, off mesh {OffMesh}, wrong floor {WrongFloor}, unreachable {Unreachable}, no line of sight {NoSight}, no lane {NoLane}, no viewer spot {NoViewerSpot}, preferred area unavailable {PreferredAreaUnavailable}";
        }

        // Without a preferred area, spawn rooms can use a distant open lane; configured areas never take this fallback.
        private static bool FindViewerLane(Player viewer, Vector3 botPosition, out LaneChoice choice, LaneSearchStats stats)
        {
            choice = default(LaneChoice);
            if (_openTestCenter.HasValue)
            {
                Vector3 center = _openTestCenter.Value;
                choice = new LaneChoice { Position = center, Yaw = 0f, Clear = _openTestRadius,
                    ViewerSpot = center + new Vector3(-4f, 0f, -4f) };
                return true;
            }
            if (_preferredTestArea.HasValue)
                return FindViewerLaneNearPreferredArea(_preferredTestArea.Value, out choice, stats);

            Vector3 viewerOnMesh = OnMesh(viewer.Position, 3f);
            Vector3 botOnMesh = OnMesh(botPosition, 3f);
            var path = new NavMeshPath();
            float bestScore = float.MinValue;
            float[] distances = { 9f, 6f, 12f };
            for (int d = 0; d < distances.Length; d++)
            {
                for (int a = 0; a < 12; a++)
                {
                    Vector3 direction = Quaternion.Euler(0f, a * 30f, 0f) * Vector3.forward;
                    LaneChoice candidate;
                    if (!TryLaneSeenFrom(viewerOnMesh, viewerOnMesh + direction * distances[d], 1.5f, 6f, path, stats, out candidate))
                        continue;
                    float score = candidate.Clear - Mathf.Abs(distances[d] - 9f) * 0.3f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        choice = candidate;
                    }
                }
            }
            if (bestScore > float.MinValue)
                return true;

            var random = new System.Random(20260915);
            bestScore = float.MinValue;
            for (int i = 0; i < 900; i++)
            {
                bool found = false;
                float angle = (float)random.NextDouble() * 360f;
                float radius = 8f + (float)random.NextDouble() * 72f;
                // search around the bot as well: the viewer may have spawned in a corner of the map
                Vector3 around = i % 2 == 0 ? viewerOnMesh : botOnMesh;
                Vector3 probe = around + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
                NavMeshHit hit;
                stats.Sampled++;
                // woods spawns can sit on terrain with sparse mesh nearby; a tight radius found nothing at all once
                if (!NavMesh.SamplePosition(probe, out hit, 8f, NavMesh.AllAreas))
                {
                    stats.OffMesh++;
                    continue;
                }
                // the viewer gets moved beside this lane, so reachability from their spawn doesn't matter here;
                // a factory spawn on a disconnected navmesh scrap made every probe "unreachable"
                float yaw, clear;
                if (!FindLane(hit.position, Vector3.zero, out yaw, out clear) || clear < 7f)
                {
                    stats.NoLane++;
                    continue;
                }
                // only lanes that passed everything else pay for the trigger-volume check (review: per-sample
                // checks inside FindLane would have been ~600k overlap queries in the worst case)
                if (LaneSlowed(hit.position, yaw, clear))
                {
                    stats.NoLane++;
                    continue;
                }
                Vector3 laneDirection = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                Vector3 side = Vector3.Cross(Vector3.up, laneDirection);
                // beside the lane gives the best leg silhouette; behind its start still keeps the bot in view
                Vector3[] offsets =
                {
                    side * 8f, -side * 8f, side * 6f, -side * 6f, side * 10f, -side * 10f,
                    -laneDirection * 4f, -laneDirection * 6f
                };
                foreach (Vector3 offset in offsets)
                {
                    NavMeshHit spot;
                    if (!NavMesh.SamplePosition(hit.position + offset, out spot, 1.5f, NavMesh.AllAreas) || Mathf.Abs(spot.position.y - hit.position.y) > 0.5f)
                        continue;
                    if (!NavMesh.CalculatePath(spot.position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                        continue;
                    if (!HasSight(spot.position, hit.position) || !HasSight(spot.position, hit.position + laneDirection * Mathf.Min(clear, 7f)))
                        continue;
                    float score = clear + Openness(hit.position) * 2f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        choice = new LaneChoice { Position = hit.position, Yaw = yaw, Clear = clear, ViewerSpot = spot.position };
                    }
                    found = true;
                    break;
                }
                if (!found)
                    stats.NoViewerSpot++;
                // a clearing beats the first gap between trees, so keep looking once something workable is in hand
                if (bestScore > 24f)
                    break;
            }
            return bestScore > float.MinValue;
        }

        private static bool FindViewerLaneNearPreferredArea(PreferredTestArea area, out LaneChoice choice, LaneSearchStats stats)
        {
            choice = default(LaneChoice);
            NavMeshHit anchor;
            stats.Sampled++;
            if (!NavMesh.SamplePosition(area.Position, out anchor, PreferredAreaSnapMeters, NavMesh.AllAreas)
                || Mathf.Abs(anchor.position.y - area.Position.y) > 0.5f)
            {
                stats.PreferredAreaUnavailable++;
                return false;
            }
            float ground;
            if (!GroundHeight(anchor.position, out ground) || Mathf.Abs(ground - anchor.position.y) > MaxSeatGroundGap)
            {
                stats.PreferredAreaUnavailable++;
                return false;
            }

            var path = new NavMeshPath();
            float bestScore = float.MinValue;
            float[] distances = { 9f, 6f, PreferredAreaSearchRadiusMeters };
            for (int d = 0; d < distances.Length; d++)
            {
                for (int a = 0; a < 12; a++)
                {
                    // Keep candidate locations stable when the viewer turns. Rotating this
                    // sparse grid by camera yaw missed the long cardinal Factory corridor.
                    Vector3 direction = Quaternion.Euler(0f, a * 30f, 0f) * Vector3.forward;
                    LaneChoice candidate;
                    if (!TryLaneSeenFrom(anchor.position, anchor.position + direction * distances[d], PreferredAreaSnapMeters, 6f, path, stats, out candidate))
                        continue;
                    candidate.ViewerSpot = anchor.position;
                    float score = candidate.Clear - Mathf.Abs(distances[d] - 9f) * 0.3f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        choice = candidate;
                    }
                }
            }
            return bestScore > float.MinValue;
        }

        private static bool TryLaneSeenFrom(Vector3 viewerOnMesh, Vector3 probe, float sampleRadius, float minimumClear, NavMeshPath path, LaneSearchStats stats, out LaneChoice choice)
        {
            choice = default(LaneChoice);
            NavMeshHit hit;
            stats.Sampled++;
            if (!NavMesh.SamplePosition(probe, out hit, sampleRadius, NavMesh.AllAreas))
                return false;
            if (Mathf.Abs(hit.position.y - viewerOnMesh.y) > 0.5f)
            {
                stats.WrongFloor++;
                return false;
            }
            // a navmesh strip on a ledge or behind a half wall samples fine but isnt walkable from here
            if (!NavMesh.CalculatePath(viewerOnMesh, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                stats.Unreachable++;
                return false;
            }
            if (!HasSight(viewerOnMesh, hit.position))
            {
                stats.NoSight++;
                return false;
            }
            Vector3 look = hit.position - viewerOnMesh;
            look.y = 0f;
            float yaw, clear;
            if (!FindLane(hit.position, look.normalized, out yaw, out clear) || clear < minimumClear)
            {
                stats.NoLane++;
                return false;
            }
            choice = new LaneChoice { Position = hit.position, Yaw = yaw, Clear = clear };
            return true;
        }

        private static bool HasSight(Vector3 viewerFeet, Vector3 targetFeet)
        {
            Vector3 eye = viewerFeet + Vector3.up * 1.6f;
            Vector3 target = targetFeet + Vector3.up * 1.2f;
            // start past the viewer's own capsule
            Vector3 start = eye + (target - eye).normalized * 0.6f;
            return !Physics.Linecast(start, target, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        }

        // lanes run back and forth, so both directions need room; across the view keeps the bot on screen
        private static bool FindLane(Vector3 origin, Vector3 viewForward, out float laneYaw, out float clear)
        {
            laneYaw = 0f;
            clear = 0f;
            float bestScore = float.MinValue;
            for (int i = 0; i < 16; i++)
            {
                float yaw = i * 22.5f;
                Vector3 direction = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                float room = Mathf.Min(ClearDistance(origin, direction), ClearDistance(origin, -direction) + 2f);
                if (room < MinLaneMeters)
                    continue;
                // gait numbers on a slope or over kerbs are not comparable between raids (user, 2026-09-16): sample
                // the real ground along both directions and refuse lanes with steps or a net rise
                float rise, stepHeight;
                if (!GroundProfile(origin, direction, room, out rise, out stepHeight))
                    continue;
                float across = viewForward.sqrMagnitude > 0f ? Mathf.Abs(Vector3.Dot(direction, viewForward)) : 0f;
                // a gap between trees passes a single-axis check; prefer somewhere actually open, and flatter
                float score = Mathf.Min(room, 14f) - across * 3f + Openness(origin) * 2f - rise * 6f;
                if (score > bestScore)
                {
                    bestScore = score;
                    laneYaw = yaw;
                    clear = room;
                }
            }
            return bestScore > float.MinValue;
        }

        private const float LaneSampleMeters = 1f;
        private const float MaxLaneStepMeters = 0.15f;
        private const float MaxLaneRiseMeters = 0.35f;
        private const float MaxSeatGroundGap = 0.3f;

        // ground height every metre along the lane in both directions: false when a sample finds no ground, a step
        // between neighbours is over a kerb's worth, or the lane climbs more than a gentle slope overall
        private static bool GroundProfile(Vector3 origin, Vector3 direction, float room, out float rise, out float stepHeight)
        {
            rise = 0f;
            stepHeight = 0f;
            float baseHeight;
            if (!GroundHeight(origin, out baseHeight))
                return false;
            // the seat is a navmesh point: with the solid ground metres below it (user: a spot high in the air, the
            // scav falls off and is put back) every sample down there still read as flat
            if (Mathf.Abs(baseHeight - origin.y) > MaxSeatGroundGap)
                return false;
            float length = Mathf.Min(room, MaxLaneMeters);
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float previous = baseHeight;
                for (float d = LaneSampleMeters; d <= length; d += LaneSampleMeters)
                {
                    float h;
                    if (!GroundHeight(origin + direction * (sign * d), out h))
                        return false;
                    stepHeight = Mathf.Max(stepHeight, Mathf.Abs(h - previous));
                    rise = Mathf.Max(rise, Mathf.Abs(h - baseHeight));
                    if (stepHeight > MaxLaneStepMeters || rise > MaxLaneRiseMeters)
                        return false;
                    previous = h;
                }
            }
            return true;
        }

        private static readonly Collider[] SlowVolumes = new Collider[64];

        // every metre along the lane, both ways, at the measured ground height
        private static bool LaneSlowed(Vector3 origin, float yaw, float clear)
        {
            Vector3 direction = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            float length = Mathf.Min(clear, MaxLaneMeters);
            for (float d = -length; d <= length; d += LaneSampleMeters)
            {
                Vector3 spot = origin + direction * d;
                float h;
                if (GroundHeight(spot, out h))
                    spot.y = h;
                if (SlowedGround(spot))
                    return true;
            }
            return false;
        }

        // water and barbed wire are trigger volumes that cap the state speed limit (0.2 and 0.15): a flooded factory
        // tunnel passed every clearance and flatness test and capped a "run" at 1.23 m/s
        private static bool SlowedGround(Vector3 spot)
        {
            int n = Physics.OverlapSphereNonAlloc(spot + Vector3.up * 0.6f, 0.5f, SlowVolumes, Physics.AllLayers, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var col = SlowVolumes[i];
                if (!col || !col.isTrigger)
                    continue;
                var obstacle = col.GetComponentInParent<EFT.Interactive.ObstacleCollider>();
                if (obstacle && obstacle.HasSwampSpeedLimit)
                    return true;
                if (col.GetComponentInParent<EFT.Interactive.BarbedWire>())
                    return true;
            }
            return false;
        }

        private static bool GroundHeight(Vector3 spot, out float height)
        {
            RaycastHit hit;
            if (Physics.Raycast(spot + Vector3.up * 1.2f, Vector3.down, out hit, 4f, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
            {
                height = hit.point.y;
                return true;
            }
            height = 0f;
            return false;
        }

        // smallest clearance over 8 directions: high only in a real clearing, low in woodland
        private static float Openness(Vector3 origin)
        {
            float smallest = MaxLaneMeters;
            for (int i = 0; i < 8; i++)
                smallest = Mathf.Min(smallest, ClearDistance(origin, YawDirection(i * 45f)));
            return smallest;
        }

        // stop steps after each leg slid 0.4 m at speed 0.1 up to 1.8 m at 0.625 in the first clean sweep
        private static float StoppingMargin(PuppetStep step)
        {
            // speed 0.3 strafes stopped within 0.29-0.36 m
            if (step.Kind == PuppetStepKind.Strafe)
                return 0.4f + 1.5f * Mathf.Max(0f, step.Speed - 0.3f) + 0.3f;
            float slide = step.Kind == PuppetStepKind.Sprint || step.Kind == PuppetStepKind.JumpSprint ? 1.8f : 0.41f + 2.65f * Mathf.Max(0f, step.Speed - 0.1f);
            return slide + 0.3f;
        }

        // a viewer spot square to the lane, on the side that leaves the bot the most room to back away
        private bool TryBesideLane(Vector3 seat, out Vector3 spot)
        {
            spot = Vector3.zero;
            Vector3 lane = _laneEnd - _laneStart;
            lane.y = 0f;
            if (lane.sqrMagnitude < 1f)
                return false;
            Vector3 side = Vector3.Cross(Vector3.up, lane.normalized);
            float bestBehind = -1f;
            foreach (float distance in new[] { 7f, 5f, 9f })
            {
                foreach (float sign in new[] { 1f, -1f })
                {
                    NavMeshHit hit;
                    if (!NavMesh.SamplePosition(seat + side * (sign * distance), out hit, 1.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - seat.y) > 0.5f)
                        continue;
                    if (!HasSight(hit.position, seat))
                        continue;
                    float behind = ClearDistance(seat, side * -sign);
                    if (behind > bestBehind)
                    {
                        bestBehind = behind;
                        spot = hit.position;
                    }
                }
                if (bestBehind >= 0f)
                    return true;
            }
            return false;
        }

        private float TravelYaw(PuppetStep step) => step.Kind == PuppetStepKind.Strafe ? _moveYaw : _headingYaw;

        private bool TryViewerYaw(out float yaw)
        {
            yaw = 0f;
            if (_viewer == null) return false;
            Vector3 to = _viewer.Position - _player.Position;
            to.y = 0f;
            if (to.sqrMagnitude < 1f) return false;
            yaw = _player.Rotation.x + Mathf.DeltaAngle(_player.Rotation.x, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
            return true;
        }

        private const float LaneReseatMeters = 1.5f;

        public int Reseats { get; private set; }

        private static Vector3 YawDirection(float yaw)
        {
            float radians = yaw * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
        }

        private static Vector3 ClosestOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            ab.y = 0f;
            Vector3 ap = point - a;
            ap.y = 0f;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(ap, ab) / ab.sqrMagnitude) : 0f;
            return a + (b - a) * t;
        }

        private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b) => Horizontal(point - ClosestOnSegment(point, a, b));

        private static float ClearDistance(Vector3 origin, Vector3 direction)
        {
            NavMeshHit hit;
            float mesh = NavMesh.Raycast(origin, origin + direction * MaxLaneMeters, out hit, NavMesh.AllAreas) ? hit.distance : MaxLaneMeters;
            return Mathf.Min(mesh, PhysicsClearDistance(origin, direction, mesh));
        }

        // sweep the real geometry too (navmesh ignores props), skipping other characters standing around. bush
        // triggers used to count as obstacles because EFT slowed bots inside them; the test install runs Bushwacker
        // now (user, 2026-09-16), so foliage is passable again
        private static float PhysicsClearDistance(Vector3 origin, Vector3 direction, float max)
        {
            if (max <= 0.01f)
                return max;
            Vector3 start = origin + Vector3.up * 0.9f;
            RaycastHit[] hits = Physics.SphereCastAll(start, 0.35f, direction, max, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            float nearest = max;
            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.distance <= 0.05f || hit.collider == null)
                    continue;
                if (hit.collider.GetComponentInParent<Player>())
                    continue;
                nearest = Mathf.Min(nearest, hit.distance);
            }
            return nearest;
        }

        // NavMesh.Raycast reports an immediate hit when it starts off the mesh, and the player's
        // position sits a little above it, so every query starts from the snapped point
        private static Vector3 OnMesh(Vector3 position, float radius = 1f)
        {
            Vector3 snapped;
            return TryOnMesh(position, radius, out snapped) ? snapped : position;
        }

        private static bool TryOnMesh(Vector3 position, float radius, out Vector3 snapped)
        {
            NavMeshHit hit;
            bool found = NavMesh.SamplePosition(position, out hit, radius, NavMesh.AllAreas);
            snapped = found ? hit.position : position;
            return found;
        }

        // navmesh points can sit under terrain; drop onto whatever collider is really there
        internal static Vector3 Grounded(Vector3 spot)
        {
            RaycastHit hit;
            if (Physics.Raycast(spot + Vector3.up * 1.2f, Vector3.down, out hit, 4f, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.1f;
            return spot + Vector3.up * 0.1f;
        }

        private bool IsUsable()
        {
            return Owner != null && Owner.GetPlayer != null && Owner.BotState == EBotState.Active && _player.HealthController != null && _player.HealthController.IsAlive;
        }

        private static float Horizontal(Vector3 delta)
        {
            return new Vector2(delta.x, delta.z).magnitude;
        }

        private static void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception ex) { Debug.LogError("[" + ModInfo.Name + "] Puppet cleanup operation failed: " + ex); }
        }
    }

    internal enum PuppetStepKind
    {
        Move,
        Sprint,
        Stop,
        Turn,
        Curve,
        Strafe,
        SprintTo,
        // move off along a new heading while the body swings onto it (a stock bot leaving a stand): "movedir:deg:speed:m"
        MoveTo,
        JumpMove,
        JumpSprint
    }

    internal struct PuppetStep
    {
        public PuppetStepKind Kind;
        public float Speed;
        public float Meters;
        public float Seconds;
        public float Degrees;
        public float Rate;

        public override string ToString()
        {
            var c = CultureInfo.InvariantCulture;
            switch (Kind)
            {
                case PuppetStepKind.Move: return "move:" + Speed.ToString(c) + ":" + Meters.ToString(c);
                case PuppetStepKind.Sprint: return "sprint:" + Meters.ToString(c);
                case PuppetStepKind.Stop: return "stop:" + Seconds.ToString(c);
                case PuppetStepKind.Turn: return "turn:" + Degrees.ToString(c) + ":" + Rate.ToString(c);
                case PuppetStepKind.Strafe: return "strafe:" + Degrees.ToString(c) + ":" + Speed.ToString(c) + ":" + Meters.ToString(c);
                case PuppetStepKind.SprintTo: return "sprintdir:" + Degrees.ToString(c) + ":" + Meters.ToString(c);
                case PuppetStepKind.MoveTo: return "movedir:" + Degrees.ToString(c) + ":" + Speed.ToString(c) + ":" + Meters.ToString(c);
                case PuppetStepKind.JumpMove: return "jumpmove:" + Speed.ToString(c) + ":" + Meters.ToString(c) + ":" + JumpAfterMeters.ToString(c);
                case PuppetStepKind.JumpSprint: return "jumpsprint:" + Meters.ToString(c) + ":" + JumpAfterMeters.ToString(c);
                default: return "curve:" + Speed.ToString(c) + ":" + Degrees.ToString(c) + ":" + Rate.ToString(c);
            }
        }

        public float JumpAfterMeters;
    }

    internal static class PuppetScript
    {
        private const int MaximumSteps = 128;

        // Player.Speed tops out near 0.625 for these bots (MaxSpeed read in capture), so the sweep stops there
        public static readonly string[] NamedScenarios = { "reactionshowcase", "sweep", "walk", "turns", "stops", "curves", "posewalk", "startstop", "strafe", "strafecuts", "strafeslow", "strafediag", "sprintbase", "sprinttrans", "runjump", "sprintjump", "jumps" };

        public static string Expand(string scenario)
        {
            switch ((scenario ?? "").Trim().ToLowerInvariant())
            {
                case "reactionshowcase":
                    return "stop:6;stop:6;stop:6;stop:6;stop:6;strafe:0:0.625:8;stop:3;strafe:180:0.45:8;stop:3;strafe:90:0.55:8;stop:3;strafe:-90:0.4:8;stop:5";
                case "gallery":
                    return "stop:3;move:0.625:8;stop:2;turn:180:120;stop:1;move:0.625:8;stop:2;turn:180:120;" +
                        "stop:1;move:0.625:8;stop:2;turn:180:120;stop:1;move:0.625:8;stop:3";
                case "sweep":
                    return "stop:1;" +
                        Leg(0.1f) + Leg(0.2f) + Leg(0.3f) + Leg(0.4f) + Leg(0.5f) + Leg(0.625f) +
                        "sprint:7;stop:1.5;turn:180:120;stop:0.5";
                case "curves":
                    // needs open floor on both sides, not just a lane; expect "blocked" in corridors
                    return "stop:1;curve:0.3:180:45;stop:1;curve:0.625:180:90;stop:1";
                case "startstop":
                    // runs then full stops; long stops give the Alyx stop clip time to settle before the next start
                    return "stop:2;move:0.625:8;stop:3;turn:180:120;stop:1.5;move:0.5:8;stop:3;turn:180:120;stop:1.5;move:0.625:8;stop:3";
                case "strafe":
                    // facing the viewer: right, back left, backpedal, forward; pairs return the bot to where it began
                    return "stop:2;strafe:90:0.3:3;stop:3;strafe:-90:0.3:3;stop:3;strafe:180:0.3:3;stop:3;strafe:0:0.3:3;stop:3";
                case "strafecuts":
                    // back-to-back strafes with no stop between them, so every leg change is a direction cut at run speed
                    return "stop:2;strafe:90:0.625:2.5;strafe:-90:0.625:2.5;strafe:90:0.625:2.5;stop:3;strafe:0:0.625:2.5;strafe:180:0.625:2.5;stop:3;strafe:-90:0.625:2;strafe:0:0.625:2;stop:3";
                case "strafeslow":
                    // walking-speed strafes and cuts: the slow clip sets (heavy soldier) should win here
                    return "stop:2;strafe:90:0.2:2.5;stop:3;strafe:-90:0.2:2.5;stop:3;strafe:180:0.2:2;stop:3;strafe:0:0.2:2;stop:3;strafe:90:0.2:2;strafe:-90:0.2:2;stop:3";
                case "strafediag":
                    // the diagonal sets, run then walk speed; the grunt has no diagonal cut clips, so reversals fall back to Tarkov
                    return "stop:2;strafe:45:0.625:2.5;stop:3;strafe:-135:0.625:2.5;stop:3;strafe:135:0.625:2.5;stop:3;strafe:-45:0.625:2.5;stop:3;strafe:45:0.3:2;stop:3;strafe:-45:0.3:2;stop:3";
                case "sprintbase":
                    // stock sprint behaviour to measure: standing into sprint, sprint to walk, sprint to stop,
                    // strafe into a sprint that turns the body, and a sprint back into a facing strafe
                    return "stop:2;sprint:7;stop:3;move:0.3:3;sprint:7;stop:3;strafe:90:0.3:2.5;sprintdir:90:7;stop:3;" +
                        "sprint:6;strafe:-90:0.3:2.5;stop:3;strafe:-90:0.3:2.5;sprintdir:-90:7;stop:3";
                case "sprinttrans":
                    // strafe facing the viewer, then turn and sprint that way; then sprint back into a facing strafe
                    return "stop:2;strafe:90:0.3:2.5;sprintdir:90:6;stop:2.5;strafe:-90:0.3:2.5;sprintdir:-90:6;stop:2.5;" +
                        "strafe:45:0.3:2.5;sprintdir:45:5;stop:2.5;strafe:135:0.3:2.5;sprintdir:135:5;stop:2.5";
                case "runjump":
                    return "stop:2;jumpmove:0.625:10:3;stop:2";
                case "sprintjump":
                    return "stop:2;jumpsprint:10:4;stop:2";
                case "jumps":
                    // General moving jumps at two speeds; finish the turn before returning along the lane.
                    return "stop:8;jumpmove:0.625:10:3;stop:15;turn:180:120;stop:1;jumpmove:0.3:8:2;stop:3";
                case "posewalk":
                    // speed 0.26 lands near the Alyx walk loop's 1.39 m/s (0.2 -> 1.23, 0.3 -> 1.53 in the sweep)
                    return "stop:1;move:0.26:7;stop:1.5;turn:180:120;stop:0.5;move:0.26:7;stop:1.5;turn:180:120;stop:0.5;move:0.26:7;stop:1.5";
                case "walk":
                    return "stop:1;" + Leg(0.3f) + Leg(0.3f) + Leg(0.3f);
                case "turns":
                    return "stop:1;turn:90:60;stop:1;turn:-90:60;stop:1;turn:180:180;stop:1;turn:-180:360;stop:1";
                case "stops":
                    return "stop:1;move:0.3:3;stop:2;move:0.625:4;stop:2;turn:180:120;move:0.3:1.5;stop:1;move:0.3:1.5;stop:2";
                default:
                    return scenario;
            }
        }

        public static PuppetStep[] Parse(string scenario)
        {
            string text = Expand(scenario);
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Puppet scenario is empty.");
            var steps = new List<PuppetStep>();
            foreach (string raw in text.Split(';'))
            {
                string token = raw.Trim();
                if (token.Length == 0)
                    continue;
                string[] f = token.Split(':');
                var step = new PuppetStep { Kind = Kind(f[0], token) };
                switch (step.Kind)
                {
                    case PuppetStepKind.Move:
                        Expect(f, 3, token);
                        step.Speed = Number(f[1], token, 0.05f, 1f);
                        step.Meters = Number(f[2], token, 0.5f, 30f);
                        break;
                    case PuppetStepKind.Sprint:
                        Expect(f, 2, token);
                        step.Speed = 1f;
                        step.Meters = Number(f[1], token, 0.5f, 30f);
                        break;
                    case PuppetStepKind.JumpMove:
                        Expect(f, 4, token);
                        step.Speed = Number(f[1], token, 0.05f, 1f);
                        step.Meters = Number(f[2], token, 0.5f, 30f);
                        step.JumpAfterMeters = Number(f[3], token, 0.5f, step.Meters);
                        if (step.JumpAfterMeters >= step.Meters)
                            throw new ArgumentException("Puppet jump distance must be at least 0.5 m and less than total meters: " + token);
                        break;
                    case PuppetStepKind.JumpSprint:
                        Expect(f, 3, token);
                        step.Speed = 1f;
                        step.Meters = Number(f[1], token, 0.5f, 30f);
                        step.JumpAfterMeters = Number(f[2], token, 0.5f, step.Meters);
                        if (step.JumpAfterMeters >= step.Meters)
                            throw new ArgumentException("Puppet jump distance must be at least 0.5 m and less than total meters: " + token);
                        break;
                    case PuppetStepKind.Stop:
                        Expect(f, 2, token);
                        step.Seconds = Number(f[1], token, 0f, 30f);
                        break;
                    case PuppetStepKind.Turn:
                        Expect(f, 3, token);
                        step.Degrees = Number(f[1], token, -720f, 720f);
                        step.Rate = Number(f[2], token, 5f, 720f);
                        break;
                    case PuppetStepKind.Curve:
                        Expect(f, 4, token);
                        step.Speed = Number(f[1], token, 0.05f, 1f);
                        step.Degrees = Number(f[2], token, -720f, 720f);
                        step.Rate = Number(f[3], token, 5f, 360f);
                        break;
                    case PuppetStepKind.SprintTo:
                        Expect(f, 3, token);
                        step.Speed = 1f;
                        step.Degrees = Number(f[1], token, -180f, 180f);
                        step.Meters = Number(f[2], token, 0.5f, 30f);
                        break;
                    case PuppetStepKind.MoveTo:
                        Expect(f, 4, token);
                        step.Degrees = Number(f[1], token, -180f, 180f);
                        step.Speed = Number(f[2], token, 0.05f, 1f);
                        step.Meters = Number(f[3], token, 0.5f, 30f);
                        break;
                    case PuppetStepKind.Strafe:
                        Expect(f, 4, token);
                        step.Degrees = Number(f[1], token, -180f, 180f);
                        step.Speed = Number(f[2], token, 0.05f, 1f);
                        step.Meters = Number(f[3], token, 0.5f, 30f);
                        break;
                }
                steps.Add(step);
                if (steps.Count > MaximumSteps)
                    throw new ArgumentException("Puppet scenario has more than " + MaximumSteps + " steps.");
            }
            if (steps.Count == 0)
                throw new ArgumentException("Puppet scenario has no steps.");
            return steps.ToArray();
        }

        private static string Leg(float speed)
        {
            string s = speed.ToString(CultureInfo.InvariantCulture);
            return "move:" + s + ":7;stop:1.5;turn:180:120;stop:0.5;";
        }

        private static PuppetStepKind Kind(string name, string token)
        {
            switch (name.Trim().ToLowerInvariant())
            {
                case "move": return PuppetStepKind.Move;
                case "sprint": return PuppetStepKind.Sprint;
                case "stop": return PuppetStepKind.Stop;
                case "turn": return PuppetStepKind.Turn;
                case "curve": return PuppetStepKind.Curve;
                case "strafe": return PuppetStepKind.Strafe;
                case "sprintdir": return PuppetStepKind.SprintTo;
                case "movedir": return PuppetStepKind.MoveTo;
                case "jumpmove": return PuppetStepKind.JumpMove;
                case "jumpsprint": return PuppetStepKind.JumpSprint;
                default: throw new ArgumentException("Unknown puppet step: " + token);
            }
        }

        private static void Expect(string[] fields, int count, string token)
        {
            if (fields.Length != count)
                throw new ArgumentException("Puppet step has the wrong number of fields: " + token);
        }

        private static float Number(string text, string token, float min, float max)
        {
            float value;
            if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || float.IsNaN(value) || value < min || value > max)
                throw new ArgumentException("Puppet step value out of range [" + min.ToString(CultureInfo.InvariantCulture) + ", " + max.ToString(CultureInfo.InvariantCulture) + "]: " + token);
            return value;
        }
    }

    internal struct PuppetReport
    {
        public string Scenario;
        public bool FreezeOthers;
        public bool BroughtToPlayer;
        public bool MovedViewer;
        public float LaneYaw;
        public float LaneClearMeters;
        public float[] LaneStart;
        public float[] LaneEnd;
        public int Reseats;
        public PuppetStepReport[] Steps;
    }

    internal struct PuppetStepReport
    {
        public int Index;
        public string Step;
        public string Kind;
        public float Speed;
        public float Degrees;
        public float RequestedMeters;
        public float PlannedMeters;
        public float StartTime;
        public int StartFrame;
        public float EndTime;
        public int EndFrame;
        public float Meters;
        public float MeanSpeedMetersPerSecond;
        public string EndReason;
        public bool JumpExpected;
        public bool JumpGroundedAtTrigger;
        public int JumpTriggerFrame;
        public float JumpTriggerTime;
        public bool JumpSawAirborne;
        public int JumpLandingFrame;
        public float JumpLandingTime;
    }
}
