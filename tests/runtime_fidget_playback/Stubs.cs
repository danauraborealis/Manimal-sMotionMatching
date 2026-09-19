using System;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsQuaternion = System.Numerics.Quaternion;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public float sqrMagnitude => x * x + y * y + z * z;

        public static Vector3 operator +(Vector3 left, Vector3 right)
        {
            return new Vector3(left.x + right.x, left.y + right.y, left.z + right.z);
        }

        public static Vector3 operator -(Vector3 left, Vector3 right)
        {
            return new Vector3(left.x - right.x, left.y - right.y, left.z - right.z);
        }

        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t)
        {
            return new Vector3(
                a.x + (b.x - a.x) * t,
                a.y + (b.y - a.y) * t,
                a.z + (b.z - a.z) * t);
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

        public Quaternion normalized
        {
            get
            {
                float norm = MathF.Sqrt(Dot(this, this));
                return norm > 0f
                    ? new Quaternion(x / norm, y / norm, z / norm, w / norm)
                    : identity;
            }
        }

        public static float Dot(Quaternion left, Quaternion right)
        {
            return left.x * right.x + left.y * right.y + left.z * right.z + left.w * right.w;
        }

        public static float Angle(Quaternion left, Quaternion right)
        {
            float dot = MathF.Abs(Dot(left.normalized, right.normalized));
            dot = MathF.Min(1f, MathF.Max(0f, dot));
            return 2f * MathF.Acos(dot) * (180f / MathF.PI);
        }

        public static Quaternion SlerpUnclamped(Quaternion left, Quaternion right, float t)
        {
            NumericsQuaternion result = NumericsQuaternion.Slerp(
                new NumericsQuaternion(left.x, left.y, left.z, left.w),
                new NumericsQuaternion(right.x, right.y, right.z, right.w),
                t);
            return new Quaternion(result.X, result.Y, result.Z, result.W).normalized;
        }

        public static Vector3 operator *(Quaternion rotation, Vector3 vector)
        {
            NumericsVector3 result = NumericsVector3.Transform(
                new NumericsVector3(vector.x, vector.y, vector.z),
                new NumericsQuaternion(rotation.x, rotation.y, rotation.z, rotation.w));
            return new Vector3(result.X, result.Y, result.Z);
        }
    }

    public static class Mathf
    {
        public static float Clamp(float value, float minimum, float maximum)
        {
            return value < minimum ? minimum : (value > maximum ? maximum : value);
        }

        public static int FloorToInt(float value) => (int)MathF.Floor(value);
    }
}
