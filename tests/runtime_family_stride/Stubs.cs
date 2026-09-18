using System;

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
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 value, float scalar) => new Vector3(value.x * scalar, value.y * scalar, value.z * scalar);
        public static Vector3 operator *(float scalar, Vector3 value) => value * scalar;
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Deg2Rad = PI / 180f;
        public const float Rad2Deg = 180f / PI;

        public static float Abs(float value) => Math.Abs(value);
        public static int Abs(int value) => Math.Abs(value);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Cos(float value) => (float)Math.Cos(value);
        public static float Sin(float value) => (float)Math.Sin(value);
        public static float Sqrt(float value) => (float)Math.Sqrt(value);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
        public static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        public static float Clamp01(float value) => Clamp(value, 0f, 1f);

        public static float DeltaAngle(float current, float target)
        {
            float delta = Repeat(target - current, 360f);
            if (delta > 180f)
                delta -= 360f;
            return delta;
        }

        private static float Repeat(float value, float length)
        {
            return Clamp(value - (float)Math.Floor(value / length) * length, 0f, length);
        }
    }
}

namespace Manimal.MotionMatching
{
    using UnityEngine;

    internal sealed class PoseClip
    {
        public string Name;
        public float Fps;
        public int Frames;
        public bool Loop;
        public float SpeedMetersPerSecond;
        public Vector3[] PelvisPosition;
        public float[] RootSpeed;
        public float[] PathLength;
        public string Family;
        public int PhaseOffset;
        public float MoveYaw;
        public float FromYaw;
        public float ToYaw;
        public int TurnFrame;
        public float YawChange;
        public float YawExcursion;
        public float[] YawProgress;
        public UnityEngine.Vector2[] RootVelocity;
        public bool[][] Contact;
        public UnityEngine.Vector3[] FootL;
        public UnityEngine.Vector3[] FootR;
        public StrideFoot[] Stride;
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
        public bool VirtualStart;
        public bool VirtualEnd;
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
}
