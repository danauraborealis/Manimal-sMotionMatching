using EFT;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // procedural weight shift for the torso: EFT bots stay bolt upright while the legs do all the work, which reads
    // as the body floating along behind its own feet (user, 2026-09-15). leans the lower spine into acceleration and
    // into turns, then counter-rotates the top of the chain so the weapon keeps pointing where the animator aimed it
    internal sealed class BodyLean
    {
        // EFT bots accelerate absurdly hard (0 to 2.8 m/s in ~0.3 s ~= 9 m/s2); a true atan(a/g) lean would be ~40 degrees
        private const float DegreesPerMetersPerSecondSquared = 1.1f;
        private const float DegreesPerDegreePerSecondOfTurn = 0.06f;
        private const float MaxLeanDegrees = 14f;
        // banking into a fast turn used the whole lean budget, and the part that reaches the chest warped the gun
        // as the bot swung onto a sprint heading (user saw this). the turn contributes far less than acceleration now
        private const float MaxTurnLeanDegrees = 5f;
        // acceleration off raw positions is noisy, and lean should settle rather than twitch
        private const float VelocitySmoothing = 0.08f;
        private const float LeanSmoothing = 0.12f;
        private const float MaxInstantSpeed = 8f;
        // steady forward lean above a walk: 0 at 1.3 m/s, 6 degrees by 2.5
        private const float SteadyLeanFromSpeed = 1.3f;
        private const float SteadyLeanDegreesPerMps = 5f;
        private const float MaxSteadyLeanDegrees = 6f;

        // lower back carries most of the lean; the chest carries little, so the gun stays level
        private static readonly string[] SpineBones = { "Base HumanSpine1", "Base HumanSpine2", "Base HumanSpine3" };
        private static readonly float[] SpineShares = { 0.45f, 0.35f, 0.2f };
        private const string CompensationBone = "Base HumanRibcage";

        private readonly Player _player;
        private readonly Transform[] _spine;
        private readonly Transform _compensation;
        private Vector3 _previousPosition;
        private Vector2 _velocity;
        private float _previousYaw;
        private bool _hasPrevious;
        private float _pitch;
        private float _roll;

        private BodyLean(Player player, Transform[] spine, Transform compensation)
        {
            _player = player;
            _spine = spine;
            _compensation = compensation;
        }

        public float Strength { get; set; } = 1f;
        // 0 lets the whole chain lean, 1 holds the weapon where the animator put it
        public float KeepWeaponLevel { get; set; } = 0.6f;
        public bool Active { get; set; } = true;
        public float PitchDegrees => _pitch;
        public float RollDegrees => _roll;
        // how far the chest ended up rotated after compensation: the weapon rides on it, so this is aim drift
        public float AimShiftDegrees { get; private set; }
        public float PeakPitch { get; private set; }
        public float PeakRoll { get; private set; }
        public float PeakAimShift { get; private set; }
        public int AppliedFrames { get; private set; }

        public static BodyLean Create(Player player)
        {
            var references = player?.Grounder?.ik?.references;
            if (references == null || !references.pelvis)
                return null;
            var spine = new Transform[SpineBones.Length];
            for (int i = 0; i < SpineBones.Length; i++)
            {
                spine[i] = FindChild(references.pelvis, SpineBones[i]);
                if (!spine[i])
                    return null;
            }
            return new BodyLean(player, spine, FindChild(references.pelvis, CompensationBone));
        }

        public void Apply()
        {
            if (NativeJumpOwnership.Owns(_player))
            {
                _hasPrevious = false;
                _velocity = new Vector2(_player.Velocity.x, _player.Velocity.z);
                _pitch = _roll = AimShiftDegrees = 0f;
                return;
            }
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;
            Vector3 position = _player.Position;
            float yaw = _player.Rotation.x;
            if (!_hasPrevious)
            {
                _previousPosition = position;
                _previousYaw = yaw;
                _hasPrevious = true;
                return;
            }

            Vector2 step = new Vector2(position.x - _previousPosition.x, position.z - _previousPosition.z);
            _previousPosition = position;
            float yawRate = Mathf.DeltaAngle(_previousYaw, yaw) / dt;
            _previousYaw = yaw;

            Vector2 previousVelocity = _velocity;
            // a puppet teleport reads as hundreds of m/s; same guard the playback uses
            if (step.magnitude / dt < MaxInstantSpeed)
                _velocity = Vector2.Lerp(_velocity, step / dt, Mathf.Clamp01(dt / VelocitySmoothing));
            Vector2 acceleration = (_velocity - previousVelocity) / dt;

            // body-local: x right, y forward
            float radians = yaw * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians), sin = Mathf.Sin(radians);
            Vector2 localAcceleration = new Vector2(acceleration.x * cos - acceleration.y * sin, acceleration.x * sin + acceleration.y * cos);
            Vector2 localVelocity = new Vector2(_velocity.x * cos - _velocity.y * sin, _velocity.x * sin + _velocity.y * cos);

            // lean into the push, and into the inside of a turn like a runner carrying speed
            // plus a steady lean with speed: the combat run cycles are authored upright (gun up) and at a jog the bot
            // "stood up straight while the legs jogged" (user), where a runner carries a few degrees forward
            float steady = Mathf.Clamp((localVelocity.magnitude - SteadyLeanFromSpeed) * SteadyLeanDegreesPerMps, 0f, MaxSteadyLeanDegrees);
            float targetPitch = Mathf.Clamp(localAcceleration.y * DegreesPerMetersPerSecondSquared + steady, -MaxLeanDegrees, MaxLeanDegrees + MaxSteadyLeanDegrees);
            float turnLean = Mathf.Clamp(yawRate * localVelocity.magnitude * DegreesPerDegreePerSecondOfTurn, -MaxTurnLeanDegrees, MaxTurnLeanDegrees);
            float targetRoll = Mathf.Clamp(localAcceleration.x * DegreesPerMetersPerSecondSquared + turnLean, -MaxLeanDegrees, MaxLeanDegrees);
            if (!Active)
                targetPitch = targetRoll = 0f;
            float blend = Mathf.Clamp01(dt / LeanSmoothing);
            _pitch = Mathf.Lerp(_pitch, targetPitch, blend);
            _roll = Mathf.Lerp(_roll, targetRoll, blend);

            float pitch = _pitch * Strength;
            float roll = _roll * Strength;
            PeakPitch = Mathf.Max(PeakPitch, Mathf.Abs(pitch));
            PeakRoll = Mathf.Max(PeakRoll, Mathf.Abs(roll));
            if (Mathf.Abs(pitch) < 0.01f && Mathf.Abs(roll) < 0.01f)
                return;

            AppliedFrames++;
            Vector3 right = new Vector3(cos, 0f, -sin);
            Vector3 forward = new Vector3(sin, 0f, cos);
            for (int i = 0; i < _spine.Length; i++)
            {
                Quaternion lean = Quaternion.AngleAxis(pitch * SpineShares[i], right) * Quaternion.AngleAxis(-roll * SpineShares[i], forward);
                _spine[i].rotation = lean * _spine[i].rotation;
            }
            if (_compensation)
            {
                Quaternion before = _compensation.rotation;
                // sprinting swings the body hard; let none of that reach the weapon
                float keepLevel = _player.IsSprintEnabled ? 1f : KeepWeaponLevel;
                if (keepLevel > 0f)
                {
                    Quaternion total = Quaternion.AngleAxis(pitch, right) * Quaternion.AngleAxis(-roll, forward);
                    _compensation.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.Inverse(total), keepLevel) * _compensation.rotation;
                }
                AimShiftDegrees = Quaternion.Angle(before, _compensation.rotation);
                PeakAimShift = Mathf.Max(PeakAimShift, AimShiftDegrees);
            }
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (!root) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChild(root.GetChild(i), name);
                if (found) return found;
            }
            return null;
        }
    }
}
