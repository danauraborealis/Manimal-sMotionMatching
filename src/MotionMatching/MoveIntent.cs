using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // where the bot is being told to go this frame, read from whichever system is driving it. the pose layer
    // must not depend on BotMover alone: SAIN pauses the mover in combat and drives Player.Move itself, ORBIT does
    // the same for its bots, and the puppet harness clears the mover path. each source answers the same three
    // questions and the playback and placer never know which one is talking
    internal sealed class MoveIntent
    {
        // travel direction relative to the body facing, degrees; null while the driver wants the bot standing
        public Func<float?> Yaw;
        // metres left to the driver's goal along its path; null when the driver has no goal or no idea
        public Func<float?> Remaining;
        // upcoming path corners in world space from the bot onward; null for a straight line along the travel
        public Func<IList<Vector3>> Corners;
        // the speed the driver is asking for, in Player.Speed units (0-1); null when unknown. Player.Speed itself lags
        // the order by frames, and a start read from it picked the run start for a walk
        public Func<float?> TargetCommand;
        public Func<string> Driver;
        // the yaw the controller intends to face, relative to the current facing; null when unknown. stage F:
        // a turn clip is chosen to match the turn the controller is going to make, never to drive it
        public Func<float?> GoalYaw;

        public static MoveIntent Standing() => new MoveIntent { Yaw = () => null, Remaining = () => null, Corners = () => null, TargetCommand = () => null, Driver = () => "none", GoalYaw = () => null };

        // the stock steering's look target (SAIN rotates through Player.Rotate itself and leaves it stale, so a
        // measured fallback stays in the playback)
        internal static float? SteeringGoal(Player player)
        {
            try
            {
                var steering = player.AIData?.BotOwner?.Steering;
                if (steering == null) return null;
                Vector3 d = steering.LookDirection; d.y = 0f;
                if (d.sqrMagnitude < 0.01f) return null;
                return Mathf.DeltaAngle(player.Rotation.x, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
            }
            catch { return null; }
        }

        public static MoveIntent FromPuppet(PuppetSession puppet)
        {
            return new MoveIntent
            {
                Yaw = () => puppet != null ? puppet.MoveIntentYaw : null,
                Remaining = () => puppet != null ? puppet.RemainingMeters : null,
                Corners = () => null,
                TargetCommand = () => puppet != null ? puppet.CommandSpeed : null,
                Driver = () => "puppet",
                GoalYaw = () => puppet != null ? puppet.GoalYaw : null
            };
        }

        // stock mover, or SAIN's own path when its layers drive the bot; falls back to the measured travel direction
        // so a bot moved by something unknown still gets cycles, just without a goal
        public static MoveIntent ForBot(Player player)
        {
            var sain = SainPath.For(player);
            var corners = new List<Vector3>(16);
            Vector3 previous = player.Position;
            bool hasPrevious = false;
            Vector2 velocity = Vector2.zero;
            Func<bool> sainActive = () => sain != null && sain.Active;
            Func<bool> stockActive = () =>
            {
                var mover = player.AIData?.BotOwner?.Mover;
                return mover != null && mover.IsMoving && !mover.Pause && mover.HasPathAndNoComplete;
            };
            Func<float?> measuredYaw = () =>
            {
                Vector3 p = player.Position;
                if (hasPrevious && Time.deltaTime > 0f)
                    velocity = Vector2.Lerp(velocity, new Vector2(p.x - previous.x, p.z - previous.z) / Time.deltaTime, Mathf.Clamp01(Time.deltaTime / 0.15f));
                previous = p;
                hasPrevious = true;
                if (velocity.magnitude < 0.3f)
                    return null;
                return Mathf.DeltaAngle(player.Rotation.x, Mathf.Atan2(velocity.x, velocity.y) * Mathf.Rad2Deg);
            };
            return new MoveIntent
            {
                GoalYaw = () => SteeringGoal(player),
                Yaw = () =>
                {
                    float? measured = measuredYaw();
                    if (sainActive())
                    {
                        Vector3? corner = sain.NextCorner();
                        return corner.HasValue ? TowardYaw(player, corner.Value) : measured;
                    }
                    if (stockActive())
                    {
                        // the mover steers by DirCurPoint (current corner minus its point on the way); the cached
                        // CurrentCornerPoint only moves in IncCornerIndex and sat behind a running scav, swinging
                        // as the bot left it and firing a cut on every swing
                        var mover = player.AIData.BotOwner.Mover;
                        Vector3 dir = mover.DirCurPoint;
                        if (dir.x * dir.x + dir.z * dir.z > 1e-4f)
                            return Mathf.DeltaAngle(player.Rotation.x, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg);
                        return TowardYaw(player, mover.CurrentCornerPoint);
                    }
                    return measured;
                },
                Remaining = () =>
                {
                    if (sainActive())
                        return sain.Remaining(player.Position);
                    if (!stockActive())
                        return null;
                    var mover = player.AIData.BotOwner.Mover;
                    // the path controller's own remaining distance; the waypoint sum below detours through a stale
                    // first corner behind the bot
                    var controller = mover.ActualPathController;
                    if (controller != null && controller.PlayerRemainingDist > 0f)
                        return controller.PlayerRemainingDist;
                    Vector3[] points = mover.GetWayPoints(16);
                    if (points == null || points.Length == 0)
                        return mover.DistDestination;
                    float sum = 0f;
                    Vector3 from = player.Position;
                    for (int i = 0; i < points.Length; i++)
                    {
                        sum += Horizontal(points[i] - from);
                        from = points[i];
                    }
                    return sum;
                },
                Corners = () =>
                {
                    corners.Clear();
                    if (sainActive())
                    {
                        sain.Corners(corners);
                        return corners.Count > 0 ? corners : null;
                    }
                    if (!stockActive())
                        return null;
                    var mover = player.AIData.BotOwner.Mover;
                    Vector3[] points = mover.GetWayPoints(16);
                    if (points == null)
                        return null;
                    Vector3 steer = mover.DirCurPoint;
                    Vector3 here = player.Position;
                    for (int i = 0; i < points.Length; i++)
                    {
                        // corners already passed (behind the steering direction) would march the feet backwards
                        Vector3 to = points[i] - here;
                        if (corners.Count == 0 && i < points.Length - 1 && to.x * steer.x + to.z * steer.z < 0f)
                            continue;
                        corners.Add(points[i]);
                    }
                    return corners.Count > 0 ? corners : null;
                },
                TargetCommand = () =>
                {
                    // SAIN pushes its own speed through ChangeSpeed each tick; there is no cheap read of its target
                    if (sainActive() || !stockActive())
                        return null;
                    return player.AIData.BotOwner.Mover.DestMoveSpeed;
                },
                Driver = () => sainActive() ? "sain" : stockActive() ? "mover" : "measured"
            };
        }

        private static float TowardYaw(Player player, Vector3 target)
        {
            Vector3 d = target - player.Position;
            if (d.x * d.x + d.z * d.z < 1e-4f)
                return 0f;
            return Mathf.DeltaAngle(player.Rotation.x, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
        }

        private static float Horizontal(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);

        // SAIN 4.5.1 keeps its combat path in BotComponent.Mover.ActivePath (PathCorners, CurrentIndex, Destination)
        // and drives only while SAINLayersActive. read through reflection so the mod loads without SAIN and survives
        // a renamed member by falling back to the stock mover; verified member names are from its decompiled source
        internal sealed class SainPath
        {
            private readonly Component _bot;
            private readonly PropertyInfo _layersActive, _mover, _activePath, _pathCorners, _currentIndex, _destination;

            private SainPath(Component bot, PropertyInfo layersActive, PropertyInfo mover, PropertyInfo activePath, PropertyInfo pathCorners, PropertyInfo currentIndex, PropertyInfo destination)
            {
                _bot = bot; _layersActive = layersActive; _mover = mover; _activePath = activePath; _pathCorners = pathCorners; _currentIndex = currentIndex; _destination = destination;
            }

            public static SainPath For(Player player)
            {
                if (!player)
                    return null;
                try
                {
                    foreach (var component in player.gameObject.GetComponents<Component>())
                    {
                        if (!component)
                            continue;
                        var type = component.GetType();
                        if (type.Name != "BotComponent" || type.Namespace == null || !type.Namespace.StartsWith("SAIN", StringComparison.Ordinal))
                            continue;
                        var layers = type.GetProperty("SAINLayersActive");
                        var mover = type.GetProperty("Mover");
                        if (layers == null || mover == null || layers.PropertyType != typeof(bool))
                            return null;
                        var activePath = mover.PropertyType.GetProperty("ActivePath");
                        if (activePath == null)
                            return null;
                        var pathType = activePath.PropertyType;
                        var corners = pathType.GetProperty("PathCorners");
                        var index = pathType.GetProperty("CurrentIndex");
                        var destination = pathType.GetProperty("Destination");
                        if (corners == null || index == null || destination == null)
                            return null;
                        return new SainPath(component, layers, mover, activePath, corners, index, destination);
                    }
                }
                catch (Exception) { }
                return null;
            }

            public bool Active
            {
                get
                {
                    try { return _bot && (bool)_layersActive.GetValue(_bot); }
                    catch (Exception) { return false; }
                }
            }

            private object Path()
            {
                var mover = _mover.GetValue(_bot);
                return mover == null ? null : _activePath.GetValue(mover);
            }

            public Vector3? NextCorner()
            {
                try
                {
                    var path = Path();
                    if (path == null) return null;
                    var corners = _pathCorners.GetValue(path) as Vector3[];
                    int index = (int)_currentIndex.GetValue(path);
                    if (corners == null || corners.Length == 0) return (Vector3)_destination.GetValue(path);
                    return corners[Mathf.Clamp(index, 0, corners.Length - 1)];
                }
                catch (Exception) { return null; }
            }

            public void Corners(List<Vector3> into)
            {
                try
                {
                    var path = Path();
                    if (path == null) return;
                    var corners = _pathCorners.GetValue(path) as Vector3[];
                    if (corners == null) return;
                    int index = Mathf.Clamp((int)_currentIndex.GetValue(path), 0, corners.Length);
                    for (int i = index; i < corners.Length; i++) into.Add(corners[i]);
                }
                catch (Exception) { }
            }

            public float? Remaining(Vector3 from)
            {
                try
                {
                    var path = Path();
                    if (path == null) return null;
                    var corners = _pathCorners.GetValue(path) as Vector3[];
                    Vector3 destination = (Vector3)_destination.GetValue(path);
                    if (corners == null || corners.Length == 0)
                        return Horizontal(destination - from);
                    int index = Mathf.Clamp((int)_currentIndex.GetValue(path), 0, corners.Length);
                    float sum = 0f;
                    for (int i = index; i < corners.Length; i++)
                    {
                        sum += Horizontal(corners[i] - from);
                        from = corners[i];
                    }
                    return sum;
                }
                catch (Exception) { return null; }
            }
        }
    }
}
