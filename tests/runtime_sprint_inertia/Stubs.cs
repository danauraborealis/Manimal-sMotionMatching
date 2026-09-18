using System;
using System.Reflection;

namespace UnityEngine
{
    public static class Time
    {
        public static int frameCount;
        public static float realtimeSinceStartup;
    }
}

namespace SPT.Reflection.Patching
{
    public abstract class ModulePatch
    {
        protected abstract MethodBase GetTargetMethod();

        public bool IsActive { get; private set; }

        public void Enable() { IsActive = true; }

        public void Disable() { IsActive = false; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class PatchPrefixAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class PatchPostfixAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class PatchFinalizerAttribute : Attribute { }
}

namespace EFT
{
    public enum EPlayerState
    {
        None,
        Sprint,
        Jump
    }

    public sealed class HealthController
    {
        public bool IsAlive = true;
    }

    public sealed class PhysicalBase
    {
        public bool Sprinting;
    }

    public sealed class Player
    {
        private static int _nextId;
        private readonly int _id = ++_nextId;

        public bool IsAI = true;
        public HealthController HealthController = new HealthController();
        public PhysicalBase Physical = new PhysicalBase();
        public bool IsInPronePose;
        public float PoseLevel = 1f;
        public MovementContext MovementContext { get; set; }

        public Player(float sprintSpeed = 0.5f)
        {
            MovementContext = new MovementContext(this, sprintSpeed);
        }

        public int GetInstanceID() { return _id; }

        public bool IsSprintEnabled => Physical != null && Physical.Sprinting;
    }

    public sealed class MovementContext
    {
        private float _sprintSpeed;
        private readonly Player _player;

        public MovementContext(Player player, float sprintSpeed)
        {
            _player = player;
            _sprintSpeed = sprintSpeed;
        }

        public bool ThrowFromAcceleration;
        public bool ThrowFromExit;
        public Action<float> WriteSpeed;

        public float SprintSpeed
        {
            get { return _sprintSpeed; }
            set { _sprintSpeed = value; }
        }

        public void SprintAcceleration(float deltaTime)
        {
            float next = SprintSpeed + deltaTime;
            if (WriteSpeed != null)
                WriteSpeed(next);
            else
                SprintSpeed = next;
            if (ThrowFromAcceleration)
                throw new InvalidOperationException("acceleration fixture failure");
        }

        public void SprintExitDelegate(EPlayerState previousState, EPlayerState nextState)
        {
            if (nextState != EPlayerState.Sprint && nextState != EPlayerState.Jump)
            {
                if (WriteSpeed != null)
                    WriteSpeed(0.5f);
                else
                    SprintSpeed = 0.5f;
            }
            if (ThrowFromExit)
                throw new InvalidOperationException("exit fixture failure");
        }
    }
}
