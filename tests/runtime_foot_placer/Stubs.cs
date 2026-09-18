using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;

        public Vector2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2 zero => new Vector2(0f, 0f);
        public float sqrMagnitude => x * x + y * y;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);

        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 value, float scalar) => new Vector2(value.x * scalar, value.y * scalar);
        public static Vector2 operator /(Vector2 value, float scalar) => new Vector2(value.x / scalar, value.y / scalar);
    }

    public struct Vector3
    {
        public float x, y, z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);

        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized
        {
            get
            {
                float length = magnitude;
                return length > 1e-8f ? this / length : zero;
            }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 value) => new Vector3(-value.x, -value.y, -value.z);
        public static Vector3 operator *(Vector3 value, float scalar) => new Vector3(value.x * scalar, value.y * scalar, value.z * scalar);
        public static Vector3 operator *(float scalar, Vector3 value) => value * scalar;
        public static Vector3 operator /(Vector3 value, float scalar) => new Vector3(value.x / scalar, value.y / scalar, value.z / scalar);

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Mathf.Clamp01(t);
            return a + (b - a) * t;
        }

        public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
        {
            Vector3 delta = target - current;
            float distance = delta.magnitude;
            if (distance <= maxDistanceDelta || distance < 1e-8f)
                return target;
            return current + delta / distance * maxDistanceDelta;
        }

        public static Vector3 ClampMagnitude(Vector3 value, float maxLength)
        {
            float length = value.magnitude;
            if (length <= maxLength || length < 1e-8f)
                return value;
            return value * (maxLength / length);
        }

        public static Vector3 ProjectOnPlane(Vector3 value, Vector3 planeNormal)
        {
            float denominator = planeNormal.sqrMagnitude;
            return denominator > 1e-8f ? value - planeNormal * (Dot(value, planeNormal) / denominator) : value;
        }

        public static Vector3 RotateTowards(Vector3 current, Vector3 target, float maxRadiansDelta, float maxMagnitudeDelta)
        {
            float currentMagnitude = current.magnitude;
            float targetMagnitude = target.magnitude;
            float outputMagnitude = Mathf.MoveTowards(currentMagnitude, targetMagnitude, maxMagnitudeDelta);
            if (currentMagnitude < 1e-8f)
                return targetMagnitude < 1e-8f ? zero : target.normalized * outputMagnitude;
            if (targetMagnitude < 1e-8f)
                return current.normalized * outputMagnitude;

            Vector3 from = current / currentMagnitude;
            Vector3 to = target / targetMagnitude;
            float angle = Mathf.Acos(Mathf.Clamp(Dot(from, to), -1f, 1f));
            if (angle <= maxRadiansDelta || angle < 1e-8f)
                return to * outputMagnitude;

            Vector3 axis = Cross(from, to);
            if (axis.sqrMagnitude < 1e-8f)
                axis = Cross(from, Vector3.up);
            if (axis.sqrMagnitude < 1e-8f)
                axis = Cross(from, Vector3.right);
            return Quaternion.AngleAxis(Mathf.Min(angle, maxRadiansDelta) * Mathf.Rad2Deg, axis.normalized) * from * outputMagnitude;
        }
    }

    public struct Quaternion
    {
        public float x, y, z, w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);

        public static Quaternion operator *(Quaternion lhs, Quaternion rhs)
        {
            return new Quaternion(
                lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
                lhs.w * rhs.y - lhs.x * rhs.z + lhs.y * rhs.w + lhs.z * rhs.x,
                lhs.w * rhs.z + lhs.x * rhs.y - lhs.y * rhs.x + lhs.z * rhs.w,
                lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);
        }

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            Vector3 q = new Vector3(rotation.x, rotation.y, rotation.z);
            Vector3 t = 2f * Vector3.Cross(q, point);
            return point + rotation.w * t + Vector3.Cross(q, t);
        }

        public static Quaternion AngleAxis(float angleDegrees, Vector3 axis)
        {
            Vector3 unit = axis.normalized;
            float half = angleDegrees * Mathf.Deg2Rad * 0.5f;
            float s = (float)Math.Sin(half);
            return Normalize(new Quaternion(unit.x * s, unit.y * s, unit.z * s, (float)Math.Cos(half)));
        }

        // Unity's Euler order for this harness: yaw, pitch, then roll, matching the existing runtime math stubs.
        public static Quaternion Euler(float x, float y, float z)
        {
            return Normalize(AngleAxis(y, Vector3.up) * AngleAxis(x, Vector3.right) * AngleAxis(z, Vector3.forward));
        }

        public static Quaternion Inverse(Quaternion value)
        {
            float norm = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            if (norm < 1e-8f)
                return identity;
            return new Quaternion(-value.x / norm, -value.y / norm, -value.z / norm, value.w / norm);
        }

        public static float Angle(Quaternion a, Quaternion b)
        {
            a = Normalize(a);
            b = Normalize(b);
            float dot = Math.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            dot = Math.Max(-1f, Math.Min(1f, dot));
            return 2f * (float)Math.Acos(dot) * Mathf.Rad2Deg;
        }

        public static Quaternion Slerp(Quaternion from, Quaternion to, float t)
        {
            from = Normalize(from);
            to = Normalize(to);
            float dot = from.x * to.x + from.y * to.y + from.z * to.z + from.w * to.w;
            if (dot < 0f)
            {
                to = new Quaternion(-to.x, -to.y, -to.z, -to.w);
                dot = -dot;
            }
            dot = Mathf.Clamp(dot, -1f, 1f);
            if (dot > 0.9995f)
                return Normalize(new Quaternion(
                    from.x + (to.x - from.x) * t,
                    from.y + (to.y - from.y) * t,
                    from.z + (to.z - from.z) * t,
                    from.w + (to.w - from.w) * t));
            float angle = Mathf.Acos(dot);
            float sine = Mathf.Sin(angle);
            float a = Mathf.Sin((1f - t) * angle) / sine;
            float b = Mathf.Sin(t * angle) / sine;
            return Normalize(new Quaternion(
                from.x * a + to.x * b,
                from.y * a + to.y * b,
                from.z * a + to.z * b,
                from.w * a + to.w * b));
        }

        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            Vector3 f = forward.normalized;
            if (f.sqrMagnitude < 1e-8f)
                return identity;
            Vector3 u = Vector3.ProjectOnPlane(upwards, f).normalized;
            if (u.sqrMagnitude < 1e-8f)
                u = Vector3.ProjectOnPlane(Vector3.up, f).normalized;
            if (u.sqrMagnitude < 1e-8f)
                u = Vector3.ProjectOnPlane(Vector3.right, f).normalized;
            Vector3 r = Vector3.Cross(u, f).normalized;
            u = Vector3.Cross(f, r).normalized;

            float trace = r.x + u.y + f.z;
            if (trace > 0f)
            {
                float s = (float)Math.Sqrt(trace + 1f) * 2f;
                return Normalize(new Quaternion((u.z - f.y) / s, (f.x - r.z) / s, (r.y - u.x) / s, 0.25f * s));
            }
            if (r.x > u.y && r.x > f.z)
            {
                float s = (float)Math.Sqrt(1f + r.x - u.y - f.z) * 2f;
                return Normalize(new Quaternion(0.25f * s, (r.y + u.x) / s, (r.z + f.x) / s, (u.z - f.y) / s));
            }
            if (u.y > f.z)
            {
                float s = (float)Math.Sqrt(1f + u.y - r.x - f.z) * 2f;
                return Normalize(new Quaternion((r.y + u.x) / s, 0.25f * s, (u.z + f.y) / s, (f.x - r.z) / s));
            }
            float last = (float)Math.Sqrt(1f + f.z - r.x - u.y) * 2f;
            return Normalize(new Quaternion((r.z + f.x) / last, (u.z + f.y) / last, 0.25f * last, (r.y - u.x) / last));
        }

        private static Quaternion Normalize(Quaternion value)
        {
            float length = (float)Math.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            return length > 1e-8f ? new Quaternion(value.x / length, value.y / length, value.z / length, value.w / length) : identity;
        }
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Deg2Rad = PI / 180f;
        public const float Rad2Deg = 180f / PI;

        public static float Abs(float value) => Math.Abs(value);
        public static float Acos(float value) => (float)Math.Acos(value);
        public static float Cos(float value) => (float)Math.Cos(value);
        public static float Sin(float value) => (float)Math.Sin(value);
        public static float Sqrt(float value) => (float)Math.Sqrt(value);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
        public static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        public static float Clamp01(float value) => Clamp(value, 0f, 1f);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Abs(target - current) <= maxDelta) return target;
            return current + Math.Sign(target - current) * maxDelta;
        }
        public static int FloorToInt(float value) => (int)Math.Floor(value);
        public static int CeilToInt(float value) => (int)Math.Ceiling(value);
        public static int RoundToInt(float value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
        public static float Round(float value) => (float)Math.Round(value, MidpointRounding.AwayFromZero);
        public static float Sign(float value) => value < 0f ? -1f : value > 0f ? 1f : 0f;
        public static float InverseLerp(float a, float b, float value)
        {
            if (a == b) return 0f;
            return Clamp01((value - a) / (b - a));
        }
        public static float SmoothStep(float from, float to, float t)
        {
            t = Clamp01((t - from) / (to - from));
            t = t * t * (3f - 2f * t);
            return t;
        }
        public static float DeltaAngle(float current, float target)
        {
            float delta = Repeat(target - current, 360f);
            if (delta > 180f) delta -= 360f;
            return delta;
        }
        public static float Repeat(float value, float length) => Clamp(value - (float)Math.Floor(value / length) * length, 0f, length);
    }

    public static class Time
    {
        public static float deltaTime;
        public static float time;
        public static int frameCount;
    }

    public sealed class Transform
    {
        private Transform _parent;
        private Vector3 _localPosition;
        private Quaternion _localRotation = Quaternion.identity;

        public Transform parent
        {
            get => _parent;
            set
            {
                Vector3 worldPosition = position;
                Quaternion worldRotation = rotation;
                _parent = value;
                position = worldPosition;
                rotation = worldRotation;
            }
        }

        public Vector3 localPosition
        {
            get => _localPosition;
            set => _localPosition = value;
        }

        public Quaternion localRotation
        {
            get => _localRotation;
            set => _localRotation = value;
        }

        public Vector3 position
        {
            get => _parent == null ? _localPosition : _parent.position + _parent.rotation * _localPosition;
            set => _localPosition = _parent == null ? value : Quaternion.Inverse(_parent.rotation) * (value - _parent.position);
        }

        public Quaternion rotation
        {
            get => _parent == null ? _localRotation : _parent.rotation * _localRotation;
            set => _localRotation = _parent == null ? value : Quaternion.Inverse(_parent.rotation) * value;
        }

        public Vector3 forward => rotation * Vector3.forward;
        public Vector3 TransformDirection(Vector3 direction) => rotation * direction;

        public void SetPositionAndRotation(Vector3 worldPosition, Quaternion worldRotation)
        {
            position = worldPosition;
            rotation = worldRotation;
        }

        public static bool operator !(Transform value) => value == null;
        public static bool operator true(Transform value) => value != null;
        public static bool operator false(Transform value) => value == null;
        public static implicit operator bool(Transform value) => value != null;
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1f)
        {
            this.r = r; this.g = g; this.b = b; this.a = a;
        }
        public static Color red => new Color(1f, 0f, 0f);
        public static Color blue => new Color(0f, 0f, 1f);
        public static Color green => new Color(0f, 1f, 0f);
        public static Color yellow => new Color(1f, 1f, 0f);
        public static Color cyan => new Color(0f, 1f, 1f);
    }
}

namespace EFT
{
    using UnityEngine;
    using EFT.InventoryLogic;
    public enum EPlayerState { Run, Idle, Sprint, Jump, JumpLanding, FallDown, ClimbOver, ClimbUp, VaultingFallDown, VaultingLanding }
    public sealed class MovementState { public EPlayerState Name; }
    public sealed class MovementContext { public bool IsGrounded = true; public MovementState CurrentState = new MovementState(); }

    public abstract class AbstractHandsController { }

    public sealed class Player
    {
        public MovementContext MovementContext = new MovementContext();
        public MovementState CurrentManagedState;
        public Grounder Grounder = new Grounder();
        public AbstractHandsController HandsController { get; set; }

        public sealed class GrenadeHandsController : AbstractHandsController { }

        public sealed class EmptyHandsController : AbstractHandsController { }

        public sealed class FirearmController : AbstractHandsController
        {
            public Weapon Item { get; set; }
            public FirearmOperation CurrentOperation { get; set; }

            public class FirearmOperation { }
            public sealed class Remove : FirearmOperation { }
            public sealed class SpawnOperation : FirearmOperation { }
        }
    }

    public sealed class Grounder
    {
        public Ik ik = new Ik();
        public GrounderSolver solver = new GrounderSolver();
        public float weight = 1f;
    }

    public sealed class Ik
    {
        public IkReferences references = new IkReferences();
    }

    public sealed class IkReferences
    {
        public Transform pelvis, leftThigh, leftCalf, leftFoot, rightThigh, rightCalf, rightFoot;
    }

    public sealed class GrounderSolver
    {
        public bool initiated = true;
        public GrounderLeg[] legs = new GrounderLeg[2];
    }

    public sealed class GrounderLeg
    {
        public bool initiated = true;
        public bool isGrounded = true;
        public float lastTime;
        public Vector3 toHitNormal = Vector3.up;
        public float up = 1f;
    }
}

namespace EFT.InventoryLogic
{
    public sealed class Weapon { }
}

namespace Manimal.MotionMatching
{
    using UnityEngine;

    internal sealed class PoseDatabase
    {
        public bool HasSolePoints;
        public Vector3[] SoleHeel = new Vector3[2];
        public Vector3[] SoleToe = new Vector3[2];
    }

    internal sealed class PoseClip
    {
        public string Name;
        public float Fps;
        public int Frames;
        public bool Loop;
        public bool EndsInTarkovIdle;
        public float SpeedMetersPerSecond;
        public float[] RootSpeed;
        public Vector2[] RootVelocity;
        public Vector3[] FootL;
        public Vector3[] FootR;
        public StrideFoot[] Stride;
        public string Family;
        public float[] PathLength;
        public float[] YawProgress;

        public void BuildPathLength()
        {
            if (PathLength != null) return;
            PathLength = new float[Frames];
            float travelled = 0f;
            for (int frame = 0; frame < Frames; frame++)
            {
                PathLength[frame] = travelled;
                travelled += RootSpeed == null || RootSpeed.Length <= frame || Fps <= 0f ? 0f : RootSpeed[frame] / Fps;
            }
        }
    }

    internal sealed class PosePlayback
    {
        public StrideState State;
        public bool Available = true;
        public Action OnTryGetStrideState;

        public bool TryGetStrideState(out StrideState state)
        {
            OnTryGetStrideState?.Invoke();
            state = State;
            return Available;
        }
    }

    internal sealed class StrideFoot
    {
        public StrideCycle[] Cycles;
        public int[] Cycle;
        public float[] Progression;
        public Vector3[] Offset;
        public float[] RotationOffset;
        public Vector3[] Footbase;
        public float[] Heading;
        public bool[] Grounded;
        public float Floor;
    }

    internal sealed class StrideCycle
    {
        public int StartFrame;
        public int EndFrame;
        public int StrikeFrame;
        public Vector3 StancePosition;
        public float StanceDirection;
        public float StrideLength;
        public float StrideYaw;
        public float RotationChange;
        public Vector3 ToStrideStartPos;
        public bool HasMidpoint;
        public int MiddleFrame;
        public Vector3 MiddlePosition;
        public Vector3 MiddleOffset;
        public float MiddleProgression;
        public float LiftCycle, OffCycle, StrikeCycle, LandCycle;
        public bool Stationary;
        public bool VirtualStart, VirtualEnd;

        public float CycleTime(float clipFrame, int period, bool loop)
        {
            float unwrapped = clipFrame;
            if (loop && period > 0 && unwrapped < StartFrame)
                unwrapped += period;
            int span = EndFrame - StartFrame;
            return span > 0 ? (unwrapped - StartFrame) / span : 0f;
        }

        public bool InAuthoredSwing(float cycleTime) => cycleTime >= LiftCycle && cycleTime < StrikeCycle;
    }

    internal static class LocomotionRefinement
    {
        public static bool Enabled = true;
    }

    internal static class DebugDraw
    {
        public static bool Enabled;
        public static void Polyline(IList<Vector3> points, Color color) { }
        public static void Box(Vector3 center, Color color, float length = 0.28f, float width = 0.12f, float height = 0.04f, float yawDegrees = 0f) { }
        public static void Line(Vector3 a, Vector3 b, Color color) { }
    }
}
