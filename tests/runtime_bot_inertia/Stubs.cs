using System;
using System.Reflection;

namespace UnityEngine
{
    public class Object
    {
        public static implicit operator bool(Object value) => !ReferenceEquals(value, null);
    }

    public struct Vector2
    {
        public float x;
        public float y;

        public Vector2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2 zero => new Vector2(0f, 0f);
        public float magnitude => (float)Math.Sqrt(x * x + y * y);

        public static Vector2 operator +(Vector2 left, Vector2 right) => new Vector2(left.x + right.x, left.y + right.y);
        public static Vector2 operator -(Vector2 left, Vector2 right) => new Vector2(left.x - right.x, left.y - right.y);
        public static Vector2 operator *(Vector2 value, float scale) => new Vector2(value.x * scale, value.y * scale);
        public static Vector2 operator /(Vector2 value, float scale) => new Vector2(value.x / scale, value.y / scale);

        public static float Dot(Vector2 left, Vector2 right) => left.x * right.x + left.y * right.y;

        public static Vector2 MoveTowards(Vector2 current, Vector2 target, float maxDistanceDelta)
        {
            Vector2 delta = target - current;
            float distance = delta.magnitude;
            if (distance <= maxDistanceDelta || distance == 0f)
                return target;
            return current + delta / distance * maxDistanceDelta;
        }
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    public static class Mathf
    {
        public static float Max(float left, float right) => left > right ? left : right;
        public static float Min(float left, float right) => left < right ? left : right;

        public static float MoveTowards(float current, float target, float maxDelta)
        {
            float delta = target - current;
            if (Math.Abs(delta) <= maxDelta)
                return target;
            return current + Math.Sign(delta) * maxDelta;
        }
    }

    public static class Time
    {
        public static float time;
    }
}

namespace EFT
{
    using UnityEngine;

    public enum EPlayerState
    {
        Idle,
        Run,
        Sprint,
        Transition,
        Jump,
        Door
    }

    public sealed class Player
    {
        public MovementContext MovementContext { get; } = new MovementContext();
        public static implicit operator bool(Player value) => !ReferenceEquals(value, null);
    }

    public sealed class MovementContext { }

    public sealed class MovementState
    {
        public MovementContext MovementContext { get; }
        public EPlayerState Name { get; }

        public MovementState(MovementContext movementContext, EPlayerState name)
        {
            MovementContext = movementContext;
            Name = name;
        }

        public void ApplyMotion(ref Vector3 motion, float deltaTime) { }
    }
}

namespace SPT.Reflection.Patching
{
    public abstract class ModulePatch
    {
        protected abstract MethodBase GetTargetMethod();
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class PatchPrefixAttribute : Attribute { }
}

namespace Manimal.MotionMatching
{
    using EFT;

    internal static class SpeedRampPatch
    {
        internal static int DetachCalls;
        internal static bool ThrowOnDetach;

        internal static void Attach(Player player) { }

        internal static void Detach(Player player)
        {
            DetachCalls++;
            if (ThrowOnDetach)
                throw new InvalidOperationException("speed ramp detach fixture failure");
        }
    }

    internal static class SprintInertia
    {
        internal static int DetachCalls;

        internal static void Attach(Player player) { }

        internal static void Detach(Player player)
        {
            DetachCalls++;
        }
    }
}
