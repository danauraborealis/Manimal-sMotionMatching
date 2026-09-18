using System.Collections.Generic;
using System.Reflection;
using EFT;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // bots have no inertia: the body is at 1.5-2 m/s within 0.2 s of an order and reverses on the spot, whatever
    // Player.Speed is ramped to (movement is animator root motion, and the AI path skips the player's smoothing).
    // every grounded state's motion passes through MovementState.ApplyMotion, so the horizontal velocity is slewed
    // there: speeding up and turning are capped, braking is left to the game (the stop clips predict its distance)
    internal sealed class BotInertiaPatch : ModulePatch
    {
        // m/s^2; 0 disables
        internal static float Acceleration;

        private sealed class State
        {
            public Vector2 Velocity;
            public float Time = -10f;
            public float CeilingTime = -10f;
            public float DriveTime = -10f;
            // m/s the body may not exceed this frame: a start clip's own root speed, so the legs are never outrun
            public float? Ceiling;
            // m/s a clip asks the body to hold (a cut's push-off): followed at DriveAcceleration instead of the cap
            public float? Drive;
            public Vector2? RootDrive;
            public float RootDriveTime = -10f;
        }

        private static readonly Dictionary<MovementContext, State> Attached = new Dictionary<MovementContext, State>();
        private const float ResetGapSeconds = 0.25f;
        private const float CommandLifetimeSeconds = 0.25f;
        private const float BrakeAcceleration = 10f;
        private const float CarryBeforePush = 0.8f;
        private const float DriveAcceleration = 14f;

        internal static void Attach(Player player)
        {
            if (player && player.MovementContext != null)
                Attached[player.MovementContext] = new State();
        }

        internal static void Detach(Player player)
        {
            if (player && player.MovementContext != null)
                Attached.Remove(player.MovementContext);
        }

        internal static void SetCeiling(Player player, float? metersPerSecond)
        {
            State state;
            if (player && player.MovementContext != null && Attached.TryGetValue(player.MovementContext, out state))
                SetCommand(ref state.Ceiling, ref state.CeilingTime, metersPerSecond);
        }

        internal static void SetDrive(Player player, float? metersPerSecond)
        {
            State state;
            if (player && player.MovementContext != null && Attached.TryGetValue(player.MovementContext, out state))
                SetCommand(ref state.Drive, ref state.DriveTime, metersPerSecond);
        }

        internal static void SetRootDrive(Player player, Vector2? velocity)
        {
            State state;
            if (!player || player.MovementContext == null || !Attached.TryGetValue(player.MovementContext, out state)) return;
            state.RootDrive = velocity.HasValue && IsFinite(velocity.Value) && velocity.Value.magnitude <= 8f ? velocity : null;
            state.RootDriveTime = state.RootDrive.HasValue ? Time.time : -10f;
        }

        private static void SetCommand(ref float? command, ref float commandTime, float? metersPerSecond)
        {
            float now = Time.time;
            if (!metersPerSecond.HasValue)
            {
                command = null;
                commandTime = -10f;
                return;
            }

            float value = metersPerSecond.Value;
            if (!IsFinite(value) || value < 0f || !IsFinite(now))
            {
                command = null;
                commandTime = -10f;
                return;
            }

            command = value;
            commandTime = now;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector2 value) => IsFinite(value.x) && IsFinite(value.y);

        private static void ClearCommands(State state)
        {
            state.Ceiling = null;
            state.CeilingTime = -10f;
            state.Drive = null;
            state.DriveTime = -10f;
            state.RootDrive = null;
            state.RootDriveTime = -10f;
        }

        private static void ExpireCommands(State state, float now)
        {
            if (!IsFresh(state.RootDriveTime, now)) state.RootDrive = null;
            if (!state.Ceiling.HasValue || !IsFresh(state.CeilingTime, now))
            {
                state.Ceiling = null;
                state.CeilingTime = -10f;
            }
            if (!state.Drive.HasValue || !IsFresh(state.DriveTime, now))
            {
                state.Drive = null;
                state.DriveTime = -10f;
            }
        }

        private static bool IsFresh(float commandTime, float now)
        {
            if (!IsFinite(commandTime) || !IsFinite(now))
                return false;
            float age = now - commandTime;
            return age >= 0f && age <= CommandLifetimeSeconds;
        }

        internal static void Clear() => Attached.Clear();

        protected override MethodBase GetTargetMethod() => typeof(MovementState).GetMethod(nameof(MovementState.ApplyMotion), BindingFlags.Instance | BindingFlags.Public);

        [PatchPrefix]
        private static void Prefix(MovementState __instance, ref Vector3 motion, float deltaTime)
        {
            float limit = Acceleration;
            float now = Time.time;
            if (!IsFinite(limit) || limit <= 0f || !IsFinite(deltaTime) || deltaTime <= 1e-5f
                || !IsFinite(now) || Attached.Count == 0 || __instance.MovementContext == null)
                return;
            State state;
            if (!Attached.TryGetValue(__instance.MovementContext, out state))
                return;
            EPlayerState name = __instance.Name;
            Vector2 wanted = new Vector2(motion.x, motion.z) / deltaTime;
            if (!IsFinite(wanted))
            {
                state.Velocity = Vector2.zero;
                state.Time = now;
                ClearCommands(state);
                return;
            }

            // only plain ground movement; jumps, vaults, doors and the rest keep the game's motion and reset the memory
            if (name != EPlayerState.Run && name != EPlayerState.Sprint && name != EPlayerState.Idle && name != EPlayerState.Transition)
            {
                state.Velocity = wanted;
                state.Time = now;
                ClearCommands(state);
                return;
            }

            ExpireCommands(state, now);
            if (!IsFinite(state.Velocity) || !IsFinite(state.Time) || now < state.Time || now - state.Time > ResetGapSeconds)
            {
                // A just-written ceiling/drive command remains valid through initialization and brief frame gaps.
                state.Velocity = wanted;
                state.Time = now;
                return;
            }
            state.Time = now;
            // the speed along the steered line has the inertia. what is left of the old heading after a direction
            // change brakes off quickly instead of vanishing: with an instant stop the body skipped the braking half
            // of a cut (the foot planted out ahead) and only showed the push-off, ankles 15-21 cm behind the pelvis
            // (user: leaning like michael jackson until the feet catch up). a slow decay ran bots into walls, so
            // the carry is short: 2.5 m/s is gone in 0.25 s and 0.3 m
            float wantedSpeed = wanted.magnitude;
            if (!IsFinite(wantedSpeed))
            {
                state.Velocity = Vector2.zero;
                ClearCommands(state);
                return;
            }

            if (wantedSpeed < 1e-4f)
                state.Velocity = Vector2.zero;
            else if (state.RootDrive.HasValue)
                // Authored horizontal motion still passes through EFT's character controller/collision.
                // A native stop (zero request), jump, stale command or detach always releases ownership.
                state.Velocity = Vector2.MoveTowards(state.Velocity, state.RootDrive.Value, DriveAcceleration * deltaTime);
            else
            {
                Vector2 line = wanted / wantedSpeed;
                float along = Mathf.Max(Vector2.Dot(state.Velocity, line), 0f);
                if (!IsFinite(along))
                {
                    state.Velocity = wanted;
                    ClearCommands(state);
                    return;
                }
                Vector2 carried = Vector2.MoveTowards(state.Velocity - line * along, Vector2.zero, BrakeAcceleration * deltaTime);
                // the push in the new direction waits for most of the braking, as a planted leg does
                float push = carried.magnitude > CarryBeforePush ? 0f : limit * deltaTime;
                float speed = wantedSpeed >= along ? Mathf.MoveTowards(along, wantedSpeed, push) : wantedSpeed;
                // A clip drive may shape acceleration, but it must not undo an explicit braking request from EFT.
                if (state.Drive.HasValue && wantedSpeed >= along && carried.magnitude <= CarryBeforePush)
                    speed = Mathf.MoveTowards(along, Mathf.Min(state.Drive.Value, wantedSpeed), DriveAcceleration * deltaTime);
                if (state.Ceiling.HasValue)
                    speed = Mathf.Min(speed, Mathf.Max(state.Ceiling.Value, along - limit * deltaTime));
                state.Velocity = line * speed + carried;
            }

            if (!IsFinite(state.Velocity))
            {
                state.Velocity = wanted;
                ClearCommands(state);
                return;
            }
            float nextX = state.Velocity.x * deltaTime;
            float nextZ = state.Velocity.y * deltaTime;
            if (!IsFinite(nextX) || !IsFinite(nextZ))
            {
                state.Velocity = wanted;
                ClearCommands(state);
                return;
            }
            motion.x = nextX;
            motion.z = nextZ;
        }
    }
}
