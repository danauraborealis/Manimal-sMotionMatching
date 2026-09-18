using System;
using ZLinq;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.MotionMatching
{
    /// <summary>
    /// Owns one short, reversible walking route for the selected bot.
    /// </summary>
    internal sealed class RouteSession : IDisposable
    {
        private const float ReachDistance = 0.35f;
        private const float EndpointTolerance = 0.55f;
        private const float StuckTimeout = 8f;
        private const float ProgressDistance = 0.08f;
        // skip the acceleration ramp so steady speed isnt dragged down by the start
        private const float SpeedWarmupSeconds = 1f;

        private static RouteSession _active;

        private readonly Player _player;
        private readonly BrainLease _brain;
        private readonly BotMover _mover;
        private readonly Vector3[] _endpoints;
        private readonly bool _wasSprinting;
        private readonly float _wasMoveSpeed;
        private readonly BotSteering _steering;
        private readonly EBotSteering _wasSteeringMode;
        private readonly float _wasSteeringSpeed;
        private readonly float _requestedMoveSpeed;

        private BotPathController _routeController;
        private AbstractBotPath _routePath;
        private Vector3 _lastProgressPosition;
        private float _lastProgressTime;
        private int _nextEndpoint;
        private bool _speedChanged;
        private bool _sprintChanged;
        private bool _steeringChanged;
        private bool _compatibilityEnabled;
        private bool _disposed;

        private Vector3 _lastSpeedPosition;
        private float _speedStartTime;
        private float _lastSpeedTime;
        private float _totalMeters;
        private float _totalSeconds;
        private float _steadyMeters;
        private float _steadySeconds;
        private double _steadyPlayerSpeedSum;
        private int _steadyPlayerSpeedSamples;
        private int _speedOverrides;
        private bool _nearDoorLastTick;

        private RouteSession(Player player, float legLength, float moveSpeed)
        {
            if (player == null)
                throw new InvalidOperationException("Walking route rejected: player is unavailable.");
            if (!player.IsAI || player.HealthController == null || !player.HealthController.IsAlive)
                throw new InvalidOperationException("Walking route rejected: selected player is not a living AI bot.");

            var owner = player.AIData?.BotOwner;
            if (owner == null)
                throw new InvalidOperationException("Walking route rejected: selected player has no BotOwner.");
            if (owner.BotState != EBotState.Active)
                throw new InvalidOperationException("Walking route rejected: bot is not active.");
            if (owner.Mover == null)
                throw new InvalidOperationException("Walking route rejected: bot mover is unavailable.");
            if (owner.Steering == null)
                throw new InvalidOperationException("Walking route rejected: bot steering is unavailable.");
            if (owner.Mover.CurrentState != EBotMoverState.Default)
                throw new InvalidOperationException("Walking route rejected: bot is in a special mover state.");
            if (owner.Mover.Pause)
                throw new InvalidOperationException("Walking route rejected: bot movement is paused.");
            if (owner.Memory == null || owner.Memory.GoalEnemy != null)
                throw new InvalidOperationException("Walking route rejected: bot is in combat.");
            if (owner.BotLay != null && owner.BotLay.IsLay)
                throw new InvalidOperationException("Walking route rejected: bot is lying down.");
            if (player.IsInPronePose || player.PoseLevel < 0.95f)
                throw new InvalidOperationException("Walking route rejected: bot is not standing.");
            if (legLength < 0.5f || float.IsNaN(legLength) || float.IsInfinity(legLength))
                throw new ArgumentOutOfRangeException(nameof(legLength), "Route leg length must be finite and positive.");
            if (float.IsNaN(moveSpeed) || moveSpeed < 0.05f || moveSpeed > 1f)
                throw new ArgumentOutOfRangeException(nameof(moveSpeed), "Route move speed must be between 0.05 and 1.");
            _requestedMoveSpeed = moveSpeed;

            _brain = new BrainLease(owner, "Walking route rejected: ");

            Owner = owner;
            _player = player;
            _mover = owner.Mover;
            _wasSprinting = _mover.Sprinting;
            _wasMoveSpeed = _mover.DestMoveSpeed;
            _steering = owner.Steering;
            _wasSteeringMode = _steering.SteeringMode;
            _wasSteeringSpeed = _steering.Speed;

            Vector3 origin = owner.Position;
            Vector3 forward = owner.Transform == null ? Vector3.zero : owner.Transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
                throw new InvalidOperationException("Walking route rejected: bot has no usable forward direction.");
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            right.y = 0f;
            right.Normalize();

            Vector3 firstTarget = origin + forward * legLength;
            Vector3 secondTarget = firstTarget + right * legLength;
            Vector3 snappedOrigin = SampleNavMesh(origin, "bot origin");
            firstTarget = SampleNavMesh(firstTarget, "forward endpoint");
            secondTarget = SampleNavMesh(secondTarget, "right endpoint");

            Vector3[] firstLeg = CalculateCompletePath(snappedOrigin, firstTarget, "forward leg");
            Vector3[] secondLeg = CalculateCompletePath(firstTarget, secondTarget, "right leg");
            Points = ConcatenateCorners(firstLeg, secondLeg);
            if (Points.Length < 3)
                throw new InvalidOperationException("Walking route rejected: NavMesh route has too few corners.");

            _endpoints = new[] { firstTarget, secondTarget };
            _lastProgressPosition = owner.Position;
        }

        public BotOwner Owner { get; }

        public Vector3[] Points { get; }

        public bool IsComplete { get; private set; }

        public RouteSummary Summary
        {
            get
            {
                return new RouteSummary
                {
                    RequestedMoveSpeed = _requestedMoveSpeed,
                    HasMeasuredSpeed = _steadySeconds > 0f,
                    MeanSpeedMetersPerSecond = _totalSeconds > 0f ? _totalMeters / _totalSeconds : 0f,
                    SteadySpeedMetersPerSecond = _steadySeconds > 0f ? _steadyMeters / _steadySeconds : 0f,
                    SteadyPlayerSpeed = _steadyPlayerSpeedSamples > 0 ? (float)(_steadyPlayerSpeedSum / _steadyPlayerSpeedSamples) : 0f,
                    SteadySeconds = _steadySeconds,
                    SpeedOverrides = _speedOverrides
                };
            }
        }

        public static RouteSession Start(Player player, float legLength, float moveSpeed)
        {
            if (_active != null && !_active._disposed)
                throw new InvalidOperationException("Walking route rejected: another route is already active.");

            var session = new RouteSession(player, legLength, moveSpeed);
            _active = session;
            try
            {
                session.Begin();
                Debug.Log("[" + ModInfo.Name + "] Walking route started; vanilla brain is paused and mover path is owned once.");
                return session;
            }
            catch
            {
                session.Rollback();
                throw;
            }
        }

        internal static bool IsOwnerActive(BotOwner owner)
        {
            return _active != null && !_active._disposed && _active.Owner == owner;
        }

        internal static bool OwnsSainInstance(object instance)
        {
            return RouteCompatibility.IsSelectedInstance(instance);
        }

        public void Tick()
        {
            if (_disposed || IsComplete)
                return;
            EnsureStillUsable();
            _steeringChanged = true;
            _steering.LookToMovingDirection();

            // count and undo foreign writes so the log shows whether something else drives speed.
            // vanilla ManualUpdate forces 0.5 near doors every tick — leave that alone and dont count its leftover
            bool nearDoor = Owner.DoorOpener != null && Owner.DoorOpener.NearDoor;
            if (!nearDoor && Mathf.Abs(_mover.DestMoveSpeed - _requestedMoveSpeed) > 0.001f)
            {
                if (!_nearDoorLastTick)
                    _speedOverrides++;
                _mover.SetTargetMoveSpeed(_requestedMoveSpeed);
                _speedChanged = true;
            }
            _nearDoorLastTick = nearDoor;
            // BotMover.Sprint(false) early-outs when the mover flag is already false, even with the player still sprinting
            if (_player.IsSprintEnabled)
            {
                _speedOverrides++;
                _player.EnableSprint(false);
                _sprintChanged = true;
            }

            Vector3 position = Owner.Position;
            MeasureSpeed(position);
            if (_nextEndpoint < _endpoints.Length && (position - _endpoints[_nextEndpoint]).sqrMagnitude <= EndpointTolerance * EndpointTolerance)
            {
                _nextEndpoint++;
                if (_nextEndpoint == _endpoints.Length)
                {
                    IsComplete = true;
                    Debug.Log("[" + ModInfo.Name + "] Walking route reached both NavMesh endpoints.");
                    return;
                }
            }

            if (_routeController == null || _routePath == null || !ReferenceEquals(_mover.ActualPathController, _routeController))
                throw new InvalidOperationException("Walking route aborted: bot mover path controller was replaced.");
            if (!ReferenceEquals(_routeController.CurPath, _routePath))
                throw new InvalidOperationException("Walking route aborted: bot mover path was replaced.");
            if (_brain.IsReRegistered)
                throw new InvalidOperationException("Walking route aborted: vanilla brain agent was re-registered.");
            if (!_mover.HasPathAndNoComplete)
                throw new InvalidOperationException("Walking route aborted: bot mover ended the route before the endpoint.");

            if ((position - _lastProgressPosition).sqrMagnitude >= ProgressDistance * ProgressDistance)
            {
                _lastProgressPosition = position;
                _lastProgressTime = Time.realtimeSinceStartup;
            }
            else if (Time.realtimeSinceStartup - _lastProgressTime > StuckTimeout)
            {
                throw new InvalidOperationException("Walking route aborted: bot did not make progress before the stuck timeout.");
            }
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
                    if (IsUsableForRestore() && _routePath != null &&
                        ReferenceEquals(_mover.ActualPathController, _routeController) &&
                        ReferenceEquals(_routeController.CurPath, _routePath)) _mover.Stop();
                });
                Cleanup(() => { if (_speedChanged && IsUsableForRestore()) _mover.SetTargetMoveSpeed(_wasMoveSpeed); });
                Cleanup(() => { if (_sprintChanged && IsUsableForRestore()) _mover.Sprint(_wasSprinting); });
                Cleanup(() =>
                {
                    if (_steeringChanged && IsUsableForRestore() && Owner.Steering == _steering)
                    { _steering.SteeringMode = _wasSteeringMode; _steering.Speed = _wasSteeringSpeed; }
                });
                Cleanup(() => _brain.Release(IsUsableForRestore()));
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
            Debug.Log("[" + ModInfo.Name + "] Walking route cleanup finished; stock AI ownership was restored when the bot remained valid.");
        }

        private void Begin()
        {
            if (!RouteCompatibility.TryEnable())
                throw new InvalidOperationException("Walking route rejected: SAIN compatibility could not be established safely.");
            _compatibilityEnabled = RouteCompatibility.IsEnabled;

            _brain.Acquire("Walking route rejected: ");

            if (!_wasSprinting)
                _mover.Sprint(false);
            else
            {
                _mover.Sprint(false);
                _sprintChanged = true;
            }
            if (Mathf.Abs(_wasMoveSpeed - _requestedMoveSpeed) > 0.001f)
            {
                _mover.SetTargetMoveSpeed(_requestedMoveSpeed);
                _speedChanged = true;
            }

            var priorPath = _mover.ActualPathController?.CurPath;
            try { _mover.GoToByWay(Points, ReachDistance); }
            finally
            {
                // Identify the path even if GoToByWay fails after installing it.
                if (!ReferenceEquals(priorPath, _mover.ActualPathController?.CurPath))
                {
                    _routeController = _mover.ActualPathController;
                    _routePath = _routeController?.CurPath;
                }
            }
            if (_routeController == null || _routePath == null)
                throw new InvalidOperationException("Walking route rejected: mover did not accept the NavMesh path.");
            _lastProgressTime = Time.realtimeSinceStartup;
            _lastSpeedPosition = Owner.Position;
            _speedStartTime = _lastSpeedTime = Time.time;
        }

        private void MeasureSpeed(Vector3 position)
        {
            float now = Time.time;
            float dt = now - _lastSpeedTime;
            if (dt > 0f)
            {
                float meters = new Vector2(position.x - _lastSpeedPosition.x, position.z - _lastSpeedPosition.z).magnitude;
                _totalMeters += meters;
                _totalSeconds += dt;
                if (now - _speedStartTime >= SpeedWarmupSeconds)
                {
                    _steadyMeters += meters;
                    _steadySeconds += dt;
                    _steadyPlayerSpeedSum += _player.Speed;
                    _steadyPlayerSpeedSamples++;
                }
            }
            _lastSpeedPosition = position;
            _lastSpeedTime = now;
        }

        private void EnsureStillUsable()
        {
            if (Owner == null || Owner.GetPlayer == null || Owner.BotState != EBotState.Active || Owner.HealthController == null || !Owner.HealthController.IsAlive)
                throw new InvalidOperationException("Walking route aborted: bot is no longer active and alive.");
            if (!_brain.IsIntact)
                throw new InvalidOperationException("Walking route aborted: bot brain changed.");
            if (Owner.Memory == null || Owner.Memory.GoalEnemy != null)
                throw new InvalidOperationException("Walking route aborted: bot entered combat.");
        }

        private bool IsUsableForRestore()
        {
            return Owner != null && Owner.GetPlayer != null && Owner.BotState == EBotState.Active && Owner.HealthController != null && Owner.HealthController.IsAlive;
        }

        private void Rollback()
        {
            Dispose();
        }

        private static void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception ex) { Debug.LogError("[" + ModInfo.Name + "] Route cleanup operation failed: " + ex); }
        }

        private static Vector3 SampleNavMesh(Vector3 requested, string name)
        {
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(requested, out hit, 2f, NavMesh.AllAreas))
                throw new InvalidOperationException("Walking route rejected: " + name + " is not on the NavMesh.");
            return hit.position;
        }

        private static Vector3[] CalculateCompletePath(Vector3 from, Vector3 to, string name)
        {
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete || path.corners == null || path.corners.Length < 2)
                throw new InvalidOperationException("Walking route rejected: " + name + " is not complete on the NavMesh.");
            Vector3[] corners = path.corners;
            if ((corners[0] - from).sqrMagnitude > 1f || (corners[corners.Length - 1] - to).sqrMagnitude > 0.36f)
                throw new InvalidOperationException("Walking route rejected: " + name + " endpoint validation failed.");
            return corners;
        }

        private static Vector3[] ConcatenateCorners(Vector3[] first, Vector3[] second)
        {
            int start = (first[first.Length - 1] - second[0]).sqrMagnitude <= 0.04f ? 1 : 0;
            return first.AsValueEnumerable().Concat(second.AsValueEnumerable().Skip(start)).ToArray();
        }
    }

    internal struct RouteSummary
    {
        public float RequestedMoveSpeed;
        public bool HasMeasuredSpeed;
        public float MeanSpeedMetersPerSecond;
        public float SteadySpeedMetersPerSecond;
        public float SteadyPlayerSpeed;
        public float SteadySeconds;
        public int SpeedOverrides;
    }
}
